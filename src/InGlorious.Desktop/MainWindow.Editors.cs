using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private string _lastSide = "left";
    private bool _editingSlider;
    private bool _showStandby;
    private async Task ShowLightingSectionAsync(string section)
    {
        if (!await GuardNavigation()) return;
        _showStandby = section == "Standby"; _showEffects = section == "Effekte";
        if (_showEffects) _globalEffect = _state.SelectedLeds().Length == 0;
        Build();
    }
    private async Task ShowBindingEditorAsync(bool slider)
    {
        if (!await GuardNavigation()) return;
        _page = "Tastenbelegung"; _editingSlider = slider && _state.Device.Id == "numpad"; Build();
    }
    private static NumericUpDown Number(int value, int min, int max) => new() { Value = value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private void Watch(Func<bool> apply, params Control[] inputs)
    {
        foreach (var input in inputs) input.PropertyChanged += (_, e) =>
        {
            if (_building) return;
            if (e.Property.Name is "Text" or "Value" or "SelectedIndex" or "IsChecked") _pendingApply = apply;
        };
    }
    private void ApplyEdited(Func<bool> action) { if (action()) { Changed(); Build(); } }
    private void CommitCandidate(Action<AppProject> change)
    {
        var candidate = ProjectService.Clone(_state.Project); change(candidate); OnboardSerializer.ValidateEditorProject(candidate);
        _state.Change(change);
    }
    private void BuildLightingEditor()
    {
        _editor.Children.Add(AsyncButton("Beleuchtungsvorlagen …", OpenLightingPresetsAsync));
        var sections = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var section in new[] { "Farbe", "Effekte", "Standby" })
        {
            var button = AsyncButton(section, () => ShowLightingSectionAsync(section)); button.Name = "Lighting" + (section == "Farbe" ? "Color" : section == "Effekte" ? "Effects" : "Standby") + "Tab";
            button.Margin = new Thickness(0, 0, 6, 5);
            if (section == (_showStandby ? "Standby" : _showEffects ? "Effekte" : "Farbe")) button.BorderBrush = Brush("#EF646B");
            sections.Children.Add(button);
        }
        _editor.Children.Add(sections);
        if (_showStandby) { BuildStandbyEditor(); return; }
        if (!_showEffects || !_globalEffect)
        {
            var surfaces = Row(AsyncButton("Tasten", async () => { if (!await GuardNavigation()) return; _state.Surface = "keys"; _globalEffect = false; Build(); }),
                AsyncButton("Seitenleisten", async () => { if (!await GuardNavigation()) return; _state.Surface = _lastSide; _globalEffect = false; Build(); }));
            if (_state.Device.BadgeLedIndices.Count > 0)
            {
                var badge = AsyncButton("Logo", async () => { if (!await GuardNavigation()) return; _state.SelectBadge(); _globalEffect = false; Build(); });
                badge.Name = "BadgeLightButton"; if (_state.Surface == "badge") badge.BorderBrush = Brush("#EF646B"); surfaces.Children.Add(badge);
            }
            _editor.Children.Add(surfaces);
        }
        var targets = new WrapPanel();
        foreach (var (id, label) in new[] { ("left", "Links"), ("right", "Rechts"), ("both", "Beide") })
        {
            var button = AsyncButton(label, async () => { if (!await GuardNavigation()) return; _lastSide = id; _state.Surface = id; _globalEffect = false; Build(); });
            button.Margin = new Thickness(0, 0, 5, 5); if (_state.Surface == id) button.BorderBrush = Brush("#EF646B"); targets.Children.Add(button);
        }
        if (_state.Surface is not ("keys" or "badge") && (!_showEffects || !_globalEffect))
        {
            _editor.Children.Add(targets);
            var whole = new CheckBox { Content = L.T("Ganze Leiste"), IsChecked = _state.Surface != "individual" };
            whole.Click += async (_, _) =>
            {
                var next = whole.IsChecked == true; if (!await GuardNavigation()) { whole.IsChecked = _state.Surface != "individual"; return; }
                if (next) _state.Surface = _lastSide;
                else { var selected = _state.SelectedLeds(); _state.Surface = "individual"; _state.SelectedSideLeds.Clear(); _state.SelectedSideLeds.UnionWith(selected); }
                Build();
            };
            _editor.Children.Add(whole);
            var sync = new CheckBox { Content = L.T("Seiten synchronisieren"), IsChecked = _state.Layer.SynchronizeSides };
            sync.Click += async (_, _) => { var next = sync.IsChecked == true; if (!await GuardNavigation()) { sync.IsChecked = _state.Layer.SynchronizeSides; return; } _state.SetSideSync(next); Changed(); Build(); }; _editor.Children.Add(sync);
            _editor.Children.Add(Text(L.T("Einzelne LEDs direkt an den Seiten der Vorschau auswählen."), true, 11));
        }
        if (_showEffects) { BuildEffectsEditor(); return; }
        var selected = _state.SelectedLeds(); var current = selected.Length == 0 ? "#FFFFFF" : _state.ColorAt(selected[0]);
        var hex = Input(current, "#RRGGBB"); hex.Name = "ColorHex";
        bool ApplyColor() { _state.SetColor(ColorPaletteStore.NormalizeColor(hex.Text ?? "")); return true; }
        Watch(ApplyColor, hex);
        var chip = new Border { Width = 42, Height = 34, CornerRadius = new CornerRadius(2), Background = Brush(current) };
        var chipLabel = Text(current); chipLabel.VerticalAlignment = VerticalAlignment.Center;
        var preview = AsyncButton("Farbwähler …", async () =>
        {
            if (!await GuardNavigation()) return;
            var picked = await PickColorAsync(ColorPaletteStore.NormalizeColor(hex.Text ?? current)); if (picked is null) return;
            _state.SetColor(picked); Changed(); Build();
        });
        preview.Name = "ColorPreview"; preview.Content = Row(chip, chipLabel); preview.HorizontalContentAlignment = HorizontalAlignment.Left;
        preview.HorizontalAlignment = HorizontalAlignment.Stretch; preview.Padding = new Thickness(8); preview.Height = 52;
        Avalonia.Automation.AutomationProperties.SetName(preview, L.T("Farbwähler …")); ToolTip.SetTip(preview, L.T("Farbwähler …"));
        _editor.Children.Add(preview);
        var colorEntry = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        colorEntry.Children.Add(hex); var applyColor = Button("Übernehmen", () => ApplyEdited(ApplyColor), true); Grid.SetColumn(applyColor, 1); colorEntry.Children.Add(applyColor); _editor.Children.Add(colorEntry);
        var swatches = new Grid { Name = "FavoriteColors", ColumnDefinitions = new ColumnDefinitions("36,36,36,36,36,36"), ColumnSpacing = 8, RowSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        string? InputColor() { try { return ColorPaletteStore.NormalizeColor(hex.Text ?? ""); } catch (InvalidDataException) { return null; } }
        Button add = null!, remove = null!;
        add = ColorToolButton("Als Favorit speichern", "M10 2 L12 7 L18 7 L13 11 L15 17 L10 13 L5 17 L7 11 L2 7 L8 7 Z M19 15 L19 23 M15 19 L23 19", () => { if (InputColor() is { } color) { UpdateFavorites(color, true); BuildSwatches(); } });
        add.Name = "AddFavorite";
        remove = ColorToolButton("Favorit entfernen", "M4 6 L20 6 M8 6 L8 3 L16 3 L16 6 M6 6 L7 22 L17 22 L18 6 M10 10 L10 18 M14 10 L14 18", () => { if (InputColor() is { } color) { UpdateFavorites(color, false); BuildSwatches(); } });
        remove.Name = "RemoveFavorite";
        void BuildSwatches()
        {
            swatches.Children.Clear(); swatches.RowDefinitions.Clear();
            var inputColor = InputColor(); add.IsEnabled = inputColor is not null && !_favorites.Contains(inputColor); remove.IsEnabled = inputColor is not null && _favorites.Contains(inputColor);
            if (inputColor is not null) { chip.Background = Brush(inputColor); chipLabel.Text = inputColor; }
            for (var i = 0; i < _favorites.Count; i++)
            {
                if (i % 6 == 0) swatches.RowDefinitions.Add(new RowDefinition(36, GridUnitType.Pixel));
                var color = _favorites[i]; var chosen = color == inputColor;
                var swatch = new Button { Width = 36, Height = 36, MinHeight = 0, BorderBrush = Brush(chosen ? "#EF646B" : "#626971"), BorderThickness = new Thickness(chosen ? 2 : 1), Padding = new Thickness(3), Content = new Border { Background = Brush(color), CornerRadius = new CornerRadius(1) } };
                ToolTip.SetTip(swatch, color); Avalonia.Automation.AutomationProperties.SetName(swatch, color);
                swatch.Click += async (_, _) => { if (!await GuardNavigation()) return; Run(() => { _state.SetColor(color); Changed(); Build(); }); };
                var menuRemove = new MenuItem { Header = L.T("Favorit entfernen") };
                menuRemove.Click += (_, _) => Run(() => { UpdateFavorites(color, false); BuildSwatches(); }); swatch.ContextMenu = new ContextMenu { ItemsSource = new[] { menuRemove } };
                Grid.SetColumn(swatch, i % 6); Grid.SetRow(swatch, i / 6); swatches.Children.Add(swatch);
            }
        }
        var favoritesHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        var favoritesLabel = Text(L.T("Favoriten")); favoritesLabel.VerticalAlignment = VerticalAlignment.Center; favoritesLabel.FontWeight = FontWeight.SemiBold;
        favoritesHeader.Children.Add(favoritesLabel); Grid.SetColumn(add, 1); favoritesHeader.Children.Add(add); Grid.SetColumn(remove, 2); favoritesHeader.Children.Add(remove);
        _editor.Children.Add(favoritesHeader); BuildSwatches(); hex.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) BuildSwatches(); };
        _editor.Children.Add(swatches);
        if (_preferences.ShowHelp) _editor.Children.Add(Text(L.T("Rechtsklick auf eine Farbe entfernt den Favoriten."), true, 11));
    }
    private Button ColorToolButton(string source, string geometry, Action action)
    {
        var button = Button(source, action); button.Width = button.Height = 32; button.MinHeight = 0; button.Padding = new Thickness(6);
        button.Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = Brush("#E4E7EB"), StrokeThickness = 1.7, Width = 18, Height = 18, Stretch = Stretch.Uniform };
        ToolTip.SetTip(button, L.T(source)); Avalonia.Automation.AutomationProperties.SetName(button, L.T(source)); return button;
    }
    private void UpdateFavorites(string color, bool add)
    {
        var updated = Program.Offline ? (add ? _favorites.Append(color).Distinct().ToArray() : _favorites.Where(c => c != color).ToArray())
            : add ? ColorPaletteStore.Add(ColorPaletteStore.DefaultPath, color) : ColorPaletteStore.Remove(ColorPaletteStore.DefaultPath, color);
        _favorites.Clear(); _favorites.AddRange(updated);
    }
    private async Task<string?> PickColorAsync(string color)
    {
        if (Program.Offline) return null;
        return await CreateColorPicker(color).ShowDialog<string?>(this);
    }
    private Window CreateColorPicker(string color)
    {
        var dialog = new Window { Title = L.T("Farbe auswählen"), Width = 470, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var picker = new ColorView { Color = Color.Parse(color), IsAlphaEnabled = false, IsAlphaVisible = false, IsColorPaletteVisible = false };
        var apply = Button("Übernehmen", () => dialog.Close($"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}"), true);
        dialog.Content = new Border { Padding = new Thickness(18), Child = Column(picker, Row(Button("Abbrechen", () => dialog.Close()), apply)) };
        return dialog;
    }
    private void BuildEffectsEditor()
    {
        _editor.Children.Add(Row(AsyncButton("Gesamte Ebene", async () => { if (!await GuardNavigation()) return; _globalEffect = true; Build(); }),
            AsyncButton("Auswahl", async () => { if (!await GuardNavigation()) return; _globalEffect = false; Build(); })));
        var effect = _state.Layer.LightingEffect;
        if (!_globalEffect && _state.SelectedLeds().FirstOrDefault(-1) is var led && _state.Layer.LedEffects.TryGetValue(led, out var own))
            effect = new() { Mode = own.Mode, Speed = own.Speed, Brightness = own.Brightness, Reverse = own.Reverse, Colors = [.. own.Colors] };
        _editor.Children.Add(Text(L.T(_globalEffect ? "Gesamte Ebene" : "Auswahl"), true));
        var mode = Choice(LightingEffects.All.Select(e => e.Label), LightingEffects.All.ToList().FindIndex(e => e.Id == effect.Mode));
        var speed = Number(effect.Speed, 0, 255); var brightness = Number(effect.Brightness, 0, 255);
        var reverse = new CheckBox { Content = L.T("Richtung umkehren"), IsChecked = effect.Reverse };
        var target = Choice(new[] { L.T("Tasten"), L.T("Seitenleisten"), L.T("Alle LEDs") }, effect.Target - 1); target.IsVisible = _globalEffect;
        var colors = Input(string.Join(", ", effect.Colors), "#FF0000, #00FF00, #0000FF"); colors.AcceptsReturn = true; colors.MinHeight = 64;
        var sequencePanel = Column(Heading("Farbfolge"), colors, Text(L.T("Bis zu 8 Farben. Leer verwendet den normalen Farbwechsel."), true, 11));
        var description = Text("", true, 11);
        void UpdateEffectControls()
        {
            if (mode.SelectedIndex < 0) return;
            var option = LightingEffects.All[mode.SelectedIndex]; reverse.IsEnabled = option.HasDirection;
            sequencePanel.IsVisible = option.Id == 2; description.Text = option.Description;
        }
        mode.SelectionChanged += (_, _) => UpdateEffectControls(); UpdateEffectControls();
        bool ApplyEffect()
        {
            var value = new LightingEffectSettings { Mode = LightingEffects.All[mode.SelectedIndex].Id, Speed = (int)(speed.Value ?? 128), Brightness = (int)(brightness.Value ?? 255), Reverse = reverse.IsChecked == true, Target = target.SelectedIndex + 1,
                Colors = (colors.Text ?? "").Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(ColorPaletteStore.NormalizeColor).ToList() };
            var test = new EditorState(_state.Project, null) { LayerIndex = _state.LayerIndex, Surface = _state.Surface }; test.SelectedKeys.UnionWith(_state.SelectedKeys); test.SelectedSideLeds.UnionWith(_state.SelectedSideLeds);
            test.SetEffect(value, _globalEffect); OnboardSerializer.ValidateEditorProject(test.Project);
            _state.SetEffect(value, _globalEffect); return true;
        }
        Watch(ApplyEffect, mode, speed, brightness, reverse, target, colors);
        _editor.Children.Add(mode); _editor.Children.Add(description); _editor.Children.Add(Heading("Geschwindigkeit")); _editor.Children.Add(speed);
        _editor.Children.Add(Heading("Helligkeit")); _editor.Children.Add(brightness); _editor.Children.Add(reverse); _editor.Children.Add(target);
        _editor.Children.Add(sequencePanel);
        _editor.Children.Add(Button("Übernehmen", () => ApplyEdited(ApplyEffect), true));
        if (!_globalEffect) _editor.Children.Add(AsyncButton("Globalen Effekt übernehmen", async () =>
        {
            if (!await GuardNavigation()) return; var leds = _state.SelectedLeds(); _state.Change(p => { foreach (var id in leds) p.Layers[_state.LayerIndex].LedEffects.Remove(id); }); Changed(); Build();
        }));
        _editor.Children.Add(Button(_previewRunning ? "Vorschau pausieren" : "Vorschau fortsetzen", () => { _previewRunning = !_previewRunning; Build(); }));
    }
    private Control BuildBindingFileActions()
    {
        var import = AsyncButton("Importieren …", ImportKeyBindingsAsync); import.Name = "ImportKeyBindings";
        var export = AsyncButton("Exportieren …", ExportKeyBindingsAsync); export.Name = "ExportKeyBindings";
        var files = new WrapPanel { Orientation = Orientation.Horizontal };
        import.Margin = new Thickness(0, 0, 7, 5); export.Margin = new Thickness(0, 0, 0, 5);
        files.Children.Add(import); files.Children.Add(export);
        return new Border { BorderBrush = Brush("#353A40"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12, 16, 7), Child = files };
    }
    private void BuildBindingEditor()
    {
        if (_state.Device.Id == "numpad")
        {
            var keys = AsyncButton("Tasten", () => ShowBindingEditorAsync(false)); keys.Name = "BindingKeysTab";
            var slider = AsyncButton("Schieberegler", () => ShowBindingEditorAsync(true)); slider.Name = "BindingSliderTab";
            ( _editingSlider ? slider : keys ).BorderBrush = Brush("#EF646B");
            _editor.Children.Add(Row(keys, slider));
            if (_editingSlider) { BuildSliderEditor(); return; }
        }
        var search = Input("", L.T("Belegung suchen")); var list = new ListBox { Height = 230 };
        void Filter() { list.ItemsSource = BindingCatalog.All.Where(b => b.Label.Contains(search.Text ?? "", StringComparison.CurrentCultureIgnoreCase) || b.Code.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).Select(b => new BindingRow(b.Code, b.Label)).ToArray(); }
        search.TextChanged += (_, _) => Filter(); Filter();
        _editor.Children.Add(search); _editor.Children.Add(list);
        _editor.Children.Add(Button("Zuweisen", () => { if (list.SelectedItem is not BindingRow binding) return; _state.SetBinding(binding.Code); Changed(); }, true));
        _editor.Children.Add(Heading("Ebenenwechsel"));
        var layer = Choice(Enumerable.Range(0, 4).Select(i => L.F($"Ebene {i}")), 1);
        var layerMode = Choice(new[] { L.T("Halten"), L.T("Umschalten"), L.T("Wechseln") });
        _editor.Children.Add(Row(layer, layerMode));
        _editor.Children.Add(Button("Zuweisen", () => { _state.SetBinding(new[] { "MO", "TG", "TO" }[layerMode.SelectedIndex] + "(" + layer.SelectedIndex + ")"); Changed(); }));
        _editor.Children.Add(Heading("Systemaktionen"));
        var system = Choice(new[] { "Windows", "macOS", "Linux" }, Array.IndexOf(new[] { "windows", "macos", "linux" }, OsActionCatalog.CurrentSystem));
        var layout = Choice(new[] { "QWERTZ · DE", "QWERTY · EN", "AZERTY · FR" });
        var actionChoice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        OsAction[] actions = [];
        void Reload() { actions = OsActionCatalog.For(new[] { "windows", "macos", "linux" }[system.SelectedIndex], new[] { "de", "en", "fr" }[layout.SelectedIndex]).ToArray(); actionChoice.ItemsSource = actions.Select(a => a.Label).ToArray(); actionChoice.SelectedIndex = 0; }
        system.SelectionChanged += (_, _) => Reload(); layout.SelectionChanged += (_, _) => Reload(); Reload();
        _editor.Children.Add(system); _editor.Children.Add(layout); _editor.Children.Add(actionChoice);
        _editor.Children.Add(Button("Zuweisen", () => { _state.AssignOsAction(actions[actionChoice.SelectedIndex]); Changed(); }));
        _editor.Children.Add(Row(Button("Durchreichen", () => { _state.SetBinding("KC_TRNS"); Changed(); }), Button("Deaktivieren", () => { _state.SetBinding("KC_NO"); Changed(); })));
    }
    private sealed record BindingRow(string Code, string Label) { public override string ToString() => Label; }
    private void BuildLayerEditor()
    {
        var name = Input(_state.Layer.Name);
        bool Rename() { var value = name.Text?.Trim(); if (string.IsNullOrEmpty(value)) throw new InvalidDataException(L.T("Bitte einen Namen eingeben.")); _state.Change(p => p.Layers[_state.LayerIndex].Name = value); return true; }
        Watch(Rename, name); _editor.Children.Add(Heading("Name")); _editor.Children.Add(name); _editor.Children.Add(Button("Übernehmen", () => ApplyEdited(Rename)));
        var destination = Choice(Enumerable.Range(0, 4).Select(i => L.F($"Ebene {i}")), (_state.LayerIndex + 1) % 4);
        _editor.Children.Add(Heading("Ebene kopieren")); _editor.Children.Add(destination);
        _editor.Children.Add(AsyncButton("Kopieren", async () => { if (!await GuardNavigation()) return; _state.CopyLayer(destination.SelectedIndex); Changed(); Build(); }));
        _editor.Children.Add(Heading("Ebene vergleichen"));
        var compare = Choice(Enumerable.Range(0, 4).Select(i => L.F($"Ebene {i}")), (_state.LayerIndex + 1) % 4); var result = Text("", true);
        void Compare() { var other = _state.Project.Layers[compare.SelectedIndex]; var count = _state.Layer.Keys.Count(k => other.Keys[k.Key].Binding != k.Value.Binding || other.Keys[k.Key].Color != k.Value.Color); result.Text = L.F($"{count} unterschiedliche Tasten"); }
        compare.SelectionChanged += (_, _) => Compare(); Compare(); _editor.Children.Add(compare); _editor.Children.Add(result);
    }
    private Control BuildDeviceWorkspace()
    {
        var body = _state.Device.IsOfflineOnly
            ? Column(Heading("Gerät"), Text(_state.Device.Name, size: 22), Heading("Offline-Profil"), BuildFirmwareVersionPanel())
            : Column(Heading("Gerät"), Text(_state.Device.Name, size: 22), Text(_state.Device.Subtitle, true),
            Heading("Firmware"), BuildFirmwareVersionPanel(),
            AsyncButton("Gerät einrichten", () => OpenDeviceSetupAsync(_state.Device.Id)),
            Heading("Original-Firmware wiederherstellen"),
            Text(L.T("Die Wiederherstellung verwendet ausschließlich die passende Original-Sicherung dieses Geräts."), true),
            AsyncButton("Original-Firmware wiederherstellen", () => OpenDeviceSetupAsync(_state.Device.Id, SetupOperation.Restore)));
        if (_state.Device.IsExperimentalFirmware)
            body.Children.Insert(2, new TextBlock { Name = "ExperimentalDeviceNotice", Text = L.T("Experimentelle Unterstützung: noch nicht an diesem Modell getestet."), Foreground = Brush("#E9B86B"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        return body;
    }
    private void BuildDeviceEditor()
    {
        var polling = Choice(new[] { "1000 Hz · 1 ms", "500 Hz · 2 ms", "250 Hz · 4 ms", "125 Hz · 8 ms" }, Array.IndexOf(new[] { 1, 2, 4, 8 }, _state.Project.DeviceSettings.UsbPollingIntervalMs));
        polling.Name = "PollingInterval";
        var debounce = Number(_state.Project.DeviceSettings.DebounceMs, 0, 40); debounce.Name = "DebounceMs";
        _editor.Children.Add(Heading("USB-Abfragerate")); _editor.Children.Add(polling); _editor.Children.Add(Heading("Entprellzeit (ms)")); _editor.Children.Add(debounce);
        bool ApplySettings()
        {
            CommitCandidate(p => p.DeviceSettings = new() { UsbPollingIntervalMs = new[] { 1, 2, 4, 8 }[polling.SelectedIndex], DebounceMs = (int)(debounce.Value ?? 5) }); return true;
        }
        Watch(ApplySettings, polling, debounce);
        _editor.Children.Add(Button("Übernehmen", () => ApplyEdited(ApplySettings), true));
        _editor.Children.Add(Text(L.T("Eine geänderte USB-Abfragerate wird nach der USB-Neuanmeldung aktiv."), true));
    }
    private void BuildSliderEditor()
    {
        var slider = _state.Project.Slider;
        var mode = Choice(new[] { L.T("Deaktiviert"), "MIDI CC", L.T("Lautstärke"), L.T("Beleuchtung dimmen") }, slider.Mode);
        var minimum = Number(slider.Minimum, 0, 1023); var maximum = Number(slider.Maximum, 0, 1023); var channel = Number(slider.MidiChannel + 1, 1, 16); var cc = Number(slider.MidiController, 0, 127);
        mode.Name = "SliderMode"; minimum.Name = "SliderMinimum"; maximum.Name = "SliderMaximum"; channel.Name = "SliderChannel"; cc.Name = "SliderController";
        var modeHint = Text("", true, 11); modeHint.Name = "SliderModeHint";
        var midi = new Grid { Name = "SliderMidiFields", ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        midi.Children.Add(Column(Text(L.T("Kanal"), true), channel));
        var controllerField = Column(Text(L.T("Controller"), true), cc); Grid.SetColumn(controllerField, 1); midi.Children.Add(controllerField);
        var bounds = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        bounds.Children.Add(Column(Text(L.T("Minimum"), true), minimum));
        var maximumField = Column(Text(L.T("Maximum"), true), maximum); Grid.SetColumn(maximumField, 1); bounds.Children.Add(maximumField);
        var range = new Expander
        {
            Name = "SliderRange", Header = L.T("Reglerbereich"), IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Column(Text(L.T("Nur anpassen, wenn der Regler nicht den gesamten Bereich erreicht."), true, 11), bounds)
        };
        var dimming = Column(Text(L.T(SharedBrightnessService.Notice), true, 11), Text(L.T("Einstellungen") + " → " + L.T("Gemeinsames Dimmen"), true, 11));
        dimming.Name = "SliderDimmingHint";
        var revealRangeError = false;
        void UpdateControls()
        {
            midi.IsVisible = mode.SelectedIndex == 1;
            dimming.IsVisible = mode.SelectedIndex == 3;
            range.IsVisible = mode.SelectedIndex != 0 || revealRangeError;
            modeHint.Text = L.T(mode.SelectedIndex switch
            {
                1 => "Steuert einen MIDI-Controller in einer kompatiblen Anwendung.",
                2 => "Ändert die Lautstärke durch Bewegen des Reglers.",
                3 => "Regelt die Helligkeit der GMMK Numpad-Beleuchtung.",
                _ => "Keine Aktion beim Bewegen des Reglers."
            });
        }
        mode.SelectionChanged += (_, _) => UpdateControls(); UpdateControls();
        _editor.Children.Add(Heading("Funktion")); _editor.Children.Add(mode); _editor.Children.Add(modeHint);
        _editor.Children.Add(midi); _editor.Children.Add(dimming); _editor.Children.Add(range);
        bool ApplySlider()
        {
            var min = (int)(minimum.Value ?? 0); var max = (int)(maximum.Value ?? 1023);
            if (min is < 0 or > 1022 || max <= min || max > 1023)
            {
                revealRangeError = true; UpdateControls(); range.IsExpanded = true;
                Status(L.T("Kalibrierung: Minimum und Maximum müssen zwischen 0 und 1023 liegen; Maximum muss größer sein."));
                return false;
            }
            CommitCandidate(p => p.Slider = new() { Mode = mode.SelectedIndex, Minimum = min, Maximum = max, MidiChannel = (int)(channel.Value ?? 1) - 1, MidiController = (int)(cc.Value ?? 62) }); return true;
        }
        Watch(ApplySlider, mode, minimum, maximum, channel, cc);
        var apply = Button("Regler übernehmen", () => ApplyEdited(ApplySlider), true); apply.Name = "SliderApply";
        _editor.Children.Add(apply); _editor.Children.Add(Text(L.T("Eine Funktion für alle Ebenen."), true, 11));
    }
    private void BuildStandbyEditor()
    {
        var value = _state.Project.Standby;
        var enabled = new CheckBox { Name = "StandbyEnabled", Content = L.T("Standby aktivieren"), IsChecked = value.Enabled };
        var presets = new[] { 30, 60, 120, 300, 600, 1800, 3600 };
        var preset = Choice(new[] { L.T("30 Sekunden"), L.T("1 Minute"), L.T("2 Minuten"), L.T("5 Minuten"), L.T("10 Minuten"), L.T("30 Minuten"), L.T("60 Minuten"), L.T("Eigene Zeit") }, Array.IndexOf(presets, value.TimeoutSeconds) is var index && index >= 0 ? index : presets.Length);
        preset.Name = "StandbyDelayPreset";
        var timeout = Number(value.TimeoutSeconds, 10, 3600); timeout.Name = "StandbyTimeoutSeconds";
        var customTime = Column(Text(L.T("Sekunden"), true), timeout); customTime.IsVisible = preset.SelectedIndex == presets.Length;
        var mode = Choice(new[] { L.T("Aus"), L.T("Dimmen"), L.T("Screensaver") }, value.Mode); mode.Name = "StandbyMode";
        var brightness = Number(value.DimBrightnessPercent, 1, 100); brightness.Name = "StandbyBrightness";
        var dimming = Column(Heading("Helligkeit (%)"), brightness);
        var screensaverHint = Text(L.T("Nach der Wartezeit wiederholt sich die vollständige Lichtsequenz bis zur nächsten Eingabe."), true, 11); screensaverHint.Name = "StandbyScreensaverHint";
        var settings = Column(Heading("Wartezeit"), preset, customTime, Text(L.T("Nach der letzten Eingabe an diesem Gerät."), true, 11), Heading("Aktion"), mode, screensaverHint, dimming);
        void UpdateControls() { settings.IsEnabled = enabled.IsChecked == true; dimming.IsVisible = mode.SelectedIndex == 1; screensaverHint.IsVisible = mode.SelectedIndex == 2; }
        preset.SelectionChanged += (_, _) => { customTime.IsVisible = preset.SelectedIndex == presets.Length; if (preset.SelectedIndex >= 0 && preset.SelectedIndex < presets.Length) timeout.Value = presets[preset.SelectedIndex]; };
        mode.SelectionChanged += (_, _) => UpdateControls(); enabled.IsCheckedChanged += (_, _) => UpdateControls(); UpdateControls();
        bool ApplyStandby()
        {
            CommitCandidate(p => p.Standby = new LightingStandbySettings { Enabled = enabled.IsChecked == true, TimeoutSeconds = (int)(timeout.Value ?? value.TimeoutSeconds), Mode = mode.SelectedIndex, DimBrightnessPercent = (int)(brightness.Value ?? value.DimBrightnessPercent) }); return true;
        }
        Watch(ApplyStandby, enabled, preset, timeout, mode, brightness);
        _editor.Children.Add(enabled); _editor.Children.Add(settings);
        _editor.Children.Add(Button("Übernehmen", () => ApplyEdited(ApplyStandby), true));
        _editor.Children.Add(Text(L.T("Gilt für das gesamte Gerät und läuft ohne geöffnete App."), true, 11));
        _editor.Children.Add(Text(L.T("Tasten, Drehknopf oder Schieberegler wecken die Beleuchtung wieder auf."), true, 11));
        _editor.Children.Add(Text(L.T("Im USB-Ruhezustand bleibt die Beleuchtung aus."), true, 11));
        _editor.Children.Add(Text(L.T("Standby benötigt passende INGLORIOUS-Firmware."), true, 11));
    }
}
