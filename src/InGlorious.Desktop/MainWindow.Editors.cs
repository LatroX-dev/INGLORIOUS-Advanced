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
    private async Task ShowLightingSectionAsync(string section)
    {
        if (!await GuardNavigation()) return;
        _page = "Beleuchtung"; _showEffects = section == "Effekte";
        if (_showEffects) _globalEffect = _state.SelectedLeds().Length == 0;
        Build();
    }
    private async Task ShowPageAsync(string page)
    {
        if (!await GuardNavigation()) return;
        _page = page; Build();
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
        var body = new StackPanel { Spacing = 9, Margin = new Thickness(0, 10, 0, 0) };
        var colors = new TabItem { Name = "LightingColorTab", Header = L.T("Farben") };
        var effects = new TabItem { Name = "LightingEffectsTab", Header = L.T("Effekte") };
        (_showEffects ? effects : colors).Content = body;
        var tabs = new TabControl { Name = "LightingTabs", HorizontalContentAlignment = HorizontalAlignment.Stretch, Items = { colors, effects }, SelectedIndex = _showEffects ? 1 : 0 };
        tabs.SelectionChanged += async (_, e) =>
        {
            if (_building || !ReferenceEquals(e.Source, tabs)) return;
            var selected = tabs.SelectedIndex;
            if (selected == (_showEffects ? 1 : 0)) return;
            await ShowLightingSectionAsync(selected == 1 ? "Effekte" : "Farbe");
            if (ReferenceEquals(Content, _root) && tabs.SelectedIndex != (_showEffects ? 1 : 0)) tabs.SelectedIndex = _showEffects ? 1 : 0;
        };
        _editor.Children.Add(tabs);
        var outerEditor = _editor;
        try { _editor = body; BuildLightingSettings(); }
        finally { _editor = outerEditor; }
    }
    private void BuildLightingSettings()
    {
        if (_showEffects)
        {
            var global = AsyncButton("Global", async () => { if (!await GuardNavigation()) return; _globalEffect = true; Build(); }); global.Name = "GlobalEffectScope";
            var selectionScope = AsyncButton("Auswahl", async () => { if (!await GuardNavigation()) return; _globalEffect = false; Build(); }); selectionScope.Name = "SelectedEffectScope";
            var scope = new Grid { Name = "EffectScope", ColumnDefinitions = new("*,*"), ColumnSpacing = 6 };
            foreach (var button in new[] { global, selectionScope }) { button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center; }
            var active = _globalEffect ? global : selectionScope; active.BorderBrush = Brush("#B9535D"); active.Background = Brush("#3A252B");
            scope.Children.Add(global); Grid.SetColumn(selectionScope, 1); scope.Children.Add(selectionScope); _editor.Children.Add(scope);
        }
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
        var effect = _state.Layer.LightingEffect;
        if (!_globalEffect && _state.SelectedLeds().FirstOrDefault(-1) is var led && _state.Layer.LedEffects.TryGetValue(led, out var own))
            effect = new() { Mode = own.Mode, Speed = own.Speed, Brightness = own.Brightness, Reverse = own.Reverse, Colors = [.. own.Colors] };
        var enabled = new CheckBox { Name = "GlobalEffectEnabled", Content = L.T("Globaler Effekt"), IsChecked = GlobalEffectSwitch.IsEnabled(effect) };
        var offHint = Text(L.T("LEDs ohne eigenen Effekt zeigen ihre festen Farben."), true, 11); offHint.Name = "GlobalEffectDisabledHint";
        if (_globalEffect) { _editor.Children.Add(enabled); _editor.Children.Add(offHint); }
        var mode = Choice(LightingEffects.All.Select(e => e.Label), LightingEffects.All.ToList().FindIndex(e => e.Id == effect.Mode)); mode.Name = "EffectMode";
        var speed = Number(effect.Speed, 0, 255); speed.Name = "EffectSpeed";
        var brightness = Number(effect.Brightness, 0, 255); brightness.Name = "EffectBrightness";
        var reverse = new CheckBox { Content = L.T("Richtung umkehren"), IsChecked = effect.Reverse };
        var target = Choice(new[] { L.T("Tasten"), L.T("Seitenleisten"), L.T("Alle LEDs") }, effect.Target - 1); target.IsVisible = _globalEffect; target.Name = "EffectTarget";
        var colors = Input(string.Join(", ", effect.Colors), "#FF0000, #00FF00, #0000FF"); colors.AcceptsReturn = true; colors.MinHeight = 64; colors.Name = "EffectColors";
        var sequencePanel = Column(Heading("Farbfolge"), colors, Text(L.T("Bis zu 8 Farben. Leer verwendet den normalen Farbwechsel."), true, 11));
        var description = Text("", true, 11);
        var settings = Column(mode, description, Heading("Geschwindigkeit"), speed, Heading("Helligkeit"), brightness, reverse, target, sequencePanel); settings.Name = "EffectSettings";
        void UpdateEffectControls()
        {
            if (mode.SelectedIndex < 0) return;
            var option = LightingEffects.All[mode.SelectedIndex]; reverse.IsEnabled = option.HasDirection;
            sequencePanel.IsVisible = option.Id == 2; description.Text = option.Description;
            settings.IsVisible = !_globalEffect || enabled.IsChecked == true;
            offHint.IsVisible = _globalEffect && enabled.IsChecked != true;
        }
        enabled.IsCheckedChanged += (_, _) =>
        {
            if (enabled.IsChecked == true)
            {
                var resumed = GlobalEffectSwitch.Enable(new() { Mode = LightingEffects.All[mode.SelectedIndex].Id, Brightness = (int)(brightness.Value ?? 255) });
                mode.SelectedIndex = LightingEffects.All.ToList().FindIndex(option => option.Id == resumed.Mode);
                brightness.Value = resumed.Brightness;
            }
            UpdateEffectControls();
        };
        mode.SelectionChanged += (_, _) => UpdateEffectControls(); UpdateEffectControls();
        bool ApplyEffect()
        {
            var value = new LightingEffectSettings { Mode = LightingEffects.All[mode.SelectedIndex].Id, Speed = (int)(speed.Value ?? 128), Brightness = (int)(brightness.Value ?? 255), Reverse = reverse.IsChecked == true, Target = target.SelectedIndex + 1,
                Colors = (colors.Text ?? "").Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(ColorPaletteStore.NormalizeColor).ToList() };
            if (_globalEffect && enabled.IsChecked != true) value = GlobalEffectSwitch.Disable(value);
            var test = new EditorState(_state.Project, null) { LayerIndex = _state.LayerIndex, Surface = _state.Surface }; test.SelectedKeys.UnionWith(_state.SelectedKeys); test.SelectedSideLeds.UnionWith(_state.SelectedSideLeds);
            test.SetEffect(value, _globalEffect); OnboardSerializer.ValidateEditorProject(test.Project);
            _state.SetEffect(value, _globalEffect); return true;
        }
        Watch(ApplyEffect, mode, speed, brightness, reverse, target, colors, enabled);
        _editor.Children.Add(settings);
        var apply = Button("Übernehmen", () => ApplyEdited(ApplyEffect), true); apply.Name = "ApplyEffect"; _editor.Children.Add(apply);
        if (!_globalEffect) _editor.Children.Add(AsyncButton("Globalen Effekt übernehmen", async () =>
        {
            if (!await GuardNavigation()) return; var leds = _state.SelectedLeds(); _state.Change(p => { foreach (var id in leds) p.Layers[_state.LayerIndex].LedEffects.Remove(id); }); Changed(); Build();
        }));
        Button preview = null!;
        preview = Button(_previewRunning ? "Vorschau pausieren" : "Vorschau fortsetzen", () =>
        {
            _previewRunning = !_previewRunning;
            var label = _previewRunning ? "Vorschau pausieren" : "Vorschau fortsetzen";
            preview.Content = ButtonContent(label); Avalonia.Automation.AutomationProperties.SetName(preview, L.T(label));
        });
        preview.Name = "EffectPreviewToggle"; _editor.Children.Add(preview);
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
        var selectedBinding = Text(L.F($"Auf Ebene {_state.LayerIndex}: {SelectedBindingSummary()}"), true); selectedBinding.Name = "CurrentKeyBindingSummary"; _editor.Children.Add(selectedBinding);
        var layerEntry = AsyncButton("Fn / Ebenenwechsel …", OpenLayerBindingEditorAsync); layerEntry.Name = "OpenLayerBindingEditor"; _editor.Children.Add(layerEntry);
        var search = Input("", L.T("Belegung suchen")); search.Name = "BindingSearch";
        var list = new ListBox { Name = "BindingChoices", Height = 230 };
        var hint = Text("", true, 11); hint.Name = "BindingFunctionHint";
        var modifiersOnly = false;
        void UpdateHint()
        {
            var code = (list.SelectedItem as BindingRow)?.Code ?? (_state.SelectedKeys.Count == 1 ? _state.Layer.Keys[_state.SelectedKeys.Single()].Binding : "");
            hint.Text = modifiersOnly ? LayerGuidance.ModifierHint : LayerGuidance.BindingDescription(code);
            hint.IsVisible = !string.IsNullOrEmpty(hint.Text);
        }
        void Filter()
        {
            var selected = (list.SelectedItem as BindingRow)?.Code ?? (_state.SelectedKeys.Count == 1 ? _state.Layer.Keys[_state.SelectedKeys.Single()].Binding : null);
            var rows = BindingCatalog.All.Where(b => (!modifiersOnly || IsModifierBinding(b.Code)) &&
                (DisplayBinding(b.Code).Contains(search.Text ?? "", StringComparison.CurrentCultureIgnoreCase) || b.Code.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)))
                .Select(b => new BindingRow(b.Code, DisplayBinding(b.Code))).ToArray();
            list.ItemsSource = rows; list.SelectedItem = rows.FirstOrDefault(row => row.Code == selected); UpdateHint();
        }
        Button modifiers = null!;
        modifiers = Button("Strg, Shift, Alt …", () => { modifiersOnly = !modifiersOnly; modifiers.BorderBrush = Brush(modifiersOnly ? "#EF646B" : "#424950"); Filter(); }); modifiers.Name = "ModifierBindings";
        search.TextChanged += (_, _) => Filter(); list.SelectionChanged += (_, _) => UpdateHint(); Filter();
        _editor.Children.Add(modifiers); _editor.Children.Add(search); _editor.Children.Add(list); _editor.Children.Add(hint);
        var applyBinding = Button("Zuweisen", () => { if (list.SelectedItem is not BindingRow binding) return; _state.SetBinding(binding.Code); Changed(); Build(); }, true);
        applyBinding.Name = "AssignKeyBinding"; applyBinding.IsEnabled = _state.Surface == "keys" && _state.SelectedKeys.Count > 0; _editor.Children.Add(applyBinding);
        _editor.Children.Add(Heading("Systemaktionen"));
        var system = Choice(new[] { "Windows", "macOS", "Linux" }, Array.IndexOf(new[] { "windows", "macos", "linux" }, OsActionCatalog.CurrentSystem));
        var layout = Choice(new[] { "QWERTZ · DE", "QWERTY · EN", "AZERTY · FR" });
        var actionChoice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        OsAction[] actions = [];
        void Reload() { actions = OsActionCatalog.For(new[] { "windows", "macos", "linux" }[system.SelectedIndex], new[] { "de", "en", "fr" }[layout.SelectedIndex]).ToArray(); actionChoice.ItemsSource = actions.Select(a => a.Label).ToArray(); actionChoice.SelectedIndex = 0; }
        system.SelectionChanged += (_, _) => Reload(); layout.SelectionChanged += (_, _) => Reload(); Reload();
        _editor.Children.Add(system); _editor.Children.Add(layout); _editor.Children.Add(actionChoice);
        _editor.Children.Add(Button("Zuweisen", () => { _state.AssignOsAction(actions[actionChoice.SelectedIndex]); Changed(); }));
        var inherit = Button("Belegung darunter verwenden", () => { _state.SetBinding("KC_TRNS"); Changed(); Build(); });
        inherit.Name = "UseUnderlyingBinding"; inherit.IsEnabled = _state.LayerIndex > 0 && _state.Surface == "keys" && _state.SelectedKeys.Count > 0; ToolTip.SetTip(inherit, LayerGuidance.BindingDescription("KC_TRNS"));
        _editor.Children.Add(inherit); _editor.Children.Add(Button("Deaktivieren", () => { _state.SetBinding("KC_NO"); Changed(); Build(); }));
    }
    private static bool IsModifierBinding(string code) => code is "KC_LCTL" or "KC_RCTL" or "KC_LSFT" or "KC_RSFT" or "KC_LALT" or "KC_RALT" or "KC_LGUI" or "KC_RGUI";
    private string DisplayBinding(string binding)
    {
        if (binding.StartsWith("MACRO:", StringComparison.Ordinal)) return _state.Project.Macros.FirstOrDefault(macro => macro.Id == binding[6..])?.Name ?? L.T("Makros");
        var label = BindingCatalog.FunctionLabel(binding);
        return LayerGuidance.TryLayerAction(binding, out _, out var target) ? label + " · " + _state.Project.Layers[target].Name : label;
    }
    private string SelectedBindingSummary()
    {
        var keys = _state.Device.Keys.Where(key => _state.Surface == "keys" && _state.SelectedKeys.Contains(key.Id)).ToArray();
        if (keys.Length == 0) return L.T("Wähle zuerst mindestens eine Taste in der Geräteansicht.");
        if (keys.Length == 1) return keys[0].AccessibleLabel + " · " + DisplayBinding(_state.Layer.Keys[keys[0].Id].Binding);
        return L.F($"{keys.Length} Tasten ausgewählt") + " · " + string.Join(", ", keys.Select(key => DisplayBinding(_state.Layer.Keys[key.Id].Binding)).Distinct().Take(3));
    }
    private async Task OpenLayerBindingEditorAsync()
    {
        if (!await GuardNavigation()) return;
        _page = "Ebenen"; _editingSlider = false; Build();
    }
    private sealed record BindingRow(string Code, string Label) { public override string ToString() => Label; }
    private void BuildLayerEditor()
    {
        _editor.Children.Add(Text(LayerGuidance.LayerSummary(_state.LayerIndex), true, 11));
        _editor.Children.Add(Heading("Ebenenwechsel zuweisen"));
        _editor.Children.Add(Text(LayerGuidance.SwitchSteps, true, 11));
        var selected = Text(L.F($"Auf Ebene {_state.LayerIndex}: {SelectedBindingSummary()}"), true, 11); selected.Name = "LayerKeySummary"; _editor.Children.Add(selected);
        var selectedCodes = _state.Device.Keys.Where(key => _state.Surface == "keys" && _state.SelectedKeys.Contains(key.Id)).Select(key => _state.Layer.Keys[key.Id].Binding).Distinct().ToArray();
        var hasAction = LayerGuidance.TryLayerAction(selectedCodes.Length == 1 ? selectedCodes[0] : "", out var currentMode, out var currentTarget);
        var target = Choice(_state.Project.Layers.Select((layer, index) => L.F($"Ebene {index} · {layer.Name}")), hasAction ? currentTarget : _state.LayerIndex == 0 ? 1 : 0); target.Name = "LayerBindingTarget";
        var behavior = Choice(new[] { L.T("Beim Halten (Fn)"), L.T("Ein-/ausschalten"), L.T("Direkt wechseln") }, hasAction ? currentMode : _state.LayerIndex == 0 ? 0 : 2); behavior.Name = "LayerBindingBehavior";
        var description = Text("", true, 11); description.Name = "LayerBindingDescription";
        var assign = AsyncButton("Dieser Taste zuweisen", async () =>
        {
            if (!await GuardNavigation()) return;
            _state.SetBinding(new[] { "MO", "TG", "TO" }[behavior.SelectedIndex] + "(" + target.SelectedIndex + ")"); Changed(); Build();
        }, true); assign.Name = "AssignLayerBinding";
        void UpdateBehavior()
        {
            var cannotHold = behavior.SelectedIndex == 0 && _state.Device.Keys.Any(key => key.IsEncoder && _state.SelectedKeys.Contains(key.Id));
            description.Text = cannotHold ? L.T("Drehbewegungen lassen sich nicht gedrückt halten. Wähle für den Drehgeber eine Taste oder „Umschalten“.") : LayerGuidance.ActionDescription(behavior.SelectedIndex, _state.LayerIndex, target.SelectedIndex);
            assign.IsEnabled = _state.Surface == "keys" && _state.SelectedKeys.Count > 0 && !cannotHold;
        }
        behavior.SelectionChanged += (_, _) => UpdateBehavior(); target.SelectionChanged += (_, _) => UpdateBehavior(); UpdateBehavior();
        _editor.Children.Add(Heading("Zielebene")); _editor.Children.Add(target);
        _editor.Children.Add(Heading("Verhalten")); _editor.Children.Add(behavior); _editor.Children.Add(description); _editor.Children.Add(assign);
        if (_preferences.ShowHelp) _editor.Children.Add(Text(LayerGuidance.FnExample, true, 11));
        _editor.Children.Add(new Separator { Margin = new Thickness(0, 10) });
        var name = Input(_state.Layer.Name);
        bool Rename() { var value = name.Text?.Trim(); if (string.IsNullOrEmpty(value)) throw new InvalidDataException(L.T("Bitte einen Namen eingeben.")); _state.Change(p => p.Layers[_state.LayerIndex].Name = value); return true; }
        Watch(Rename, name); _editor.Children.Add(Heading("Name")); _editor.Children.Add(name); _editor.Children.Add(Button("Übernehmen", () => ApplyEdited(Rename)));
        var destination = Choice(_state.Project.Layers.Select((layer, index) => L.F($"Ebene {index} · {layer.Name}")), (_state.LayerIndex + 1) % 4);
        _editor.Children.Add(Heading("Ebene kopieren")); _editor.Children.Add(destination);
        _editor.Children.Add(AsyncButton("Kopieren", async () => { if (!await GuardNavigation()) return; _state.CopyLayer(destination.SelectedIndex); Changed(); Build(); }));
        _editor.Children.Add(Heading("Ebene vergleichen"));
        var compare = Choice(_state.Project.Layers.Select((layer, index) => L.F($"Ebene {index} · {layer.Name}")), (_state.LayerIndex + 1) % 4); var result = Text("", true);
        void Compare() { var other = _state.Project.Layers[compare.SelectedIndex]; var count = _state.Layer.Keys.Count(k => other.Keys[k.Key].Binding != k.Value.Binding || other.Keys[k.Key].Color != k.Value.Color); result.Text = L.F($"{count} unterschiedliche Tasten"); }
        compare.SelectionChanged += (_, _) => Compare(); Compare(); _editor.Children.Add(compare); _editor.Children.Add(result);
    }
    private Control BuildDeviceWorkspace()
    {
        var body = Column(BuildDeviceSettings()); body.Name = "DeviceWorkspace"; body.Spacing = 18;
        if (_state.Device.IsExperimentalFirmware)
            body.Children.Insert(0, new TextBlock { Name = "ExperimentalDeviceNotice", Text = L.T("Experimentelle Unterstützung: noch nicht an diesem Modell getestet."), Foreground = Brush("#E9B86B"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new Separator());
        var firmware = Column(Heading(_state.Device.IsOfflineOnly ? "Offline-Profil" : "Firmware"), BuildFirmwareVersionPanel()); firmware.Name = "DeviceFirmwareSection";
        if (_state.Device.IsOfflineOnly) { body.Children.Add(firmware); return body; }
        firmware.Children.Add(AsyncButton("Gerät einrichten", () => OpenDeviceSetupAsync(_state.Device.Id)));
        var restore = AsyncButton("Original-Firmware wiederherstellen", () => OpenDeviceSetupAsync(_state.Device.Id, SetupOperation.Restore)); restore.Name = "RestoreOriginalFirmware";
        var folder = AsyncButton("Sicherungsordner öffnen", () => OpenRecoveryFolderAsync(Status)); folder.Name = "OpenDeviceRecoveryFolder";
        var recovery = Column(Heading("Sicherungen"),
            Text(L.T("Deine Original-Firmware wird bei der Einrichtung auf diesem Computer gesichert."), true, 11),
            Text(L.T("Die Wiederherstellung verwendet ausschließlich die passende Original-Sicherung dieses Geräts."), true, 11),
            folder, restore); recovery.Name = "DeviceRecoverySection";
        var maintenance = new Grid { Name = "DeviceMaintenance", ColumnDefinitions = new("*,*"), ColumnSpacing = 36 };
        maintenance.Children.Add(firmware); Grid.SetColumn(recovery, 1); maintenance.Children.Add(recovery); body.Children.Add(maintenance);
        return body;
    }
    private Control BuildDeviceSettings()
    {
        var polling = Choice(new[] { "1000 Hz · 1 ms", "500 Hz · 2 ms", "250 Hz · 4 ms", "125 Hz · 8 ms" }, Array.IndexOf(new[] { 1, 2, 4, 8 }, _state.Project.DeviceSettings.UsbPollingIntervalMs)); polling.Name = "PollingInterval";
        var debounce = Number(_state.Project.DeviceSettings.DebounceMs, 0, 40); debounce.Name = "DebounceMs";
        var timingFields = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        timingFields.Children.Add(Column(Text(L.T("USB-Abfragerate"), true, 12), polling));
        var debounceField = Column(Text(L.T("Entprellzeit (ms)"), true, 12), debounce); Grid.SetColumn(debounceField, 1); timingFields.Children.Add(debounceField);
        var timing = Column(Heading("Reaktionszeit"), timingFields,
            Text(L.T("Eine geänderte USB-Abfragerate wird nach der USB-Neuanmeldung aktiv."), true, 11)); timing.Name = "DeviceTimingSection";
        var value = _state.Project.Standby;
        var enabled = new CheckBox { Name = "StandbyEnabled", Content = L.T("Standby aktivieren"), IsChecked = value.Enabled };
        var presets = new[] { 30, 60, 120, 300, 600, 1800, 3600 };
        var preset = Choice(new[] { L.T("30 Sekunden"), L.T("1 Minute"), L.T("2 Minuten"), L.T("5 Minuten"), L.T("10 Minuten"), L.T("30 Minuten"), L.T("60 Minuten"), L.T("Eigene Zeit") }, Array.IndexOf(presets, value.TimeoutSeconds) is var index && index >= 0 ? index : presets.Length); preset.Name = "StandbyDelayPreset";
        var timeout = Number(value.TimeoutSeconds, 10, 3600); timeout.Name = "StandbyTimeoutSeconds";
        var customTime = Column(Text(L.T("Sekunden"), true, 11), timeout); customTime.IsVisible = preset.SelectedIndex == presets.Length;
        var mode = Choice(new[] { L.T("Aus"), L.T("Dimmen"), L.T("Screensaver") }, value.Mode); mode.Name = "StandbyMode";
        var brightness = Number(value.DimBrightnessPercent, 1, 100); brightness.Name = "StandbyBrightness";
        var dimming = Column(Text(L.T("Helligkeit (%)"), true, 11), brightness);
        var screensaverHint = Text(L.T("Nach der Wartezeit wiederholt sich die vollständige Lichtsequenz bis zur nächsten Eingabe."), true, 11); screensaverHint.Name = "StandbyScreensaverHint";
        var fields = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        fields.Children.Add(Column(Text(L.T("Wartezeit"), true, 12), preset, customTime));
        var action = Column(Text(L.T("Aktion"), true, 12), mode, dimming); Grid.SetColumn(action, 1); fields.Children.Add(action);
        var settings = Column(fields, Text(L.T("Nach der letzten Eingabe an diesem Gerät."), true, 11), screensaverHint);
        void UpdateControls() { settings.IsEnabled = enabled.IsChecked == true; dimming.IsVisible = mode.SelectedIndex == 1; screensaverHint.IsVisible = mode.SelectedIndex == 2; }
        preset.SelectionChanged += (_, _) => { customTime.IsVisible = preset.SelectedIndex == presets.Length; if (preset.SelectedIndex >= 0 && preset.SelectedIndex < presets.Length) timeout.Value = presets[preset.SelectedIndex]; };
        mode.SelectionChanged += (_, _) => UpdateControls(); enabled.IsCheckedChanged += (_, _) => UpdateControls(); UpdateControls();
        var standbyHeading = Heading("Beleuchtung bei Inaktivität"); standbyHeading.Name = "StandbyHeading";
        var standby = Column(standbyHeading, enabled, settings,
            Text(L.T("Gilt für das gesamte Gerät und läuft ohne geöffnete App.") + " " + L.T("Tasten, Drehknopf oder Schieberegler wecken die Beleuchtung wieder auf."), true, 11),
            Text(L.T("Im USB-Ruhezustand bleibt die Beleuchtung aus.") + " " + L.T("Standby benötigt passende INGLORIOUS-Firmware."), true, 11)); standby.Name = "DeviceStandbySection";
        bool ApplySettings()
        {
            CommitCandidate(p =>
            {
                p.DeviceSettings = new() { UsbPollingIntervalMs = new[] { 1, 2, 4, 8 }[polling.SelectedIndex], DebounceMs = (int)(debounce.Value ?? 5) };
                p.Standby = new() { Enabled = enabled.IsChecked == true, TimeoutSeconds = (int)(timeout.Value ?? value.TimeoutSeconds), Mode = mode.SelectedIndex, DimBrightnessPercent = (int)(brightness.Value ?? value.DimBrightnessPercent) };
            }); return true;
        }
        Watch(ApplySettings, polling, debounce, enabled, preset, timeout, mode, brightness);
        var sections = new Grid { Name = "DeviceSettingsSections", ColumnDefinitions = new("*,*"), ColumnSpacing = 36 };
        sections.Children.Add(timing); Grid.SetColumn(standby, 1); sections.Children.Add(standby);
        var apply = Button("Übernehmen", () => ApplyEdited(ApplySettings), true); apply.Name = "ApplyDeviceSettings";
        return Column(sections, apply);
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
}
