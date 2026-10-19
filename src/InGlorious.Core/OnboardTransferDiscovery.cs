using System.ComponentModel;
using System.IO;

namespace InGlorious.Core;

internal enum OnboardDiscoveryStage { Enumeration, Open, Info, Extensions, ProfileStatus, InstallationIdentity, Close, ConnectionCheck }
internal enum OnboardDiscoveryFailure { Timeout, Busy, ConnectionReset, NativeFailure, DeviceRejected, InvalidReply, IdentityChanged, Unexpected, MetadataReadFailed }
internal sealed record OnboardDiscoveryDiagnostic(string DeviceId, OnboardDiscoveryStage Stage, int Attempt, OnboardDiscoveryFailure Failure, int? Code = null,
    OnboardMetadataOperation? MetadataOperation = null, OnboardNativeErrorDomain? NativeDomain = null, uint? NativeCode = null);
internal sealed record OnboardTransferTarget(OnboardDevice Device, OnboardInfo Info, OnboardExtensions? Extensions,
    OnboardProfileBinding? ProfileBinding, OnboardHardwareProfileStatus? ProfileStatus);
internal sealed record OnboardTransferDiscoveryResult(IReadOnlyList<OnboardTransferTarget> Targets, bool Failed);

/// <summary>Retries only a transient, read-only discovery. No configuration, runtime-setting or flash commands are sent.</summary>
internal static class OnboardTransferDiscovery
{
    internal const int MaximumAttempts = 2;

    public static Task<OnboardTransferDiscoveryResult> FindAsync(string deviceId, bool requireExtensions,
        OnboardProfileBinding? binding, Action<OnboardDiscoveryDiagnostic>? record = null, CancellationToken cancellationToken = default) =>
        FindAsync(deviceId, requireExtensions, binding, ct => Task.Run(OnboardClient.FindDevices, ct), OnboardClient.Open,
            ct => Task.Delay(200, ct), record, cancellationToken);

    internal static async Task<OnboardTransferDiscoveryResult> FindAsync(string deviceId, bool requireExtensions,
        OnboardProfileBinding? binding, Func<CancellationToken, Task<IReadOnlyList<OnboardDevice>>> discover,
        Func<OnboardDevice, OnboardClient> open, Func<CancellationToken, Task> pause,
        Action<OnboardDiscoveryDiagnostic>? record = null, CancellationToken cancellationToken = default)
    {
        if (!BoardDescriptorRegistry.TryGet(deviceId, out _) || binding is not null && binding.DeviceId != deviceId)
            throw new ArgumentException("Discovery model and binding must agree.", nameof(deviceId));
        OnboardDevice[]? original = null;
        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stage = OnboardDiscoveryStage.Enumeration;
            var targets = new List<OnboardTransferTarget>();
            bool failed = false, transientOnly = true;
            try
            {
                var candidates = (await discover(cancellationToken).ConfigureAwait(false)).Where(d => d.DeviceId == deviceId).ToArray();
                if (original is not null && !SameEndpoints(original, candidates))
                {
                    Record(new(deviceId, OnboardDiscoveryStage.ConnectionCheck, attempt, OnboardDiscoveryFailure.IdentityChanged));
                    return new([], true);
                }
                original ??= candidates;
                foreach (var device in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(12));
                    stage = OnboardDiscoveryStage.Open;
                    try
                    {
                        OnboardTransferTarget target;
                        var client = open(device);
                        Exception? probeFailure = null;
                        var probeStage = stage;
                        try
                        {
                            stage = OnboardDiscoveryStage.Info;
                            var info = await client.GetInfoAsync(deadline.Token).ConfigureAwait(false);
                            stage = OnboardDiscoveryStage.Extensions;
                            var extensions = requireExtensions || deviceId == "gmmk3-pro-100-iso"
                                ? await client.GetExtensionsAsync(deadline.Token).ConfigureAwait(false) : null;
                            OnboardHardwareProfileStatus? status = null; OnboardProfileBinding? profile = null;
                            if (extensions?.SupportsHardwareProfiles == true)
                            {
                                stage = OnboardDiscoveryStage.ProfileStatus;
                                status = await client.GetHardwareProfileStatusAsync(binding?.Slot ?? 0, deadline.Token).ConfigureAwait(false)
                                    ?? throw new InvalidDataException("Profile status missing.");
                                stage = OnboardDiscoveryStage.InstallationIdentity;
                                profile = status.Bind(await client.GetInstallationIdAsync(deadline.Token).ConfigureAwait(false));
                            }
                            target = new(device, info, extensions, profile, status);
                        }
                        catch (Exception error) { probeFailure = error; probeStage = stage; throw; }
                        finally
                        {
                            try { stage = OnboardDiscoveryStage.Close; await client.DisposeAsync().ConfigureAwait(false); }
                            catch when (probeFailure is not null) { /* Preserve the original rejection, never turn it into a retryable close failure. */ }
                            finally { if (probeFailure is not null) stage = probeStage; }
                        }
                        // A different installation is not a transient error, and must never become a replacement target.
                        if (binding is not null && target.ProfileBinding is { } found && found.InstallationId != binding.InstallationId)
                        {
                            Record(new(deviceId, OnboardDiscoveryStage.InstallationIdentity, attempt, OnboardDiscoveryFailure.IdentityChanged));
                            failed = true; transientOnly = false; continue;
                        }
                        targets.Add(target);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        failed = true;
                        var failure = Classify(deviceId, stage, attempt, error);
                        transientOnly &= IsTransient(failure);
                        Record(failure);
                    }
                }
                // Never hand out a stale endpoint after a hotplug during the probe.
                stage = OnboardDiscoveryStage.ConnectionCheck;
                var current = (await discover(cancellationToken).ConfigureAwait(false)).Where(d => d.DeviceId == deviceId).ToArray();
                if (!SameEndpoints(candidates, current))
                {
                    Record(new(deviceId, stage, attempt, OnboardDiscoveryFailure.IdentityChanged));
                    return new([], true);
                }
                // A partial result could silently turn an ambiguous pair into one selected keyboard.
                // Keep transfer unavailable until every candidate in this discovery has been checked.
                if (!failed || !transientOnly || attempt == MaximumAttempts) return new(failed ? [] : targets, failed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var failure = Classify(deviceId, stage, attempt, error);
                Record(failure);
                if (!transientOnly || !IsTransient(failure) || attempt == MaximumAttempts) return new([], true);
            }
            await pause(cancellationToken).ConfigureAwait(false);
        }
        return new([], true);

