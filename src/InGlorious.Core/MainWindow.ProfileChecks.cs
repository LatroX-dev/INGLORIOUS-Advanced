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
            RunOnboardingIdentityChecks(check);
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
    private void RunOnboardingIdentityChecks(Action<bool, string> check)
    {
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var instance = "USB\\VID_FEED&PID_" + board.RuntimeProductId.ToString("X4") + "\\fixture";
            var detected = new DetectedDevice(board.DeviceId, "fixture", "FEED", "0000", "0001", instance, "", "");
            var candidate = new OnboardDevice(board.DeviceId, "fixture", "fixture") { UsbInstanceId = instance };
            check(MatchesOnboardingDevice(candidate, detected)
                && !MatchesOnboardingDevice(candidate with { UsbInstanceId = null }, detected)
                && !MatchesOnboardingDevice(candidate with { UsbInstanceId = instance + "-other" }, detected)
                && !MatchesOnboardingDevice(candidate with { DeviceId = "unknown" }, detected),
                board.DeviceId + ": onboarding binds the exact PnP/HID USB instance; no model-only fallback");
        }
    }
}
