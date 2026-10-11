namespace InGlorious.Core;

public partial class FirmwareWindow
{
    private FirmwareRuntimeVersion? _runtimeVersionHint;
    private readonly Func<FirmwarePlan, Task<FirmwareAvailability>> _checkBootloader;
    private bool UpdateVersionMissing => Operation == FirmwareOperation.Update && _runtimeVersionHint is null;
    private bool VersionBlocksUpdate => Operation == FirmwareOperation.Update
        && (_runtimeVersionHint is not { } runtime || runtime.Report.Version >= FirmwareRelease.CurrentVersion);

    internal void SetRuntimeVersionHint(FirmwareRuntimeVersion? hint)
    {
        if (_plan is not null || _preparing || _executionAttempted) return;
        var pid = _deviceId == "pro-iso" ? "1C01" : "1C02";
        _runtimeVersionHint = Operation == FirmwareOperation.Update && hint is not null && hint.Report.DeviceId == _deviceId && hint.Report.BuildId != 0
            && System.Text.RegularExpressions.Regex.IsMatch(hint.UsbInstanceId ?? "", @"\AUSB\\VID_FEED&PID_" + pid + @"\\[^\\\r\n\x00]{1,256}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            && hint.ObservedAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - hint.ObservedAt <= TimeSpan.FromMinutes(1) ? hint : null;
        RenderStep();
    }

    private void RenderInstalledVersion()
    {
        UpdateChangelog.ShowChanges(_deviceId, Operation == FirmwareOperation.Update && _step == Step.Overview
            ? _runtimeVersionHint?.Report.Version : null, FirmwareRelease.CurrentVersion);
        if (Operation != FirmwareOperation.Update) return;
        VersionText.Text = _runtimeVersionHint is { } runtime
            ? "INGLORIOUS " + runtime.Report.Version + (VersionBlocksUpdate ? "" : " → " + FirmwareRelease.DisplayVersion)
            : L.T("Die Firmware-Version konnte nicht geprüft werden.");
        ScopeText.Visibility = VersionBlocksUpdate ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        ScopeText.Text = L.T("Deine Einstellungen bleiben erhalten. Die App sichert den aktuellen Zustand vor dem Update.");
    }

    internal void RunRuntimeVersionChecks(Action<bool, string> check)
    {
        if (!_offlinePreview || Operation != FirmwareOperation.Update) return;
        var now = DateTimeOffset.UtcNow;
        FirmwareRuntimeVersion Hint(FirmwareSemanticVersion version) => new(new(_deviceId, version, 1), "USB\\VID_FEED&PID_" + (_deviceId == "pro-iso" ? "1C01" : "1C02") + "\\fixture", now);
        foreach (var version in new[] { new FirmwareSemanticVersion(0, 7, 0), new FirmwareSemanticVersion(0, 8, 0), new FirmwareSemanticVersion(0, 9, 0), FirmwareRelease.CurrentVersion, new FirmwareSemanticVersion(0, 9, 3) })
        {
            ShowOfflineStep("overview"); _runtimeVersionHint = null; SetRuntimeVersionHint(Hint(version));
            check(VersionText.Text.Contains("INGLORIOUS " + version) && VersionBlocksUpdate == (version >= FirmwareRelease.CurrentVersion),
                _deviceId + ": wizard displays only observed runtime version and blocks current/newer release");
            var expectedNotes = FirmwareChangelog.Between(_deviceId, version, FirmwareRelease.CurrentVersion);
            check(UpdateChangelog.Notes.SequenceEqual(expectedNotes)
                && UpdateChangelog.Visibility == (expectedNotes.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed),
                _deviceId + ": update overview shows only unapplied release notes");
            var changelogContent = UpdateChangelog.Child;
            RenderStep();
            check(ReferenceEquals(changelogContent, UpdateChangelog.Child), _deviceId + ": unchanged status refresh does not rebuild or flicker changelog");
            _plan = new(_deviceId, Operation, "offline fixture"); _step = Step.Connect;
            ApplyCoreStatus(false); ApplyBootloaderState(true); ConfirmationCheck.IsChecked = true;
            check(CanExecuteConfirmed == (version < FirmwareRelease.CurrentVersion),
                _deviceId + ": known version, identity and separate acknowledgement gate update without a full-read probe");
            check(UpdateChangelog.Visibility == System.Windows.Visibility.Collapsed, _deviceId + ": connection instructions stay clear of release notes");
        }
        foreach (var hint in new FirmwareRuntimeVersion?[] { null, Hint(new(0, 7, 0)) with { ObservedAt = now.AddMinutes(-5) }, Hint(new(0, 7, 0)) with { Report = new("unknown", new(0, 7, 0), 1) }, Hint(new(0, 7, 0)) with { UsbInstanceId = "USB\\fixture" }, Hint(new(0, 7, 0)) with { UsbInstanceId = "USB\\VID_FEED&PID_0000\\fixture" } })
        {
            ShowOfflineStep("overview"); _runtimeVersionHint = null; SetRuntimeVersionHint(hint);
            check(_runtimeVersionHint is null && VersionBlocksUpdate && !CanContinue && VersionText.Text == L.T("Die Firmware-Version konnte nicht geprüft werden."),
                _deviceId + ": absent, stale and foreign hints cannot enter update preparation");
            check(UpdateChangelog.Visibility == System.Windows.Visibility.Collapsed, _deviceId + ": invalid version hints do not imply a changelog comparison");
        }
        _plan = new(_deviceId, Operation, "unverified fixture"); _step = Step.Connect;
        ApplyCoreStatus(false); ApplyBootloaderState(true); ConfirmationCheck.IsChecked = true;
        check(!CanExecuteConfirmed && !ContinueButton.IsEnabled && AcknowledgementPanel.Visibility == System.Windows.Visibility.Collapsed,
            _deviceId + ": synthetic plan and identity cannot bypass the runtime-version requirement");
        ShowOfflineStep("overview"); _runtimeVersionHint = null;
    }
}