        void Record(OnboardDiscoveryDiagnostic diagnostic)
        {
            try { record?.Invoke(diagnostic); }
            catch { /* A private diagnostic cannot change device discovery or trigger another attempt. */ }
        }
    }

    private static bool SameEndpoints(IReadOnlyList<OnboardDevice> before, IReadOnlyList<OnboardDevice> after) =>
        before.Count == after.Count && before.Distinct().Count() == before.Count && after.Distinct().Count() == after.Count &&
        before.All(device => after.Contains(device));

    internal static bool IsTransient(OnboardDiscoveryDiagnostic diagnostic) => diagnostic.Failure is
        OnboardDiscoveryFailure.Timeout or OnboardDiscoveryFailure.Busy or OnboardDiscoveryFailure.ConnectionReset;
        // Native metadata owns its bounded retry. Do not multiply an exhausted native budget here.

    internal static OnboardDiscoveryDiagnostic Classify(string id, OnboardDiscoveryStage stage, int attempt, Exception error)
    {
        // Cancellation of the caller is handled before classification. Only command reads have internal deadlines.
        bool commandRead = stage is OnboardDiscoveryStage.Info or OnboardDiscoveryStage.Extensions or
            OnboardDiscoveryStage.ProfileStatus or OnboardDiscoveryStage.InstallationIdentity;
        if (error is OnboardMetadataQueryException metadata)
            return new(id, stage, attempt, OnboardDiscoveryFailure.MetadataReadFailed, MetadataOperation: metadata.Operation,
                NativeDomain: metadata.Domain, NativeCode: metadata.Code);
        if (commandRead && error is (OperationCanceledException or TimeoutException))
            return new(id, stage, attempt, OnboardDiscoveryFailure.Timeout);
        if (error is OnboardCommandException rejected)
            return new(id, stage, attempt, rejected.Status == 3 ? OnboardDiscoveryFailure.Busy : OnboardDiscoveryFailure.DeviceRejected, rejected.Status);
        if (error is OnboardTransportResetException)
            return new(id, stage, attempt, OnboardDiscoveryFailure.ConnectionReset);
        if (error is InvalidDataException) return new(id, stage, attempt, OnboardDiscoveryFailure.InvalidReply);
        int? native = error is Win32Exception win32 ? win32.NativeErrorCode :
            error is IOException && ((uint)error.HResult & 0xffff0000) == 0x80070000 ? error.HResult & 0xffff : null;
        if (native is { } code)
            return new(id, stage, attempt, code switch
            {
                121 => OnboardDiscoveryFailure.Timeout, // ERROR_SEM_TIMEOUT
                21 or 32 or 33 or 170 => OnboardDiscoveryFailure.Busy,
                995 or 1167 => OnboardDiscoveryFailure.ConnectionReset,
                _ => OnboardDiscoveryFailure.NativeFailure
            }, code);
        return new(id, stage, attempt, OnboardDiscoveryFailure.Unexpected);
    }
}

