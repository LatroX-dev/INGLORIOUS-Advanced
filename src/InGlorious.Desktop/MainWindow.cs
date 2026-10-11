using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InGlorious.Core;
using System.Diagnostics;

namespace InGlorious.Desktop;

public sealed partial class MainWindow : Window
{
    private AppPreferences _preferences;
    private readonly Dictionary<string, EditorState> _sessions = [];
    private EditorState _state = null!;
    private readonly ProfileLibrary _profiles = new(Paths.DataRoot);
    private readonly Dictionary<string, string> _confirmed = [];
    private string _page = "Beleuchtung", _status = "";
    private bool _busy, _building, _navigating, _allowClose;
    private Func<bool>? _pendingApply;
    private Grid _root = null!;
    private StackPanel _editor = null!;
    private Canvas _keyboard = null!;
    private TextBlock _statusText = null!, _selectionText = null!, _deviceState = null!;
    private ComboBox _layerChoice = null!;
    private readonly Dictionary<int, Border> _ledViews = [];
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private LightingPreviewScene? _scene;
    private PreviewRgb[] _frame = [];
    private readonly List<PreviewHit> _hits = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string? _macroId;
    private bool _globalEffect = true, _showEffects;
    private bool _previewRunning = true;
    private readonly List<string> _favorites = [];
    private readonly Dictionary<string, Button> _keyButtons = [];
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    public MainWindow(AppPreferences preferences, string? warning)
    {
        _preferences = preferences; _status = warning ?? L.T("Bereit");
        Title = "INGLORIOUS Advanced"; Width = preferences.RememberWindowSize ? preferences.WindowWidth : 1360;
        using (var icon = AssetLoader.Open(new Uri("avares://INGLORIOUS Advanced/Assets/inglorious.ico")))
            Icon = new WindowIcon(icon);
        Height = preferences.RememberWindowSize ? preferences.WindowHeight : 900;
        MinWidth = 1150; MinHeight = 760;
        if (Program.Arguments.Contains("--minimum")) { Width = MinWidth; Height = MinHeight; }
        var deviceArgument = Array.IndexOf(Program.Arguments, "--device");
        var initialDevice = Program.Arguments.Contains("--numpad") ? "numpad" : preferences.ResolveStartDevice();
        if (Program.Offline && deviceArgument >= 0 && deviceArgument + 1 < Program.Arguments.Length &&
            DeviceCatalog.All.Any(device => device.Id == Program.Arguments[deviceArgument + 1])) initialDevice = Program.Arguments[deviceArgument + 1];
        _state = OpenSession(initialDevice, allowLocalProfile: Program.Offline);
        try { var palette = Program.Offline ? new ColorPaletteLoadResult(ColorPaletteStore.DefaultColors, null) : ColorPaletteStore.Load(ColorPaletteStore.DefaultPath); _favorites.AddRange(palette.Colors); if (palette.Error is not null) _status = palette.Error; }
        catch (Exception ex) { _status = ex.Message; }
        _startupLoading = _busy = !Program.Offline;
        if (_startupLoading) _status = L.T("Konfigurationen werden von den Geräten gelesen …");
        InitializeOfflineConnections();
        Build();
        _animation.Tick += (_, _) => Animate(); _animation.Start();
        Closing += OnClosing; Closed += (_, _) => { _startupWindowClosed = true; _startupCancellation?.Cancel(); StopSoftwareUpdates(); _animation.Stop(); StopConnections(); SaveWindowPreferences(); };
        KeyDown += OnKeyDown;
        Opened += (_, _) => InitializeWindowOnce();
    }
    private EditorState OpenSession(string id, bool allowLocalProfile = true)
    {
        if (_sessions.TryGetValue(id, out var session)) return session;
        session = new(id); session.MarkInitialClean();
        if (!Program.Offline && !allowLocalProfile) _startupPlaceholders.Add(id);
        if (!Program.Offline && allowLocalProfile)
        {
            var path = _profiles.ActivePath(id) ?? ProjectService.DefaultPath(id);
            try
            {
                if (File.Exists(path))
                {
                    var project = ProjectService.Load(path);
                    if (project.DeviceId != id) throw new InvalidDataException(L.T("Die gespeicherte Datei gehört zu einem anderen Gerät."));
                    session = new(project, path);
                }
            }
            catch (Exception ex) { _status = L.F($"{Path.GetFileName(path)} konnte nicht geladen werden: {ex.Message}"); }
        }
        return _sessions[id] = session;
    }
    private TextBlock Text(string value, bool muted = false, double size = 13)
    {
        var label = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (muted) label.Classes.Add("muted"); return label;
    }
    private TextBlock Heading(string source)
    { var label = Text(L.T(source)); label.Classes.Add("section"); return label; }
    private Button Button(string source, Action action, bool primary = false)
    {
        var button = new Button { Content = ButtonContent(source) }; if (primary) button.Classes.Add("primary");
        Avalonia.Automation.AutomationProperties.SetName(button, L.T(source is "↶" ? "Rückgängig" : source is "↷" ? "Wiederholen" : source));
        button.Click += (_, _) => Run(action); return button;
    }
    private Button AsyncButton(string source, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = ButtonContent(source) }; if (primary) button.Classes.Add("primary");
        Avalonia.Automation.AutomationProperties.SetName(button, L.T(source is "↶" ? "Rückgängig" : source is "↷" ? "Wiederholen" : source));
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Status(ex.Message); } }; return button;
    }
    private static StackPanel Row(params Control[] children)
    { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 }; foreach (var child in children) row.Children.Add(child); return row; }
    private static StackPanel Column(params Control[] children)
    { var column = new StackPanel { Spacing = 8 }; foreach (var child in children) column.Children.Add(child); return column; }
    private static TextBox Input(string text, string? watermark = null) => new() { Text = text, PlaceholderText = watermark, HorizontalAlignment = HorizontalAlignment.Stretch };
    private ComboBox Choice(IEnumerable<string> labels, int selected = 0) => new() { ItemsSource = labels.ToArray(), SelectedIndex = selected, MinWidth = 130 };
    private void Run(Action action)
    { if (_busy) return; try { action(); } catch (Exception ex) { Status(ex.Message); } }
    private void Status(string message) { _status = message; if (_statusText is not null) { _statusText.Text = message; ToolTip.SetTip(_statusText, message); } }
    private void Changed()
    { _pendingApply = null; RebuildPreview(); UpdateFooter(); }

    private void Build()
    {
        _building = true;
        try
        {
            _root = new Grid { RowDefinitions = new("56,*,68") };
            var header = new Grid { ColumnDefinitions = new("210,*,Auto"), Margin = new Thickness(16, 8) };
            var brand = Row(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://INGLORIOUS Advanced/Assets/inglorious-mark.png"))), Width = 35, Height = 35 },
                new StackPanel { Spacing = 0, Children = { new TextBlock { Name = "ProductBrand", Text = "INGLORIOUS", FontSize = 20, FontFamily = new FontFamily("avares://INGLORIOUS Advanced/Assets/Fonts/INGLORIOUS-Oxanium-SemiBold.ttf#IG Preview Oxanium"), FontWeight = FontWeight.SemiBold }, Text("ADVANCED", true, 12) } });
            header.Children.Add(brand);
            var toolbar = Row(AsyncButton("Profil öffnen …", OpenProfileAsync), AsyncButton("Profil speichern", () => SaveAsync(false)),
                AsyncButton("↶", async () => { if (!await GuardNavigation()) return; _state.Undo(); Changed(); Build(); }), AsyncButton("↷", async () => { if (!await GuardNavigation()) return; _state.Redo(); Changed(); Build(); }));
            Grid.SetColumn(toolbar, 1); header.Children.Add(toolbar);
            var help = AsyncButton("?", OpenHelpAsync); help.Name = "HelpButton"; help.FontSize = 22; help.FontWeight = FontWeight.SemiBold; help.Width = 40;
            ToolTip.SetTip(help, L.T("Anleitung")); Avalonia.Automation.AutomationProperties.SetName(help, L.T("Anleitung"));
            var settings = AsyncButton("⚙", OpenSettingsAsync); settings.FontSize = 22; ToolTip.SetTip(settings, L.T("Einstellungen")); Avalonia.Automation.AutomationProperties.SetName(settings, L.T("Einstellungen"));
            var utilities = Row(BuildSoftwareUpdateHint(), help, settings); Grid.SetColumn(utilities, 2); header.Children.Add(utilities);
            _root.Children.Add(header);

            var body = new Grid { ColumnDefinitions = new("176,*,320") }; Grid.SetRow(body, 1); _root.Children.Add(body);
            var nav = new StackPanel { Spacing = 5, Margin = new Thickness(12) };
            _deviceButtonsPanel = new StackPanel { Name = "DeviceButtons", Spacing = 4 };
            _deviceButtons.Clear();
            Avalonia.Automation.AutomationProperties.SetName(_deviceButtonsPanel, L.T("Geräte"));
            _connectionHint = Text("", true, 11); _connectionHint.Name = "ConnectionHint";
            nav.Children.Add(_deviceButtonsPanel); nav.Children.Add(_connectionHint);
            UpdateConnectionSelector();
            nav.Children.Add(new Separator { Margin = new Thickness(0, 10) });
            foreach (var page in new[] { "Beleuchtung", "Tastenbelegung", "Ebenen", "Makros", "Profile", "Gerät" })
            {
                var button = AsyncButton(page, async () => { if (!await GuardNavigation()) return; _page = page; Build(); });
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Classes.Add("nav");
                if (_page == page) button.Background = Brush("#3A252B"); nav.Children.Add(button);
            }
            body.Children.Add(new Border { Background = Brush("#1C1F22"), Child = nav });
            var workspace = new StackPanel { Spacing = 12, Margin = new Thickness(22, 18) };
            var top = new Grid { ColumnDefinitions = new("*,220") };
            var heading = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(Text(_page == "Profile" ? L.T("Profile") : _state.Device.Name, size: 22));
            if (_page != "Profile" && _state.Device.IsExperimentalFirmware)
            {
                var experimental = ExperimentalLabel("ExperimentalHeaderBadge");
                experimental.Margin = new Thickness(10, 5, 10, 0); heading.Children.Add(experimental);
            }
            top.Children.Add(heading);
            _layerChoice = Choice(_state.Project.Layers.Select((l, i) => L.F($"Ebene {i} · {l.Name}")), _state.LayerIndex);
            _layerChoice.SelectionChanged += async (_, _) =>
            {
                if (_building || _layerChoice.SelectedIndex < 0 || _layerChoice.SelectedIndex == _state.LayerIndex) return;
                var index = _layerChoice.SelectedIndex;
                if (!await GuardNavigation()) { _building = true; _layerChoice.SelectedIndex = _state.LayerIndex; _building = false; return; }
                _state.LayerIndex = index; Build();
            };
            _layerChoice.IsVisible = _page is not ("Profile" or "Gerät");
            Grid.SetColumn(_layerChoice, 1); top.Children.Add(_layerChoice); workspace.Children.Add(top);
            if (_state.Device.IsOfflineOnly)
                workspace.Children.Add(new TextBlock { Name = "OfflineModelNotice", Text = L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar."), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A0A7B0") });
            else if (_startupFallbackNotices.TryGetValue(_state.Device.Id, out var startupNotice))
                workspace.Children.Add(new TextBlock { Name = "StartupFallbackNotice", Text = startupNotice, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#EAB985") });
            _keyboard = new Canvas(); _selectionText = Text("", true);
            if (_page is "Beleuchtung" or "Tastenbelegung" or "Ebenen")
            {
                workspace.Children.Add(new Border { Background = Brush("#171A1D"), BorderBrush = Brush("#353A40"), BorderThickness = new Thickness(1), Padding = new Thickness(16), Child = new Viewbox { Child = _keyboard, MaxHeight = Math.Clamp(Height - 310, 320, 540), Stretch = Stretch.Uniform } });
                workspace.Children.Add(_selectionText);
                workspace.Children.Add(Row(AsyncButton("Alle auswählen", async () => { if (!await GuardNavigation()) return; _state.Surface = "keys"; _state.SelectedKeys.UnionWith(_state.Device.Keys.Select(k => k.Id)); _globalEffect = false; Build(); }),
                    AsyncButton("Auswahl aufheben", async () => { if (!await GuardNavigation()) return; _state.SelectedKeys.Clear(); _state.SelectedSideLeds.Clear(); _state.Surface = "keys"; Build(); })));
            }
            if (_page == "Makros") workspace.Children.Add(BuildMacroWorkspace());
            if (_page == "Profile") workspace.Children.Add(BuildProfilesWorkspace());
            if (_page == "Gerät") workspace.Children.Add(BuildDeviceWorkspace());
            var workspaceScroll = new ScrollViewer { Content = workspace, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumn(workspaceScroll, 1); body.Children.Add(workspaceScroll);
            _editor = new StackPanel { Spacing = 9, Margin = new Thickness(16) };
            _editor.Children.Add(Heading(_page));
            switch (_page)
            {
                case "Beleuchtung": BuildLightingEditor(); break;
                case "Tastenbelegung": BuildBindingEditor(); break;
                case "Ebenen": BuildLayerEditor(); break;
                case "Makros": BuildMacroEditor(); break;
                case "Gerät": BuildDeviceEditor(); break;
                default: _editor.Children.Add(Text(L.T("Ein Profil enthält Farben, Belegungen, Ebenen und Makros für ein Gerät."), true)); break;
            }
            var inspector = new Border { Background = Brush("#1C1F22"), Child = new ScrollViewer { Content = _editor } };
            Grid.SetColumn(inspector, 2); body.Children.Add(inspector);
            var footer = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(16, 7) };
            _statusText = Text(_status, true, 11); _deviceState = Text("", size: 12);
            footer.Children.Add(Column(_deviceState, _statusText));
            _readDeviceButton = AsyncButton("Von Tastatur laden", ReadDeviceAsync); _readDeviceButton.Name = "ReadDeviceButton";
            _transferButton = AsyncButton("Auf Tastatur übertragen", TransferAsync, true); _transferButton.Name = "TransferButton";
            var actions = Row(_readDeviceButton, _transferButton);
            if (_state.Device.IsOfflineOnly) ToolTip.SetTip(actions, L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar."));
            Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); _root.Children.Add(footer);
            _root.IsEnabled = !_busy && !_initialConnectionsPending;
            Content = _root; RebuildPreview(); UpdateFooter();
        }
        finally { _building = false; }
    }
    private void UpdateFooter()
    {
        UpdateExperimentalDeviceLabels();
        ClearChangedStartupNotice();
        var same = _confirmed.TryGetValue(_state.Device.Id, out var saved) && saved == EditorState.Fingerprint(_state.Project);
        _deviceState.Text = _state.Device.IsOfflineOnly ? L.T("Offline-Profil") : _connectionsFailed ? L.T("Gerätesuche fehlgeschlagen") : !CurrentModelConnected ? L.T("Gerät nicht verbunden") : same ? L.T("Auf Tastatur bestätigt") : _confirmed.ContainsKey(_state.Device.Id) ? L.T("Änderungen noch nicht übertragen") : L.T("Noch nicht übertragen");
        UpdateConnectionActions();
        Title = "INGLORIOUS Advanced · " + _state.Project.Name + (_state.Dirty ? " *" : "");
    }
    private TextBlock ExperimentalLabel(string name) => new()
    {
        Name = name, Text = L.T("Experimentell"), FontSize = 11,
        Foreground = Brush("#E9B86B"), VerticalAlignment = VerticalAlignment.Center
    };
    private void UpdateExperimentalDeviceLabels()
    {
        // Presence refreshes also update the footer; append only once so unchanged
        // scans keep their existing button controls and never disturb editor input.
        foreach (var (id, button) in _deviceButtons)
        {
            if (!DeviceCatalog.Get(id).IsExperimentalFirmware || button.Content is not Grid body || body.Children[1] is not StackPanel labels) continue;
            if (!labels.Children.Any(child => child.Name == "ExperimentalDeviceBadge")) labels.Children.Add(ExperimentalLabel("ExperimentalDeviceBadge"));
        }
    }
    private void RebuildPreview()
    {
        _keyboard.Children.Clear(); _ledViews.Clear(); _keyButtons.Clear();
        const double unit = 49; const double edge = 42;
        var topInset = _state.Device.PreviewTopInset * unit;
        _keyboard.Width = _state.Device.PreviewWidth * unit + edge * 2; _keyboard.Height = _state.Device.PreviewHeight * unit + 14;
        var showSelection = !(_page == "Beleuchtung" && _showEffects && _globalEffect);
        var selectedLeds = showSelection ? _state.SelectedLeds().ToHashSet() : [];
        foreach (var key in _state.Device.Keys)
        {
            var cap = new Grid();
            var legendScale = Width < 1280 ? 1.4 : 1;
            var displayLabel = key.DisplayLabel;
            var label = Text(displayLabel, size: key.Label == "Knob" ? (displayLabel.Length > 8 ? 8 : 9) : (displayLabel.Length > 3 ? 11 : 14) * legendScale);
            label.TextWrapping = TextWrapping.NoWrap;
            cap.Children.Add(key.IsEncoder ? (Control)ButtonContent(key.Label) : key.Label == "Knob" ? new Viewbox { Child = label, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Center } : label);
            if (key.LedIndex is { } led)
            {
                var light = new Border { Height = 3, Background = Brush(_state.ColorAt(led)), VerticalAlignment = VerticalAlignment.Bottom };
                cap.Children.Add(light); _ledViews[led] = light;
            }
            var chosen = showSelection && _state.Surface == "keys" && _state.SelectedKeys.Contains(key.Id);
            var button = new Button { Content = cap, Width = (key.W + (key.IsIsoEnter ? .25 : 0)) * unit - 4, Height = key.H * unit - 4, Padding = new Thickness(key.IsEncoder ? 0 : 4), Background = Brush("#252A2F"), BorderBrush = Brush(chosen ? "#EF646B" : "#424950"), BorderThickness = new Thickness(chosen ? 2 : 1), FontSize = 11 };
            if (key.Label == "Knob") { button.CornerRadius = new CornerRadius(99); label.VerticalAlignment = VerticalAlignment.Center; label.HorizontalAlignment = HorizontalAlignment.Center; }
            if (key.IsIsoEnter)
            {
                var width = button.Width; var height = button.Height;
                var outline = Geometry.Parse(FormattableString.Invariant($"M 1,1 L {width-1},1 {width-1},{height-1} {.25*unit+1},{height-1} {.25*unit+1},{unit-5} 1,{unit-5} Z"));
                button.Clip = outline; button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(0);
                cap.Children.Insert(0, new Avalonia.Controls.Shapes.Path { Data = outline, Fill = Brush("#252A2F"), Stroke = Brush(chosen ? "#EF646B" : "#424950"), StrokeThickness = chosen ? 2 : 1 }); label.Margin = new Thickness(5);
                if (key.LedIndex is { } index) _ledViews[index].Margin = new Thickness(.25 * unit + 5, 0, 5, 5);
            }
            var multiple = false;
            button.PointerPressed += (_, e) => multiple = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0;
            button.Click += async (_, _) => { if (!await GuardNavigation()) return; _state.SelectKey(key.Id, multiple); SimulateKey(key); _globalEffect = false; _editingSlider = false; Build(); };
            ToolTip.SetTip(button, key.AccessibleLabel + " · " + BindingCatalog.FunctionLabel(_state.Layer.Keys[key.Id].Binding));
            Avalonia.Automation.AutomationProperties.SetName(button, key.AccessibleLabel);
            Canvas.SetLeft(button, edge + (key.X - (key.IsIsoEnter ? .25 : 0)) * unit); Canvas.SetTop(button, topInset + key.Y * unit); _keyboard.Children.Add(button); _keyButtons[key.Id] = button;
        }
        foreach (var (indices, x) in new[] { (_state.Device.LeftSideLedIndices, 0d), (_state.Device.RightSideLedIndices, _keyboard.Width - 17) })
        {
            var height = Math.Min(_state.Device.Height * unit - 1, 6 * unit);
            for (var i = 0; i < indices.Count; i++)
            {
                var led = indices[i]; var light = new Border { Background = Brush(_state.ColorAt(led)) }; _ledViews[led] = light;
                var segment = new Button { Width = 17, Height = height / indices.Count - 2, Padding = new Thickness(2), Content = light, BorderBrush = Brush(selectedLeds.Contains(led) ? "#EF646B" : "#424950") };
                var multiple = false; segment.PointerPressed += (_, e) => multiple = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0;
                segment.Click += async (_, _) => { if (!await GuardNavigation()) return; _state.SelectSide(led, multiple); _globalEffect = false; Build(); };
                ToolTip.SetTip(segment, "LED " + (i + 1)); Canvas.SetLeft(segment, x); Canvas.SetTop(segment, topInset + i * height / indices.Count); _keyboard.Children.Add(segment);
            }
        }
        if (_state.Device.BadgeGeometry is { } geometry)
        {
            var content = new Grid();
            var label = Text(L.T("Logo"), size: 10);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = geometry.IsHorizontal ? VerticalAlignment.Center : VerticalAlignment.Top;
            label.TextWrapping = TextWrapping.NoWrap;
            label.Margin = new Thickness(0, geometry.IsHorizontal ? 0 : 5, 0, 0);
            content.Children.Add(label);
            Control badge;
            if (geometry.LedIndex is { } led)
            {
                var light = new Border { Height = .28 * unit, Background = Brush(_state.ColorAt(led)), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(1) };
                content.Children.Add(light); _ledViews[led] = light;
                var button = AsyncButton("Logo", async () => { if (!await GuardNavigation()) return; _state.SelectBadge(); _page = "Beleuchtung"; _showStandby = false; _globalEffect = false; Build(); });
                button.Name = "BadgeLed" + led; button.Content = content; button.Padding = new Thickness(3);
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
                button.BorderBrush = Brush(showSelection && _state.Surface == "badge" ? "#EF646B" : "#424950");
                ToolTip.SetTip(button, L.T("Logo-Beleuchtung"));
                Avalonia.Automation.AutomationProperties.SetName(button, L.T("Logo-Beleuchtung"));
                badge = button;
            }
            else
            {
                badge = new Border { Name = "BadgeHousing", Child = content, Padding = new Thickness(3), Background = Brush("#252A2F"), BorderBrush = Brush("#424950"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
            }
            badge.Width = geometry.W * unit - 4; badge.Height = geometry.H * unit - 4;
            Canvas.SetLeft(badge, edge + geometry.X * unit); Canvas.SetTop(badge, topInset + geometry.Y * unit); _keyboard.Children.Add(badge);
        }
        if (_state.Device.Id == "numpad")
        {
            var track = new Border { Width = 8, Height = 118, Background = Brush("#141618"), BorderBrush = Brush("#424950"), BorderThickness = new Thickness(1), Child = new Border { Width = 20, Height = 13, Background = Brush("#A0A7B0"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            var slider = AsyncButton("", () => ShowBindingEditorAsync(true)); slider.Name = "PreviewSlider"; slider.Content = track; slider.Width = 38; slider.Height = 142; slider.Padding = new Thickness(0); slider.Background = Brushes.Transparent; slider.BorderThickness = new Thickness(0);
            ToolTip.SetTip(slider, L.T("Schieberegler anpassen")); Canvas.SetLeft(slider, edge + (_state.Device.Width - .55) * unit - 15); Canvas.SetTop(slider, topInset + 1.65 * unit - 12); _keyboard.Children.Add(slider);
        }
        _selectionText.Text = _page == "Beleuchtung" && _showEffects && _globalEffect ? L.T("Gesamte Ebene") : _state.Surface == "keys" ? L.F($"{_state.SelectedKeys.Count} Tasten ausgewählt") : _state.Surface == "badge" ? L.T("Logo-Beleuchtung") : L.F($"{selectedLeds.Count} Seiten-LEDs");
        _scene = LightingPreviewEngine.CreateScene(_state.Device, _state.Layer); _frame = new PreviewRgb[_state.Device.LedCount];
        RenderLightingPreviewFrame((uint)_clock.ElapsedMilliseconds);
    }
    private void SimulateKey(KeyDefinition key)
    {
        if (key.LedIndex is not { } index) return;
        var led = LightingPreviewEngine.Layout(_state.Device).First(l => l.Index == index);
        _hits.Add(new((uint)_clock.ElapsedMilliseconds, led.X, led.Y, led.Index));
        while (_hits.Count > LightingPreviewEngine.MaxHits) _hits.RemoveAt(0);
    }
    private void Animate()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _scene is null || !_previewRunning || _page != "Beleuchtung") return;
        RenderLightingPreviewFrame((uint)_clock.ElapsedMilliseconds);
    }
    private void RenderLightingPreviewFrame(uint time)
    {
        if (_scene is null) return;
        _scene.Render(time, _hits.ToArray(), _frame);
        foreach (var (index, border) in _ledViews) { var c = _frame[index]; border.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); }
    }
}
