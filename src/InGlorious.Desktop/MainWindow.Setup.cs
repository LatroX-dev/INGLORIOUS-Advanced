using Avalonia.Controls;
using InGlorious.Core;
using System.Diagnostics;

namespace InGlorious.Desktop;

internal enum SetupRuntimeState { Unknown, Current, UpdateAvailable, Newer }
internal sealed record SetupRuntimeAssessment(SetupRuntimeState State, OnboardFirmwareVersion? Version);

public sealed partial class MainWindow
{
    private bool _firmwareFlow, _automaticSetupChecked;
    private readonly Dictionary<string, SetupRuntimeAssessment> _firmwareAssessments = [];

    internal static SetupRuntimeState ClassifySetupFirmware(string id, OnboardInfo? info, OnboardFirmwareVersion? version)
    {
        if (!BoardDescriptorRegistry.TryGet(id, out _) || info is null || info.DeviceId != id || !info.Persistent ||
            (info.Flags & 4) != 0 || info.StagingBytes != 0 || version is null || version.DeviceId != id || version.BuildId == 0 ||
            !FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(id), out var target)) return SetupRuntimeState.Unknown;
        return version.Version < target ? SetupRuntimeState.UpdateAvailable : version.Version > target ? SetupRuntimeState.Newer : SetupRuntimeState.Current;
    }

    private async Task OpenDeviceSetupAsync(string? preferredDeviceId = null, SetupOperation operation = SetupOperation.Install, Window? owner = null, bool allowBusy = false,
        string? preferredInstanceId = null)
    {
        if (Program.Offline || _firmwareFlow || _startupLoading || _startupWindowClosed || _allowClose || _busy && !allowBusy) return;
        if (_pendingApply is not null && !await GuardNavigation()) return;
        if (_firmwareFlow || _startupWindowClosed || _allowClose) return;
        var priorBusy = _busy; _firmwareFlow = _busy = true; _root.IsEnabled = false;
        CoreImportSelection? importedCore = null;
        string? feedbackDeviceId = null;
        OnboardFirmwareVersion? feedbackVersion = null;
        IDisposable? knobStatusPause = null;
        try
        {
            knobStatusPause = await PauseKnobStatusAsync();
            await _sharedBrightness.StopAsync();
            var dialog = new DeviceSetupWindow(preferredDeviceId, operation, preferredInstanceId: preferredInstanceId);
            await dialog.ShowDialog(owner ?? this);
            InvalidateFirmwareOutcome(dialog);
            if (dialog.Completed) Status(L.T("Prüfe jetzt die Tasten und die Beleuchtung."));
            if (dialog.Completed) importedCore = dialog.ImportedCoreSelection;
            if (dialog.Completed && operation == SetupOperation.Install)
            { feedbackDeviceId = dialog.DeviceId; feedbackVersion = dialog.ReportedVersion; }
        }
        catch (Exception ex) { Trace.TraceError("Firmware setup failed: {0}", ex); Status(DeviceSetupService.ForUser(ex)); }
        finally
        {
            try
            {
                _firmwareFlow = _busy = false;
                if (importedCore is not null) { _root.IsEnabled = true; await AdoptSetupCoreImportAsync(importedCore); }
                if (feedbackDeviceId is not null) await MaybeOfferExperimentalFeedbackAsync(feedbackDeviceId, DeviceFeedbackTrigger.Installation, feedbackVersion, owner);
                _busy = priorBusy;
                if (!_startupWindowClosed && !_allowClose)
                { _root.IsEnabled = !_busy; UpdateFooter(); await UpdateBrightnessService(); await RefreshConnectionsAsync(); }
            }
            finally { knobStatusPause?.Dispose(); }
        }
    }

    private void InvalidateFirmwareOutcome(DeviceSetupWindow dialog)
    {
        if (!dialog.DeviceStateMayHaveChanged || dialog.DeviceId is not { } id) return;
        _confirmed.Remove(id); _firmwareAssessments.Remove(id); _startupNotices.Remove(id); _startupReadFingerprints.Remove(id);
    }

    private async Task CheckFirmwareSetupAsync(bool force = false, string? requestedId = null, Window? owner = null)
    {
        if (Program.Offline || _firmwareFlow || _busy || _startupLoading || _startupWindowClosed || _allowClose || !force && _automaticSetupChecked) return;
        if (requestedId is not null && DeviceCatalog.Get(requestedId).IsOfflineOnly) return;
        if (_pendingApply is not null && !await GuardNavigation()) return;
        if (_firmwareFlow || _startupWindowClosed || _allowClose) return;
        _automaticSetupChecked = true; _firmwareFlow = _busy = true; _root.IsEnabled = false;
        CoreImportSelection? importedCore = null;
        string? feedbackDeviceId = null;
        OnboardFirmwareVersion? feedbackVersion = null;
        IDisposable? knobStatusPause = null;
        try
        {
            knobStatusPause = await PauseKnobStatusAsync();
            await _sharedBrightness.StopAsync();
            if (DeviceSetupService.IsGloriousCoreRunning() is not false)
            { if (force) Status(L.T("Schließe Glorious CORE, bevor du fortfährst.")); return; }
            var service = new DeviceSetupService();
            var devices = await service.ListDevicesAsync();
            if (_startupWindowClosed || _allowClose) return;
            var eligible = devices.Where(device => device.HasFirmwareCandidate && (requestedId is null || device.CandidateDeviceIds.Contains(requestedId))).ToArray();
            if (eligible.Any(device => !device.IsRuntime))
            {
                var install = new DeviceSetupWindow(requestedId);
                await install.ShowDialog(owner ?? this); InvalidateFirmwareOutcome(install);
                if (install.Completed) importedCore = install.ImportedCoreSelection;
                if (install.Completed) { feedbackDeviceId = install.DeviceId; feedbackVersion = install.ReportedVersion; }
                return;
            }
            var ids = requestedId is not null ? new[] { requestedId } : eligible.Where(d => d.IsRuntime).SelectMany(d => d.CandidateDeviceIds).Distinct().ToArray();
            foreach (var id in ids)
            {
                _firmwareAssessments.Remove(id);
                var physical = eligible.Where(device => device.IsRuntime && device.CandidateDeviceIds.Count == 1 && device.CandidateDeviceIds[0] == id).ToArray();
                if (physical.Length != 1) { _firmwareAssessments[id] = new(SetupRuntimeState.Unknown, null); continue; }
                _firmwareAssessments[id] = await ReadSetupRuntimeAsync(physical[0]);
            }
            if (_startupWindowClosed || _allowClose) return;
            var updateId = ids.FirstOrDefault(id => _firmwareAssessments.TryGetValue(id, out var assessment) && assessment.State == SetupRuntimeState.UpdateAvailable);
            if (updateId is not null)
            {
                var update = new DeviceSetupWindow(updateId, SetupOperation.Update);
                await update.ShowDialog(owner ?? this); InvalidateFirmwareOutcome(update);
            }
            else if (force)
                Status(requestedId is not null && _firmwareAssessments.TryGetValue(requestedId, out var result)
                    ? SetupStatusText(result) : L.T("Die Firmware-Version konnte nicht geprüft werden."));
        }
        catch (Exception ex) { Trace.TraceError("Firmware discovery failed: {0}", ex); Status(DeviceSetupService.ForUser(ex)); }
        finally
        {
            try
            {
                _firmwareFlow = _busy = false;
                if (importedCore is not null) { _root.IsEnabled = true; await AdoptSetupCoreImportAsync(importedCore); }
                if (!_startupWindowClosed && !_allowClose) { Build(); await UpdateBrightnessService(); await RefreshConnectionsAsync(); }
                if (feedbackDeviceId is not null) await MaybeOfferExperimentalFeedbackAsync(feedbackDeviceId, DeviceFeedbackTrigger.Installation, feedbackVersion, owner);
            }
            finally { knobStatusPause?.Dispose(); }
        }
    }

    private static async Task<SetupRuntimeAssessment> ReadSetupRuntimeAsync(SetupDevice physical)
    {
        try
        {
            if (DeviceSetupService.IsGloriousCoreRunning() is not false) return new(SetupRuntimeState.Unknown, null);
            var candidates = (await Task.Run(OnboardClient.FindDevices)).Where(device => DeviceSetupWindow.MatchesSelectedRuntime(device, physical)).ToArray();
            if (candidates.Length != 1) return new(SetupRuntimeState.Unknown, null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var client = OnboardClient.Open(candidates[0]);
            var info = await client.GetInfoAsync(timeout.Token);
            var version = await client.GetFirmwareVersionAsync(timeout.Token);
            if (DeviceSetupService.IsGloriousCoreRunning() is not false) return new(SetupRuntimeState.Unknown, null);
            var state = ClassifySetupFirmware(candidates[0].DeviceId, info, version);
            return new(state, state == SetupRuntimeState.Unknown ? null : version);
        }
        catch (Exception ex) { Trace.TraceError("Runtime version read failed: {0}", ex); return new(SetupRuntimeState.Unknown, null); }
    }

    private static string SetupStatusText(SetupRuntimeAssessment assessment) => assessment.State switch
    {
        SetupRuntimeState.Current => L.T("Firmware ist aktuell"),
        SetupRuntimeState.UpdateAvailable => L.T("Eine neuere Firmware-Version ist verfügbar."),
        SetupRuntimeState.Newer => L.T("Die installierte Firmware ist neuer als die Version dieser App. Es wird kein Downgrade angeboten."),
        _ => L.T("Die Firmware-Version konnte nicht geprüft werden.")
    };

    private DeviceSetupWindow CreateOfflineSetupPreview()
    {
        if (!Program.Offline) throw new InvalidOperationException("Offline setup preview required.");
        var operation = Program.Arguments.Contains("--setup-restore") ? SetupOperation.Restore
            : Program.Arguments.Contains("--setup-update") ? SetupOperation.Update : SetupOperation.Install;
        var window = new DeviceSetupWindow(_state.Device.Id, operation, offlinePreview: true);
        var step = Array.IndexOf(Program.Arguments, "--setup-step");
        window.ShowOfflineStep(step >= 0 && step + 1 < Program.Arguments.Length ? Program.Arguments[step + 1] : "select", _state.Device.Id);
        if (Program.Arguments.Contains("--minimum")) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
        return window;
    }
}
