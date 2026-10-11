using System.Windows;

namespace InGlorious.Core;

internal enum FirmwareGuideState { Unverified, UpdateRequired, Ready, NewerVersion }
internal sealed record FirmwareGuideDevice(string DeviceId, FirmwareGuideState State)
{
    public FirmwareRuntimeVersion? RuntimeVersion { get; init; }
    public string Status => L.T(State switch
    {
        FirmwareGuideState.UpdateRequired => "INGLORIOUS erkannt · Aktualisierung erforderlich",
        FirmwareGuideState.Ready => "Firmware ist aktuell",
        FirmwareGuideState.NewerVersion => "Neuere Firmware installiert",
        _ => "Gerät nicht eindeutig erkannt"
    });
}

public partial class MainWindow
{
    private bool _checkingFirmwareGuide, _automaticFirmwareGuideChecked;
    private readonly Dictionary<string, FirmwareGuideDevice> _firmwareAssessments = [];
    private void RefreshFirmwareVersionLabel()
    {
        if (FindName("DeviceFirmwareVersionText") is not System.Windows.Controls.TextBlock label) return;
        label.Text = Device.IsOfflineOnly ? L.T("Offline-Profil") : _firmwareAssessments.TryGetValue(Device.Id, out var assessment)
            ? assessment.RuntimeVersion is { } runtime ? "INGLORIOUS " + runtime.Report.Version + " · " + assessment.Status : assessment.Status
            : L.T("Version noch nicht geprüft");
    }
    internal static bool MatchesOnboardingDevice(OnboardDevice candidate, DetectedDevice detected) => candidate.DeviceId == detected.DeviceId
        && candidate.UsbInstanceId is not null && string.Equals(candidate.UsbInstanceId, detected.InstanceId, StringComparison.OrdinalIgnoreCase);

    // UI discovery only. The updater independently approves the full installed image
    // and its original recovery archive before allowing any write.
    internal static FirmwareGuideState ClassifySetupFirmware(OnboardInfo? info, string deviceId, OnboardFirmwareVersion? version)
    {
        if (!BoardDescriptorRegistry.TryGet(deviceId, out _) || info is null || info.DeviceId != deviceId || !info.Persistent || (info.Flags & 4) != 0 || info.StagingBytes != 0
            || version is null || version.DeviceId != deviceId || version.BuildId == 0
            || !FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersion, out var target)) return FirmwareGuideState.Unverified;
        return version.Version < target ? FirmwareGuideState.UpdateRequired : version.Version > target ? FirmwareGuideState.NewerVersion : FirmwareGuideState.Ready;
    }

    public async Task CheckFirmwareGuideAsync(bool force = false, string? deviceId = null)
    {
        if (force && deviceId is not null && DeviceCatalog.Get(deviceId).IsOfflineOnly) return;
        if (_screenshotMode || _startupLoading || _startupClosed || _checkingFirmwareGuide || _readingDevice || _scanning || (!force && _automaticFirmwareGuideChecked)) return;
        _checkingFirmwareGuide = true; _automaticFirmwareGuideChecked = true;
        try
        {
            _firmwareAssessments.Clear(); RefreshFirmwareVersionLabel();
            await _sharedBrightness.StopAsync();
            if (_startupClosed) return;
            if (await Task.Run(DeviceSetupService.IsGloriousCoreRunning) is not false)
            {
                if (force) Status(L.T("Schließe Glorious CORE, bevor du fortfährst."));
                return;
            }
            // Original devices use the same discovery and backup path as manual setup.
            var setupDevices = await new DeviceSetupService().ListDevicesAsync();
            if (setupDevices.Any(candidate => !candidate.IsRuntime &&
                (!force || deviceId is null || candidate.CandidateDeviceIds.Contains(deviceId))))
            {
                var setup = new DeviceSetupWindow(deviceId) { Owner = this };
                setup.ShowDialog();
                RequestConnectionRefresh();
                return;
            }
            // PnP metadata alone is not an INGLORIOUS capability confirmation.
            var detectedDevices = await DeviceScanner.ScanAsync();
            if (_startupClosed) return;
            var candidates = await Task.Run(OnboardClient.FindDevices);
            var assessments = new List<FirmwareGuideDevice>();
            foreach (var device in DeviceCatalog.All.Where(d => !d.IsOfflineOnly))
            {
                if (_startupClosed) return;
                var detected = detectedDevices.Where(item => item.DeviceId == device.Id).ToArray();
                var state = FirmwareGuideState.Unverified;
                FirmwareRuntimeVersion? runtimeVersion = null;
                if (detected.Length == 1)
                {
                    var usb = detected[0];
                    var board = BoardDescriptorRegistry.Get(device.Id);
                    if (usb.VendorId == "FEED" && usb.ProductId == board.RuntimeProductId.ToString("X4"))
                    {
                        var matches = candidates.Where(candidate => MatchesOnboardingDevice(candidate, usb)).ToArray();
                        if (matches.Length == 1)
                        {
                            try
                            {
                                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
                                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                                await using var client = OnboardClient.Open(matches[0]);
                                var info = await client.GetInfoAsync(timeout.Token);
                                var reported = await client.GetFirmwareVersionAsync(timeout.Token);
                                state = ClassifySetupFirmware(info, device.Id, reported);
                                if (reported is not null && state != FirmwareGuideState.Unverified)
                                    runtimeVersion = new(reported, usb.InstanceId, DateTimeOffset.UtcNow);
                            }
                            catch { state = FirmwareGuideState.Unverified; }
                        }
                    }
                }
                var assessment = new FirmwareGuideDevice(device.Id, state) { RuntimeVersion = runtimeVersion };
                assessments.Add(assessment);
                _firmwareAssessments[device.Id] = assessment;
            }
            RefreshFirmwareVersionLabel();
            if (_startupClosed || !IsVisible) return;
            var target = force && deviceId is not null ? assessments.FirstOrDefault(item => item.DeviceId == deviceId)
                : assessments.FirstOrDefault(item => item.State == FirmwareGuideState.UpdateRequired);
            if (target?.State == FirmwareGuideState.UpdateRequired)
            {
                var update = new DeviceSetupWindow(target.DeviceId, SetupOperation.Update) { Owner = this };
                update.ShowDialog();
                if (update.DeviceStateMayHaveChanged) { _confirmedDeviceSaves.Remove(target.DeviceId); _firmwareAssessments.Remove(target.DeviceId); }
                RequestConnectionRefresh(); RefreshFirmwareVersionLabel();
            }
            else if (force) Status(target?.State is FirmwareGuideState.Ready or FirmwareGuideState.NewerVersion ? target.Status
                : L.T("Die Firmware-Version konnte nicht geprüft werden."));

        }
        catch { Status(L.T("Der Gerätestatus konnte nicht geprüft werden. Prüfe die USB-Verbindung und öffne die Anleitung erneut.")); }
        finally
        {
            _checkingFirmwareGuide = false;
            StartSharedBrightnessIfReady();
        }
    }
}
