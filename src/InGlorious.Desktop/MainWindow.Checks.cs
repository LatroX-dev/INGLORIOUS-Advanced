using InGlorious.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private int? _offlinePromptChoice;
    [System.Runtime.InteropServices.DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int ReadNativeTitleBarDarkMode(IntPtr window, uint attribute, out int value, uint valueSize);
    private async Task<string> RunSmoke()
    {
        if (!Program.Offline) throw new InvalidOperationException("Smoke requires offline mode.");
        var count = 0;
        void Check(bool condition, string name) { count++; if (!condition) throw new InvalidOperationException("Check failed: " + name); }
        void Reject(Action action, string name) { var rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, name); }
        Check(typeof(MainWindow).Assembly.GetName().Name == "INGLORIOUS Advanced" && typeof(MainWindow).Assembly.GetName().Version?.ToString(3) == "0.6.55", "visible product assembly metadata uses INGLORIOUS 0.6.55");
        await RunConnectionsChecksAsync(Check);
        await RunProfileReplacementChecksAsync(Check);
        await RunKeyBindingChecksAsync(Check);
        RunExperimentalLabelsChecks(Check);
        RunLanguagePreferenceChecks(Check);
        AppAutostartEditor.RunOfflineChecks(Check);
        RunSettingsLayoutChecks(Check);
        await RunSoftwareUpdateChecks(Check);
        var settingsPreview = CreateSettingsWindow(_preferences, offlinePreview: true);
        var startupPreview = SettingsControls(settingsPreview).OfType<ToggleSwitch>().Single(control => control.Name == "AutostartCheck");
        Check(startupPreview.IsChecked == false && !startupPreview.IsEnabled && !settingsPreview.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "SaveSettings").IsEnabled,
            "Settings: offline startup switch is neutral and cannot modify the system");
        RunStartupSessionChecks(Check);
        RunBackgroundWindowChecks(Check);
        RunHelpChecks(Check);
        Check(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark, "window inherits dark application theme");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Check(handle != IntPtr.Zero && ReadNativeTitleBarDarkMode(handle, 20, out var darkTitleBar, sizeof(int)) == 0 && darkTitleBar == 1,
                "native Windows title bar already uses immersive dark mode");
        }
        Check(_root.GetLogicalDescendants().OfType<TextBlock>().Single(c => c.Name == "ProductBrand").Text == "INGLORIOUS", "header uses uppercase product branding");
        var brand = _root.GetLogicalDescendants().OfType<TextBlock>().Single(c => c.Name == "ProductBrand");
        var brandCaption = _root.GetLogicalDescendants().OfType<TextBlock>().Single(c => c.Text == "ADVANCED");
        Check(brand.FontSize == 20 && brandCaption.FontSize == 12, "header wordmark and subtitle use requested 20/12 sizes");
        Check(brand.Parent is StackPanel brandStack && brandStack.Spacing == 0 && ReferenceEquals(brandCaption.Parent, brandStack), "brand lines share a dedicated zero-spacing stack");
        Check(Avalonia.Application.Current!.Name == "INGLORIOUS Advanced", "application identity uses complete product name");
        Check(Icon is not null && Avalonia.Platform.AssetLoader.Exists(new Uri("avares://INGLORIOUS Advanced/Assets/inglorious.ico")), "window icon resolves embedded product asset");
        var brandTypeface = new Avalonia.Media.Typeface(brand.FontFamily, brand.FontStyle, brand.FontWeight);
        Check(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(brandTypeface, out var brandGlyphs), "embedded wordmark font resolves");
        Check(brandGlyphs!.FamilyName == "IG Preview Oxanium" && brandGlyphs.Weight == Avalonia.Media.FontWeight.SemiBold, "wordmark uses actual Oxanium 600 without system-font fallback");
        var initialState = _state;
        _state = new EditorState("pro-iso"); _state.Change(p => p.Name = "InGlorious · mein Profil"); UpdateFooter();
        Check(Title == "INGLORIOUS Advanced · InGlorious · mein Profil *" && _state.Project.Name == "InGlorious · mein Profil", "product branding does not rewrite user profile names");
        _state = initialState; UpdateFooter();
        var saveCalled = false;
        Task<bool> FailedSave() { saveCalled = true; return Task.FromResult(false); }
        Check(!ResolveReplaceChoice(0, FailedSave).GetAwaiter().GetResult() && !saveCalled, "cancel never saves or discards");
        Check(!ResolveReplaceChoice(1, FailedSave).GetAwaiter().GetResult() && saveCalled, "failed or cancelled file save blocks close");
        saveCalled = false; Check(ResolveReplaceChoice(2, FailedSave).GetAwaiter().GetResult() && !saveCalled, "explicit discard does not save");
        Check(ResolveReplaceChoice(1, () => Task.FromResult(true)).GetAwaiter().GetResult(), "successful save permits close");
        Check(!ResolveReplaceChoice(-1, FailedSave).GetAwaiter().GetResult(), "unknown prompt result cancels");
        var prompt = CreatePrompt("Ungespeichertes Profil", "Test", "Abbrechen", "Profil speichern", "Verwerfen");
        var promptBody = (Avalonia.Controls.StackPanel)((Avalonia.Controls.ScrollViewer)prompt.Content!).Content!;
        var buttons = ((Avalonia.Controls.WrapPanel)promptBody.Children[^1]).Children.Cast<Avalonia.Controls.Button>().ToArray();
        Check(buttons[0].IsCancel && buttons[0].IsDefault && !buttons[1].IsDefault && buttons[1].Classes.Contains("primary"), "prompt has safe keyboard default and red save");
        byte[] descriptor = [0x06, 0x60, 0xFF, 0x09, 0x61, 0xA1, 0x01, 0x09, 0x62, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x95, 0x20, 0x75, 0x08, 0x81, 0x02, 0x09, 0x63, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x95, 0x20, 0x75, 0x08, 0x91, 0x02, 0xC0];
        Check(OnboardHidTransport.IsAllowedDescriptor(new HidSharp.Reports.ReportDescriptor(descriptor)), "canonical QMK RAW descriptor");
        var wrongUsage = descriptor.ToArray(); wrongUsage[4] = 0x62; Check(!OnboardHidTransport.IsAllowedDescriptor(new HidSharp.Reports.ReportDescriptor(wrongUsage)), "wrong usage rejected");
        var wrongLength = descriptor.ToArray(); wrongLength[15] = 31; Check(!OnboardHidTransport.IsAllowedDescriptor(new HidSharp.Reports.ReportDescriptor(wrongLength)), "wrong report size rejected");
        var reportId = descriptor[..7].Concat(new byte[] { 0x85, 1 }).Concat(descriptor[7..]).ToArray(); Check(!OnboardHidTransport.IsAllowedDescriptor(new HidSharp.Reports.ReportDescriptor(reportId)), "report IDs rejected");
        var packet = new byte[32]; packet[0] = (byte)'I'; packet[1] = (byte)'G'; packet[2] = 1; packet[3] = 1; OnboardHidTransport.ValidateOutbound(packet);
        packet[3] = 0xFF; Reject(() => OnboardHidTransport.ValidateOutbound(packet), "unknown command blocked"); packet[3] = 1; packet[31] = 1; Reject(() => OnboardHidTransport.ValidateOutbound(packet), "nonzero reserved packet bytes blocked");
        Check(BoardDescriptorRegistry.Get("gmmk3-pro-100-iso").RuntimeProductId == 0x1C10 && !BoardDescriptorRegistry.Get("gmmk3-pro-100-iso").IsEngineeringOnly,
            "Portable PRO uses its own reviewed runtime identity");
        foreach (var device in DeviceCatalog.All)
        {
            var state = new EditorState(device.Id); state.MarkInitialClean(); var before = EditorState.Fingerprint(state.Project);
            var key = device.Keys.First(k => k.LedIndex.HasValue);
            state.SelectKey(key.Id, false); Check(before == EditorState.Fingerprint(state.Project) && !state.CanUndo, "selection is pure");
            state.SetColor("#123456"); Check(state.Layer.Keys[key.Id].Color == "#123456" && state.Dirty, "color applies");
            state.Undo(); Check(EditorState.Fingerprint(state.Project) == before, "undo exact"); state.Redo(); Check(state.Layer.Keys[key.Id].Color == "#123456", "redo exact");
            Reject(() => state.SetBinding("KC_TRNS"), "base transparency blocked"); state.LayerIndex = 1; state.SetBinding("KC_TRNS");
            var effect = new LightingEffectSettings { Mode = 5, Colors = ["#FF0000", "#00FFFF"] }; state.SetEffect(effect, false); effect.Colors[0] = "#FFFFFF";
            Check(state.Layer.LedEffects[key.LedIndex!.Value].Colors[0] == "#FF0000", "effect palette detached");
            if (!device.IsOfflineOnly)
            {
            var image = OnboardSerializer.Prepare(state.Project); Check(image.SchemaVersion == 3, "palette schema3");
            var oldInfo = new OnboardInfo(device.Id, 1, OnboardSerializer.Prepare(ProjectService.Create(device.Id)).UsedBytes, 1, 0, 0, 1);
            Check(PlatformDeviceService.UnsupportedReason(oldInfo, OnboardSerializer.Prepare(ProjectService.Create(device.Id))) is null, "old firmware still accepts supported static profile");
            Check(PlatformDeviceService.UnsupportedReason(oldInfo, image) == "Eigene Farbfolgen benötigen ein Firmware-Update. Es wird nichts übertragen.", "sequence transfer receives explicit update instruction before write");
            Check(PlatformDeviceService.UnsupportedReason(oldInfo with { Flags = 121 }, image) is null, "positive capabilities accept effects despite active schema1");
            var timing = ProjectService.Create(device.Id); timing.DeviceSettings.DebounceMs = 9;
            Check(PlatformDeviceService.UnsupportedReason(oldInfo with { Flags = 9 }, OnboardSerializer.Prepare(timing)) is not null, "effect-only capability does not allow timing upload");
            var decoded = OnboardSerializer.Decode(image.Payload); Check(OnboardSerializer.Prepare(decoded).Payload.AsSpan().SequenceEqual(image.Payload), "onboard roundtrip");
            }
            else Reject(() => OnboardSerializer.Prepare(state.Project), "offline editor cannot create a transfer payload");
            state.Surface = "left"; state.SetSideSync(true); state.SetColor("#ABCDEF"); Check(device.SideLedIndices.All(i => state.ColorAt(i) == "#ABCDEF"), "whole sides synchronize");
            state.SelectSide(device.LeftSideLedIndices[0], false); Check(state.SelectedLeds().Length == 2, "individual paired sides");
            state.SetSideSync(false); Check(state.SelectedLeds().Length == 1, "individual unsynchronized");
            state.CopyLayer(2); state.Layer.LedEffects[key.LedIndex.Value].Colors[0] = "#010203";
            Check(state.Project.Layers[2].LedEffects[key.LedIndex.Value].Colors[0] == "#FF0000", "layer copy detached");
            state.Surface = "keys"; state.AssignOsAction(OsActionCatalog.For("macos").First(a => a.Id == "copy")); var macroCount = state.Project.Macros.Count;
            state.AssignOsAction(OsActionCatalog.For("macos").First(a => a.Id == "copy")); Check(state.Project.Macros.Count == macroCount, "OS shortcut macro reused");
            var imported = SharedMacroLibrary.CopyIntoProfile(state.Project, [SharedMacroLibrary.FromProfile(state.Project.Macros[0])]); OnboardSerializer.ValidateEditorProject(imported.Project);
            Check(imported.AddedIds[0] != state.Project.Macros[0].Id && state.Project.Macros.Count == macroCount, "macro import detached new ID");
            var preset = LightingPresetStore.CreateInGlorious(device.Id); var priorBindings = state.Layer.Keys.ToDictionary(k => k.Key, k => k.Value.Binding);
            var presetApplied = LightingPresetStore.Apply(state.Project, state.LayerIndex, preset); OnboardSerializer.ValidateEditorProject(presetApplied);
            Check(presetApplied.Layers[state.LayerIndex].Keys.All(k => priorBindings[k.Key] == k.Value.Binding), "preset preserves bindings");
            Check(presetApplied.Macros.Count == state.Project.Macros.Count && presetApplied.Layers[0].Name == state.Project.Layers[0].Name, "preset preserves macros and names");
            Reject(() => LightingPresetStore.Apply(state.Project, 0, LightingPresetStore.CreateInGlorious(device.Id == "numpad" ? "pro-iso" : "numpad")), "wrong device preset blocked");
            if (device.BadgeGeometry is { } geometry)
            {
                _state = new EditorState(device.Id); _page = "Beleuchtung"; Build();
                var visual = _keyboard.Children.Single(control => control.Name == (geometry.LedIndex is int led ? "BadgeLed" + led : "BadgeHousing"));
                Check(Math.Abs(Canvas.GetLeft(visual) - (42 + geometry.X * 49)) < .001 && Math.Abs(Canvas.GetTop(visual) - (device.PreviewTopInset + geometry.Y) * 49) < .001,
                    "physical badge uses shared housing coordinates without detached bottom row");
                Check(Math.Abs(visual.Width - (geometry.W * 49 - 4)) < .001 && Math.Abs(visual.Height - (geometry.H * 49 - 4)) < .001,
                    "physical badge preserves photographed orientation and proportions");
                Check(Canvas.GetTop(visual) >= 0 && Canvas.GetLeft(visual) >= 0 && Canvas.GetLeft(visual) + visual.Width <= _keyboard.Width && Canvas.GetTop(visual) + visual.Height <= _keyboard.Height,
                    "badge is wholly inside canvas including raised housing");
                Check(_keyButtons.All(pair => Math.Abs(Canvas.GetTop(pair.Value) - (device.PreviewTopInset + device.Keys.Single(k => k.Id == pair.Key).Y) * 49) < .001),
                    "top inset moves all keys together without changing row geometry");
                var knob = device.Keys.Single(k => k.Label == "Knob");
                if (geometry.IsHorizontal)
                {
                    Check(visual is Border && !visual.IsHitTestVisible && geometry.LedIndex is null && device.BadgeLedIndices.Count == 0,
                        "65 percent passive housing invents neither an LED nor an editing action");
                    Check(Canvas.GetTop(visual) >= Canvas.GetTop(_keyButtons[knob.Id]) + _keyButtons[knob.Id].Height,
                        "65 percent badge is below the physical knob");
                    var rightSegments = _keyboard.Children.OfType<Button>().Where(button => Math.Abs(button.Width - 17) < .001 && Canvas.GetLeft(button) > _keyboard.Width / 2).ToArray();
                    Check(rightSegments.Length == device.RightSideLedIndices.Count && rightSegments.All(button => Canvas.GetLeft(button) > Canvas.GetLeft(visual) + visual.Width),
                        "wider 65 percent badge does not collide with right side strip");
                }
                else
                {
                    Check(visual is Button && geometry.LedIndex.HasValue && _ledViews.ContainsKey(geometry.LedIndex.Value),
                        "75 and 100 percent physical badges retain their actual RGB endpoint");
                    Check(Canvas.GetLeft(visual) + visual.Width < Canvas.GetLeft(_keyButtons[knob.Id]) && Canvas.GetTop(visual) < Canvas.GetTop(_keyButtons[knob.Id]),
                        "vertical badge sits left of knob and above the function row");
                }
                foreach (var physical in _keyButtons.Values)
                    Check(Math.Min(Canvas.GetLeft(visual) + visual.Width, Canvas.GetLeft(physical) + physical.Width) <= Math.Max(Canvas.GetLeft(visual), Canvas.GetLeft(physical)) ||
                          Math.Min(Canvas.GetTop(visual) + visual.Height, Canvas.GetTop(physical) + physical.Height) <= Math.Max(Canvas.GetTop(visual), Canvas.GetTop(physical)),
                        "physical badge never covers a rendered key");
            }
            if (device.BadgeLedIndices.Count > 0)
            {
                var badgeState = new EditorState(device.Id); badgeState.MarkInitialClean();
                var badgeInitial = EditorState.Fingerprint(badgeState.Project); badgeState.SelectedKeys.Add(key.Id); badgeState.SelectBadge();
                var badge = device.BadgeLedIndices.Single();
                Check(badgeState.SelectedLeds().SequenceEqual([badge]) && badgeState.SelectedKeys.Count == 0 && !badgeState.Dirty && !badgeState.CanUndo, "badge is independent lighting selection without key binding");
                var sidesBefore = device.SideLedIndices.ToDictionary(led => led, badgeState.ColorAt);
                badgeState.SetColor("#14C8E6"); Check(badgeState.ColorAt(badge) == "#14C8E6" && sidesBefore.All(pair => badgeState.ColorAt(pair.Key) == pair.Value), "badge painting preserves all side LEDs");
                badgeState.Undo(); Check(EditorState.Fingerprint(badgeState.Project) == badgeInitial, "badge painting has exact undo"); badgeState.Redo();
                badgeState.SetEffect(new() { Mode = 2, Speed = 77, Brightness = 219, Colors = ["#0033FF", "#FFD400"] }, false);
                var badgeEffect = System.Text.Json.JsonSerializer.Serialize(badgeState.Layer.LedEffects[badge]);
                badgeState.SetSideSync(true); badgeState.Surface = "both"; badgeState.SetColor("#B32244");
                Check(badgeState.SelectedLeds().Length == 20 && device.SideLedIndices.All(led => badgeState.ColorAt(led) == "#B32244") && badgeState.ColorAt(badge) == "#14C8E6" && System.Text.Json.JsonSerializer.Serialize(badgeState.Layer.LedEffects[badge]) == badgeEffect, "all sides and sync never overwrite logo settings");
                _state = badgeState; _page = "Tastenbelegung"; Build();
                var badgeButton = _keyboard.Children.OfType<Button>().Single(button => button.Name == "BadgeLed" + badge);
                badgeButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Check(_page == "Beleuchtung" && _state.Surface == "badge" && _state.SelectedKeys.Count == 0 && _ledViews.ContainsKey(badge), "clicking badge shows editable and animated logo lighting");
                Check(_root!.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "BadgeLightButton") && !_editor.GetLogicalDescendants().OfType<CheckBox>().Any(box => Equals(box.Content, L.T("Seiten synchronisieren"))), "badge editor does not expose side-only controls");
                if (!device.IsOfflineOnly)
                {
                    var image = OnboardSerializer.Prepare(_state.Project); Check(OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.SequenceEqual(image.Payload), "badge colors and own palette survive wire roundtrip");
                }
                else Reject(() => OnboardSerializer.Prepare(_state.Project), "Engineering badge edits remain local");
            }
            _state = OpenSession(device.Id);
            var presetEntries = DeviceCatalog.All.SelectMany(d => new[] { new LightingPresetEntry("", LightingPresetStore.CreateInGlorious(d.Id), true), new LightingPresetEntry("", LightingPresetStore.CreateInGlorious(d.Id)) }).ToArray();
            var compatible = CurrentDevicePresets(presetEntries);
            Check(compatible.Length == 2 && compatible.All(e => e.Preset.DeviceId == device.Id) && compatible.Count(e => e.IsBuiltIn) == 1, "preset list includes only matching built-in and user entries");
            Reject(() => CheckPresetDevice(LightingPresetStore.CreateInGlorious(device.Id == "numpad" ? "pro-iso" : "numpad")), "foreign preset import rejected before saving");
            var presetDialog = CreateLightingPresetsWindow(presetEntries);
            var presetControls = presetDialog.GetLogicalDescendants().OfType<Control>().ToArray();
            var showAll = presetControls.OfType<CheckBox>().Single(c => c.Name == "ShowAllPresetDevices");
            var presetList = presetControls.OfType<ListBox>().Single(c => c.Name == "LightingPresetList");
            var applyPreset = presetControls.OfType<Button>().Single(c => c.Name == "ApplyLightingPreset");
            var presetStatus = presetControls.OfType<TextBlock>().Single(c => c.Name == "LightingPresetStatus");
            Check(showAll.IsChecked == false && presetList.Items.Count == 2 && applyPreset.IsEnabled, "preset switch defaults to current device with compatible selection");
            showAll.IsChecked = true;
            Check(presetList.Items.Count == DeviceCatalog.All.Count * 2 && presetList.Items.Cast<PresetRow>().All(r => r.ToString().Contains(r.Entry.DeviceName)), "all-device switch shows explicit model labels");
            presetList.SelectedItem = presetList.Items.Cast<PresetRow>().First(r => r.Entry.Preset.DeviceId != device.Id);
            Check(!applyPreset.IsEnabled && presetStatus.Text == L.T("Diese Beleuchtungsvorlage gehört zu einem anderen Gerätemodell."), "foreign selection cannot apply and explains mismatch");
            showAll.IsChecked = false;
            Check(presetList.Items.Count == 2 && applyPreset.IsEnabled && ((PresetRow)presetList.SelectedItem!).Entry.Preset.DeviceId == device.Id, "hiding other devices restores compatible selection");
            var beforePresetUi = EditorState.Fingerprint(_state.Project);
            _page = "Gerät"; _showEffects = false; _globalEffect = false; Build();
            ApplyLightingPreset(preset);
            Check(_showEffects && _page == "Beleuchtung" && _globalEffect && _scene?.Animated == true && _pendingApply is null,
                "applying animated preset opens whole-layer effects and activates preview " + device.Id);
            var afterPresetUi = EditorState.Fingerprint(_state.Project);
            var firstPreview = new PreviewRgb[_state.Device.LedCount]; var laterPreview = new PreviewRgb[firstPreview.Length];
            _scene!.Render(0, [], firstPreview); var animationChanged = false;
            for (uint time = 137; time <= 2740; time += 137)
            { _scene.Render(time, [], laterPreview); animationChanged |= !firstPreview.SequenceEqual(laterPreview); }
            Check(animationChanged && EditorState.Fingerprint(_state.Project) == afterPresetUi,
                "preset preview animates LED frames without changing profile data " + device.Id);
            _showEffects = false; Build(); ApplyLightingPreset(preset);
            Check(_showEffects && EditorState.Fingerprint(_state.Project) == afterPresetUi,
                "reapplying identical preset restores effect preview " + device.Id);
            _state.Undo(); Changed(); Build();
            Check(EditorState.Fingerprint(_state.Project) == beforePresetUi,
                "animated preset and identical reapply remain a single undoable change " + device.Id);
            ApplyLightingPreset(LightingPresetStore.Capture(ProjectService.Create(device.Id), 0, "Static preview fixture"));
            Check(!_showEffects && _page == "Beleuchtung" && _scene?.Animated == false,
                "colors-only preset keeps color editor and does not invent animation " + device.Id);
            _state.Undo(); Changed(); Build();
            foreach (var page in new[] { "Beleuchtung", "Tastenbelegung", "Ebenen", "Makros", "Gerät", "Profile" })
            { _page = page; Build(); UpdateLayout(); Check(Content is not null && _root is not null, "UI builds " + device.Id + " " + page); }
            _page = "Beleuchtung"; _showEffects = true; Build(); UpdateLayout(); Check(_root!.GetLogicalDescendants().OfType<TabControl>().Single(c => c.Name == "LightingTabs").SelectedIndex == 1 && _root!.GetLogicalDescendants().OfType<NumericUpDown>().Count() >= 2, "effect editor builds within its selected tab");
        }
        Check(PhysicalCode(Avalonia.Input.PhysicalKey.P) == "KC_P", "physical letter distinct from numpad");
        RunSliderNavigationChecks(Check);
        RunLayerGuidanceChecks(Check);
        RunColorEditorChecks(Check);
        RunLightingPreviewChecks(Check);
        RunGlobalEffectSwitchChecks(Check);
        RunStandbyEditorChecks(Check);
        _state = new EditorState("pro-iso");
        foreach (var operation in Enum.GetValues<SetupOperation>())
            new DeviceSetupWindow("gmmk3-75-iso", operation, offlinePreview: true).RunOfflineChecks(Check);
        RunSetupRoutingChecks(Check);
        var versionPanel = BuildFirmwareVersionPanel();
        Check(versionPanel.GetLogicalDescendants().OfType<TextBlock>().Single(x => x.Name == "FirmwareVersionStatus").Text == L.T("Version noch nicht geprüft")
            && versionPanel.GetLogicalDescendants().OfType<Button>().Any(x => x.Name == "ReadFirmwareVersion"), "opening version panel neither queries hardware nor invents an installed version");
        var notesPanel = versionPanel.GetLogicalDescendants().OfType<StackPanel>().Single(x => x.Name == "FirmwareChangelog");
        Check(!notesPanel.IsVisible && notesPanel.Children.Count == 0, "unread installed version never invents update notes");
        foreach (var device in DeviceCatalog.All)
        {
            ShowFirmwareChangelog(notesPanel, device.Id, new(0, 8, 0));
            if (device.IsOfflineOnly)
            {
                Check(!notesPanel.IsVisible && notesPanel.Children.Count == 0, "Engineering model never invents active release notes");
                _state = new EditorState(device.Id); Build(); ApplyConnectionSnapshot([device.Id]);
                Check(_deviceState.Text == L.T("Offline-Profil") && !_readDeviceButton!.IsEnabled && !_transferButton!.IsEnabled,
                    "Exact engineering metadata preserves the offline status and disabled configuration actions");
                Check(!BuildFirmwareVersionPanel().GetLogicalDescendants().OfType<Button>().Any() &&
                    !BuildDeviceWorkspace().GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "RestoreOriginalFirmware"),
                    "Engineering model offers no firmware query or restore action");
                ApplyConnectionSnapshot([]);
                continue;
            }
            if (BoardDescriptorRegistry.Get(device.Id).BoardId >= 3)
            {
                FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(device.Id), out var candidateTarget);
                var documented = candidateTarget >= new FirmwareSemanticVersion(0, 11, 3);
                Check(notesPanel.IsVisible == documented && (documented || notesPanel.Children.Count == 0), "candidate notes appear only for an included documented release");
                if (documented)
                {
                    var candidateNotes = notesPanel.GetLogicalDescendants().OfType<TextBlock>().Select(item => item.Text).ToArray();
                    Check(candidateNotes.Contains("INGLORIOUS 0.11.3") && candidateNotes.Contains("Red default lighting when no saved colors are available.") &&
                        !candidateNotes.Contains("INGLORIOUS 0.9.0"), "candidate notes never borrow another model's older releases");
                    Check(candidateNotes.Contains("Individual key, knob and LED tests.") == (device.Id == RetainedLoaderLayout.DeviceId), "only the PRO diagnostic target describes individual device tests");
                    var includesLightingFix = candidateTarget >= new FirmwareSemanticVersion(0, 11, 4);
                    Check(candidateNotes.Contains("INGLORIOUS 0.11.4") == includesLightingFix &&
                        candidateNotes.Contains("Fixed lighting tests being blocked when no key is pressed.") == includesLightingFix,
                        "candidate lighting-test fix is shown only when its firmware release is included");
                }
                ShowFirmwareChangelog(notesPanel, device.Id, null);
                Check(!notesPanel.IsVisible && notesPanel.Children.Count == 0, "unknown candidate version clears release notes");
                ShowFirmwareChangelog(notesPanel, device.Id, candidateTarget);
                Check(!notesPanel.IsVisible, "current candidate firmware offers no pending release notes");
                _state = new EditorState(device.Id); Build();
                ApplyConnectionSnapshot([]);
                Check(_deviceState.Text == L.T("Gerät nicht verbunden"), "disconnected candidate has clear footer status");
                Check(_root!.GetLogicalDescendants().OfType<Button>().Where(b => b.Name is "ReadDeviceButton" or "TransferButton").All(b => !b.IsEnabled), "disconnected candidate cannot access device actions");
                ApplyConnectionSnapshot([device.Id]);
                Check(_root!.GetLogicalDescendants().OfType<Button>().Where(b => b.Name is "ReadDeviceButton" or "TransferButton").All(b => b.IsEnabled), "connected candidate can read and transfer its own configuration");
                Check(BuildFirmwareVersionPanel().GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "ReadFirmwareVersion"), "candidate has a real runtime version query action");
                Check(BuildDeviceWorkspace().GetLogicalDescendants().OfType<Button>().Any(b => Equals(b.Content, L.T("Gerät einrichten"))), "experimental devices retain first installation");
                ApplyConnectionSnapshot([]);
                continue;
            }
            var noteText = notesPanel.GetLogicalDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
            Check(notesPanel.IsVisible && noteText.Contains("What’s new") && noteText.Contains("INGLORIOUS 0.9.0"), "available update has visible English versioned release notes " + device.Id);
            Check(noteText.Contains("New lighting test after firmware updates.") && noteText.Contains("Optional screensaver."), "release notes stay brief for supported hardware " + device.Id);
            ShowFirmwareChangelog(notesPanel, device.Id, null);
            Check(!notesPanel.IsVisible && notesPanel.Children.Count == 0, "unknown refreshed version clears prior release notes");
            FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(device.Id), out var targetRelease);
            ShowFirmwareChangelog(notesPanel, device.Id, targetRelease);
            Check(!notesPanel.IsVisible, "current firmware shows no pending changes");
        }
        _state = OpenSession(Program.Arguments.Contains("--numpad") ? "numpad" : "pro-iso"); _page = "Beleuchtung"; _showEffects = false; Build();
        return $"PASS {count} offline desktop checks; no device discovery, HID, process, or user-data writes.\n";
    }
    private void RunSetupRoutingChecks(Action<bool, string> check)
    {
        FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersion, out var target);
        var savedState = _state;
        foreach (var board in BoardDescriptorRegistry.All)
        {
            var id = board.DeviceId;
            var info = new OnboardInfo(id, 249, 128, 1, 0, 0, 3);
            var report = new OnboardFirmwareVersion(id, target, 1);
            check(ClassifySetupFirmware(id, info, report) == SetupRuntimeState.Current, "Setup routing: validated current target " + id);
            check(ClassifySetupFirmware(id, info, report with { Version = new(0, 0, 1) }) == SetupRuntimeState.UpdateAvailable, "Setup routing: actual older version enables update " + id);
            check(ClassifySetupFirmware(id, info, report with { Version = new(ushort.MaxValue, 0, 0) }) == SetupRuntimeState.Newer, "Setup routing: newer version never offers downgrade " + id);
            check(ClassifySetupFirmware(id, info, null) == SetupRuntimeState.Unknown && ClassifySetupFirmware(id, null, report) == SetupRuntimeState.Unknown &&
                ClassifySetupFirmware(id, info, report with { BuildId = 0 }) == SetupRuntimeState.Unknown &&
                ClassifySetupFirmware(id, info, report with { DeviceId = "foreign-device" }) == SetupRuntimeState.Unknown &&
                ClassifySetupFirmware(id, info with { Flags = 0 }, report) == SetupRuntimeState.Unknown &&
                ClassifySetupFirmware(id, info with { Flags = 253 }, report) == SetupRuntimeState.Unknown &&
                ClassifySetupFirmware(id, info with { StagingBytes = 1 }, report) == SetupRuntimeState.Unknown,
                "Setup routing: incomplete, mismatched, nonpersistent or staged reports never enable update " + id);
            _state = new EditorState(id);
            foreach (var state in Enum.GetValues<SetupRuntimeState>())
            {
                _firmwareAssessments[id] = new(state, state == SetupRuntimeState.Unknown ? null : report);
                var panel = BuildFirmwareVersionPanel();
                check(panel.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "UpdateFirmware").IsVisible == (state == SetupRuntimeState.UpdateAvailable),
                    "Setup routing: update action follows the verified assessment " + id + "/" + state);
            }
            _firmwareAssessments.Remove(id);
        }
        _state = savedState;
        var settings = BuildSetupSettingsSection(this, new TextBlock());
        var names = settings.GetLogicalDescendants().OfType<Button>().Select(button => button.Name).ToArray();
        check(names.Contains("SetupFromSettings") && names.Contains("RestoreFromSettings") && names.Contains("OpenRecoveryFolder"), "Setup settings: first installation, restore and recovery folder are accessible");
        byte[] command = [(byte)'I', (byte)'G', 1, OnboardProtocol.LightingTest, 0, 0, 1, 0, .. new byte[24]];
        bool Valid(byte[] bytes) { try { OnboardHidTransport.ValidateOutbound(bytes); return true; } catch (InvalidDataException) { return false; } }
        foreach (byte action in new byte[] { 0, 1, 2 }) { command[8] = action; check(Valid(command), "Setup lighting: defined RAW12 action accepted " + action); }
        command[8] = 3; check(!Valid(command), "Setup lighting: unknown RAW12 action rejected");
        command[8] = 0; command[6] = 0; check(!Valid(command), "Setup lighting: missing RAW12 action rejected");
        command[6] = 2; check(!Valid(command), "Setup lighting: oversized RAW12 payload rejected");
        command[6] = 1; command[31] = 1; check(!Valid(command), "Setup lighting: reserved RAW12 data rejected");
    }

    private void RunExperimentalLabelsChecks(Action<bool, string> check)
    {
        var original = _state; var page = _page; var connected = _connectedDeviceIds.ToArray(); var failed = _connectionsFailed;
        var busy = _busy; var loading = _startupLoading; var initial = _initialConnectionsPending;
        var pending = _pendingApply;
        try
        {
            _busy = _startupLoading = _initialConnectionsPending = false; _pendingApply = null;
            check(DeviceCatalog.All.Count(device => device.IsExperimentalFirmware) == 13, "exactly thirteen experimental models remain identified");
            foreach (var device in DeviceCatalog.All)
            {
                _state = new EditorState(device.Id); _state.MarkInitialClean(); _page = "Gerät"; Build();
                var before = EditorState.Fingerprint(_state.Project);
                ApplyConnectionSnapshot([device.Id]);
                var labels = _root.GetLogicalDescendants().OfType<TextBlock>().ToArray();
                var button = _deviceButtons[device.Id];
                var buttonLabels = button.GetLogicalDescendants().OfType<TextBlock>().Where(label => label.Name == "ExperimentalDeviceBadge").ToArray();
                check(buttonLabels.Length == (device.IsExperimentalFirmware ? 1 : 0), "only experimental connected buttons receive a label");
                check(labels.Count(label => label.Name == "ExperimentalHeaderBadge") == (device.IsExperimentalFirmware ? 1 : 0), "active header distinguishes experimental from stable models");
                check(labels.Count(label => label.Name == "ExperimentalDeviceNotice") == (device.IsExperimentalFirmware ? 1 : 0), "device page explains missing model hardware testing only where relevant");
                if (device.IsOfflineOnly)
                {
                    check(!_readDeviceButton!.IsEnabled && !_transferButton!.IsEnabled && !device.HasApprovedFirmwareManagement,
                        "Engineering metadata cannot enable read, transfer or firmware management");
                    check(!_state.Dirty && EditorState.Fingerprint(_state.Project) == before &&
                        !_root.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "RestoreOriginalFirmware"),
                        "Engineering editor rendering preserves its separate local profile and omits firmware actions");
                    continue;
                }
                check(_root.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Content, L.T("Gerät einrichten"))) &&
                    _root.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Content, L.T("Original-Firmware wiederherstellen"))),
                    "every device page exposes installation and original restore");
                check(_root.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DeviceTestButton" && button.IsVisible) == (device.Id == RetainedLoaderLayout.DeviceId),
                    "the detailed device test is offered only for the exact diagnostic-capable PRO model");
                check(_readDeviceButton!.IsEnabled && _transferButton!.IsEnabled && button.IsEnabled && device.SupportsOnboardConfiguration,
                    "experimental label does not add opt-in or disable connected runtime configuration");
                check(OnboardSerializer.Prepare(_state.Project).Payload.Length > 0 && !_state.Dirty && EditorState.Fingerprint(_state.Project) == before,
                    "label rendering leaves profile and valid configuration payload unchanged");
                check(device.HasApprovedFirmwareManagement == !device.IsExperimentalFirmware, "marking does not promote experimental firmware management");
                ApplyConnectionSnapshot([device.Id]);
                check(ReferenceEquals(button, _deviceButtons[device.Id]) && button.GetLogicalDescendants().OfType<TextBlock>().Count(label => label.Name == "ExperimentalDeviceBadge") == buttonLabels.Length,
                    "repeated presence snapshot preserves button and never duplicates badge");
                if (device.IsExperimentalFirmware)
                {
                    check(buttonLabels.Single().Text == L.T("Experimentell") && buttonLabels.Single().Foreground is Avalonia.Media.SolidColorBrush brush && brush.Color == Avalonia.Media.Color.Parse("#E9B86B"),
                        "experimental badge uses localized text and muted amber");
                    check(labels.Single(label => label.Name == "ExperimentalDeviceNotice").Text == L.T("Experimentelle Unterstützung: noch nicht vollständig geprüft."),
                        "experimental device notice gives the precise testing limitation");
                }
            }
        }
        finally
        {
            _state = original; _page = page; _busy = busy; _startupLoading = loading; _initialConnectionsPending = initial; _pendingApply = pending;
            _connectedDeviceIds = connected; _connectionsFailed = failed; Build();
        }
    }
    private void RunLanguagePreferenceChecks(Action<bool, string> check)
    {
        var originalLanguage = L.CurrentLanguage;
        try
        {
            check(new AppPreferences().Language == L.SystemLanguage, "new preferences follow system language");
            foreach (var uiLanguage in new[] { "de", "en" })
            {
                L.SetLanguage(uiLanguage);
                var options = L.LanguageChoices;
                var system = CreateLanguageChoice(L.SystemLanguage, options);
                check(options.Count == L.SupportedLanguages.Count + 1 && options[0].Code == L.SystemLanguage &&
                    options[0].NativeName == L.T("Systemsprache") && system.SelectedIndex == 0,
                    "system language is the first localized settings choice: " + uiLanguage);
                L.SetLanguage(L.SystemLanguage);
                check(options[system.SelectedIndex].Code == L.SystemLanguage && L.IsSupportedLanguage(L.CurrentLanguage),
                    "resolving system language does not rewrite the preference selection: " + uiLanguage);
                foreach (var explicitLanguage in L.SupportedLanguages)
                {
                    var existing = new AppPreferences { Language = explicitLanguage.Code };
                    var choice = CreateLanguageChoice(existing.Language, options);
                    check(choice.SelectedIndex > 0 && options[choice.SelectedIndex].Code == existing.Language,
                        "existing explicit language remains selected: " + uiLanguage + "/" + explicitLanguage.Code);
                }
            }
            var argument = Array.IndexOf(Program.Arguments, "--language");
            if (argument >= 0 && argument + 1 < Program.Arguments.Length && L.IsLanguagePreference(Program.Arguments[argument + 1]))
                check(_preferences.Language == Program.Arguments[argument + 1], "offline language option preserves the requested preference code");
        }
        finally { L.SetLanguage(originalLanguage); }
    }
    private void RunStandbyEditorChecks(Action<bool, string> check)
    {
        T Find<T>(string name) where T : Control => _root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        var original = _state; var language = L.CurrentLanguage; var page = _page; var effects = _showEffects;
        try
        {
            foreach (var device in DeviceCatalog.All)
            {
                _state = new EditorState(device.Id);
                _state.Change(p => p.Standby = new() { Enabled = false, TimeoutSeconds = 157, Mode = 1, DimBrightnessPercent = 17 });
                var initial = EditorState.Fingerprint(_state.Project); _page = "Beleuchtung";
                ShowPageAsync("Gerät").GetAwaiter().GetResult();
                check(Find<Grid>("WorkspaceColumns").ColumnDefinitions.Count == 2 && !_root.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "RightInspector"), "device page uses the entire workspace without an inspector " + device.Id);
                check(Find<Grid>("DeviceSettingsSections").Children.Count == 2 && Find<StackPanel>("DeviceTimingSection").GetLogicalAncestors().OfType<Control>().Any(control => control.Name == "DeviceWorkspace") && Find<StackPanel>("DeviceStandbySection").GetLogicalAncestors().OfType<Control>().Any(control => control.Name == "DeviceWorkspace"), "timing and inactivity controls share the main device workspace " + device.Id);
                check(_root.GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "ApplyDeviceSettings") == 1 && !_root.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "LightingPresetsButton"), "device page keeps one combined apply and no lighting preset entry " + device.Id);
                if (!device.IsOfflineOnly) check(Find<Button>("RestoreOriginalFirmware").IsVisible && Find<Button>("OpenDeviceRecoveryFolder").IsVisible, "recovery and backup-folder actions stay directly available " + device.Id);
                check(_page == "Gerät" && Find<CheckBox>("StandbyEnabled").IsChecked == false && Find<NumericUpDown>("StandbyTimeoutSeconds").Value == 157 && Find<NumericUpDown>("StandbyBrightness").Value == 17, "standby opens exact existing device values " + device.Id);
                check(Find<ComboBox>("StandbyDelayPreset").SelectedIndex == 7 && !Find<ComboBox>("StandbyMode").IsEffectivelyEnabled && _pendingApply is null && EditorState.Fingerprint(_state.Project) == initial, "custom timeout preserved and disabled section is pure");
                Find<CheckBox>("StandbyEnabled").IsChecked = true;
                Find<ComboBox>("StandbyDelayPreset").SelectedIndex = 2;
                Find<NumericUpDown>("StandbyBrightness").Value = 31;
                check(Find<NumericUpDown>("StandbyTimeoutSeconds").Value == 120 && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == initial, "standby preset edits remain pending");
                _offlinePromptChoice = 0; ShowLightingSectionAsync("Farbe").GetAwaiter().GetResult();
                check(_page == "Gerät" && _pendingApply is not null && Find<NumericUpDown>("StandbyBrightness").Value == 31, "cancel keeps standby settings entered");
                _offlinePromptChoice = 1; ShowLightingSectionAsync("Farbe").GetAwaiter().GetResult();
                check(_page == "Beleuchtung" && _pendingApply is null && _state.Project.Standby is { Enabled: true, TimeoutSeconds: 120, Mode: 1, DimBrightnessPercent: 31 }, "apply changes standby atomically before navigation");
                var expected = ProjectService.Create(device.Id); expected.Standby = new() { Enabled = true, TimeoutSeconds = 120, Mode = 1, DimBrightnessPercent = 31 };
                check(EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(expected), "standby apply preserves lighting bindings macros layers and slider");
                if (!device.IsOfflineOnly)
                {
                var image = OnboardSerializer.Prepare(_state.Project);
                check(image.SchemaVersion == 4 && image.RequiresStandby && OnboardSerializer.Prepare(OnboardSerializer.Decode(image.Payload)).Payload.AsSpan().SequenceEqual(image.Payload), "standby schema4 roundtrip preserves exact configuration");
                var previousFirmware = new OnboardInfo(device.Id, 121, image.UsedBytes, 1, 0, 0, 3);
                check(PlatformDeviceService.UnsupportedReason(previousFirmware, image) == "Standby benötigt passende INGLORIOUS-Firmware." && PlatformDeviceService.UnsupportedReason(previousFirmware with { Flags = 249 }, image) is null, "standby capability blocks old firmware before backup or write");
                }
                _state.Undo(); Changed(); check(EditorState.Fingerprint(_state.Project) == initial, "standby undo restores exact pre-edit project");
                ShowPageAsync("Gerät").GetAwaiter().GetResult(); Find<CheckBox>("StandbyEnabled").IsChecked = true; Find<ComboBox>("StandbyMode").SelectedIndex = 0;
                _offlinePromptChoice = 2; ShowLightingSectionAsync("Effekte").GetAwaiter().GetResult();
                check(_page == "Beleuchtung" && _showEffects && EditorState.Fingerprint(_state.Project) == initial, "discard preserves stored standby config and opens effects");
                ShowPageAsync("Gerät").GetAwaiter().GetResult();
                Find<CheckBox>("StandbyEnabled").IsChecked = true; Find<ComboBox>("StandbyMode").SelectedIndex = 2;
                check(_pendingApply is not null && EditorState.Fingerprint(_state.Project) == initial, "screensaver choice stays pending until apply");
                _offlinePromptChoice = 1; ShowLightingSectionAsync("Farbe").GetAwaiter().GetResult();
                if (!device.IsOfflineOnly)
                {
                var screensaver = OnboardSerializer.Prepare(_state.Project);
                var previousFirmware = new OnboardInfo(device.Id, 121, screensaver.UsedBytes, 1, 0, 0, 3);
                check(_state.Project.Standby is { Enabled: true, Mode: 2 } && screensaver.RequiresScreensaver, "screensaver applies as device mode2");
                check(PlatformDeviceService.UnsupportedReason(previousFirmware with { Flags = 249 }, screensaver, new(1, 1, 4250)) is not null && PlatformDeviceService.UnsupportedReason(previousFirmware with { Flags = 249 }, screensaver) is not null, "old or unknown RAW11 capability blocks screensaver");
                check(PlatformDeviceService.UnsupportedReason(previousFirmware with { Flags = 249 }, screensaver, new(1, 3, 21000)) is null, "new firmware capability accepts screensaver");
                }
                else check(_state.Project.Standby is { Enabled: true, Mode: 2 }, "screensaver can be saved in offline profile");
                _state.Undo(); Changed(); check(EditorState.Fingerprint(_state.Project) == initial, "screensaver undo restores full previous profile");
                ShowPageAsync("Gerät").GetAwaiter().GetResult(); Find<CheckBox>("StandbyEnabled").IsChecked = true; Find<NumericUpDown>("StandbyTimeoutSeconds").Value = 173;
                _offlinePromptChoice = 0; OpenSettingsAsync().GetAwaiter().GetResult();
                check(L.CurrentLanguage == language && _page == "Gerät" && Find<NumericUpDown>("StandbyTimeoutSeconds").Value == 173 && _pendingApply is not null, "cancel before app settings preserves language and pending standby");
                _offlinePromptChoice = 1; OpenSettingsAsync().GetAwaiter().GetResult();
                check(_pendingApply is null && _state.Project.Standby.TimeoutSeconds == 173 && _state.Project.Standby.Enabled, "app settings guard applies standby before language changes");
                var applied = EditorState.Fingerprint(_state.Project);
                L.SetLanguage(language == "en" ? "de" : "en"); Build();
                check(EditorState.Fingerprint(_state.Project) == applied && _pendingApply is null && Find<NumericUpDown>("StandbyTimeoutSeconds").Value == 173 && Find<ComboBox>("StandbyDelayPreset").SelectedIndex == 7, "language rebuild retains exact custom standby values without creating pending edits");
                L.SetLanguage(language); Build();
                var beforeCombined = EditorState.Fingerprint(_state.Project);
                Find<NumericUpDown>("DebounceMs").Value = 13;
                Find<CheckBox>("StandbyEnabled").IsChecked = false;
                _offlinePromptChoice = 1; ShowPageAsync("Beleuchtung").GetAwaiter().GetResult();
                check(_state.Project.DeviceSettings.DebounceMs == 13 && !_state.Project.Standby.Enabled, "one device apply preserves timing and inactivity edits together");
                _state.Undo(); Changed();
                check(EditorState.Fingerprint(_state.Project) == beforeCombined, "combined timing and inactivity edit uses one undo step");
                Build();
                check(!_root.GetLogicalDescendants().OfType<Control>().Any(c => c.Name is "StandbyEnabled" or "StandbyHeading"), "inactivity settings are absent from the lighting tabs");
                var tabs = Find<TabControl>("LightingTabs");
                check(tabs.Items.Count == 2 && tabs.Items.Cast<TabItem>().Select(item => item.Name).SequenceEqual(new[] { "LightingColorTab", "LightingEffectsTab" }), "lighting contains only colors and effects tabs");
                var presets = Find<Button>("LightingPresetsButton");
                check(presets.GetLogicalAncestors().OfType<Control>().Any(control => control.Name == "InspectorHeader") && presets.Content is Avalonia.Controls.Shapes.Path && presets.Width == 32 && Equals(ToolTip.GetTip(presets), L.T("Beleuchtungsvorlagen")) && Avalonia.Automation.AutomationProperties.GetName(presets) == L.T("Beleuchtungsvorlagen"), "lighting presets use an accessible compact icon in the inspector header " + device.Id);
            }
        }
        finally { L.SetLanguage(language); _offlinePromptChoice = null; _pendingApply = null; _showEffects = effects; _state = original; _page = page; }
    }
    private void RunLightingPreviewChecks(Action<bool, string> check)
    {
        var original = _state; var page = _page; var showEffects = _showEffects;
        var globalEffect = _globalEffect; var previewRunning = _previewRunning; var hits = _hits.ToArray();
        try
        {
            foreach (var device in DeviceCatalog.All)
            {
                _state = new EditorState(device.Id);
                _state.Layer.SideLedColor = "#FF0000"; _state.Layer.ExtraLedColors.Clear();
                _state.Layer.SynchronizeSides = true; _state.Layer.LedEffects.Clear();
                foreach (var led in device.NonKeyLedIndices)
                    _state.Layer.LedEffects[led] = new() { Mode = 2, Speed = 200, Colors = ["#0000FF", "#FFFF00"] };
                _state.Surface = "left"; _page = "Beleuchtung"; _showEffects = false;
                _globalEffect = false; _previewRunning = true; _hits.Clear(); Build();
                var originalProfile = EditorState.Fingerprint(_state.Project); var undo = _state.CanUndo;
                RenderLightingPreviewFrame(0);
                check(_scene?.Animated == true && device.NonKeyLedIndices.All(led => ((Avalonia.Media.SolidColorBrush)_ledViews[led].Background!).Color == Avalonia.Media.Colors.Blue),
                    device.Id + ": color editor renders blue side sequence instead of red base on both strips");
                var controls = _root.GetLogicalDescendants().OfType<Control>().ToArray();
                var hex = controls.OfType<TextBox>().Single(c => c.Name == "ColorHex");
                var preview = controls.OfType<Button>().Single(c => c.Name == "ColorPreview");
                var chip = preview.GetLogicalDescendants().OfType<Border>().Single(c => c.Width == 42);
                check(hex.Text == "#FF0000" && ((Avalonia.Media.SolidColorBrush)chip.Background!).Color == Avalonia.Media.Colors.Red,
                    device.Id + ": color input and chip still represent the editable red base");
                hex.Text = "#123456"; RenderLightingPreviewFrame(500); var colorFrame = _frame.ToArray();
                check(device.NonKeyLedIndices.Any(led => colorFrame[led] != new PreviewRgb(0, 0, 255)) &&
                    _pendingApply is not null && hex.Text == "#123456" && EditorState.Fingerprint(_state.Project) == originalProfile && _state.CanUndo == undo,
                    device.Id + ": side animation leaves pending hex and profile history untouched");
                _offlinePromptChoice = 2; ShowLightingSectionAsync("Effekte").GetAwaiter().GetResult();
                RenderLightingPreviewFrame(500);
                check(_showEffects && _pendingApply is null && colorFrame.SequenceEqual(_frame) && EditorState.Fingerprint(_state.Project) == originalProfile,
                    device.Id + ": color and effect editors display identical committed LED output");
                _showEffects = false; _state.Layer.LedEffects.Clear(); Build(); RenderLightingPreviewFrame(500);
                check(_scene?.Animated == false && device.NonKeyLedIndices.All(led => ((Avalonia.Media.SolidColorBrush)_ledViews[led].Background!).Color == Avalonia.Media.Colors.Red),
                    device.Id + ": static side configuration continues to show its stored base");
            }
        }
        finally
        {
            _state = original; _page = page; _showEffects = showEffects;
            _globalEffect = globalEffect; _previewRunning = previewRunning; _hits.Clear(); _hits.AddRange(hits);
            _offlinePromptChoice = null; _pendingApply = null;
        }
    }
    private void RunGlobalEffectSwitchChecks(Action<bool, string> check)
    {
        T Find<T>(string name) where T : Control => _root.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var original = _state; var page = _page; var showEffects = _showEffects; var global = _globalEffect;
        var pending = _pendingApply; var choice = _offlinePromptChoice; var busy = _busy; var connectionPending = _initialConnectionsPending;
        var running = _previewRunning; var hits = _hits.ToArray();
        try
        {
            _busy = _initialConnectionsPending = false; _pendingApply = null; _previewRunning = true; _hits.Clear();
            foreach (var device in new[] { "pro-iso", "numpad" })
            {
                _state = new EditorState(device); var keys = _state.Device.Keys.Where(key => key.LedIndex.HasValue).Take(3).ToArray();
                _state.Layer.LightingEffect = new() { Mode = 2, Speed = 91, Brightness = 180, Target = 3, Reverse = true, Colors = ["#FF0000", "#AA0000"] };
                _state.Layer.Keys[keys[0].Id].Color = "#123456"; _state.Layer.Keys[keys[1].Id].Color = "#000000";
                _state.Layer.LedEffects[keys[2].LedIndex!.Value] = new() { Mode = 2, Speed = 180, Brightness = 255, Colors = ["#0000FF", "#FFFF00"] };
                _state.SelectedKeys.Add(keys[2].Id); _state.MarkInitialClean();
                _page = "Beleuchtung"; _showEffects = _globalEffect = true; Build();
                var initial = EditorState.Fingerprint(_state.Project);
                check(Find<CheckBox>("GlobalEffectEnabled").IsChecked == true && Find<StackPanel>("EffectSettings").IsVisible, "global effect controls reflect the stored active effect " + device);
                Find<CheckBox>("GlobalEffectEnabled").IsChecked = false;
                check(!Find<StackPanel>("EffectSettings").IsVisible && Find<TextBlock>("GlobalEffectDisabledHint").IsVisible && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == initial && !_state.CanUndo,
                    "switching global effect off remains a pending edit and preserves all stored settings " + device);
                var pendingOff = _pendingApply; var switchControl = Find<CheckBox>("GlobalEffectEnabled");
                Click("EffectPreviewToggle");
                check(!_previewRunning && ReferenceEquals(Find<CheckBox>("GlobalEffectEnabled"), switchControl) && switchControl.IsChecked == false && ReferenceEquals(pendingOff, _pendingApply) && EditorState.Fingerprint(_state.Project) == initial,
                    "pausing the preview preserves the pending global switch without rebuilding its controls " + device);
                Click("EffectPreviewToggle");
                Find<CheckBox>("GlobalEffectEnabled").IsChecked = true;
                check(LightingEffects.All[Find<ComboBox>("EffectMode").SelectedIndex].Id == 2 && Find<NumericUpDown>("EffectBrightness").Value == 180,
                    "off then on before apply preserves the chosen effect and brightness " + device);
                Find<CheckBox>("GlobalEffectEnabled").IsChecked = false;
                _offlinePromptChoice = 0; Click("SelectedEffectScope");
                check(_globalEffect && Find<CheckBox>("GlobalEffectEnabled").IsChecked == false && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == initial,
                    "canceling a scope change retains the pending global switch " + device);
                _offlinePromptChoice = 2; Click("SelectedEffectScope");
                check(!_globalEffect && !_root.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "GlobalEffectEnabled") && _pendingApply is null && EditorState.Fingerprint(_state.Project) == initial,
                    "discarding a global switch keeps the selection effect independent " + device);
                Click("GlobalEffectScope"); Find<CheckBox>("GlobalEffectEnabled").IsChecked = false; Click("ApplyEffect");
                var expected = ProjectService.Clone(_state.Project); expected.Layers[_state.LayerIndex].LightingEffect.Mode = 2; expected.Layers[_state.LayerIndex].LightingEffect.Brightness = 180;
                check(_state.Layer.LightingEffect is { Mode: 0, Brightness: 255, Speed: 91, Target: 3, Reverse: true } && EditorState.Fingerprint(expected) == initial && _pendingApply is null,
                    "applying off restores fixed colors and preserves overrides, palettes, targets, bindings and other layers " + device);
                RenderLightingPreviewFrame(0);
                check(_frame[keys[0].LedIndex!.Value] == new PreviewRgb(0x12, 0x34, 0x56) && _frame[keys[1].LedIndex!.Value] == new PreviewRgb(0, 0, 0) && _frame[keys[2].LedIndex!.Value] == new PreviewRgb(0, 0, 255) && _scene?.Animated == true,
                    "applied off preview shows fixed colors, keeps black LEDs dark and retains the individual animation " + device);
                var disabled = EditorState.Fingerprint(_state.Project);
                _state.Undo(); Changed(); Build();
                check(EditorState.Fingerprint(_state.Project) == initial && Find<CheckBox>("GlobalEffectEnabled").IsChecked == true, "undo restores the complete active effect in one step " + device);
                _state.Redo(); Changed(); Build();
                check(EditorState.Fingerprint(_state.Project) == disabled && Find<CheckBox>("GlobalEffectEnabled").IsChecked == false, "redo returns the exact off configuration " + device);
                _state = new EditorState(OnboardSerializer.Decode(OnboardSerializer.Prepare(_state.Project).Payload), null); Build();
                check(Find<CheckBox>("GlobalEffectEnabled").IsChecked == false && !Find<StackPanel>("EffectSettings").IsVisible, "device configuration read recognizes the inactive global effect " + device);
                var read = EditorState.Fingerprint(_state.Project);
                Find<CheckBox>("GlobalEffectEnabled").IsChecked = true;
                check(LightingEffects.All[Find<ComboBox>("EffectMode").SelectedIndex].Id == 1 && Find<NumericUpDown>("EffectBrightness").Value == 255 && EditorState.Fingerprint(_state.Project) == read,
                    "re-enabling a saved static configuration proposes breathing without changing the profile " + device);
                _offlinePromptChoice = 0; var pendingEnabled = _pendingApply; Click("LightingPresetsButton");
                check(ReferenceEquals(_pendingApply, pendingEnabled) && _globalEffect && Find<CheckBox>("GlobalEffectEnabled").IsChecked == true && EditorState.Fingerprint(_state.Project) == read, "moved preset entry preserves pending effect edits when canceled " + device);
                _offlinePromptChoice = 0; Find<TabControl>("LightingTabs").SelectedIndex = 0;
                check(_showEffects && Find<TabControl>("LightingTabs").SelectedIndex == 1 && _pendingApply is not null, "canceling a tab change retains the effect editor and pending switch " + device);
                Click("ApplyEffect");
                check(_state.Layer.LightingEffect.Mode == 1 && _state.Layer.LedEffects[keys[2].LedIndex!.Value].Mode == 2 && _pendingApply is null,
                    "enabling a global effect does not replace independent LED effects " + device);
            }
        }
        finally
        {
            _state = original; _page = page; _showEffects = showEffects; _globalEffect = global; _pendingApply = pending; _offlinePromptChoice = choice;
            _busy = busy; _initialConnectionsPending = connectionPending; _previewRunning = running; _hits.Clear(); _hits.AddRange(hits);
        }
    }
    private void RunColorEditorChecks(Action<bool, string> check)
    {
        T Find<T>(string name) where T : Control => _root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        var original = _state; var favorites = _favorites.ToArray();
        try
        {
            _state = new EditorState("pro-iso"); _state.SelectKey(_state.Device.Keys.First(k => k.LedIndex.HasValue).Id, false);
            _page = "Beleuchtung"; _showEffects = false; Build();
            var before = EditorState.Fingerprint(_state.Project);
            var hex = Find<TextBox>("ColorHex"); hex.Text = "#012345";
            var add = Find<Button>("AddFavorite"); var remove = Find<Button>("RemoveFavorite"); var grid = Find<Grid>("FavoriteColors");
            check(grid.ColumnDefinitions.Count == 6 && grid.ColumnSpacing == 8 && grid.Children.OfType<Button>().All(b => b.Width == 36 && b.Height == 36), "favorites use six equal fields and regular spacing");
            check(add.Width == 32 && remove.Width == 32 && !string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(add)), "favorite actions have compact accessible icon buttons");
            add.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            check(_favorites.Contains("#012345") && remove.IsEnabled && !add.IsEnabled && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == before, "saving favorite retains pending color and leaves device draft unchanged");
            var chosen = grid.Children.OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "#012345");
            check(chosen.BorderThickness.Left == 2, "current favorite has clear selection border");
            remove.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            check(!_favorites.Contains("#012345") && !remove.IsEnabled && add.IsEnabled && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == before, "removing favorite preserves pending color and profile");
            hex.Text = "#BAD";
            check(!add.IsEnabled && !remove.IsEnabled, "invalid hex disables favorite actions");
        }
        finally { _favorites.Clear(); _favorites.AddRange(favorites); _pendingApply = null; _state = original; }
    }
    private async Task RunProfileReplacementChecksAsync(Action<bool, string> check)
    {
        var sessions = _sessions.ToArray(); var current = _state; var macro = _macroId;
        var notices = _startupNotices.ToArray();
        var folder = Path.Combine(Path.GetTempPath(), "inglorious-profile-opening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var source in new[] { "pro-iso", "numpad" })
            {
                var target = source == "pro-iso" ? "numpad" : "pro-iso";
                var sourceDraft = new EditorState(source); sourceDraft.Change(p => p.Name = "Source unsaved fixture");
                var targetDraft = new EditorState(target); targetDraft.Change(p => p.Name = "Target unsaved fixture");
                _sessions[source] = _state = sourceDraft; _sessions[target] = targetDraft;
                var sourceBefore = EditorState.Fingerprint(sourceDraft.Project);
                var targetBefore = EditorState.Fingerprint(targetDraft.Project);
                var path = Path.Combine(folder, "selected.json");
                var selected = ProjectService.Create(target); selected.Name = "Selected fixture";
                ProjectService.Save(path, selected);
                check(!await LoadProfileCoreAsync(path, prior =>
                {
                    check(ReferenceEquals(prior, targetDraft), "profile confirmation belongs to the selected device draft");
                    return Task.FromResult(false);
                }), "cancel leaves the selected profile unopened");
                foreach (var change in new Action[] { () => File.Delete(path), () => File.WriteAllText(path, "{broken"), () => ProjectService.Save(path, ProjectService.Create(source)) })
                {
                    ProjectService.Save(path, selected);
                    var rejected = false;
                    try { await LoadProfileCoreAsync(path, async _ => { await Task.Yield(); change(); return true; }); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { rejected = true; }
                    check(rejected && ReferenceEquals(_state, sourceDraft) && ReferenceEquals(_sessions[target], targetDraft) && ReferenceEquals(_sessions[source], sourceDraft),
                        "changed or missing profile cannot replace another device or the visible session");
                    check(EditorState.Fingerprint(sourceDraft.Project) == sourceBefore && EditorState.Fingerprint(targetDraft.Project) == targetBefore && sourceDraft.Dirty && targetDraft.Dirty,
                        "both unsaved profiles survive failed replacement exactly");
                }
                ProjectService.Save(path, selected);
                check(await LoadProfileCoreAsync(path, _ =>
                {
                    selected.Name = "Saved during confirmation"; ProjectService.Save(path, selected);
                    return Task.FromResult(true);
                }) && _state.Device.Id == target && _state.Project.Name == selected.Name && !_state.Dirty,
                    "a successful same-device reread adopts the latest saved profile");
                check(ReferenceEquals(_sessions[source], sourceDraft) && EditorState.Fingerprint(sourceDraft.Project) == sourceBefore,
                    "successful replacement keeps the other device draft");
            }
        }
        finally
        {
            _sessions.Clear(); foreach (var pair in sessions) _sessions.Add(pair.Key, pair.Value);
            _startupNotices.Clear(); foreach (var pair in notices) _startupNotices.Add(pair.Key, pair.Value);
            _state = current; _macroId = macro; Directory.Delete(folder, recursive: true);
        }
    }
    private void RunLayerGuidanceChecks(Action<bool, string> check)
    {
        T Find<T>(string name) where T : Control => _root.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var original = _state; var page = _page; var slider = _editingSlider; var pending = _pendingApply;
        var choice = _offlinePromptChoice; var busy = _busy; var initialPending = _initialConnectionsPending;
        try
        {
            _busy = _initialConnectionsPending = false; _pendingApply = null;
            _state = new EditorState("pro-iso"); _state.MarkInitialClean(); _page = "Tastenbelegung"; _editingSlider = false;
            var fn = _state.Device.Keys.First(key => key.DefaultBinding == "MO(1)").Id;
            _state.SelectKey(fn, false); Build();
            var initial = EditorState.Fingerprint(_state.Project);
            check(Find<StackPanel>("LayerEditorSelector").IsVisible && Find<TextBlock>("LayerEditingHint").Text == LayerGuidance.EditingHint &&
                Find<TextBlock>("CurrentKeyBindingSummary").Text!.Contains(_state.Project.Layers[1].Name) &&
                !Find<TextBlock>("CurrentKeyBindingSummary").Text!.Contains("MO("),
                "binding workspace distinguishes editing from device activation and explains the selected Fn assignment without protocol codes");
            check(!Find<Button>("UseUnderlyingBinding").IsEnabled, "base-layer bindings cannot be replaced with a transparent action");
            _layerChoice.SelectedIndex = 2;
            check(_state.LayerIndex == 2 && EditorState.Fingerprint(_state.Project) == initial && ! _state.CanUndo,
                "choosing an editor layer changes no device configuration or history");
            _state.LayerIndex = 0; Build(); OpenLayerBindingEditorAsync().GetAwaiter().GetResult();
            check(_page == "Ebenen" && _state.SelectedKeys.SetEquals([fn]) && Find<ComboBox>("LayerBindingTarget").SelectedIndex == 1 &&
                Find<ComboBox>("LayerBindingBehavior").SelectedIndex == 0 && EditorState.Fingerprint(_state.Project) == initial,
                "Fn entry retains the selected key and reflects its existing target and hold behavior without editing it");
            check(Find<ComboBox>("LayerBindingTarget").Items.Cast<string>().All(label => _state.Project.Layers.Any(layer => label.EndsWith(layer.Name))),
                "layer action destinations include the editable layer names");
            for (var mode = 0; mode < 3; mode++)
            {
                Find<ComboBox>("LayerBindingTarget").SelectedIndex = 2;
                Find<ComboBox>("LayerBindingBehavior").SelectedIndex = mode;
                check(Find<TextBlock>("LayerBindingDescription").Text == LayerGuidance.ActionDescription(mode), "layer action description follows behavior " + mode);
                var expected = ProjectService.Clone(_state.Project); expected.Layers[0].Keys[fn].Binding = new[] { "MO(2)", "TG(2)", "TO(2)" }[mode];
                Click("AssignLayerBinding");
                check(EditorState.Fingerprint(_state.Project) == EditorState.Fingerprint(expected),
                    "assigning a layer action changes only the chosen source key and never rewrites its target layer: " + mode);
                _state.Undo(); Changed(); Build();
                check(EditorState.Fingerprint(_state.Project) == initial && !_state.CanUndo, "a layer action is exactly one reversible change: " + mode);
            }
            _state.LayerIndex = 1; Build();
            check(Find<ComboBox>("LayerBindingTarget").SelectedIndex == 0 && Find<ComboBox>("LayerBindingBehavior").SelectedIndex == 2 &&
                EditorState.Fingerprint(_state.Project) == initial && !_state.CanUndo,
                "an unassigned upper-layer key offers a direct return to the base without changing the draft");
            Find<ComboBox>("LayerBindingBehavior").SelectedIndex = 0;
            check(Find<TextBlock>("LayerBindingDescription").Text == LayerGuidance.ActionDescription(0, 1, 0) &&
                Find<TextBlock>("LayerBindingDescription").Text != LayerGuidance.ActionDescription(0) && Find<Button>("AssignLayerBinding").IsEnabled,
                "holding a lower layer explains its limitation without removing advanced assignments");
            Find<ComboBox>("LayerBindingTarget").SelectedIndex = 2;
            check(Find<ComboBox>("LayerBindingBehavior").SelectedIndex == 0 && Find<TextBlock>("LayerBindingDescription").Text == LayerGuidance.ActionDescription(0),
                "changing only the destination refreshes the hold explanation without resetting the chosen mode");
            Find<ComboBox>("LayerBindingTarget").SelectedIndex = 1;
            check(Find<TextBlock>("LayerBindingDescription").Text == LayerGuidance.ActionDescription(0, 1, 1) &&
                Find<TextBlock>("LayerBindingDescription").Text != LayerGuidance.ActionDescription(0), "holding the source layer has a contextual explanation");
            Find<ComboBox>("LayerBindingBehavior").SelectedIndex = 1;
            check(Find<ComboBox>("LayerBindingTarget").SelectedIndex == 1 && Find<Button>("AssignLayerBinding").IsEnabled &&
                Find<TextBlock>("LayerBindingDescription").Text == LayerGuidance.ActionDescription(1), "toggling the current upper layer stays available as a return action");
            foreach (var code in new[] { "TG(2)", "TO(3)", "MO(0)" })
            {
                _state.SetBinding(code); var assigned = EditorState.Fingerprint(_state.Project); Build();
                LayerGuidance.TryLayerAction(code, out var mode, out var target);
                check(Find<ComboBox>("LayerBindingTarget").SelectedIndex == target && Find<ComboBox>("LayerBindingBehavior").SelectedIndex == mode &&
                    EditorState.Fingerprint(_state.Project) == assigned, "existing layer actions retain their exact mode and target on reopening: " + code);
                _state.Undo(); Changed(); Build();
            }
            _state.SelectKey(_state.Device.Keys.First(key => !key.IsEncoder && key.Id != fn).Id, true); _state.SetBinding("TG(2)"); Build();
            check(Find<ComboBox>("LayerBindingTarget").SelectedIndex == 2 && Find<ComboBox>("LayerBindingBehavior").SelectedIndex == 1,
                "multiple keys sharing one existing layer action keep that action selected");
            _state.Undo(); Changed(); _state.LayerIndex = 0; _state.SelectKey(fn, false); Build();
            check(EditorState.Fingerprint(_state.Project) == initial && !_state.CanUndo, "contextual layer guidance leaves the complete original profile intact");
            _state.SelectedKeys.Clear(); Build(); check(!Find<Button>("AssignLayerBinding").IsEnabled, "layer assignment needs a selected key");
            _state.SelectKey(_state.Device.Keys.First(key => key.IsEncoder).Id, false); Build();
            check(!Find<Button>("AssignLayerBinding").IsEnabled, "rotary movement cannot be assigned a held Fn action");
            Find<ComboBox>("LayerBindingBehavior").SelectedIndex = 1;
            check(Find<Button>("AssignLayerBinding").IsEnabled, "rotary movement can still toggle a layer");
            _state.SelectKey(fn, false); _page = "Tastenbelegung"; Build(); Click("ModifierBindings");
            var modifiers = Find<ListBox>("BindingChoices").Items.Cast<BindingRow>().ToArray();
            check(modifiers.Length == 8 && modifiers.All(row => IsModifierBinding(row.Code) && !row.Label.Contains("KC_")) &&
                modifiers.Select(row => row.Label).Distinct().Count() == 8 && Find<TextBlock>("BindingFunctionHint").Text == LayerGuidance.ModifierHint,
                "modifier shortcut offers eight clearly distinguished actions with an explanation and no raw codes");
            _state.LayerIndex = 1; Build();
            check(Find<TextBlock>("BindingFunctionHint").Text == LayerGuidance.BindingDescription("KC_TRNS") && Find<Button>("UseUnderlyingBinding").IsEnabled,
                "the inherited binding explains its lower-layer behavior");
            var applied = 0; _pendingApply = () => { applied++; return true; }; _offlinePromptChoice = 0;
            OpenLayerBindingEditorAsync().GetAwaiter().GetResult();
            check(_page == "Tastenbelegung" && _pendingApply is not null && applied == 0 && EditorState.Fingerprint(_state.Project) == initial,
                "canceling pending edits stops Fn navigation without losing input or changing the profile");
        }
        finally
        {
            _state = original; _page = page; _editingSlider = slider; _offlinePromptChoice = choice;
            _busy = busy; _initialConnectionsPending = initialPending; Build(); _pendingApply = pending;
        }
    }

    private void RunSliderNavigationChecks(Action<bool, string> check)
    {
        T? Find<T>(string name) where T : Control => _root.GetLogicalDescendants().OfType<T>().SingleOrDefault(c => c.Name == name);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var original = _state;
        try
        {
            _state = new EditorState("numpad");
            _state.Change(p => { p.Slider = new() { Mode = 3, Minimum = 11, Maximum = 990, MidiChannel = 4, MidiController = 24 }; p.DeviceSettings = new() { UsbPollingIntervalMs = 2, DebounceMs = 8 }; });
            var initial = EditorState.Fingerprint(_state.Project);
            _page = "Gerät"; Build();
            check(Find<ComboBox>("SliderMode") is null && Find<ComboBox>("PollingInterval") is not null, "device editor has timing but no slider");
            _page = "Beleuchtung"; Build(); Click(Find<Button>("PreviewSlider")!);
            check(_page == "Tastenbelegung" && _editingSlider && Find<ComboBox>("SliderMode") is not null, "preview slider opens bindings slider editor");
            check(Find<ComboBox>("SliderMode")!.SelectedIndex == 3 && Find<NumericUpDown>("SliderMinimum")!.Value == 11 && Find<NumericUpDown>("SliderMaximum")!.Value == 990 && Find<NumericUpDown>("SliderChannel")!.Value == 5 && Find<NumericUpDown>("SliderController")!.Value == 24, "slider existing fields preserved");
            check(EditorState.Fingerprint(_state.Project) == initial && _pendingApply is null, "opening slider is pure");
            var range = Find<Expander>("SliderRange")!;
            check(range.IsVisible && !range.IsExpanded && !Find<Grid>("SliderMidiFields")!.IsVisible && Find<StackPanel>("SliderDimmingHint")!.IsVisible, "dimming mode shows its hint and a collapsed range without MIDI fields");
            range.IsExpanded = true; range.IsExpanded = false;
            check(_pendingApply is null && EditorState.Fingerprint(_state.Project) == initial, "opening and closing slider range never creates pending edits");
            foreach (var sliderMode in new[] { 0, 1, 2, 3 })
            {
                Find<ComboBox>("SliderMode")!.SelectedIndex = sliderMode;
                check(Find<Grid>("SliderMidiFields")!.IsVisible == (sliderMode == 1) && range.IsVisible == (sliderMode != 0) &&
                    Find<StackPanel>("SliderDimmingHint")!.IsVisible == (sliderMode == 3) && !string.IsNullOrWhiteSpace(Find<TextBlock>("SliderModeHint")!.Text),
                    "slider mode exposes only its relevant fields and hint: " + sliderMode);
                check(EditorState.Fingerprint(_state.Project) == initial && Find<NumericUpDown>("SliderMinimum")!.Value == 11 &&
                    Find<NumericUpDown>("SliderMaximum")!.Value == 990 && Find<NumericUpDown>("SliderChannel")!.Value == 5 && Find<NumericUpDown>("SliderController")!.Value == 24,
                    "changing slider mode retains hidden draft values without applying: " + sliderMode);
            }
            Find<ComboBox>("SliderMode")!.SelectedIndex = 1; Find<NumericUpDown>("SliderChannel")!.Value = 8;
            check(_pendingApply is not null && EditorState.Fingerprint(_state.Project) == initial, "slider edits are pending until apply");
            _offlinePromptChoice = 0; Click(Find<Button>("BindingKeysTab")!);
            check(_editingSlider && _pendingApply is not null && Find<ComboBox>("SliderMode")!.SelectedIndex == 1, "cancel keeps slider editor and entered values");
            _offlinePromptChoice = 1; Click(Find<Button>("BindingKeysTab")!);
            check(!_editingSlider && _pendingApply is null && _state.Project.Slider.Mode == 1 && _state.Project.Slider.MidiChannel == 7, "apply switches to key bindings with slider saved in draft");
            check(_state.Project.DeviceSettings.UsbPollingIntervalMs == 2 && _state.Project.DeviceSettings.DebounceMs == 8, "slider apply preserves timing");
            _state.Undo(); Changed(); check(EditorState.Fingerprint(_state.Project) == initial, "slider apply undo restores exact project");
            ShowBindingEditorAsync(true).GetAwaiter().GetResult(); Find<NumericUpDown>("SliderMinimum")!.Value = 1000; Find<NumericUpDown>("SliderMaximum")!.Value = 100;
            Find<ComboBox>("SliderMode")!.SelectedIndex = 0;
            check(!Find<Expander>("SliderRange")!.IsVisible && !Find<Expander>("SliderRange")!.IsExpanded, "disabled slider initially hides unneeded range even when its uncommitted values are invalid");
            _offlinePromptChoice = 1; Click(Find<Button>("BindingKeysTab")!);
            check(_editingSlider && _pendingApply is not null && EditorState.Fingerprint(_state.Project) == initial, "invalid slider apply keeps editor and project atomically");
            check(Find<Expander>("SliderRange")!.IsVisible && Find<Expander>("SliderRange")!.IsExpanded && Find<ComboBox>("SliderMode")!.SelectedIndex == 0,
                "invalid hidden slider range is revealed and expanded without changing disabled mode");
            _offlinePromptChoice = 2; Click(Find<Button>("BindingKeysTab")!);
            check(!_editingSlider && _pendingApply is null && EditorState.Fingerprint(_state.Project) == initial, "discard changes no stored slider values");
            _page = "Gerät"; Build(); Find<NumericUpDown>("DebounceMs")!.Value = 9;
            _offlinePromptChoice = 1; ShowBindingEditorAsync(true).GetAwaiter().GetResult();
            check(_state.Project.DeviceSettings.DebounceMs == 9 && _state.Project.Slider.Mode == 3 && _state.Project.Slider.Minimum == 11 && _state.Project.Slider.Maximum == 990 && _state.Project.Slider.MidiChannel == 4 && _state.Project.Slider.MidiController == 24, "timing apply preserves entire slider");
            Click(_keyButtons[_state.Device.Keys[0].Id]); check(!_editingSlider && Find<ComboBox>("SliderMode") is null, "physical key click returns to key editor");
            _state = new EditorState("pro-iso"); _editingSlider = true; _page = "Tastenbelegung"; Build();
            check(Find<ComboBox>("SliderMode") is null && Find<Button>("BindingSliderTab") is null && Find<Button>("PreviewSlider") is null, "PRO never exposes slider controls");
        }
        finally { _offlinePromptChoice = null; _pendingApply = null; _editingSlider = false; _state = original; }
    }
}
