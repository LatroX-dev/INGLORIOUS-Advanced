using System.IO;

namespace InGlorious.Core;

internal enum OnboardMetadataOperation
{
    OpenMetadata, Attributes, Manufacturer, Product, ReportDescriptor, ReportCapabilities,
    InterfaceSet, InterfaceEnumeration, InterfaceOpen, InterfaceDetails, DeviceInstanceId, DeviceParent
}
internal enum OnboardNativeErrorDomain { Win32, NtStatus, ConfigRet }

/// <summary>A native query did not produce a value. This is distinct from successfully reading the wrong identity.</summary>
internal sealed class OnboardMetadataQueryException(OnboardMetadataOperation operation, OnboardNativeErrorDomain domain, uint code)
    : IOException("A device metadata query did not complete.")
{
    public OnboardMetadataOperation Operation { get; } = operation;
    public OnboardNativeErrorDomain Domain { get; } = domain;
    public uint Code { get; } = code;
    internal bool DiagnosticRecorded { get; set; }
    internal bool CleanupFailed { get; set; }
}

internal enum OnboardMetadataStage { Enumeration, Open }
internal enum OnboardMetadataOutcome { Retry, Recovered, Failed, Rejected }
internal sealed record OnboardMetadataDiagnostic(string? DeviceId, OnboardMetadataStage Stage, int Attempt,
    OnboardMetadataOutcome Outcome, OnboardMetadataOperation Operation, OnboardNativeErrorDomain Domain, uint Code);

internal readonly record struct OnboardHidAttributes(ushort VendorId, ushort ProductId);
internal readonly record struct OnboardHidCapabilities(ushort UsagePage, ushort Usage, ushort InputBytes, ushort OutputBytes);

// The production reader uses only a synchronous metadata handle. Offline fixtures can inject individual failed queries.
internal interface IOnboardMetadataReader
{
    OnboardHidAttributes Attributes();
    string Manufacturer();
    string Product();
    OnboardHidCapabilities Capabilities();
    string? UsbRoot();
}
internal interface IOnboardMetadataSession : IOnboardMetadataReader, IDisposable { }
internal enum OnboardHidHandleMode { Metadata, Reports }

internal static class OnboardMetadata
{
    internal const int MaximumAttempts = 2;
    internal static Action<OnboardMetadataDiagnostic>? DiagnosticSink { get; set; }

    // Only complete native metadata sessions are repeated. No report exchange can occur in this callback.
    internal static T Read<T>(string? deviceId, OnboardMetadataStage stage, Func<T> read, Action? pause = null)
    {
        OnboardMetadataQueryException? prior = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = read();
                // After a failed query, a now-nonmatching collection is a changed/uncertain candidate,
                // not proof that the original endpoint can safely disappear from a multi-device scan.
                if (prior is not null && result is null)
                    throw new InvalidDataException("The device identity changed during metadata verification.");
                if (prior is not null) Record(deviceId, stage, attempt, OnboardMetadataOutcome.Recovered, prior);
                return result;
            }
            catch (OnboardMetadataQueryException error)
            {
                var retry = attempt < MaximumAttempts && !error.CleanupFailed && CanRetry(error.Operation, error.Domain, error.Code);
                Record(deviceId, stage, attempt, retry ? OnboardMetadataOutcome.Retry : OnboardMetadataOutcome.Failed, error);
                if (!retry) throw;
                prior = error;
            }
            catch
            {
                if (prior is not null) Record(deviceId, stage, attempt, OnboardMetadataOutcome.Rejected, prior);
                throw;
            }
            if (pause is null) Thread.Sleep(60);
            else pause();
        }
    }

    internal static void Record(string? deviceId, OnboardMetadataStage stage, int attempt,
        OnboardMetadataOutcome outcome, OnboardMetadataQueryException error)
    {
        error.DiagnosticRecorded = true;
        try { DiagnosticSink?.Invoke(new(deviceId, stage, attempt, outcome, error.Operation, error.Domain, error.Code)); }
        catch { /* Diagnostics must not change rejection, retry or the result of a read. */ }
    }

    internal static void RequireWin32(bool success, OnboardMetadataOperation operation, int code)
    {
        if (!success) throw new OnboardMetadataQueryException(operation, OnboardNativeErrorDomain.Win32, unchecked((uint)code));
    }
    internal static void RequireConfigRet(uint status, OnboardMetadataOperation operation)
    {
        if (status != 0) throw new OnboardMetadataQueryException(operation, OnboardNativeErrorDomain.ConfigRet, status);
    }
    internal static void RequireCapabilitiesStatus(int status)
    {
        if (status != 0x00110000) throw new OnboardMetadataQueryException(OnboardMetadataOperation.ReportCapabilities,
            OnboardNativeErrorDomain.NtStatus, unchecked((uint)status));
    }
    internal static bool CanRetry(OnboardMetadataOperation operation, OnboardNativeErrorDomain domain, uint code)
    {
        if (!Enum.IsDefined(operation)) return false;
        // Unknown-error false returns stay explicit and blocked. Only an actual failed native query reaches this policy;
        // successful but wrong identity data never does. Access denial, invalid arguments and malformed descriptors do not retry.
        return domain switch
        {
            OnboardNativeErrorDomain.Win32 => code is 21 or 32 or 33 or 121 or 170 or 995 or 1167,
            OnboardNativeErrorDomain.ConfigRet => code is 0x0D or 0x24, // node disappeared, device absent
            _ => false
        };
    }
}
