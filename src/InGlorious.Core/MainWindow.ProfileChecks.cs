using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void RunProfileOpeningChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Offline checks only.");
        var folder = Path.Combine(Path.GetTempPath(), "inglorious-profile-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RunLightingPresetDialogChecks(folder, check);
            RunFirmwareGuideChecks(check);
            foreach (var origin in new[] { "pro-iso", "numpad" })
            {
                ResetWorkflowFixture(origin);
                var target = origin == "pro-iso" ? "numpad" : "pro-iso";
                var path = Path.Combine(folder, target + ".json");
                var loaded = ProjectService.Create(target); loaded.Name = "Loaded fixture";
                ProjectService.Save(path, loaded);
                _project.Name = "Original unsaved draft"; RefreshAll();
                var before = Fingerprint(_project);
                check(!OpenProfilePathCore(path, () => false) && Device.Id == origin && Fingerprint(_project) == before && IsDirty,
                    origin + ": cancelling profile replacement restores the exact original dirty session");
                var failed = false;
                try { OpenProfilePathCore(path, () => { File.Delete(path); return true; }); }
                catch (IOException) { failed = true; }
                check(failed && Device.Id == origin && Fingerprint(_project) == before && IsDirty,
                    origin + ": file removed during confirmation restores the original dirty session");
                ProjectService.Save(path, loaded); failed = false;
                try { OpenProfilePathCore(path, () => { ProjectService.Save(path, ProjectService.Create(origin)); return true; }); }
                catch (InvalidDataException) { failed = true; }
                check(failed && Device.Id == origin && Fingerprint(_project) == before,
                    origin + ": changed file identity cannot bypass target-profile confirmation");
                ProjectService.Save(path, loaded);
                check(OpenProfilePathCore(path, () => true) && Device.Id == target && _project.Name == loaded.Name,
                    origin + ": successful reread adopts the selected profile");
                SwitchDevice(origin);
                check(Fingerprint(_project) == before && IsDirty, origin + ": opening another device preserves the original draft");
            }
            foreach (var preference in new[] { "de", L.SystemLanguage })
            {
                var settings = new AppSettingsWindow(new() { Language = preference }, offlinePreview: true);
                try
                {
                    check((string?)settings.LanguageChoice.SelectedValue == preference && settings.LanguageChoice.ItemTemplate is not null && string.IsNullOrEmpty(settings.LanguageChoice.DisplayMemberPath),
                        preference + ": Settings preserve the saved language preference, including system, with readable names");
                    check(settings.LanguageChoice.Items[0] is LanguageOption { Code: L.SystemLanguage } option && option.NativeName == L.T("Systemsprache"),
                        preference + ": localized system language is the first settings choice");
                }
                finally { settings.Close(); }
            }
            var dialog = new UnsavedChangesWindow("Unsaved fixture", "Message fixture");
            try
            {
                check(dialog.Choice == UnsavedChangesChoice.Cancel && dialog.CancelButton.IsCancel && dialog.CancelButton.IsDefault && !dialog.SaveButton.IsDefault,
                    "Unsaved dialog defaults to cancel for Enter, Escape and window close");
                dialog.Close();
                check(dialog.Choice == UnsavedChangesChoice.Cancel, "Closing unsaved dialog never implicitly saves or discards");
            }
            finally { dialog.Close(); }
        }
        finally { Directory.Delete(folder, true); ResetWorkflowFixture("pro-iso"); }
    }
    private static void RunLightingPresetDialogChecks(string folder, Action<bool, string> check)
    {
        var library = Path.Combine(folder, "lighting-library");
        var originals = DeviceCatalog.All.Select(device =>
        {
            var preset = LightingPresetStore.CreateInGlorious(device.Id);
            preset.Name = "User preset " + device.Id;
            return LightingPresetStore.SaveToLibrary(preset, library);
        }).ToArray();
        var originalBytes = originals.ToDictionary(entry => entry.Path, entry => File.ReadAllBytes(entry.Path));
        foreach (var device in DeviceCatalog.All)
        {
            var dialog = new LightPresetsWindow(ProjectService.Create(device.Id), 0, library);
            try
            {
                check(dialog.VisibleEntries.Count == 2 && dialog.VisibleEntries.Count(entry => entry.IsBuiltIn) == 1 && dialog.VisibleEntries.All(entry => entry.Preset.DeviceId == device.Id),
                    device.Id + ": lighting dialog shows one matching builtin and only matching user presets");
                var other = originals.First(entry => entry.Preset.DeviceId != device.Id);
                var allToggle = (ToggleButton)dialog.FindName("ShowAllDevicesToggle");
                var list = (ListBox)dialog.FindName("PresetList");
                check(allToggle.IsChecked == false, device.Id + ": all-devices toggle defaults off for each dialog");
                allToggle.IsChecked = true;
                check(dialog.VisibleEntries.Count(entry => entry.IsBuiltIn) == DeviceCatalog.All.Count && dialog.VisibleEntries.Any(entry => entry.Path == other.Path),
                    device.Id + ": optional all-devices view includes both labeled builtin/user device groups");
                list.SelectedItem = list.Items.Cast<ListBoxItem>().Single(item => ((LightingPresetEntry)item.Tag).Path == other.Path);
                check(!((Button)dialog.FindName("ApplyPresetButton")).IsEnabled && ((TextBlock)dialog.FindName("PresetStatus")).Text == L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell."),
                    device.Id + ": foreign preset is inspectable but cannot be applied and explains model mismatch");
                allToggle.IsChecked = false;
                check(dialog.VisibleEntries.Count == 2 && dialog.VisibleEntries.All(entry => entry.Preset.DeviceId == device.Id) && ((Button)dialog.FindName("ApplyPresetButton")).IsEnabled,
                    device.Id + ": turning all-devices off replaces foreign selection with a compatible preset");
                var beforePaths = Directory.GetFiles(library).Order().ToArray();
                var rejected = false;
                try { dialog.ImportFile(other.Path); }
                catch (InvalidDataException ex) { rejected = ex.Message == L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell."); }
                check(rejected && Directory.GetFiles(library).Order().SequenceEqual(beforePaths),
                    device.Id + ": mismatched lighting import is clearly rejected before any library write");
                check(dialog.VisibleEntries.Count == 2 && originalBytes.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    device.Id + ": rejected import leaves list and other-device files unchanged");
                var own = originals.Single(entry => entry.Preset.DeviceId == device.Id);
                dialog.ImportFile(own.Path);
                check(dialog.VisibleEntries.Count == 3 && dialog.VisibleEntries.Count(entry => entry.IsBuiltIn) == 1 && dialog.VisibleEntries.All(entry => entry.Preset.DeviceId == device.Id),
                    device.Id + ": compatible import refreshes the filtered list without adding another builtin");
                check(originalBytes.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                    device.Id + ": filtering and compatible import preserve every existing preset file");
            }
            finally { dialog.Close(); }
        }
    }
    private void RunFirmwareGuideChecks(Action<bool, string> check)
    {
        check(SelectFirmwareGuideTarget([], manual: false) is null && SelectFirmwareGuideTarget([], manual: true) is null,
            "Empty firmware assessments cannot open a workflow");
        var unsupported = new FirmwareGuideDevice("unknown", FirmwareGuideState.Original, true);
        check(SelectFirmwareGuideTarget([unsupported], manual: false) is null && SelectFirmwareGuideTarget([unsupported], manual: true) is null,
            "An unsupported identity never becomes a setup target");
        foreach (var proState in Enum.GetValues<FirmwareGuideState>())
        foreach (var padState in Enum.GetValues<FirmwareGuideState>())
        {
            FirmwareGuideDevice[] reports = [new("pro-iso", proState, true), new("numpad", padState, true)];
            var needsAction = reports.Any(report => report.State is FirmwareGuideState.Original or FirmwareGuideState.UpdateRequired);
            var automatic = SelectFirmwareGuideTarget(reports, manual: false);
            check((automatic is not null) == needsAction,
                $"{proState}/{padState}: automatic workflow exists only for confirmed installation or update needs");
            check(automatic is null || automatic.State is FirmwareGuideState.Original or FirmwareGuideState.UpdateRequired,
                $"{proState}/{padState}: ready and unverified devices never cause an automatic introduction");
            foreach (var report in reports)
            {
                var manual = SelectFirmwareGuideTarget(reports, manual: true, requestedDeviceId: report.DeviceId);
                check(ReferenceEquals(manual, report),
                    $"{proState}/{padState}: manual status requests preserve the selected device even when no action is available");
                var selectedAutomatic = SelectFirmwareGuideTarget(reports, manual: false, requestedDeviceId: report.DeviceId);
                if (report.State is FirmwareGuideState.Original or FirmwareGuideState.UpdateRequired)
                {
                    check(ReferenceEquals(selectedAutomatic, report),
                        $"{proState}/{padState}: an explicitly requested actionable device selects its own workflow");
                    check(selectedAutomatic!.Operation == (report.State == FirmwareGuideState.Original
                        ? FirmwareOperation.InstallFromOriginal : FirmwareOperation.Update),
                        $"{proState}/{padState}: existing INGLORIOUS firmware routes to update, never installation");
                }
                else
                    check(!ReferenceEquals(selectedAutomatic, report),
                        $"{proState}/{padState}: automatic routing cannot select a ready or unverified device");
            }
        }
        foreach (var state in new[] { FirmwareGuideState.Original, FirmwareGuideState.UpdateRequired })
        {
            var unavailable = new FirmwareGuideDevice("pro-iso", state, false);
            check(ReferenceEquals(SelectFirmwareGuideTarget([unavailable], manual: false), unavailable) && !unavailable.CanOpenManager,
                state + ": missing local files preserve status help without authorizing a firmware workflow");
        }
        foreach (var device in DeviceCatalog.All.Where(d => d.HasApprovedFirmwareManagement))
        {
            foreach (var operation in Enum.GetValues<FirmwareOperation>())
            {
                var wizard = new FirmwareWindow(device.Id, offlinePreview: true, initialOperation: operation);
                try
                {
                    wizard.RunOfflineChecks(check); wizard.RunRuntimeVersionChecks(check);
                    if (operation == FirmwareOperation.RestoreOriginal)
                    {
                        _firmwareAssessments[device.Id] = new(device.Id, FirmwareGuideState.Ready);
                        ApplyFirmwareManagerOutcome(wizard, device.Id);
                        check(!_firmwareAssessments.ContainsKey(device.Id), device.Id + ": returning from original restore invalidates the previous INGLORIOUS version label");
                    }
                }
                finally { wizard.Close(); }
            }
            var info = new OnboardInfo(device.Id, device.Id == "pro-iso" ? (byte)1 : (byte)3, 0, 7, 0, 0, 1);
            var current = new OnboardFirmwareVersion(device.Id, FirmwareRelease.CurrentVersion, FirmwareRelease.CurrentBuildId);
            check(FirmwareGuideDevice.Classify(info, device.Id, current with { BuildId = 0 }) == FirmwareGuideState.Unverified,
                device.Id + ": missing build identity cannot classify as current");
            var instance = "USB\\VID_FEED&PID_" + (device.Id == "pro-iso" ? "1C01" : "1C02") + "\\fixture";
            var detected = new DetectedDevice(device.Id, "fixture", "FEED", "0000", "0001", instance, "", "");
            var candidate = new OnboardDevice(device.Id, "fixture", "fixture") { UsbInstanceId = instance };
            check(MatchesOnboardingDevice(candidate, detected)
                && !MatchesOnboardingDevice(candidate with { UsbInstanceId = null }, detected)
                && !MatchesOnboardingDevice(candidate with { UsbInstanceId = instance + "-other" }, detected)
                && !MatchesOnboardingDevice(candidate with { DeviceId = "unknown" }, detected),
                device.Id + ": onboarding binds the exact PnP/HID USB instance; no model-only fallback");
            check(FirmwareGuideDevice.Classify(info, device.Id, null) == FirmwareGuideState.Unverified,
                device.Id + ": unsupported RAW13 is unverified and cannot offer an update");
            check(FirmwareGuideDevice.Classify(info, device.Id, current) == FirmwareGuideState.Ready,
                device.Id + ": actual current version is current regardless of active configuration schema");
            check(FirmwareGuideDevice.Classify(info, device.Id, current with { Version = new(0, 7, 99) }) == FirmwareGuideState.UpdateRequired,
                device.Id + ": older firmware with unchanged feature flags still requires an update");
            check(FirmwareGuideDevice.Classify(info, device.Id, current with { Version = new(0, 9, 3) }) == FirmwareGuideState.NewerVersion,
                device.Id + ": newer patch never offers downgrade");
            foreach (var flags in new byte[] { 120, 125 })
                check(FirmwareGuideDevice.Classify(info with { Flags = flags }, device.Id, current) == FirmwareGuideState.Unverified,
                    device.Id + ": unavailable persistence or active staging remains unverified");
            check(FirmwareGuideDevice.Classify(info, "unknown", current) == FirmwareGuideState.Unverified
                && FirmwareGuideDevice.Classify(null, device.Id, current) == FirmwareGuideState.Unverified
                && FirmwareGuideDevice.Classify(info, device.Id, current with { DeviceId = "unknown" }) == FirmwareGuideState.Unverified,
                device.Id + ": version and INFO identity must match the selected device");
            check(FirmwareWindow.IsExpectedRuntimeVersion(current, device.Id)
                && !FirmwareWindow.IsExpectedRuntimeVersion(null, device.Id)
                && !FirmwareWindow.IsExpectedRuntimeVersion(current with { BuildId = current.BuildId + 1 }, device.Id)
                && !FirmwareWindow.IsExpectedRuntimeVersion(current with { Version = new(0, 9, 3) }, device.Id)
                && !FirmwareWindow.IsExpectedRuntimeVersion(current, "unknown"),
                device.Id + ": post-update confirmation requires exact board, release and build");
            foreach (var state in Enum.GetValues<FirmwareGuideState>())
            {
                foreach (var localFiles in new[] { false, true })
                {
                    var report = new FirmwareGuideDevice(device.Id, state, localFiles)
                    {
                        RuntimeVersion = new(new(device.Id, new(0, 8, 0), 0x00080001), instance, DateTimeOffset.UtcNow)
                    };
                    var guide = new FirmwareGuideWindow([report], device.Id);
                    try
                    {
                        var expected = localFiles && state is FirmwareGuideState.Original or FirmwareGuideState.UpdateRequired;
                        check(((Button)guide.FindName("GuideOpenManager")).IsEnabled == expected && guide.Action == FirmwareGuideAction.None,
                            device.Id + ": guide manager button requires known state plus verified local files; construction performs no action");
                        check(guide.RequestedOperation == (state == FirmwareGuideState.Original ? FirmwareOperation.InstallFromOriginal : FirmwareOperation.Update),
                            device.Id + ": guide distinguishes original installation from firmware update");
                        check(!((Expander)guide.FindName("GuideDetails")).IsExpanded,
                            device.Id + ": guide opens with technical details collapsed");
                        var changelog = (FirmwareChangelogPanel)guide.FindName("GuideChangelog");
                        var expectedNotes = FirmwareChangelog.Between(device.Id,
                            state == FirmwareGuideState.UpdateRequired ? report.RuntimeVersion!.Report.Version : null, FirmwareRelease.CurrentVersion);
                        check(changelog.Visibility == (expectedNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed)
                            && changelog.Notes.SequenceEqual(expectedNotes),
                            device.Id + ": release notes are shown in update guide, never in restore or setup");
                        var expectedTitle = state switch { FirmwareGuideState.Original => L.T("INGLORIOUS einrichten"), FirmwareGuideState.UpdateRequired => L.T("Firmware aktualisieren"), _ => L.T("Gerätestatus") };
                        check(guide.Title.StartsWith(expectedTitle, StringComparison.Ordinal),
                            device.Id + ": status help uses setup only for positively identified original firmware");
                    }
                    finally { guide.Close(); }
                }
            }
        }
        check(!new FirmwareGuideDevice("unknown", FirmwareGuideState.UpdateRequired, true).CanOpenManager,
            "Unknown model cannot acquire a firmware-manager action through verified-file flag");
    }
}
