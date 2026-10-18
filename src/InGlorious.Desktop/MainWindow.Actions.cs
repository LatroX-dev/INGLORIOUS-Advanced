using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InGlorious.Core;
using System.Diagnostics;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private async Task<int> Ask(string title, string message, params string[] choices)
    {
        if (Program.Offline) return _offlinePromptChoice ?? choices.Length - 1;
        return await CreatePrompt(title, message, choices).ShowDialog<int>(this);
    }
    private Window CreatePrompt(string title, string message, params string[] choices)
    {
        var dialog = new Window { Title = L.T(title), Width = 640, MaxHeight = 650, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Background = Brush("#141618") };
        var heading = Text(L.T(title), size: 21); heading.FontWeight = FontWeight.SemiBold;
        var body = Column(heading, Text(message)); body.Margin = new Thickness(24);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (var i = 0; i < choices.Length; i++)
        {
            var result = i; var button = new Button { Content = ButtonContent(choices[i]), Margin = new Thickness(7, 0, 0, 5), IsCancel = i == 0, IsDefault = i == 0 };
            if (choices[i] is "Profil speichern" or "Speichern" or "Übernehmen") button.Classes.Add("primary");
            button.Click += (_, _) => dialog.Close(result); actions.Children.Add(button);
        }
        body.Children.Add(actions); dialog.Content = new ScrollViewer { Content = body };
        return dialog;
    }
    private async Task<bool> GuardNavigation()
    {
        if (_busy || _navigating || _initialConnectionsPending) return false;
        if (_pendingApply is null) return true;
        _navigating = true;
        try
        {
            var choice = await Ask("Änderungen übernehmen?", L.F($"{L.T("Einstellungen")} wurden geändert, aber noch nicht ins Profil übernommen."), "Abbrechen", "Übernehmen", "Verwerfen");
            if (choice == 0) return false;
            if (choice == 1 && !_pendingApply()) return false;
            _pendingApply = null; return true;
        }
        catch (Exception ex) { Status(ex.Message); return false; }
        finally { _navigating = false; }
    }
    private async Task<bool> MayReplace(EditorState state)
    {
        if (!state.Dirty) return true;
        var result = await Ask("Ungespeichertes Profil", L.F($"Änderungen an „{state.Project.Name}“ zuerst speichern?"), "Abbrechen", "Profil speichern", "Verwerfen");
        return await ResolveReplaceChoice(result, () => SaveStateAsync(state, false));
    }
    internal static Task<bool> ResolveReplaceChoice(int choice, Func<Task<bool>> save) => choice switch { 1 => save(), 2 => Task.FromResult(true), _ => Task.FromResult(false) };
    private async Task SaveAsync(bool copy)
    { if (await GuardNavigation()) await SaveStateAsync(_state, copy); }
    private async Task<bool> SaveStateAsync(EditorState state, bool copy)
    {
        try
        {
            var errors = ProjectService.Validate(state.Project); if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            if (Program.Offline) throw new InvalidOperationException("Offline UI mode never opens file dialogs or writes user profiles.");
            var path = state.Path;
            if (copy || path is null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = L.T("Profil speichern"), SuggestedFileName = ProfileLibrary.SuggestFileName(state.Project.Name), DefaultExtension = "json", ShowOverwritePrompt = true, FileTypeChoices = [new("INGLORIOUS JSON") { Patterns = ["*.json"] }] });
                path = file?.TryGetLocalPath(); if (path is null) return false;
            }
            ProjectService.Save(path, state.Project);
            if (!copy) state.MarkSaved(path);
            try { _profiles.Remember(path, state.Device.Id, !copy); } catch (Exception ex) { Status(ex.Message); }
            Changed(); Status(L.T("Profil gespeichert")); return true;
        }
        catch (Exception ex) { Status(ex.Message); return false; }
    }
    private async Task OpenProfileAsync()
    {
        if (!await GuardNavigation() || Program.Offline) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("INGLORIOUS-Profil öffnen"), AllowMultiple = false, FileTypeFilter = [new("INGLORIOUS JSON") { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadProfileAsync(path);
    }
    private async Task LoadProfileAsync(string path)
    {
        if (!await GuardNavigation()) return;
        try
        {
            if (!await LoadProfileCoreAsync(path, MayReplace)) return;
            Build(); Status(L.F($"Profil „{_state.Project.Name}“ geöffnet"));
            // A damaged recent-file index must not leave the new session behind the old UI.
            if (!Program.Offline)
                try { _profiles.Remember(path, _state.Device.Id); }
                catch (Exception ex) { Status(L.T("Die Profilliste konnte nicht gespeichert werden: ") + ex.Message); }
        }
        catch (Exception ex) { Status(ex.Message); }
    }
    private async Task<bool> LoadProfileCoreAsync(string path, Func<EditorState, Task<bool>> confirmReplace)
    {
        var first = ProjectService.Load(path);
        var prior = OpenSession(first.DeviceId);
        if (!await confirmReplace(prior)) return false;
        // The save dialog can change this file. Confirmation covers only that device's draft.
        var project = ProjectService.Load(path);
        if (project.DeviceId != first.DeviceId)
            throw new InvalidDataException(L.T("Die Profildatei wurde während des Öffnens geändert. Bitte erneut öffnen."));
        _sessions[project.DeviceId] = _state = new EditorState(project, path); _macroId = null;
        _startupNotices.Remove(project.DeviceId);
        return true;
    }
    private Control BuildProfilesWorkspace()
    {
        var name = Input(_state.Project.Name); name.Name = "ProfileName";
        name.LostFocus += (_, _) => { if (IsHelpTakingFocus) return; var value = name.Text?.Trim(); if (!string.IsNullOrWhiteSpace(value) && value.Length <= 80) { _state.Change(p => p.Name = value); UpdateFooter(); } };
        var panel = Column(Heading("Aktuelles Profil"), name,
            Text(_state.Path is null ? L.T("Noch nicht gespeichert") : Path.GetFileName(_state.Path), true),
            Row(AsyncButton("Profil speichern", () => SaveAsync(false), true), AsyncButton("Kopie speichern …", () => SaveAsync(true))),
            Row(AsyncButton("Profil öffnen …", OpenProfileAsync), AsyncButton("Neues Profil", async () =>
            {
                if (!await GuardNavigation() || !await MayReplace(_state)) return;
                _startupNotices.Remove(_state.Device.Id);
                _sessions[_state.Device.Id] = _state = new EditorState(_state.Device.Id); _state.MarkInitialClean(); Build();
            })), Heading("Profile"));
        var entries = Program.Offline ? [] : _profiles.Discover(out _);
        foreach (var entry in entries)
        {
            var item = AsyncButton(entry.Name + " · " + DeviceCatalog.Get(entry.DeviceId).Name, () => LoadProfileAsync(entry.Path));
            item.HorizontalContentAlignment = HorizontalAlignment.Left; ToolTip.SetTip(item, entry.Path); panel.Children.Add(item);
        }
        return panel;
    }
    private async Task<OnboardDevice?> ChooseDeviceAsync()
    {
        if (Program.Offline || _state.Device.IsOfflineOnly) return null;
        Status(L.T("Passende INGLORIOUS-Firmware wird gesucht. Es werden keine Einstellungen geschrieben …"));
        var devices = (await PlatformDeviceService.FindAsync()).Where(d => d.DeviceId == _state.Device.Id).ToArray();
        if (devices.Length == 0)
        {
            await OpenDeviceSetupAsync(_state.Device.Id, allowBusy: true);
            return null;
        }
        if (devices.Length == 1) return devices[0];
        var window = new Window { Title = L.T("Gerät auswählen"), Width = 540, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = devices.Select((d, i) => new ListBoxItem { Content = DeviceCatalog.Get(d.DeviceId).Name + " · " + (i + 1), [ToolTip.TipProperty] = d.Path }).ToArray(), Height = 210 };
        var accept = new Button { Content = L.T("Übernehmen"), IsEnabled = false };
        list.SelectionChanged += (_, _) => accept.IsEnabled = list.SelectedIndex >= 0;
        accept.Click += (_, _) => window.Close(devices[list.SelectedIndex]);
        window.Content = new Border { Padding = new Thickness(20), Child = Column(list, accept) };
        return await window.ShowDialog<OnboardDevice?>(this);
    }
    private Control BuildFirmwareVersionPanel()
    {
        if (_state.Device.IsOfflineOnly) return Text(L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar."), true);
        var id = _state.Device.Id;
        _firmwareAssessments.TryGetValue(id, out var assessment);
        var status = Text(assessment?.Version is { } reported ? "INGLORIOUS " + reported.Version + " · " + SetupStatusText(assessment)
            : assessment is null ? L.T("Version noch nicht geprüft") : SetupStatusText(assessment));
        status.Name = "FirmwareVersionStatus";
        var changelog = new StackPanel { Name = "FirmwareChangelog", IsVisible = false };
        ShowFirmwareChangelog(changelog, id, assessment?.Version?.Version);
        var read = AsyncButton("Version prüfen", () => CheckFirmwareSetupAsync(force: true, requestedId: id)); read.Name = "ReadFirmwareVersion";
        var update = AsyncButton("Firmware aktualisieren", () => CheckFirmwareSetupAsync(force: true, requestedId: id), true);
        update.Name = "UpdateFirmware"; update.IsVisible = assessment?.State == SetupRuntimeState.UpdateAvailable;
        return Column(status, changelog, read, update);
    }
    private static void ShowFirmwareChangelog(StackPanel target, string deviceId, FirmwareSemanticVersion? installed)
    {
        target.Children.Clear();
        if (!FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(deviceId), out var targetVersion)) { target.IsVisible = false; return; }
        var notes = FirmwareChangelog.Between(deviceId, installed, targetVersion);
        target.IsVisible = notes.Count != 0;
        if (notes.Count == 0) return;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "What’s new", FontSize = 15, FontWeight = FontWeight.SemiBold });
        foreach (var release in notes)
        {
            body.Children.Add(new TextBlock { Text = "INGLORIOUS " + release.Version, Foreground = Brush("#EF646B"), FontSize = 12, Margin = new(0, 4, 0, 0) });
            foreach (var change in release.Changes)
            {
                var row = new Grid { ColumnDefinitions = new("16,*") };
                row.Children.Add(new TextBlock { Text = "•" });
                var text = new TextBlock { Text = change, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1); row.Children.Add(text); body.Children.Add(row);
            }
        }
        target.Children.Add(new Border { Child = body, Background = Brush("#1C1F22"), BorderBrush = Brush("#353A40"), BorderThickness = new(1), CornerRadius = new(6), Padding = new(16), Margin = new(0, 10, 0, 10) });
    }
    private async Task ReadDeviceAsync()
    {
        if (_state.Device.IsOfflineOnly || !await GuardNavigation() || Program.Offline) return;
        try
        {
            _busy = true; _root.IsEnabled = false; await _sharedBrightness.StopAsync();
            var device = await ChooseDeviceAsync(); if (device is null) return;
            _busy = true; _root.IsEnabled = false; RequireDeviceConfigurationRead(device.DeviceId); UpdateFooter();
            var read = await PlatformDeviceService.ReadAsync(device);
            if (!await MayReplace(_state)) return;
            MarkDeviceConfigurationRead(read.Project, read.Info);
            _sessions[device.DeviceId] = _state = EditorState.FromDevice(read.Project);
            _startupNotices.Remove(device.DeviceId);
            _startupReadFingerprints.Remove(device.DeviceId);
            _startupFallbackNotices.Remove(device.DeviceId);
            if (!read.Info.Persistent) _startupFallbackNotices[device.DeviceId] = L.T("Das Gerät meldet keinen verfügbaren dauerhaften Speicher.");
            if (read.Info.Persistent) _confirmed[device.DeviceId] = EditorState.Fingerprint(_state.Project);
            Build(); Status(read.Info.Persistent ? L.T("Konfiguration von Tastatur geladen") : L.T("Das Gerät meldet keinen verfügbaren dauerhaften Speicher."));
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { _busy = false; _root.IsEnabled = true; UpdateFooter(); await UpdateBrightnessService(); }
    }
    private async Task TransferAsync()
    {
        if (_state.Device.IsOfflineOnly || !await GuardNavigation() || Program.Offline) return;
        if (DeviceConfigurationNeedsRead) { Status(DeviceConfigurationReadNotice); return; }
        var snapshot = ProjectService.Clone(_state.Project);
        var image = OnboardSerializer.Prepare(snapshot);
        string? feedbackDeviceId = null;
        try
        {
        OnboardDevice? target;
        _busy = true; _root.IsEnabled = false;
        try { await _sharedBrightness.StopAsync(); target = await ChooseDeviceAsync(); }
        finally { _busy = false; _root.IsEnabled = true; }
        if (target is null) return;
        OnboardInfo info;
        OnboardExtensions? extensions = null;
        _busy = true; _root.IsEnabled = false;
        try
        {
            info = await PlatformDeviceService.GetInfoAsync(target);
            if (image.RequiresScreensaver || image.RequiresKnobCycle) extensions = await PlatformDeviceService.GetExtensionsAsync(target);
        }
        finally { _busy = false; _root.IsEnabled = true; }
        if (PlatformDeviceService.UnsupportedReason(info, image, extensions) is { } unsupported)
        {
            Status(unsupported); await CheckFirmwareSetupAsync(force: true, requestedId: target.DeviceId); return;
        }
        var dialog = new Window { Title = L.T("Auf Tastatur übertragen"), Width = 540, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var status = Text(L.T("Bereit zur Übertragung"));
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, IsVisible = false };
        var go = new Button { Content = ButtonContent("Auf Tastatur übertragen") }; go.Classes.Add("primary");
        var readBack = new Button { Content = L.T("Gerätestand lesen") }; readBack.IsVisible = false;
        var close = new Button { Content = L.T("Schließen") }; close.Click += (_, _) => dialog.Close();
        var guide = AsyncButton("Firmware", () => CheckFirmwareSetupAsync(force: true, requestedId: target.DeviceId, owner: dialog)); guide.IsVisible = false;
        var running = false;
        string? completionNotice = null;
        dialog.Closing += (_, e) => { if (running) e.Cancel = true; };
        go.Click += async (_, _) =>
        {
            if (running) return;
            running = _busy = true; go.IsEnabled = close.IsEnabled = false; _confirmed.Remove(target.DeviceId); UpdateFooter();
            var writeStarted = false;
            var progressActive = true;
            var saved = false;
            progress.IsVisible = true;
            try
            {
                var result = await PlatformDeviceService.ApplyAsync(target, image,
                    new Progress<int>(value =>
                    {
                        if (!progressActive) return;
                        progress.Value = value;
                        status.Text = value < 70 ? L.Format("Das Profil wird übertragen … {0} %", value)
                            : value < 80 ? L.T("Im Gerät speichern … dieser Schritt kann bis zu zwei Minuten dauern. Bitte angeschlossen lassen.")
                            : L.T("Die gespeicherten Einstellungen werden geprüft …");
                    }), () => writeStarted = true);
                if (!result.Transfer.ReadbackVerified) throw new IOException("Device readback was not verified.");
                MarkDeviceConfigurationRead(snapshot);
                _confirmed[target.DeviceId] = EditorState.Fingerprint(snapshot); progress.Value = 100;
                completionNotice = result.UsbNotice;
                feedbackDeviceId = target.DeviceId;
                saved = true;
            }
            catch (Exception ex)
            {
                Trace.TraceError("Configuration transfer failed: {0}", ex);
                if (writeStarted) RequireDeviceConfigurationRead(target.DeviceId);
                status.Text = ex is FirmwareUpdateRequiredException ? ex.Message : L.T(writeStarted
                    ? "Das Ergebnis ist unklar. Prüfe zuerst den Gerätestand."
                    : "Die Übertragung wurde nicht gestartet. Versuche es erneut.");
                readBack.IsVisible = writeStarted;
                guide.IsVisible = ex is FirmwareUpdateRequiredException;
                if (!writeStarted && !guide.IsVisible) go.IsEnabled = true;
            }
            finally
            {
                progressActive = false; running = _busy = false; close.IsEnabled = true; UpdateFooter();
                if (saved) { Status(completionNotice ?? L.T("Auf Tastatur bestätigt")); dialog.Close(); }
            }
        };
        readBack.Click += async (_, _) =>
        {
            if (running || !readBack.IsVisible) return;
            running = _busy = true; readBack.IsEnabled = close.IsEnabled = go.IsEnabled = false;
            var saved = false;
            try
            {
                var read = await PlatformDeviceService.ReadAsync(target);
                var exact = read.Info.Persistent && read.Payload.AsSpan().SequenceEqual(image.Payload);
                if (exact)
                {
                    MarkDeviceConfigurationRead(read.Project, read.Info);
                    _confirmed[target.DeviceId] = EditorState.Fingerprint(snapshot); saved = true;
                    feedbackDeviceId = target.DeviceId;
                    if (read.Info.SupportsTiming) completionNotice = L.T("Bitte verbinde das USB-Kabel erneut, damit die neue Abfragerate aktiv wird.");
                }
                else { _confirmed.Remove(target.DeviceId); status.Text = L.T("Änderungen noch nicht übertragen"); go.IsEnabled = true; }
            }
            catch (Exception ex) { Trace.TraceError("Configuration readback failed: {0}", ex); _confirmed.Remove(target.DeviceId); status.Text = L.T("Der Gerätestand konnte nicht bestätigt werden. Prüfe die USB-Verbindung und versuche es erneut."); }
            finally
            {
                running = _busy = false; close.IsEnabled = readBack.IsEnabled = true; UpdateFooter();
                if (saved) { Status(completionNotice ?? L.T("Auf Tastatur bestätigt")); dialog.Close(); }
            }
        };
        dialog.Content = new Border { Padding = new Thickness(24), Child = Column(Heading("Auf Tastatur übertragen"), Text(snapshot.Name + " · " + DeviceCatalog.Get(target.DeviceId).Name),
            Text(L.T("Der bisherige Gerätestand wird automatisch gesichert."), true), status, progress, Row(guide, readBack, close, go)) };
        await dialog.ShowDialog(this);
        }
        finally
        {
            _busy = false; _root.IsEnabled = true; await UpdateBrightnessService();
            if (feedbackDeviceId is not null)
            {
                _firmwareAssessments.TryGetValue(feedbackDeviceId, out var assessment);
                await MaybeOfferExperimentalFeedbackAsync(feedbackDeviceId, DeviceFeedbackTrigger.ConfigurationTransfer, assessment?.Version);
            }
        }
    }
    private bool _closing;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (KeepWindowInTray(e.CloseReason)) { e.Cancel = true; return; }
        if (Program.Offline) return;
        if (_startupLoading) { _allowClose = true; _startupCancellation?.Cancel(); return; }
        e.Cancel = true; if (_busy || _closing) { _applicationExitRequested = false; return; }
        _closing = true;
        try
        {
            if (!await GuardNavigation()) return;
            foreach (var session in _sessions.Values) if (!await MayReplace(session)) return;
            await _sharedBrightness.StopAsync(); _allowClose = true; Close();
        }
        finally { _closing = false; _applicationExitRequested = false; }
    }
    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (_busy || e.Source is TextBox) return;
        var command = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
        if ((e.KeyModifiers & command) == 0 || _pendingApply is not null) return;
        if (e.Key == Avalonia.Input.Key.Z) { _state.Undo(); Changed(); Build(); e.Handled = true; }
        if (e.Key == Avalonia.Input.Key.Y) { _state.Redo(); Changed(); Build(); e.Handled = true; }
    }
    private async Task OnOpened()
    {
        if (_startupWindowClosed || _allowClose) return;
        if (Program.Offline)
        {
            // Opened can run before the application lifetime has entered its main loop.
            // Let startup finish before a failed check can close the main window.
            await Dispatcher.Yield(DispatcherPriority.Background);
            try
            {
                var smoke = Array.IndexOf(Program.Arguments, "--smoke");
                if (smoke >= 0)
                {
                    string report;
                    if (Program.Arguments.Contains("--knob-checks"))
                    {
                        var checkedCount = 0;
                        void CheckRelevant(bool valid, string description) { if (!valid) throw new InvalidOperationException(description); checkedCount++; }
                        await RunKnobPresetChecksAsync(CheckRelevant); RunLayerGuidanceChecks(CheckRelevant); RunSliderNavigationChecks(CheckRelevant);
                        report = $"PASS {checkedCount} focused offline knob/layer checks; no hardware or user-data access.";
                    }
                    else report = await RunSmoke();
                    if (smoke + 1 < Program.Arguments.Length) await File.WriteAllTextAsync(Program.Arguments[smoke + 1], report);
                }
                var shot = Array.IndexOf(Program.Arguments, "--screenshot");
                if (shot >= 0 && shot + 1 < Program.Arguments.Length)
                {
                    if (Program.Arguments.Contains("--help") || Program.Arguments.Contains("--close-prompt") || Program.Arguments.Contains("--picker") || Program.Arguments.Contains("--device-setup") || Program.Arguments.Contains("--device-test") || Program.Arguments.Contains("--settings") || Program.Arguments.Contains("--knob-cycle-options") || Program.Arguments.Contains("--os-actions"))
                    {
                        var helpTopic = Array.IndexOf(Program.Arguments, "--help-topic");
                        if (Program.Arguments.Contains("--knob-cycle-all")) _state.Layer.KnobCycleMask = 255;
                        var prompt = Program.Arguments.Contains("--help") ? CreateUserGuide(helpTopic >= 0 && helpTopic + 1 < Program.Arguments.Length ? Program.Arguments[helpTopic + 1] : CurrentGuideTopic())
                            : Program.Arguments.Contains("--knob-cycle-options") ? CreateKnobCycleOptionsWindow(_state.Layer)
                            : Program.Arguments.Contains("--os-actions") ? CreateOsActionWindow()
                            : Program.Arguments.Contains("--device-test") ? new DeviceTestWindow(_state.Device.Id, null, preview: true)
                            : Program.Arguments.Contains("--settings") ? CreateSettingsWindow(_preferences, offlinePreview: true)
                            : Program.Arguments.Contains("--device-setup") ? CreateOfflineSetupPreview() : Program.Arguments.Contains("--picker") ? CreateColorPicker("#EF646B") : CreatePrompt("Ungespeichertes Profil", L.F($"Änderungen an „{_state.Project.Name}“ zuerst speichern?"), "Abbrechen", "Profil speichern", "Verwerfen");
                        if ((Program.Arguments.Contains("--help") || Program.Arguments.Contains("--settings") || Program.Arguments.Contains("--device-test")) && Program.Arguments.Contains("--minimum")) { prompt.Width = prompt.MinWidth; prompt.Height = prompt.MinHeight; }
                        prompt.Opened += async (_, _) =>
                        {
                            if (Program.Arguments.Contains("--knob-compact")) ConstrainKnobDialog(prompt, 1280 / 1.5, 720 / 1.5);
                            try
                            {
                                await Task.Delay(200);
                                if (Program.Arguments.Contains("--knob-scroll-bottom") && prompt.Content is Grid knobLayout && knobLayout.Children.FirstOrDefault() is ScrollViewer knobScroll)
                                { knobScroll.Offset = new Vector(0, knobScroll.Extent.Height); prompt.UpdateLayout(); }
                                using var captured = new RenderTargetBitmap(new PixelSize((int)prompt.ClientSize.Width, (int)prompt.ClientSize.Height), new Vector(96, 96)); captured.Render(prompt); captured.Save(Program.Arguments[shot + 1], PngBitmapEncoderOptions.Default);
                            }
                            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                            finally { prompt.Close(); }
                        };
                        await prompt.ShowDialog(this); return;
                    }
                    var page = Array.IndexOf(Program.Arguments, "--page");
                    if (page >= 0 && page + 1 < Program.Arguments.Length) _page = Program.Arguments[page + 1];
                    if (Program.Arguments.Contains("--sample-key"))
                    {
                        var sample = _state.Device.Keys.FirstOrDefault(key => key.DefaultBinding == (_page == "Ebenen" ? "MO(1)" : "KC_A"))
                            ?? _state.Device.Keys.First(key => !key.IsEncoder && key.Label != "Knob");
                        _state.SelectKey(sample.Id, false);
                    }
                    if (Program.Arguments.Contains("--slider") && _state.Device.Id == "numpad") { _page = "Tastenbelegung"; _editingSlider = true; }
                    if ((Program.Arguments.Contains("--knob-presets") || Program.Arguments.Contains("--knob-manual")) && _state.Device.HasEncoder)
                    {
                        _page = "Tastenbelegung"; _editingSlider = false; _state.SelectKey("encoder:ccw", false);
                        KnobPresets.Apply(_state.Layer, "brightness"); _state.Layer.KnobCycleEnabled = true;
                        if (Program.Arguments.Contains("--knob-press-by-function"))
                        {
                            _state.Layer.KnobPressByFunction = true;
                            KnobPresets.SetPressBinding(_state.Layer, 1, "KC_MUTE");
                            KnobPresets.SetPressBinding(_state.Layer, 4, "KC_MPLY");
                        }
                        if (Program.Arguments.Contains("--knob-both")) _state.SelectKey("encoder:cw", true);
                        _knobModeState = _state; _knobModeLayer = _state.LayerIndex; _knobManual = Program.Arguments.Contains("--knob-manual");
                    }
                    if (Program.Arguments.Contains("--effects")) _showEffects = true;
                    if (Program.Arguments.Contains("--standby")) _page = "Gerät";
                    if (Program.Arguments.Contains("--sides")) _state.Surface = "left";
                    if (Program.Arguments.Contains("--badge")) { _state.SelectBadge(); _globalEffect = false; }
                    if (Program.Arguments.Contains("--preset")) { var candidate = LightingPresetStore.Apply(_state.Project, _state.LayerIndex, LightingPresetStore.CreateInGlorious(_state.Device.Id)); _state.Change(p => p.Layers = candidate.Layers); _showEffects = true; }
                    if (Program.Arguments.Contains("--global-off")) { _page = "Beleuchtung"; _showEffects = _globalEffect = true; _state.Layer.LightingEffect = GlobalEffectSwitch.Disable(_state.Layer.LightingEffect); }
                    Build();
                    await Task.Delay(250); UpdateLayout();
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)ClientSize.Width, (int)ClientSize.Height), new Vector(96, 96));
                    bitmap.Render(this); bitmap.Save(Program.Arguments[shot + 1], PngBitmapEncoderOptions.Default);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex); Environment.ExitCode = 1;
                var failedSmoke = Array.IndexOf(Program.Arguments, "--smoke");
                if (failedSmoke >= 0 && failedSmoke + 1 < Program.Arguments.Length)
                    try { await File.WriteAllTextAsync(Program.Arguments[failedSmoke + 1], "FAILED\n" + ex); }
                    catch (Exception reportError) { Console.Error.WriteLine(reportError); }
            }
            finally { Close(); }
            return;
        }
        await LoadStartupDevicesAsync();
        if (_startupWindowClosed || _allowClose) return;
        await LoadInitialConnectionsAsync();
        if (_startupWindowClosed || _allowClose) return;
        if (IsVisible && !_preferences.SharedBrightnessNoticeDismissed)
        {
            var result = await Ask("Gemeinsames Dimmen", L.T("Gemeinsames Dimmen von GMMK Numpad und Tastatur benötigt INGLORIOUS Advanced im Hintergrund. Nur das GMMK Numpad dimmt auch ohne laufende App. Farben, Effekte und Belegungen bleiben auf den Geräten gespeichert."), "Schließen", "Verstanden");
            if (result == 1)
            {
                try { var session = AppPreferencesStore.BeginEdit(); _preferences = session.Preferences with { SharedBrightnessNoticeDismissed = true }; AppPreferencesStore.Save(session, _preferences); }
                catch (Exception ex) { Status(ex.Message); }
            }
        }
        _sharedBrightness.StatusChanged += value => Dispatcher.UIThread.Post(() => { if (!_busy && value.Length > 0) Status(value); });
        await UpdateBrightnessService();
        _ = CheckSoftwareUpdateOnStartupAsync();
        if (IsVisible) await CheckFirmwareSetupAsync();
    }
    private void SaveWindowPreferences()
    {
        if (Program.Offline) return;
        try { _preferences = AppPreferencesStore.SaveWindowState(_state.Device.Id, Width, Height); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
}
