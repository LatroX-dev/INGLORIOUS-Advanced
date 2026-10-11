using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace InGlorious.Core;

public partial class MainWindow : Window
{
    private AppPreferences _preferences = new();
    private bool _refreshingLayerNavigation;
    private bool _editingSlider;
    private AppProject _project;
    private int _layerIndex;
    private string _savedFingerprint;
    private string? _projectPath;
    private readonly HashSet<string> _selection = [];
    private readonly Dictionary<string, Button> _keyButtons = [];
    private readonly Stack<AppProject> _undo = new();
    private readonly Stack<AppProject> _redo = new();
    private IReadOnlyList<DetectedDevice> _devices = [];
    private BackupReport? _backup = null;
    private bool _refreshing;
    private bool _scanning;
    private bool _screenshotMode;
    private bool _readingDevice;
    private readonly bool _loadDefaultProjects;
    private string _sourceDescription = "Lokale Vorlage · nicht aus Gerät gelesen";
    private string _sourceFingerprint = "";
    private readonly Dictionary<string, (string Fingerprint, DateTime Time)> _confirmedDeviceSaves = [];
    private string? _defaultLoadError;
    private int _activeTab;
    private bool _showLightingEffects;
    private bool _effectForSelection;
    private bool _wholeSideSelection;
    private bool _macroAssigning;
    private bool _refreshingLayerTools;
    private bool _refreshingEffects;
    private enum LightingTarget { Keys, Left, Right, Both, SelectedLeds, Badge }
    private LightingTarget _lightingTarget;
    private LightingTarget _lastSideTarget = LightingTarget.Left;
    private readonly HashSet<int> _selectedSideLeds = [];
    private string _palettePath = Path.Combine(Paths.DataRoot, "custom-colors.json");
    private IReadOnlyList<string> _customColors = [];
    private string? _paletteError;
    private string? _macroId;
    private readonly Dictionary<string, EditorSession> _sessions = [];
    private sealed record EditorSession(AppProject Project, string SavedFingerprint, string? Path, int Layer, string? MacroId, string[] Selection, AppProject[] Undo, AppProject[] Redo, string SourceDescription, string SourceFingerprint, string? LoadError);
    private sealed record ProjectOpening(AppProject Project, string? Path, string SourceDescription, string? Error);
    private DeviceDefinition Device => DeviceCatalog.Get(_project.DeviceId);
    private LayerProfile Layer => _project.Layers[_layerIndex];
    private bool IsDirty => Fingerprint(_project) != _savedFingerprint;
    private MacroDefinition? CurrentMacro => _project.Macros.FirstOrDefault(m => m.Id == _macroId);

    public MainWindow()
    {
        var args = Environment.GetCommandLineArgs();
        _screenshotMode = args.Contains("--screenshot") || args.Contains("--ui-smoke");
        _loadDefaultProjects = !_screenshotMode || args.Contains("--load-default");
        var preferences = _screenshotMode ? new AppPreferencesLoadResult(new(), null) : AppPreferencesStore.Load();
        _preferences = preferences.Preferences;
        var languageIndex = Array.IndexOf(args, "--language");
        // Existing UI regression fixtures use German labels; screenshots and normal starts use the saved/system preference.
        var language = _screenshotMode && languageIndex >= 0 && languageIndex + 1 < args.Length ? args[languageIndex + 1] : args.Contains("--ui-smoke") ? "de" : _preferences.Language;
        L.SetLanguage(language); LocalizationUi.Initialize();
        // Normal startup reads hardware before opening a saved profile as a fallback.
        var initial = OpenDefaultProject(_preferences.ResolveStartDevice(), allowDefault: _screenshotMode && _loadDefaultProjects);
        _project = initial.Project; _projectPath = initial.Path; _sourceDescription = initial.SourceDescription; _defaultLoadError = initial.Error;
        _savedFingerprint = Fingerprint(_project);
        _sourceFingerprint = _savedFingerprint;
        InitializeComponent();
        if (_screenshotMode)
        {
            var fixtureIndex = Array.IndexOf(args, "--connected-devices");
            SetOfflineConnectionFixture(fixtureIndex >= 0 && fixtureIndex + 1 < args.Length
                ? args[fixtureIndex + 1].Split(',') : args.Contains("--ui-smoke") ? ["pro-iso", "numpad"] : []);
        }
        InitializeStartup();
        Resources["HelpVisibility"] = _preferences.ShowHelp ? Visibility.Visible : Visibility.Collapsed;
        if (!_screenshotMode && _preferences.RememberWindowSize)
        { Width = Math.Min(_preferences.WindowWidth, SystemParameters.WorkArea.Width); Height = Math.Min(_preferences.WindowHeight, SystemParameters.WorkArea.Height); }
        EffectChoice.ItemsSource = LightingEffects.All;
        RefreshBindingChoices();
        StepKeyChoice.ItemsSource = BindingCatalog.All.Where(b => BindingCatalog.IsBasic(b.Code) && b.Code is not "KC_MUTE" and not "KC_VOLU" and not "KC_VOLD" and not "KC_MNXT" and not "KC_MPRV" and not "KC_MSTP" and not "KC_MPLY" and not "KC_CALC" and not "KC_MAIL" and not "KC_MYCM").Select(b => new BindingOption(b.Code, BindingCatalog.FunctionLabel(b.Code))).ToList();
        if (StepKeyChoice.Items.Count > 0) StepKeyChoice.SelectedIndex = 0;
        LoadColorPalette();
        RefreshAll();
        InitializePendingEdits();
        InitializeSharedBrightness();
        Status(_startupLoading ? L.T("Konfigurationen werden von den Geräten gelesen …") : _defaultLoadError ?? preferences.Warning ?? L.T("Bereit"));
    }

    private static string Fingerprint(AppProject project) => JsonSerializer.Serialize(project);
    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return Color.FromRgb(163, 139, 250); }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        StartLightingPreview();
        var args = Environment.GetCommandLineArgs();
        var screenshotIndex = Array.FindIndex(args, a => a == "--screenshot");
        var smokeIndex = Array.FindIndex(args, a => a == "--ui-smoke");
        _screenshotMode = screenshotIndex >= 0 || smokeIndex >= 0;
        if (_screenshotMode)
        {
            var widthIndex = Array.FindIndex(args, a => a == "--width");
            var heightIndex = Array.FindIndex(args, a => a == "--height");
            if (widthIndex >= 0 && widthIndex + 1 < args.Length && double.TryParse(args[widthIndex + 1], out var width)) Width = Math.Max(MinWidth, width);
            if (heightIndex >= 0 && heightIndex + 1 < args.Length && double.TryParse(args[heightIndex + 1], out var height)) Height = Math.Max(MinHeight, height);
            var deviceIndex = Array.IndexOf(args, "--device");
            if (deviceIndex >= 0 && deviceIndex + 1 < args.Length && DeviceCatalog.All.Any(device => device.Id == args[deviceIndex + 1])) SwitchDevice(args[deviceIndex + 1]);
            else if (args.Contains("--numpad")) SwitchDevice("numpad");
            if (args.Contains("--inglorious-preset")) ApplyLightingPreset(LightingPresetStore.CreateInGlorious(Device.Id));
            if (args.Contains("--custom-cycle")) { Layer.LightingEffect = new() { Mode = 2, Colors = ["#FF243F", "#22040B", "#FFFFFF"] }; RefreshAll(); }
            if (args.Contains("--blue-yellow-sides"))
            {
                Layer.SideLedColor = "#E00000"; Layer.ExtraLedColors.Clear();
                foreach (var led in Device.NonKeyLedIndices)
                    Layer.LedEffects[led] = new() { Mode = 2, Speed = 164, Brightness = 240, Colors = ["#46C7F4", "#FFD447"] };
                RefreshAll();
            }
            if (args.Contains("--binding")) SetTab(1);
            if (args.Contains("--slider"))
            {
                var modeIndex = Array.IndexOf(args, "--slider-mode");
                if (Device.Id == "numpad" && modeIndex >= 0 && modeIndex + 1 < args.Length && int.TryParse(args[modeIndex + 1], out var mode) && mode is >= 0 and <= 3)
                { _project.Slider.Mode = mode; RefreshAll(); }
                ShowBindingTarget(true);
            }
            if (args.Contains("--macros")) SetTab(2);
            if (args.Contains("--layers")) SetTab(3);
            if (args.Contains("--side-left")) SetLightingTarget(LightingTarget.Left);
            if (args.Contains("--badge")) ChooseLightingSurface(LightingTarget.Badge);
            if (args.Contains("--side-leds")) SelectSideLed(Device.LeftSideLedIndices[0], toggle: false);
            if (args.Contains("--side-sync")) SetSideSynchronization(true);
            if (args.Contains("--effects")) SetLightingEditor(true);
            if (args.Contains("--standby")) { _project.Standby = new() { Enabled = true, Mode = args.Contains("--screensaver") ? (byte)2 : (byte)1 }; RefreshAll(); SetStandbyEditor(); }
            if (args.Contains("--whole-side")) SetWholeSideSelection(true);
            if (args.Contains("--effects-selected")) { SetLightingEditor(true); SetEffectScope(true); }
            if (args.Contains("--device-settings")) SetTab(4);
            if (args.Contains("--profiles")) SetTab(5);
        }
        if (_screenshotMode) await InspectBackupAsync(); // Offline verification never enumerates or opens hardware.
        else
        {
            _startupFinishing = true;
            try
            {
                await LoadStartupDevicesAsync();
                if (!_startupComplete || _startupClosed) return;
                await Task.WhenAll(ScanDevicesAsync(selectInitial: true), InspectBackupAsync());
                if (_startupClosed) return;
                if (IsVisible) await CheckFirmwareGuideAsync();
            }
            finally
            {
                _startupFinishing = false;
                if (!_startupClosed) EditorSurface.IsEnabled = true;
                StartSharedBrightnessIfReady();
                StartConnectionMonitor();
            }
        }
        if (_defaultLoadError is not null) Status(_defaultLoadError);
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--help"))
        {
            var topicIndex = Array.IndexOf(args, "--help-topic");
            var help = CreateHelpWindow(topicIndex >= 0 && topicIndex + 1 < args.Length ? args[topicIndex + 1] : null);
            if (args.Contains("--help-minimum")) { help.Width = help.MinWidth; help.Height = help.MinHeight; }
            help.ContentRendered += async (_, _) =>
            {
                await Task.Delay(150);
                try { SaveVisualScreenshot((FrameworkElement)help.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                finally { help.Close(); }
            };
            help.ShowDialog(); Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--backup"))
        {
            ShowBackupDialog(Path.GetFullPath(args[screenshotIndex + 1]));
            Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--transfer"))
        {
            try
            {
                var transferPreview = new TransferWindow(ProjectService.Clone(_project), Path.GetFullPath(args[screenshotIndex + 1])) { Owner = this };
                if (args.Contains("--transfer-single")) transferPreview.ShowOfflineTargetPreview();
                transferPreview.ShowDialog();
            }
            catch (Exception ex) { Directory.CreateDirectory(Paths.DataRoot); File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--color-picker"))
        {
            var picker = new ColorPickerWindow(ColorHex.Text) { Owner = this };
            picker.Loaded += async (_, _) =>
            {
                await Task.Delay(180);
                try { SaveVisualScreenshot((FrameworkElement)picker.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                picker.Close();
            };
            picker.ShowDialog(); Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--firmware-guide"))
        {
            var guidePreview = new FirmwareGuideDevice(Device.Id, FirmwareGuideState.UpdateRequired, args.Contains("--changelog"));
            if (args.Contains("--changelog")) guidePreview = guidePreview with { RuntimeVersion = new(new(Device.Id, new(0, 8, 0), 0x00080001), "USB\\VID_FEED&PID_" + (Device.Id == "pro-iso" ? "1C01" : "1C02") + "\\fixture", DateTimeOffset.UtcNow) };
            var guide = new FirmwareGuideWindow([guidePreview], Device.Id) { Owner = this };
            if (args.Contains("--guide-minimum")) { guide.Width = guide.MinWidth; guide.Height = guide.MinHeight; }
            guide.ContentRendered += async (_, _) =>
            {
                await Task.Delay(150);
                try { SaveVisualScreenshot((FrameworkElement)guide.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                finally { guide.Close(); }
            };
            guide.ShowDialog(); Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--device-setup"))
        {
            var stepIndex = Array.IndexOf(args, "--setup-step");
            var setup = new DeviceSetupWindow(Device.Id, args.Contains("--setup-restore") ? SetupOperation.Restore : args.Contains("--setup-update") ? SetupOperation.Update : SetupOperation.Install, offlinePreview: true) { Owner = this };
            setup.ShowOfflineStep(stepIndex >= 0 && stepIndex + 1 < args.Length ? args[stepIndex + 1] : "select", Device.Id);
            setup.Loaded += async (_, _) =>
            {
                await Task.Delay(150); setup.UpdateLayout();
                try { SaveVisualScreenshot((FrameworkElement)setup.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                setup.Close();
            };
            setup.ShowDialog(); Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--firmware"))
        {
            var operation = args.Contains("--firmware-install") ? FirmwareOperation.InstallFromOriginal : args.Contains("--firmware-restore") ? FirmwareOperation.RestoreOriginal : FirmwareOperation.Update;
            var manager = new FirmwareWindow(Device.Id, offlinePreview: true, initialOperation: operation) { Owner = this };
            if (args.Contains("--firmware-minimum")) { manager.Width = manager.MinWidth; manager.Height = manager.MinHeight; }
            manager.ShowOfflineStep(args.Contains("--firmware-confirm") ? "ready" : args.Contains("--firmware-connect") ? "connect" : args.Contains("--firmware-unplug") ? "unplug" : args.Contains("--firmware-reconnect") ? "reconnect" : args.Contains("--firmware-running") ? "running" : args.Contains("--firmware-complete") ? "complete" : "overview");
            if (args.Contains("--changelog")) manager.SetRuntimeVersionHint(new(new(Device.Id, new(0, 8, 0), 0x00080001), "USB\\VID_FEED&PID_" + (Device.Id == "pro-iso" ? "1C01" : "1C02") + "\\fixture", DateTimeOffset.UtcNow));
            if (args.Contains("--firmware-core-running")) manager.ShowOfflineCoreStatus(true);
            if (args.Contains("--firmware-error")) manager.ShowOfflineProtocolError();
            manager.Loaded += async (_, _) =>
            {
                await Task.Delay(180);
                try { SaveVisualScreenshot((FrameworkElement)manager.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                manager.Close();
            };
            manager.ShowDialog(); Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--recorder"))
        {
            var recorder = new MacroRecorderWindow { Owner = this };
            recorder.Loaded += async (_, _) =>
            {
                await Task.Delay(180);
                try { SaveVisualScreenshot((FrameworkElement)recorder.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                recorder.Close();
            };
            recorder.ShowDialog(); Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--unsaved-dialog"))
        {
            var dialog = new UnsavedChangesWindow(L.T("Ungespeicherte Profile"), L.T("Diese geänderten Profile vor dem Beenden speichern?\n\n"), ["INGLORIOUS · GMMK PRO ISO", "INGLORIOUS · GMMK Numpad"]) { Owner = this };
            dialog.Loaded += async (_, _) =>
            {
                await Task.Delay(180);
                try { SaveVisualScreenshot((FrameworkElement)dialog.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                dialog.Close();
            };
            dialog.ShowDialog(); Close(); return;
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length && args.Contains("--app-settings"))
        {
            var settings = new AppSettingsWindow(_preferences, offlinePreview: true, connectedDevices: ConnectedDevices) { Owner = this };
            settings.Loaded += async (_, _) =>
            {
                await Task.Delay(180);
                try { SaveVisualScreenshot((FrameworkElement)settings.Content, Path.GetFullPath(args[screenshotIndex + 1])); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                settings.Close();
            };
            settings.ShowDialog(); Close(); return;
        }
        if (smokeIndex >= 0 && smokeIndex + 1 < args.Length)
        {
            var smokePath = Path.GetFullPath(args[smokeIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(smokePath)!);
            try { File.WriteAllText(smokePath, await RunUiSmoke()); }
            catch (Exception ex) { File.WriteAllText(smokePath, "FAILED\n" + ex); Environment.ExitCode = 1; }
        }
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
        {
            try
            {
                if (args.Contains("--animation-proof"))
                {
                    await CaptureAnimationProofAsync(Path.GetFullPath(args[screenshotIndex + 1]));
                    Close(); return;
                }
                // Rendering uses the WPF visual tree; it does not synthesize device input.
                await Task.Delay(180);
                UpdateLayout();
                var visual = (FrameworkElement)Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.GetFullPath(args[screenshotIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(Paths.DataRoot);
                File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString());
                Environment.ExitCode = 1;
            }
            Close();
        }
        else if (smokeIndex >= 0) Close();
    }

    private void RefreshAll()
    {
        _refreshing = true;
        try
        {
            ProjectName.Text = _project.Name;
            BuildDeviceButtons();
            BuildLayerButtons();
            RenderKeyboard();
            RefreshMacros();
            RefreshDeviceSettings();
            RefreshStandbyControls();
            SetTab(_activeTab);
            UpdateDirtyState();
            RefreshProfiles();
        }
        finally { _refreshing = false; }
        RefreshInspector();
    }

    private void BuildDeviceButtons()
    {
        UpdateConnectedDeviceButtons();
        EmptyDeviceSetup.Visibility = ConnectedDevices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExperimentalSetupActions.Visibility = Visibility.Collapsed;
        var current = _devices.FirstOrDefault(d => d.IsSupported && d.DeviceId == _project.DeviceId);
        var offline = Device.IsOfflineOnly;
        ExperimentalDeviceNotice.Visibility = Device.IsExperimentalFirmware ? Visibility.Visible : Visibility.Collapsed;
        DeviceHardwareOverview.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
        OfflineDeviceOverview.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        DeviceModeText.Text = _connectionScanFailed ? L.T("Gerätesuche fehlgeschlagen") : ConnectedDevices.Length == 0 ? L.T("Kein Gerät verbunden")
            : IsActiveDeviceConnected ? L.T("USB verbunden") : L.T("Gerät nicht verbunden");
        DeviceModeText.Visibility = IsActiveDeviceConnected ? Visibility.Collapsed : Visibility.Visible;
        DeviceModeText.Foreground = Brush(IsActiveDeviceConnected ? "#75D9CF" : "#A0A7B0");
        ReadDeviceButton.IsEnabled = TransferButton.IsEnabled = !offline && IsActiveDeviceConnected;
        ManageFirmwareButton.IsEnabled = RestoreOriginalFirmwareButton.IsEnabled = !offline && IsActiveDeviceConnected;
        ManageFirmwareButton.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
        DeviceRecoveryOverview.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
        FirmwareInstallationNotice.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        foreach (var action in new[] { ManageFirmwareButton, RestoreOriginalFirmwareButton })
            action.ToolTip = !IsActiveDeviceConnected ? L.T("Gerät nicht verbunden") : null;
        ReadDeviceButton.ToolTip = TransferButton.ToolTip = offline ? L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar.")
            : !IsActiveDeviceConnected ? L.T(_connectionScanFailed ? "Gerätesuche fehlgeschlagen" : "Gerät nicht verbunden") : null;
        ConnectionText.Text = _screenshotMode ? L.T("Offline-Vorschau · USB-Status wird hier nicht geprüft.") : _scanning ? L.T("USB-Geräte werden geprüft …") : current is not null ? L.F($"USB erkannt · {current.VendorId}:{current.ProductId}") : L.T("Im normalen USB-Betrieb nicht erkannt. Lokaler Entwurf ist möglich.");
    }

    private void BuildLayerButtons()
    {
        _refreshingLayerNavigation = true;
        try
        {
            CurrentLayerChoice.ItemsSource = _project.Layers.Select((layer, index) => L.F($"Ebene {index} · {layer.Name}")).ToArray();
            CurrentLayerChoice.SelectedIndex = _layerIndex;
        }
        finally { _refreshingLayerNavigation = false; }
    }

    private void CurrentLayer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingLayerNavigation || CurrentLayerChoice.SelectedIndex < 0) return;
        SelectLayer(CurrentLayerChoice.SelectedIndex);
        // SelectionChanged fires after the UI moved. A canceled/invalid apply must restore it.
        _refreshingLayerNavigation = true;
        try { CurrentLayerChoice.SelectedIndex = _layerIndex; }
        finally { _refreshingLayerNavigation = false; }
    }

    private void RenderKeyboard()
    {
        BeginLightingPreviewRender();
        const double unit = 51;
        const double inset = 32;
        var topInset = Device.PreviewTopInset * unit;
        KeyboardCanvas.Children.Clear();
        _keyButtons.Clear();
        KeyboardCanvas.Width = Device.PreviewWidth * unit + inset * 2;
        KeyboardCanvas.Height = Device.PreviewHeight * unit;
        var largeLayout = Device.Id.StartsWith("gmmk3-100-", StringComparison.Ordinal);
        PreviewDeviceName.Text = Device.Name;
        PreviewLayerName.Text = L.F($"{Device.Subtitle} · Ebene {_layerIndex}");
        WorkspaceHeading.Text = _activeTab == 5 ? L.T("Profile") : Device.Name;
        ExperimentalDeviceLabel.Visibility = Device.IsExperimentalFirmware && _activeTab != 5 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var key in Device.Keys)
        {
            var assignment = Assignment(key.Id);
            var color = key.LedIndex is null ? Color.FromRgb(140, 149, 144) : ParseColor(assignment.Color);
            // Neutral legends remain readable even when the configured RGB is black or very dark.
            var capColor = Color.FromRgb((byte)(color.R * .055 + 34), (byte)(color.G * .055 + 37), (byte)(color.B * .055 + 41));
            var selected = _selection.Contains(key.Id) && (_activeTab != 0 || _lightingTarget == LightingTarget.Keys && (!_showLightingEffects || _effectForSelection));
            var content = new Grid { Margin = key.IsEncoder || key.Label == "Knob" ? new Thickness(0) : largeLayout ? new Thickness(4, 3, 4, 4) : new Thickness(6, 5, 6, 6), IsHitTestVisible = false };
            var displayLabel = key.DisplayLabel;
            if (largeLayout && key.DefaultBinding == "KC_SCRL") displayLabel = "ScrLk";
            var centered = key.Label == "Knob" || key.IsEncoder;
            var label = new TextBlock { Text = displayLabel, FontSize = key.IsEncoder ? 22 : key.Label == "Knob" ? (displayLabel.Length > 8 ? 8 : 9) : displayLabel.Length > 6 ? 10 : displayLabel.Length > 3 ? 11 : 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E4E7EB"), VerticalAlignment = centered ? VerticalAlignment.Center : VerticalAlignment.Top, HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left, TextWrapping = TextWrapping.NoWrap };
            if (largeLayout && !centered) label.FontSize *= 1.4;
            else if (largeLayout && key.Label == "Knob") label.FontSize *= 1.2;
            content.Children.Add(key.Label == "Knob" ? new Viewbox { Child = label, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(3), VerticalAlignment = VerticalAlignment.Center } : label);
            var bottom = new Border { Height = 2, CornerRadius = new CornerRadius(0), Background = new SolidColorBrush(color), Opacity = key.LedIndex is null ? .25 : .95, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0) };
            if (key.IsIsoEnter) bottom.Margin = new Thickness(.25 * unit, 0, 0, 0);
            if (key.LedIndex is not null) content.Children.Add(bottom);
            var inherited = assignment.Binding == "KC_TRNS";
            if (!centered && assignment.Binding != key.DefaultBinding && !inherited)
            {
                content.Children.Add(new TextBlock { Text = ShortBinding(assignment.Binding), FontSize = 8, Foreground = Brush("#C3C9D0"), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis });
            }
            else if (!centered && inherited)
                content.Children.Add(new TextBlock { Text = "▽", FontSize = 9, Foreground = Brush("#A0A7B0"), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4) });
            var button = new Button
            {
                Width = Math.Max(20, (key.W + (key.IsIsoEnter ? .25 : 0)) * unit - 5), Height = Math.Max(20, key.H * unit - 5),
                Content = content, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(capColor), BorderBrush = selected ? Brush("#EF646B") : Brush("#424950"), BorderThickness = new Thickness(selected ? 2 : 1),
                ToolTip = L.F($"{key.AccessibleLabel}\n{BindingLabel(assignment.Binding)} · {(key.LedIndex is null ? L.T("Keine eigene RGB-LED") : assignment.Color)}\nUmschalt+Klick: zur Auswahl hinzufügen"), Focusable = false,
                Template = (ControlTemplate)FindResource("KeycapTemplate")
            };
            if (key.LedIndex is int led) RegisterPreviewLed(led, bottom, button);
            System.Windows.Automation.AutomationProperties.SetName(button, key.AccessibleLabel);
            button.Click += (_, _) => { PreviewKeyHit(key.Id); SelectKey(key.Id); };
            if (key.Label == "Knob") button.Template = (ControlTemplate)FindResource("RoundKeyTemplate");
            if (key.IsIsoEnter)
            {
                // QMK's 1.25 × 2 ISO definition includes a 0.25u extension on the upper left.
                button.Tag = IsoEnterOutline(button.Width, button.Height, unit, 1);
                button.Clip = IsoEnterOutline(button.Width, button.Height, unit, 0);
                button.Template = (ControlTemplate)FindResource("IsoEnterTemplate");
            }
            Canvas.SetLeft(button, inset + (key.X - (key.IsIsoEnter ? .25 : 0)) * unit);
            Canvas.SetTop(button, topInset + key.Y * unit);
            KeyboardCanvas.Children.Add(button);
            _keyButtons[key.Id] = button;
        }
        RenderSideStrip(Device.LeftSideLedIndices, LightingTarget.Left, 4, unit, topInset);
        RenderSideStrip(Device.RightSideLedIndices, LightingTarget.Right, inset + Device.PreviewWidth * unit + 12, unit, topInset);
        if (Device.BadgeGeometry is { } geometry)
        {
            var content = new Grid { Margin = new Thickness(5), IsHitTestVisible = false };
            content.Children.Add(new TextBlock { Text = L.T("Logo"), Foreground = Brush("#C3C9D0"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = geometry.IsHorizontal ? VerticalAlignment.Center : VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis });
            FrameworkElement badge;
            if (geometry.LedIndex is int led)
            {
                var light = new Border { Background = Brush(SideLedColor(led)), Height = geometry.IsHorizontal ? 5 : 25, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2) };
                content.Children.Add(light);
                var button = new Button { Name = "BadgeLed" + led, Content = content, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, ToolTip = L.T("Logo-Beleuchtung"), Background = Brush("#252A2F"), BorderBrush = Brush(_activeTab == 0 && _lightingTarget == LightingTarget.Badge && (!_showLightingEffects || _effectForSelection) ? "#EF646B" : "#424950") };
                System.Windows.Automation.AutomationProperties.SetName(button, L.T("Logo-Beleuchtung"));
                button.Click += (_, _) => ChooseLightingSurface(LightingTarget.Badge);
                RegisterPreviewLed(led, light);
                badge = button;
            }
            else
            {
                badge = new Border { Name = "PassiveBadge", Child = content, Background = Brush("#252A2F"), BorderBrush = Brush("#424950"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ToolTip = L.T("Keine eigene RGB-LED"), Focusable = false };
                System.Windows.Automation.AutomationProperties.SetName(badge, L.T("Logo"));
            }
            badge.Width = geometry.W * unit - 4;
            badge.Height = geometry.H * unit - 4;
            Canvas.SetLeft(badge, inset + geometry.X * unit);
            Canvas.SetTop(badge, topInset + geometry.Y * unit);
            KeyboardCanvas.Children.Add(badge);
        }
        if (Device.Id == "numpad")
        {
            var track = new Border { Width = 8, Height = 118, Background = Brush("#141618"), BorderBrush = Brush("#424950"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ToolTip = L.T("Schieberegler im lokalen Entwurf: ") + SliderModeLabel(_project.Slider.Mode), Child = new Border { Width = 20, Height = 13, CornerRadius = new CornerRadius(3), Background = Brush(_project.Slider.Mode == 0 ? "#9199A2" : "#EF646B"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            var sliderButton = new Button { Content = track, Width = 38, Height = 142, Padding = new Thickness(0), Background = Brushes.Transparent, BorderBrush = _activeTab == 1 && _editingSlider ? Brush("#EF646B") : Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L.T("Schieberegler anpassen") };
            System.Windows.Automation.AutomationProperties.SetName(sliderButton, L.T("Schieberegler anpassen"));
            sliderButton.Click += (_, _) => ShowBindingTarget(true);
            Canvas.SetLeft(sliderButton, inset + (Device.Width - .55) * unit - 15); Canvas.SetTop(sliderButton, topInset + 1.65 * unit - 12); KeyboardCanvas.Children.Add(sliderButton);
            var sliderLabel = new TextBlock { Text = _project.Slider.Mode switch { 1 => "MIDI", 2 => L.T("Lautstärke"), 3 => L.T("Helligkeit"), _ => L.T("Regler\naus") }, FontSize = 8, Foreground = Brush("#A0A7B0"), TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(sliderLabel, inset + (Device.Width - .91) * unit); Canvas.SetTop(sliderLabel, topInset + 4.2 * unit); KeyboardCanvas.Children.Add(sliderLabel);
        }
        EndLightingPreviewRender();
    }

    private static Geometry IsoEnterOutline(double width, double height, double unit, double inset) => Geometry.Parse(FormattableString.Invariant(
        $"M {inset},{inset} L {width-inset},{inset} {width-inset},{height-inset} {.25*unit+inset},{height-inset} {.25*unit+inset},{unit-5-inset} {inset},{unit-5-inset} Z"));

    private string SideLedColor(int index) => Layer.ExtraLedColors.TryGetValue(index, out var color) ? color : Layer.SideLedColor;

    private void RenderSideStrip(IReadOnlyList<int> leds, LightingTarget side, double x, double unit, double topInset)
    {
        if (leds.Count == 0) return;
        var segments = new System.Windows.Controls.Primitives.UniformGrid { Columns = 1, Width = 12, Height = Math.Max(30, (Device.KeyAreaHeight - .2) * unit) };
        foreach (var led in leds)
        {
            var selected = _activeTab == 0 && (!_showLightingEffects || _effectForSelection) && SideIndices(_lightingTarget).Contains(led);
            var strip = new Button { Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, BorderBrush = selected ? Brush("#EF646B") : Brushes.Transparent, BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Content = new Border { Background = Brush(SideLedColor(led)), IsHitTestVisible = false }, ToolTip = SideLedLabel(led) + " · " + SideLedColor(led) + L.T("\nUmschalt + Klick: Mehrfachauswahl") };
            RegisterPreviewLed(led, (Border)strip.Content);
            strip.Click += (_, _) => SelectSideLed(led);
            System.Windows.Automation.AutomationProperties.SetName(strip, SideLedLabel(led));
            segments.Children.Add(strip);
        }
        Canvas.SetLeft(segments, x); Canvas.SetTop(segments, topInset + 4); KeyboardCanvas.Children.Add(segments);
    }

    private string SideLedLabel(int index)
    {
        var left = Device.LeftSideLedIndices.ToList().IndexOf(index);
        return left >= 0 ? L.F($"Links · LED {left + 1}") : L.F($"Rechts · LED {Device.RightSideLedIndices.ToList().IndexOf(index) + 1}");
    }

    private void SelectSideLed(int index, bool? toggle = null)
    {
        if (!Device.SideLedIndices.Contains(index)) return;
        if (!ResolvePendingEdits()) return;
        if (_wholeSideSelection)
        {
            _lightingTarget = Device.LeftSideLedIndices.Contains(index) ? LightingTarget.Left : LightingTarget.Right;
            _selectedSideLeds.Clear();
            if (_showLightingEffects) _effectForSelection = true;
            SetTab(0);
            return;
        }
        var multiple = toggle ?? (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
        var previous = SideIndices(_lightingTarget).ToArray();
        _selectedSideLeds.Clear();
        if (multiple) _selectedSideLeds.UnionWith(previous);
        var pair = Layer.SynchronizeSides ? WithSidePartners([index]) : new[] { index };
        if (multiple && pair.All(_selectedSideLeds.Contains)) _selectedSideLeds.ExceptWith(pair);
        else _selectedSideLeds.UnionWith(pair);
        _lightingTarget = LightingTarget.SelectedLeds;
        if (_showLightingEffects) _effectForSelection = true;
        SetTab(0);
    }

    private void RenderSideLedRows()
    {
        SideLedSelectionPanel.Visibility = _lightingTarget is LightingTarget.Keys or LightingTarget.Badge || _wholeSideSelection ? Visibility.Collapsed : Visibility.Visible;
        SideLedRows.Children.Clear();
        if (_lightingTarget is LightingTarget.Keys or LightingTarget.Badge) return;
        foreach (var (name, leds) in new[] { (L.T("Links"), Device.LeftSideLedIndices), (L.T("Rechts"), Device.RightSideLedIndices) })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = name, Foreground = Brush("#A0A7B0"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var buttons = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
            for (var i = 0; i < leds.Count; i++)
            {
                var led = leds[i]; var color = ParseColor(SideLedColor(led));
                var selected = SideIndices(_lightingTarget).Contains(led);
                var button = new Button { Content = (i + 1).ToString(CultureInfo.InvariantCulture), Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0), FontSize = 10, Background = selected ? Brush("#3A252B") : Brush("#252A2F"), Foreground = Brush("#E4E7EB"), BorderBrush = selected ? Brush("#EF646B") : Brush("#424950"), BorderThickness = new Thickness(selected ? 2 : 1), ToolTip = SideLedLabel(led) + " · " + SideLedColor(led) };
                var cap = new Grid { Margin = new Thickness(3, 1, 3, 2), IsHitTestVisible = false };
                cap.Children.Add(new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
                cap.Children.Add(new Border { Height = 3, Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Bottom });
                button.Content = cap; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
                button.Click += (_, _) => SelectSideLed(led);
                System.Windows.Automation.AutomationProperties.SetName(button, SideLedLabel(led));
                buttons.Children.Add(button);
            }
            Grid.SetColumn(buttons, 1); row.Children.Add(buttons); SideLedRows.Children.Add(row);
        }
    }

    private KeyAssignment Assignment(string keyId)
    {
        if (Layer.Keys.TryGetValue(keyId, out var assignment)) return assignment;
        // Incomplete older projects are validated by ProjectService; this fallback only keeps the view usable.
        return new KeyAssignment { Binding = _layerIndex == 0 ? Device.Keys.First(k => k.Id == keyId).DefaultBinding : "KC_TRNS", Color = "#A38BFA" };
    }

    private string BindingLabel(string binding)
    {
        if (binding.StartsWith("MACRO:", StringComparison.Ordinal)) return L.F($"Makro: {_project.Macros.FirstOrDefault(m => m.Id == binding[6..])?.Name ?? L.T("fehlt")}");
        if (binding.StartsWith("MO(", StringComparison.Ordinal)) return L.F($"Ebene {binding[3..].TrimEnd(')')} halten");
        if (binding.StartsWith("TG(", StringComparison.Ordinal)) return L.F($"Ebene {binding[3..].TrimEnd(')')} umschalten");
        return BindingCatalog.FunctionLabel(binding);
    }

    private static string ShortBinding(string binding) => binding.StartsWith("MACRO:", StringComparison.Ordinal) ? L.T("MAKRO") : binding.Replace("KC_", "", StringComparison.Ordinal);

    private void SelectKey(string keyId)
    {
        if (!ResolvePendingEdits()) return;
        _editingSlider = false;
        if (_activeTab == 0 && _showLightingEffects) _effectForSelection = true;
        _lightingTarget = LightingTarget.Keys;
        _selectedSideLeds.Clear();
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0)
        {
            if (!_selection.Add(keyId)) _selection.Remove(keyId);
        }
        else { _selection.Clear(); _selection.Add(keyId); }
        RenderKeyboard(); RefreshInspector();
    }

    private void RefreshInspector()
    {
        var keys = Device.Keys.Where(k => _selection.Contains(k.Id)).ToList();
        SelectionText.Text = keys.Count switch { 0 => L.T("Keine Taste ausgewählt"), 1 => L.F($"{keys[0].Label} ausgewählt"), _ => L.F($"{keys.Count} Tasten ausgewählt") };
        InspectorTitle.Text = keys.Count switch { 0 => L.T("Taste auswählen"), 1 => keys[0].Label, _ => L.F($"{keys.Count} Tasten") };
        InspectorSubtitle.Text = keys.Count switch { 0 => L.T("Wähle eine Taste in der Vorschau."), 1 => L.F($"Ebene {_layerIndex} · {BindingLabel(Assignment(keys[0].Id).Binding)}{(keys[0].LedIndex is null ? L.T(" · Keine eigene RGB-LED") : "")}"), _ => L.F($"Gemeinsam bearbeiten · Ebene {_layerIndex}") };
        if (keys.Count > 0)
        {
            var colors = keys.Select(k => Assignment(k.Id).Color).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (colors.Count == 1) ColorHex.Text = colors[0];
            if (FindName("SelectedColorChip") is Border chip) chip.Background = colors.Count == 1 ? Brush(colors[0]) : Brush("#353A40");
            if (FindName("SelectedColorLabel") is TextBlock colorLabel) colorLabel.Text = keys.All(k => k.LedIndex is null) ? L.T("Keine RGB-LED") : colors.Count == 1 ? colors[0].ToUpperInvariant() : L.T("Mehrere Farben");
            var bindings = keys.Select(k => Assignment(k.Id).Binding).Distinct().ToList();
            if (bindings.Count == 1) BindingChoice.SelectedValue = bindings[0];
            else BindingChoice.SelectedIndex = -1;
        }
        else
        {
            if (FindName("SelectedColorChip") is Border chip) chip.Background = Brush("#353A40");
            if (FindName("SelectedColorLabel") is TextBlock colorLabel) colorLabel.Text = L.T("Keine Auswahl");
        }
        LayerName.Text = Layer.Name;
        for (var i = 0; i < TargetLayer.Items.Count; i++) ((ComboBoxItem)TargetLayer.Items[i]).Content = L.F($"Ebene {i} · {_project.Layers[i].Name}");
        RefreshLightingTarget();
        RefreshEffectControls();
        RefreshStandbyControls();
        RefreshLayerTools();
        CurrentBindingText.Text = keys.Count == 0 ? L.T("Zuerst eine Taste in der Vorschau auswählen.") : L.T("Aktuell: ") + string.Join(" / ", keys.Select(k => BindingLabel(Assignment(k.Id).Binding)).Distinct());
        MacroAssignmentHint.Text = _macroAssigning
            ? keys.Count == 0 ? L.T("Eine oder mehrere Tasten in der Vorschau auswählen.") : string.Join(", ", keys.Select(k => k.AccessibleLabel)) + L.F($" · Ebene {_layerIndex}")
            : L.T("Wähle nach dem Bearbeiten die Taste, die das Makro auslösen soll.");
        AssignMacroButton.Content = _macroAssigning ? L.T("Makro zuweisen") : L.T("Taste auswählen …");
        AssignMacroButton.IsEnabled = CurrentMacro?.Steps.Count > 0 && (!_macroAssigning || keys.Count > 0);
        AssignBindingButton.IsEnabled = ResetBindingButton.IsEnabled = DisableBindingButton.IsEnabled = keys.Count > 0;
        ApplyColorButton.IsEnabled = SelectedEffectLeds().Length > 0;
        ApplyEffectHost.Visibility = _activeTab == 0 && _showLightingEffects ? Visibility.Visible : Visibility.Collapsed;
        RefreshStandbySurface();
        RefreshWorkspaceMode();
        if (_activeTab == 2)
        {
            InspectorTitle.Text = CurrentMacro?.Name ?? L.T("Neues Makro");
            InspectorSubtitle.Text = _macroAssigning ? L.T("Zuweisung auf der aktuellen Ebene") : L.T("Schritte und Zuweisung");
        }
        if (_activeTab == 4)
        {
            InspectorTitle.Text = L.T("Reaktionszeit");
            InspectorSubtitle.Text = Device.Name + L.T(" · alle Ebenen");
        }
        RefreshBindingTarget();
        RefreshLightingPreview();
    }

    private void KeyBindings_Click(object sender, RoutedEventArgs e) => ShowBindingTarget(false);
    private void SliderBindings_Click(object sender, RoutedEventArgs e) => ShowBindingTarget(true);
    private void ShowBindingTarget(bool slider)
    {
        if (_readingDevice || !ResolvePendingEdits()) return;
        _editingSlider = slider && Device.Id == "numpad";
        if (_editingSlider) { _selection.Clear(); _selectedSideLeds.Clear(); _lightingTarget = LightingTarget.Keys; }
        SetTab(1);
        if (_editingSlider) { SliderPanel.BringIntoView(); SliderModeChoice.Focus(); }
        else KeyBindingPanel.BringIntoView();
    }
    private void RefreshBindingTarget()
    {
        var slider = _editingSlider && Device.Id == "numpad";
        BindingTargetButtons.Visibility = Device.Id == "numpad" ? Visibility.Visible : Visibility.Collapsed;
        KeyBindingPanel.Visibility = slider ? Visibility.Collapsed : Visibility.Visible;
        SliderPanel.Visibility = slider ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, selected) in new[] { (KeyBindingsButton, !slider), (SliderBindingsButton, slider) })
        {
            button.Background = Brush(selected ? "#3A252B" : "#252A2F");
            button.BorderBrush = Brush(selected ? "#A34F59" : "#353A40");
        }
        if (_activeTab == 1 && slider)
        {
            InspectorTitle.Text = L.T("Schieberegler");
            InspectorSubtitle.Text = L.T("Eine Funktion für alle Ebenen.");
            SelectionText.Text = L.T("Schieberegler");
        }
    }

    private void AddColorSwatches()
    {
        foreach (var color in new[] { "#FFE501", "#62F6FF", "#E9B86B", "#75D9CF", "#F08F77", "#CB9BF4", "#A6CF87", "#C6D1DF", "#FFFFFF", "#000000" })
        {
            var swatch = new Button { Background = Brush(color), BorderBrush = Brush(color == "#000000" ? "#626C77" : color), Width = 38, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 9, 9), ToolTip = color == "#000000" ? L.T("Licht aus") : color, Tag = "ColorSwatch" };
            swatch.Click += (_, _) => { ColorHex.Text = color; ApplyLightingColor(color); };
            ColorSwatches.Children.Add(swatch);
        }
    }

    private IReadOnlyList<int> SideIndices(LightingTarget target)
    {
        IReadOnlyList<int> indices = target switch
        {
            LightingTarget.Left => Device.LeftSideLedIndices,
            LightingTarget.Right => Device.RightSideLedIndices,
            LightingTarget.Both => Device.SideLedIndices,
            LightingTarget.Badge => Device.BadgeLedIndices,
            LightingTarget.SelectedLeds => Device.LeftSideLedIndices.Concat(Device.RightSideLedIndices).Where(_selectedSideLeds.Contains).ToArray(),
            _ => Array.Empty<int>()
        };
        return Layer.SynchronizeSides && target != LightingTarget.Badge ? WithSidePartners(indices) : indices;
    }

    private int[] WithSidePartners(IEnumerable<int> indices)
    {
        var result = indices.ToHashSet();
        for (var i = 0; i < Math.Min(Device.LeftSideLedIndices.Count, Device.RightSideLedIndices.Count); i++)
        {
            var left = Device.LeftSideLedIndices[i]; var right = Device.RightSideLedIndices[i];
            if (result.Contains(left) || result.Contains(right)) { result.Add(left); result.Add(right); }
        }
        return Device.LeftSideLedIndices.Concat(Device.RightSideLedIndices).Where(result.Contains).ToArray();
    }

    private void SideSync_Click(object sender, RoutedEventArgs e) => SetSideSynchronization(SideSyncToggle.IsChecked == true);

    private void WholeSide_Click(object sender, RoutedEventArgs e) => SetWholeSideSelection(WholeSideToggle.IsChecked == true);
    private void SetWholeSideSelection(bool enabled)
    {
        if (enabled != _wholeSideSelection && !ResolvePendingEdits()) { WholeSideToggle.IsChecked = _wholeSideSelection; return; }
        if (!enabled && _wholeSideSelection && _lightingTarget is LightingTarget.Left or LightingTarget.Right or LightingTarget.Both)
        {
            var selected = SideIndices(_lightingTarget).ToArray();
            _selectedSideLeds.Clear(); _selectedSideLeds.UnionWith(selected);
            _lightingTarget = LightingTarget.SelectedLeds;
        }
        _wholeSideSelection = enabled;
        if (enabled && _lightingTarget == LightingTarget.SelectedLeds)
        {
            var left = _selectedSideLeds.Any(Device.LeftSideLedIndices.Contains);
            var right = _selectedSideLeds.Any(Device.RightSideLedIndices.Contains);
            if (left || right) _lightingTarget = left && right ? LightingTarget.Both : left ? LightingTarget.Left : LightingTarget.Right;
            _selectedSideLeds.Clear();
        }
        RenderKeyboard(); RefreshInspector();
    }

    private void SetSideSynchronization(bool enabled)
    {
        if (Layer.SynchronizeSides == enabled) return;
        if (!ResolvePendingEdits()) { SideSyncToggle.IsChecked = Layer.SynchronizeSides; return; }
        BeginChange();
        Layer.SynchronizeSides = enabled;
        if (enabled)
            for (var i = 0; i < Math.Min(Device.LeftSideLedIndices.Count, Device.RightSideLedIndices.Count); i++)
            {
                Layer.ExtraLedColors[Device.RightSideLedIndices[i]] = SideLedColor(Device.LeftSideLedIndices[i]);
                if (Layer.LedEffects.TryGetValue(Device.LeftSideLedIndices[i], out var effect))
                    Layer.LedEffects[Device.RightSideLedIndices[i]] = CopyLedEffect(effect);
                else Layer.LedEffects.Remove(Device.RightSideLedIndices[i]);
            }
        FinishChange(enabled
            ? L.F($"Seiten synchron auf Ebene {_layerIndex}. Linkes Muster übernommen; weitere Änderungen gelten paarweise.")
            : L.F($"Seiten auf Ebene {_layerIndex} getrennt bearbeiten. Farben und Effekte bleiben erhalten."));
    }

    private void LightingTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<LightingTarget>(name, out var target)) ChooseLightingSurface(target);
    }

    private void SidesLight_Click(object sender, RoutedEventArgs e)
    {
        if (_lightingTarget is LightingTarget.Keys or LightingTarget.Badge) ChooseLightingSurface(_lastSideTarget);
    }

    private void ChooseLightingSurface(LightingTarget target)
    {
        if (target == LightingTarget.Badge && Device.BadgeLedIndices.Count == 0) return;
        if (!ResolvePendingEdits()) return;
        if (target == LightingTarget.Badge) { _selection.Clear(); _wholeSideSelection = false; _showLightingStandby = false; }
        if (target is LightingTarget.Left or LightingTarget.Right or LightingTarget.Both)
        { _wholeSideSelection = true; _lastSideTarget = target; }
        SetLightingTarget(target);
        if (target == LightingTarget.Badge) SetTab(0);
    }

    private void SetLightingTarget(LightingTarget target)
    {
        if (!ResolvePendingEdits()) return;
        _lightingTarget = target;
        if (_showLightingEffects) _effectForSelection = true;
        _selectedSideLeds.Clear();
        RenderKeyboard(); RefreshInspector();
    }

    private void RefreshLightingTarget()
    {
        if (_lightingTarget == LightingTarget.Badge && Device.BadgeLedIndices.Count == 0) _lightingTarget = LightingTarget.Keys;
        var sides = _lightingTarget is LightingTarget.Left or LightingTarget.Right or LightingTarget.Both or LightingTarget.SelectedLeds;
        BadgeLightButton.Visibility = Device.BadgeLedIndices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SideChoicePanel.Visibility = sides ? Visibility.Visible : Visibility.Collapsed;
        SidesLightButton.Background = Brush(sides ? "#3A252B" : "#252A2F");
        SidesLightButton.BorderBrush = Brush(sides ? "#A74B54" : "#353A40");
        SidesLightButton.Foreground = Brush(sides ? "#FFA3A8" : "#A0A7B0");
        // Click handles mutations. Refreshing the switch never changes the project or undo stack.
        SideSyncToggle.Visibility = WholeSideToggle.Visibility = sides ? Visibility.Visible : Visibility.Collapsed;
        SideSyncToggle.IsChecked = Layer.SynchronizeSides;
        WholeSideToggle.IsChecked = _wholeSideSelection;
        WholeSideHint.Text = _wholeSideSelection ? L.T("Ein Klick wählt die ganze Leiste. Farben und Effekte gelten für alle ihre LEDs.") : L.T("Ein Klick wählt einzelne LEDs. Umschalt fügt weitere hinzu.");
        SideSyncHint.Text = Layer.SynchronizeSides
            ? L.T("Gleiche LED-Position, gleiche Farbe und gleicher Effekt. Gilt für diese Ebene.")
            : L.T("Beim Einschalten: linke Seite auf rechte Seite kopieren.");
        var displayedTarget = _lightingTarget;
        if (sides)
        {
            var selected = SideIndices(_lightingTarget);
            var left = selected.Any(Device.LeftSideLedIndices.Contains);
            var right = selected.Any(Device.RightSideLedIndices.Contains);
            if (left || right) displayedTarget = left && right ? LightingTarget.Both : left ? LightingTarget.Left : LightingTarget.Right;
        }
        var targets = new[] { (KeysLightButton, LightingTarget.Keys), (LeftLightButton, LightingTarget.Left), (RightLightButton, LightingTarget.Right), (BothLightButton, LightingTarget.Both), (BadgeLightButton, LightingTarget.Badge) };
        foreach (var (button, target) in targets)
        {
            button.Background = Brush(target == displayedTarget ? "#3A252B" : "#252A2F");
            button.BorderBrush = Brush(target == displayedTarget ? "#A74B54" : "#353A40");
            button.Foreground = Brush(target == displayedTarget ? "#FFA3A8" : "#A0A7B0");
        }
        LightingTargetHint.Text = _lightingTarget == LightingTarget.Keys
            ? L.F($"Ausgewählte Tasten · Ebene {_layerIndex}.")
            : _lightingTarget == LightingTarget.Badge ? L.T("Logo-Beleuchtung")
            : L.F($"Gehäuse-LEDs: {SideIndices(_lightingTarget).Count} · Ebene {_layerIndex}.") + (Layer.SynchronizeSides ? L.T(" Beide Seiten werden gemeinsam geändert.") : L.T(" Anwenden färbt nur diesen Bereich."));
        RenderSideLedRows();
        if (_activeTab != 0 || _lightingTarget == LightingTarget.Keys) return;
        var indices = SideIndices(_lightingTarget);
        var label = _lightingTarget switch { LightingTarget.Badge => L.T("Logo-Beleuchtung"), LightingTarget.Left => L.T("Seite links"), LightingTarget.Right => L.T("Seite rechts"), LightingTarget.Both => L.T("Beide Seiten"), _ => indices.Count == 1 ? SideLedLabel(indices[0]) : L.F($"{indices.Count} Seiten-LEDs") };
        if (sides && Layer.SynchronizeSides && indices.Count > 0) label = indices.Count == 2 ? L.F($"LED {Device.LeftSideLedIndices.ToList().IndexOf(indices[0]) + 1} · beide Seiten") : L.T("Beide Seiten");
        InspectorTitle.Text = label;
        InspectorSubtitle.Text = L.F($"Gehäusebeleuchtung · Ebene {_layerIndex}");
        SelectionText.Text = L.F($"{label} ausgewählt");
        var colors = indices.Select(SideLedColor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SelectedColorLabel.Text = colors.Count == 1 ? colors[0].ToUpperInvariant() : colors.Count == 0 ? L.T("Keine Auswahl") : L.T("Mehrere Farben");
        SelectedColorChip.Background = colors.Count == 1 ? Brush(colors[0]) : Brush("#353A40");
        if (colors.Count == 1) ColorHex.Text = colors[0];
    }

    private void SetLightingEditor(bool effects)
    {
        if ((effects != _showLightingEffects || _showLightingStandby) && !ResolvePendingEdits()) return;
        if (effects && !_showLightingEffects) _effectForSelection = SelectedEffectLeds().Length > 0;
        _showLightingEffects = effects;
        _showLightingStandby = false;
        RenderKeyboard();
        RefreshInspector();
    }

    private void LightColors_Click(object sender, RoutedEventArgs e) => SetLightingEditor(false);
    private void LightEffects_Click(object sender, RoutedEventArgs e) => SetLightingEditor(true);

    private int[] SelectedEffectLeds() => _lightingTarget == LightingTarget.Keys
        ? Device.Keys.Where(k => _selection.Contains(k.Id) && k.LedIndex.HasValue).Select(k => k.LedIndex!.Value).Distinct().ToArray()
        : SideIndices(_lightingTarget).ToArray();

    private static PerLedEffectSettings CopyLedEffect(PerLedEffectSettings value) => new()
    { Mode = value.Mode, Speed = value.Speed, Brightness = value.Brightness, Reverse = value.Reverse, Colors = value.Colors.ToList() };

    private void SetEffectScope(bool selection) { if (selection != _effectForSelection && !ResolvePendingEdits()) return; _effectForSelection = selection; RenderKeyboard(); RefreshInspector(); }
    private void GlobalEffect_Click(object sender, RoutedEventArgs e) => SetEffectScope(false);
    private void SelectedEffect_Click(object sender, RoutedEventArgs e) => SetEffectScope(true);

    private void RefreshEffectControlsCore()
    {
        _refreshingEffects = true;
        try
        {
            var effect = Layer.LightingEffect;
            var leds = SelectedEffectLeds();
            var selectedEffects = leds.Select(i => Layer.LedEffects.GetValueOrDefault(i)).ToArray();
            var common = selectedEffects.Length > 0 && selectedEffects.All(e => JsonSerializer.Serialize(e) == JsonSerializer.Serialize(selectedEffects[0]));
            var own = _effectForSelection && common ? selectedEffects[0] : null;
            EffectChoice.SelectedValue = own?.Mode ?? effect.Mode;
            EffectSpeed.Value = own?.Speed ?? effect.Speed; EffectBrightness.Value = own?.Brightness ?? effect.Brightness;
            SetEffectColors(own?.Colors ?? effect.Colors);
            EffectTarget.SelectedIndex = effect.Target - 1; EffectReverse.IsChecked = own?.Reverse ?? effect.Reverse;
            EffectTargetPanel.Visibility = _effectForSelection ? Visibility.Collapsed : Visibility.Visible;
            InheritEffectButton.Visibility = _effectForSelection ? Visibility.Visible : Visibility.Collapsed;
            InheritEffectButton.IsEnabled = leds.Any(Layer.LedEffects.ContainsKey);
            ApplyEffectButton.IsEnabled = !_effectForSelection || leds.Length > 0;
            ApplyEffectButton.Content = _effectForSelection ? L.T("Für Auswahl übernehmen") : L.T("Globalen Effekt übernehmen");
            EffectScopeHint.Text = !_effectForSelection
                ? L.F($"Ebene {_layerIndex} · Gilt für LEDs ohne eigenen Effekt. {Layer.LedEffects.Count} LEDs haben eigene Einstellungen.")
                : leds.Length == 0 ? L.T("Tasten oder Seiten-LEDs in der Vorschau auswählen.")
                : !common ? L.F($"{leds.Length} LEDs · Unterschiedliche Einstellungen. Übernehmen setzt für alle den gewählten Effekt.")
                : own is null ? L.F($"{leds.Length} LEDs · Verwenden den globalen Effekt. Übernehmen legt eigene Einstellungen fest.")
                : L.F($"{leds.Length} LEDs · Eigener Effekt. Tempo und Helligkeit gelten nur für diese Auswahl.");
            StaticColorsPanel.Visibility = _showLightingEffects ? Visibility.Collapsed : Visibility.Visible;
            EffectsPanel.Visibility = _showLightingEffects ? Visibility.Visible : Visibility.Collapsed;
            EffectScopeButtons.Visibility = EffectsPanel.Visibility;
            LightingSelectionPanel.Visibility = !_showLightingEffects || _effectForSelection ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (button, active) in new[] { (LightColorsTab, !_showLightingEffects), (LightEffectsTab, _showLightingEffects), (GlobalEffectButton, !_effectForSelection), (SelectedEffectButton, _effectForSelection) })
            {
                button.Background = Brush(active ? "#3A252B" : "#252A2F");
                button.BorderBrush = Brush(active ? "#A74B54" : "#353A40");
            }
            if (_activeTab == 0 && _showLightingEffects)
            {
                InspectorTitle.Text = _effectForSelection ? L.F($"Effekt · {leds.Length} LEDs") : L.F($"Global · Ebene {_layerIndex}");
                InspectorSubtitle.Text = _effectForSelection ? L.T("Tasten und Seiten-LEDs in der Vorschau auswählen.") : LightingEffects.All.First(o => o.Id == effect.Mode).Label;
            }
        }
        finally { _refreshingEffects = false; }
        UpdateEffectDescription(); UpdateEffectSliderLabels();
    }

    private void EffectChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingEffects && EffectDescription is not null) UpdateEffectDescription();
    }

    private void UpdateEffectDescription()
    {
        if (EffectChoice.SelectedItem is not LightingEffectOption option) return;
        EffectDescription.Text = option.Description;
        if (EffectColorsPanel is not null) EffectColorsPanel.Visibility = option.Id == 2 ? Visibility.Visible : Visibility.Collapsed;
        EffectSpeed.IsEnabled = option.Id is not 0 and not 13;
        EffectReverse.IsEnabled = option.HasDirection;
    }

    private void EffectSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateEffectSliderLabels();

    private void UpdateEffectSliderLabels()
    {
        if (EffectSpeedValue is null || EffectBrightnessValue is null || EffectSpeed is null || EffectBrightness is null) return;
        EffectSpeedValue.Text = $"{Math.Round(EffectSpeed.Value / 255 * 100)} %";
        EffectBrightnessValue.Text = $"{Math.Round(EffectBrightness.Value / 255 * 100)} %";
    }

    private bool TryApplyEffect()
    {
        if (EffectChoice.SelectedItem is not LightingEffectOption option) return false;
        var effect = new LightingEffectSettings
        {
            Mode = option.Id, Speed = option.Id == 0 ? 128 : (int)Math.Round(EffectSpeed.Value),
            Brightness = (int)Math.Round(EffectBrightness.Value), Target = EffectTarget.SelectedIndex + 1,
            Reverse = option.HasDirection && EffectReverse.IsChecked == true, Colors = _effectColors.ToList()
        };
        if (_effectForSelection)
        {
            var leds = SelectedEffectLeds();
            if (leds.Length == 0) { Status(L.T("Bitte mindestens eine Taste oder Seiten-LED auswählen.")); return false; }
            var selected = new PerLedEffectSettings { Mode = effect.Mode, Speed = effect.Speed, Brightness = effect.Brightness, Reverse = effect.Reverse, Colors = effect.Colors.ToList() };
            if (leds.All(i => JsonSerializer.Serialize(Layer.LedEffects.GetValueOrDefault(i)) == JsonSerializer.Serialize(selected))) return true;
            BeginChange();
            foreach (var led in leds) Layer.LedEffects[led] = CopyLedEffect(selected);
            FinishChange(L.F($"{option.Label} für {leds.Length} LEDs auf Ebene {_layerIndex} übernommen."));
            return true;
        }
        if (effect.Target is < 1 or > 3) { Status(L.T("Bitte den Bereich für den Effekt auswählen.")); return false; }
        if (JsonSerializer.Serialize(effect) == JsonSerializer.Serialize(Layer.LightingEffect)) return true;
        BeginChange(); Layer.LightingEffect = effect;
        FinishChange(L.F($"{option.Label} für Ebene {_layerIndex} übernommen."));
        return true;
    }

    private void InheritEffect_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePendingEdits()) return;
        var leds = SelectedEffectLeds();
        if (!leds.Any(Layer.LedEffects.ContainsKey)) return;
        BeginChange();
        foreach (var led in leds) Layer.LedEffects.Remove(led);
        FinishChange(L.F($"{leds.Length} LEDs verwenden wieder den globalen Effekt der Ebene {_layerIndex}."));
    }

    private void ApplyLightingColor(string hex)
    {
        try { hex = ColorPaletteStore.NormalizeColor(hex); }
        catch (InvalidDataException ex) { Status(ex.Message); return; }
        if (_lightingTarget == LightingTarget.Keys) ApplyColor(hex);
        else ApplySideColor(hex, _lightingTarget);
    }

    private void OpenColorPicker_Click(object sender, RoutedEventArgs e)
    {
        string initial;
        try { initial = ColorPaletteStore.NormalizeColor(ColorHex.Text); }
        catch (InvalidDataException) { initial = "#FFFFFF"; }
        var picker = new ColorPickerWindow(initial, _palettePath) { Owner = this };
        if (picker.ShowDialog() == true) { ColorHex.Text = picker.SelectedHex; ApplyLightingColor(picker.SelectedHex); }
        LoadColorPalette();
    }

    private void LoadColorPalette()
    {
        var palette = ColorPaletteStore.Load(_palettePath);
        _customColors = palette.Colors; _paletteError = palette.Error;
        RenderColorPalette();
    }

    private void RenderColorPalette()
    {
        CustomColorSwatches.Children.Clear();
        PaletteHint.Text = _paletteError ?? L.T("Für alle Geräte und Profile.");
        SaveColorButton.IsEnabled = _paletteError is null;
        foreach (var color in _customColors)
        {
            var swatch = new Button { Height = 40, Padding = new Thickness(3), Margin = new Thickness(3), Background = Brushes.Transparent, BorderBrush = Brush("#353A40"), Tag = color, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, ToolTip = color + L.T(" · Anwenden\nRechtsklick: Entfernen") };
            swatch.Content = new Border { Background = Brush(color), BorderBrush = Brush("#40FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            swatch.Click += (_, _) => { ColorHex.Text = color; ApplyLightingColor(color); };
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = L.T("Farbe entfernen") };
            remove.Click += (_, _) => RemoveFavorite(color);
            menu.Items.Add(remove); swatch.ContextMenu = menu;
            System.Windows.Automation.AutomationProperties.SetName(swatch, L.T("Gespeicherte Farbe ") + color);
            CustomColorSwatches.Children.Add(swatch);
        }
        UpdateFavoriteSelection();
    }

    private void SavePalette(IReadOnlyList<string> colors, string status)
    {
        try { _customColors = ColorPaletteStore.Save(_palettePath, colors); _paletteError = null; RenderColorPalette(); RenderEffectColors(); Status(status); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { Status(L.T("Farbpalette konnte nicht gespeichert werden: ") + ex.Message); }
    }

    private void SaveColor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var color = ColorPaletteStore.NormalizeColor(ColorHex.Text);
            _customColors = ColorPaletteStore.Add(_palettePath, color); _paletteError = null;
            RenderColorPalette(); RenderEffectColors(); Status(L.T("Farbe gespeichert. Sie ist auch nach einem Neustart verfügbar."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Status(ex.Message); }
    }

    private void RenameLayer_Click(object sender, RoutedEventArgs e)
    {
        var name = LayerName.Text.Trim();
        if (name.Length == 0) { Status(L.T("Bitte einen Namen für die Ebene eingeben.")); return; }
        if (name == Layer.Name) return;
        BeginChange(); Layer.Name = name; BuildLayerButtons(); FinishChange(L.T("Ebenenname im Profil geändert."));
    }

    private static void SaveVisualScreenshot(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private void BeginChange()
    {
        _undo.Push(ProjectService.Clone(_project));
        _redo.Clear();
    }

    private void FinishChange(string message, bool redraw = true)
    {
        if (redraw) RenderKeyboard();
        RefreshInspector(); UpdateDirtyState(); Status(message);
    }

    private void UpdateDirtyState()
    {
        var dirty = IsDirty;
        Title = $"INGLORIOUS Advanced · {_project.Name}{(dirty ? " *" : "")}";
        DirtyText.Text = dirty ? L.T("Ungespeicherte Änderungen") : _projectPath is null ? L.T("Noch nicht gespeichert") : L.T("Profil gespeichert");
        DirtyText.Foreground = Brush(dirty ? "#EF646B" : "#A0A7B0");
        ProfileFileText.Text = _projectPath is null ? Device.Name : Path.GetFileName(_projectPath);
        ProfileFileText.ToolTip = _projectPath;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        UpdateSourceHint();
    }

    private void UpdateSourceHint()
    {
        var startupNotice = _startupLoading ? L.T("Konfigurationen werden von den Geräten gelesen …") : Device.IsOfflineOnly ? null : StartupNotice;
        SafetyBanner.Visibility = _defaultLoadError is null && startupNotice is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectSourceText.Text = _defaultLoadError is null ? "" : L.T("Profil konnte nicht geladen werden");
        ProjectSourceText.Visibility = _defaultLoadError is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectSourceHint.Text = _defaultLoadError is not null && startupNotice is not null ? startupNotice + "\n" + _defaultLoadError : _defaultLoadError ?? startupNotice ?? "";
        var hasSaved = _confirmedDeviceSaves.TryGetValue(Device.Id, out var saved);
        var matchesSaved = hasSaved && saved.Fingerprint == Fingerprint(_project);
        DeviceSaveState.Text = Device.IsOfflineOnly ? L.T("Offline-Profil") : !IsActiveDeviceConnected ? L.T(_connectionScanFailed ? "Gerätesuche fehlgeschlagen" : "Gerät nicht verbunden")
            : matchesSaved ? L.F($"✓ Auf Tastatur bestätigt · {saved.Time:HH:mm}") : hasSaved ? L.T("Änderungen noch nicht übertragen") : L.T("Noch nicht übertragen");
        DeviceSaveState.Foreground = Brush(IsActiveDeviceConnected && matchesSaved ? "#75D9CF" : "#A0A7B0");
    }

    private void SetProjectSource(string source)
    {
        _sourceDescription = source; _sourceFingerprint = Fingerprint(_project); _defaultLoadError = null;
    }

    private static ProjectOpening OpenDefaultProject(string id, string? path = null, bool allowDefault = true)
    {
        var fresh = ProjectService.Create(id);
        path ??= allowDefault ? new ProfileLibrary(Paths.DataRoot).ActivePath(id) ?? ProjectService.DefaultPath(id) : ProjectService.DefaultPath(id);
        if (!allowDefault || !File.Exists(path)) return new(fresh, null, "Lokale Vorlage · nicht aus Gerät gelesen", null);
        try
        {
            var project = ProjectService.Load(path);
            if (project.DeviceId != id) throw new InvalidDataException(L.T("Die gespeicherte Datei gehört zu einem anderen Gerät."));
            return new(project, path, "Gespeichertes Profil · nicht mit dem Gerät abgeglichen", null);
        }
        catch (Exception ex)
        {
            return new(fresh, null, "Lokale Vorlage · Profildatei konnte nicht geladen werden", L.F($"{Path.GetFileName(path)} konnte nicht geladen werden: {ex.Message}"));
        }
    }

    private void ApplyOpening(ProjectOpening opening)
    {
        _macroAssigning = false;
        _project = opening.Project; _projectPath = opening.Path; _savedFingerprint = Fingerprint(_project);
        _sourceDescription = opening.SourceDescription; _sourceFingerprint = _savedFingerprint; _defaultLoadError = opening.Error;
        _layerIndex = 0; _macroId = null;
        _selection.Clear(); _undo.Clear(); _redo.Clear();
    }

    private void Status(string text) => StatusText.Text = text;
    private void ShowError(string text, Exception? ex = null)
    {
        Status(text);
        MessageBox.Show(this, ex is null ? text : $"{text}\n\n{ex.Message}", "INGLORIOUS Advanced", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool RequireSelection()
    {
        if (_selection.Count > 0) return true;
        Status(L.T("Wähle zuerst mindestens eine Taste in der Geräteansicht."));
        return false;
    }

    private void ApplyColor(string hex)
    {
        if (!RequireSelection()) return;
        var rgbKeys = Device.Keys.Where(k => _selection.Contains(k.Id) && k.LedIndex is not null).ToList();
        if (rgbKeys.Count == 0) { Status(L.T("Diese Auswahl hat keine eigene RGB-LED. Die Belegung kannst du trotzdem ändern.")); return; }
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        { Status(L.T("Bitte eine Farbe als #RRGGBB eingeben, zum Beispiel #A38BFA.")); return; }
        hex = hex.ToUpperInvariant();
        if (rgbKeys.All(key => Assignment(key.Id).Color.Equals(hex, StringComparison.OrdinalIgnoreCase))) return;
        BeginChange();
        foreach (var key in rgbKeys)
        {
            var id = key.Id;
            var assignment = Assignment(id); assignment.Color = hex; Layer.Keys[id] = assignment;
        }
        FinishChange(L.F($"Farbe für {rgbKeys.Count} Taste(n) geändert · lokal, noch nicht auf dem Gerät."));
    }

    private void ApplyPreset(string name, string start, string end)
    {
        BeginChange();
        Layer.SideLedColor = start; Layer.ExtraLedColors.Clear();
        var a = ParseColor(start); var b = ParseColor(end);
        foreach (var key in Device.Keys)
        {
            if (key.LedIndex is null) continue;
            var t = Math.Clamp((key.X + key.Y * .35) / Math.Max(1, Device.Width - 1), 0, 1);
            var color = Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
            var assignment = Assignment(key.Id); assignment.Color = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; Layer.Keys[key.Id] = assignment;
        }
        FinishChange(L.F($"{name} auf Ebene {_layerIndex} angewendet · Rückgängig mit Strg+Z."));
    }

    private void ApplyBinding(string binding)
    {
        if (!RequireSelection()) return;
        if (binding == "KC_TRNS" && _layerIndex == 0) { Status(L.T("Die Basisebene benötigt eine eigene Belegung. Durchreichen ist auf den Ebenen 1–3 möglich.")); return; }
        if (binding.StartsWith("MO(", StringComparison.Ordinal) && Device.Keys.Any(k => k.IsEncoder && _selection.Contains(k.Id))) { Status(L.T("Drehbewegungen lassen sich nicht gedrückt halten. Wähle für den Drehgeber eine Taste oder „Umschalten“.")); return; }
        if (_selection.All(id => Assignment(id).Binding == binding)) return;
        BeginChange();
        foreach (var id in _selection)
        {
            var assignment = Assignment(id); assignment.Binding = binding; Layer.Keys[id] = assignment;
        }
        FinishChange(L.F($"{BindingLabel(binding)} für {_selection.Count} Taste(n) zugewiesen."));
    }

    private void ApplyColor_Click(object sender, RoutedEventArgs e) => ApplyLightingColor(ColorHex.Text.Trim());
    private void Aurora_Click(object sender, RoutedEventArgs e) => ApplyPreset(L.T("Violett–Türkis"), "#AE85ED", "#6CD5CE");
    private void Sunset_Click(object sender, RoutedEventArgs e) => ApplyPreset(L.T("Orange–Rosa"), "#F5AB76", "#CA79B4");
    private void Mono_Click(object sender, RoutedEventArgs e) => ApplyPreset(L.T("Hellblau"), "#BED2E4", "#BED2E4");
    private void AssignBinding_Click(object sender, RoutedEventArgs e)
    {
        if (BindingChoice.SelectedItem is BindingOption choice) ApplyBinding(choice.Code);
        else Status(L.T("Wähle zuerst eine Taste oder Funktion aus der Liste."));
    }
    private void AssignMomentary_Click(object sender, RoutedEventArgs e) => ApplyBinding($"MO({Math.Max(0, TargetLayer.SelectedIndex)})");
    private void AssignToggle_Click(object sender, RoutedEventArgs e) => ApplyBinding($"TG({Math.Max(0, TargetLayer.SelectedIndex)})");
    private void AssignTransparent_Click(object sender, RoutedEventArgs e) => ApplyBinding("KC_TRNS");
    private void SelectAll_Click(object sender, RoutedEventArgs e) { if (!ResolvePendingEdits()) return; _lightingTarget = LightingTarget.Keys; foreach (var key in Device.Keys) _selection.Add(key.Id); RenderKeyboard(); RefreshInspector(); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { if (!ResolvePendingEdits()) return; _selection.Clear(); _selectedSideLeds.Clear(); _lightingTarget = LightingTarget.Keys; RenderKeyboard(); RefreshInspector(); }
    private void BindingSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshBindingChoices();
    }
    private void BindingCategory_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshBindingChoices();
    private static int BindingCategoryId(string code)
    {
        if (code.StartsWith("MACRO:")) return 8;
        if (code.StartsWith("MO(") || code.StartsWith("TG(") || code.StartsWith("TO(")) return 7;
        if (code is "KC_LCTL" or "KC_RCTL" or "KC_LSFT" or "KC_RSFT" or "KC_LALT" or "KC_RALT" or "KC_LGUI" or "KC_RGUI") return 6;
        if (code is "KC_VOLU" or "KC_VOLD" or "KC_MUTE" or "KC_MPLY" or "KC_MSTP" or "KC_MNXT" or "KC_MPRV" or "KC_CALC") return 5;
        if (code == "KC_NUM" || code.StartsWith("KC_P") && code is not ("KC_PSCR" or "KC_PGUP" or "KC_PGDN" or "KC_PAUS" or "KC_P")) return 4;
        if (code.Length > 4 && code.StartsWith("KC_F") && int.TryParse(code.AsSpan(4), out _)) return 3;
        return code.Length == 4 && char.IsLetterOrDigit(code[3]) ? 1 : 2;
    }
    private void RefreshBindingChoices()
    {
        if (BindingChoice is null || BindingCategory is null || BindingSearch is null) return;
        var previous = (BindingChoice.SelectedItem as BindingOption)?.Code;
        var query = BindingSearch.Text.Trim(); var group = BindingCategory.SelectedIndex;
        var options = BindingCatalog.All.Where(b => !b.Code.StartsWith("RM_", StringComparison.Ordinal)).Select(b => new BindingOption(b.Code, BindingCatalog.FunctionLabel(b.Code)))
            .Concat(Enumerable.Range(0, 4).SelectMany(i => new[] { new BindingOption($"MO({i})", L.F($"Ebene {i} halten")), new BindingOption($"TG({i})", L.F($"Ebene {i} umschalten")) }))
            .Concat(_project.Macros.Select(m => new BindingOption("MACRO:" + m.Id, L.T("Makro · ") + m.Name)))
            .Where(b => (group <= 0 || BindingCategoryId(b.Code) == group) && (b.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Code.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        BindingChoice.ItemsSource = options;
        BindingChoice.SelectedValue = previous;
        if (BindingChoice.SelectedIndex < 0 && options.Count > 0) BindingChoice.SelectedIndex = 0;
    }
    private void ResetBinding_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection()) return;
        var selected = Device.Keys.Where(k => _selection.Contains(k.Id)).ToArray();
        if (selected.All(k => Assignment(k.Id).Binding == (_layerIndex == 0 ? k.DefaultBinding : "KC_TRNS"))) return;
        BeginChange();
        foreach (var key in selected) Layer.Keys[key.Id].Binding = _layerIndex == 0 ? key.DefaultBinding : "KC_TRNS";
        FinishChange(L.T("Standardbelegung für die Auswahl wiederhergestellt."));
    }
    private void DisableBinding_Click(object sender, RoutedEventArgs e) => ApplyBinding("KC_NO");

    private void SetTab(int tab)
    {
        if (tab != _activeTab && !ResolvePendingEdits()) return;
        if (tab != _activeTab) _macroAssigning = false;
        _activeTab = tab;
        ColorPanel.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        BindingPanel.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        MacroPanel.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        LayerPanel.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
        DeviceSettingsPanel.Visibility = tab == 4 ? Visibility.Visible : Visibility.Collapsed;
        RefreshWorkspaceMode();
        EditorSectionTitle.Text = tab switch { 0 => L.T("Beleuchtung"), 1 => L.T("Tastenbelegung"), 2 => L.T("Makros"), 3 => L.T("Ebenen"), 4 => L.T("Gerät"), _ => L.T("Profile") };
        var buttons = new[] { ColorTab, BindingTab, MacroTab, LayerTab, DeviceTab, ProfilesTab };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = Brush(i == tab ? "#282D32" : "#1C1F22");
            buttons[i].BorderBrush = i == tab ? Brush("#EF646B") : Brushes.Transparent;
            buttons[i].Foreground = Brush(i == tab ? "#FFA3A8" : "#A0A7B0");
        }
        RenderKeyboard(); RefreshInspector();
    }
    private void ColorTab_Click(object sender, RoutedEventArgs e) => SetTab(0);
    private void BindingTab_Click(object sender, RoutedEventArgs e) => SetTab(1);
    private void MacroTab_Click(object sender, RoutedEventArgs e) => SetTab(2);
    private void LayerTab_Click(object sender, RoutedEventArgs e) => SetTab(3);
    private void DeviceTab_Click(object sender, RoutedEventArgs e) => SetTab(4);
    private void ProfilesTab_Click(object sender, RoutedEventArgs e) => SetTab(5);
    private void RefreshWorkspaceMode()
    {
        RefreshFirmwareVersionLabel();
        MacroWorkspace.Visibility = _activeTab == 2 && !_macroAssigning ? Visibility.Visible : Visibility.Collapsed;
        DeviceOverview.Visibility = _activeTab == 4 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesWorkspace.Visibility = _activeTab == 5 ? Visibility.Visible : Visibility.Collapsed;
        InspectorHost.Visibility = _activeTab == 5 ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan((UIElement)WorkspaceScroll.Parent, _activeTab == 5 ? 2 : 1);
        KeyboardWorkspace.Visibility = MacroWorkspace.Visibility == Visibility.Visible || _activeTab >= 4 ? Visibility.Collapsed : Visibility.Visible;
        var editingSlider = _activeTab == 1 && _editingSlider && Device.Id == "numpad";
        CurrentLayerChoice.Visibility = editingSlider || _activeTab >= 4 || (_activeTab == 2 && !_macroAssigning) || (_activeTab == 0 && _showLightingStandby) ? Visibility.Collapsed : Visibility.Visible;
        SelectionBar.Visibility = editingSlider || _activeTab == 0 && (_showLightingStandby || _showLightingEffects && !_effectForSelection) ? Visibility.Collapsed : KeyboardWorkspace.Visibility;
        LayerOverview.Visibility = Visibility.Collapsed;
        BackToMacroButton.Visibility = _activeTab == 2 && _macroAssigning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackToMacro_Click(object sender, RoutedEventArgs e) { _macroAssigning = false; RefreshWorkspaceMode(); RefreshInspector(); }

    private void SelectLayer(int layer)
    {
        if (layer < 0 || layer >= _project.Layers.Count || layer == _layerIndex) return;
        if (!ResolvePendingEdits()) return;
        CommitNames(); _layerIndex = layer; BuildLayerButtons(); RenderKeyboard(); RefreshInspector();
        Status(L.F($"Ebene {layer} · {Layer.Name} wird bearbeitet."));
    }
    private void RefreshLayerTools()
    {
        if (_refreshingLayerTools) return;
        _refreshingLayerTools = true;
        try
        {
            var target = CopyTargetLayer.SelectedIndex; var compare = CompareLayer.SelectedIndex;
            var labels = _project.Layers.Select((l, i) => L.F($"Ebene {i} · {l.Name}")).ToArray();
            CopyTargetLayer.ItemsSource = labels; CompareLayer.ItemsSource = labels;
            CopyTargetLayer.SelectedIndex = target is >= 0 and < 4 && target != _layerIndex ? target : (_layerIndex + 1) % 4;
            CompareLayer.SelectedIndex = compare is >= 0 and < 4 && compare != _layerIndex ? compare : (_layerIndex == 0 ? 1 : 0);
            LayerCards.Children.Clear();
            for (var i = 0; i < 4; i++)
            {
                var layer = i; var profile = _project.Layers[i];
                var direct = profile.Keys.Count(k => k.Value.Binding is not "KC_TRNS" and not "KC_NO");
                var macros = profile.Keys.Count(k => k.Value.Binding.StartsWith("MACRO:"));
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = $"{i} · {profile.Name}", FontWeight = FontWeights.SemiBold, FontSize = 12 });
                content.Children.Add(new TextBlock { Text = L.F($"{direct} Belegungen · {macros} Makrozuweisungen"), FontSize = 11, Foreground = Brush("#A0A7B0"), Margin = new Thickness(0, 6, 0, 0) });
                content.Children.Add(new TextBlock { Text = LightingEffects.All.First(e => e.Id == profile.LightingEffect.Mode).Label, FontSize = 11, Foreground = Brush("#A0A7B0"), Margin = new Thickness(0, 4, 0, 0) });
                var button = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderBrush = Brush(layer == _layerIndex ? "#A74B54" : "#353A40") };
                button.Click += (_, _) => SelectLayer(layer); LayerCards.Children.Add(button);
            }
        }
        finally { _refreshingLayerTools = false; }
        UpdateLayerComparison();
    }
    private void CompareLayer_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshingLayerTools && LayerComparison is not null) UpdateLayerComparison(); }
    private void UpdateLayerComparison()
    {
        if (CompareLayer.SelectedIndex is < 0 or > 3) return;
        var other = _project.Layers[CompareLayer.SelectedIndex];
        var changes = Device.Keys.Where(k => Layer.Keys[k.Id].Binding != other.Keys[k.Id].Binding).Select(k => $"{k.AccessibleLabel}: {BindingLabel(Layer.Keys[k.Id].Binding)} → {BindingLabel(other.Keys[k.Id].Binding)}").ToArray();
        var colors = Device.Keys.Count(k => k.LedIndex.HasValue && Layer.Keys[k.Id].Color != other.Keys[k.Id].Color);
        var effectsDiffer = JsonSerializer.Serialize(Layer.LightingEffect) != JsonSerializer.Serialize(other.LightingEffect) || JsonSerializer.Serialize(Layer.LedEffects) != JsonSerializer.Serialize(other.LedEffects);
        var sideColors = Device.NonKeyLedIndices.Count(i => !SideLedColor(i).Equals(other.ExtraLedColors.GetValueOrDefault(i, other.SideLedColor), StringComparison.OrdinalIgnoreCase));
        LayerComparison.Text = L.F($"{changes.Length} andere Belegungen · {colors} andere Tastenfarben · {sideColors} andere Seiten-LEDs") + (effectsDiffer ? L.T(" · andere Effekte") : "") + (Layer.SynchronizeSides != other.SynchronizeSides ? L.T(" · andere Seitensynchronisierung") : "") + (changes.Length > 0 ? "\n\n" + string.Join("\n", changes.Take(12)) + (changes.Length > 12 ? L.F($"\n… und {changes.Length - 12} weitere") : "") : "");
    }
    private void CopyLayer_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePendingEdits()) return;
        var target = CopyTargetLayer.SelectedIndex;
        if (target is < 0 or > 3 || target == _layerIndex) { Status(L.T("Bitte eine andere Zielebene auswählen.")); return; }
        if (target == 0 && Layer.Keys.Values.Any(k => k.Binding == "KC_TRNS")) { Status(L.T("Die Basisebene benötigt eigene Belegungen. Kopiere eine Ebene ohne durchgereichte Tasten oder wähle eine andere Zielebene.")); return; }
        var copy = ProjectService.Clone(_project).Layers[_layerIndex]; copy.Name = _project.Layers[target].Name;
        if (JsonSerializer.Serialize(copy) == JsonSerializer.Serialize(_project.Layers[target])) return;
        BeginChange(); _project.Layers[target] = copy; RefreshAll();
        FinishChange(L.F($"Ebene {_layerIndex} nach Ebene {target} kopiert. Rückgängig mit Strg+Z."));
    }

    private void ApplySideColor(string color, LightingTarget target)
    {
        var indices = SideIndices(target);
        if (indices.Count == 0) { Status(L.T("Bitte mindestens eine Seiten-LED auswählen.")); return; }
        if (indices.All(i => SideLedColor(i).Equals(color, StringComparison.OrdinalIgnoreCase))) return;
        BeginChange();
        if (target == LightingTarget.Both && Device.BadgeLedIndices.Count == 0) { Layer.SideLedColor = color; Layer.ExtraLedColors.Clear(); }
        else foreach (var index in indices) Layer.ExtraLedColors[index] = color;
        FinishChange(L.F($"Gehäusebeleuchtung auf Ebene {_layerIndex} geändert."));
    }

    private static string SliderModeLabel(int mode) => mode switch { 1 => L.T("MIDI-Controller"), 2 => L.T("Relative Lautstärke"), 3 => L.T("Beleuchtung dimmen"), _ => L.T("Deaktiviert") };
    private void RefreshDeviceSettingsCore()
    {
        PollingChoice.SelectedValue = _project.DeviceSettings.UsbPollingIntervalMs.ToString(CultureInfo.InvariantCulture);
        DebounceInput.Text = _project.DeviceSettings.DebounceMs.ToString(CultureInfo.InvariantCulture);
        SliderModeChoice.SelectedIndex = _project.Slider.Mode;
        SliderMinimum.Text = _project.Slider.Minimum.ToString(CultureInfo.InvariantCulture);
        SliderMaximum.Text = _project.Slider.Maximum.ToString(CultureInfo.InvariantCulture);
        MidiChannel.Text = (_project.Slider.MidiChannel + 1).ToString(CultureInfo.InvariantCulture);
        MidiController.Text = _project.Slider.MidiController.ToString(CultureInfo.InvariantCulture);
        UpdateSliderHint();
    }
    private bool TryGetSliderInput(out SliderSettings slider)
    {
        slider = _project.Slider;
        if (Device.Id != "numpad") return true;
        if (SliderModeChoice.SelectedIndex is < 0 or > 3 || !int.TryParse(SliderMinimum.Text, out var minimum) || !int.TryParse(SliderMaximum.Text, out var maximum) || minimum < 0 || maximum > 1023 || maximum <= minimum)
        {
            SliderCalibration.Visibility = Visibility.Visible; SliderCalibration.IsExpanded = true;
            return PendingInputError(L.T("Kalibrierung: Minimum und Maximum müssen zwischen 0 und 1023 liegen; Maximum muss größer sein."), SliderMinimum);
        }
        if (!int.TryParse(MidiChannel.Text, out var channel) || channel is < 1 or > 16 || !int.TryParse(MidiController.Text, out var controller) || controller is < 0 or > 127)
        {
            MidiFields.Visibility = Visibility.Visible;
            return PendingInputError(L.T("MIDI benötigt einen Kanal von 1–16 und einen Controller von 0–127."), MidiChannel);
        }
        slider = new() { Mode = SliderModeChoice.SelectedIndex, Minimum = minimum, Maximum = maximum, MidiChannel = channel - 1, MidiController = controller };
        return true;
    }
    private bool TryApplyDeviceSettings()
    {
        if (!int.TryParse(PollingChoice.SelectedValue?.ToString(), out var interval) || interval is not (1 or 2 or 4 or 8)) { Status(L.T("Bitte eine USB-Abfragerate auswählen.")); return false; }
        if (!int.TryParse(DebounceInput.Text, out var debounce) || debounce is < 0 or > 40) { Status(L.T("Die Entprellzeit muss zwischen 0 und 40 ms liegen.")); return false; }
        if (!TryGetSliderInput(out var slider)) return false;
        if (_project.DeviceSettings.UsbPollingIntervalMs == interval && _project.DeviceSettings.DebounceMs == debounce && JsonSerializer.Serialize(_project.Slider) == JsonSerializer.Serialize(slider)) return true;
        BeginChange();
        _project.DeviceSettings.UsbPollingIntervalMs = interval; _project.DeviceSettings.DebounceMs = debounce;
        _project.Slider = slider;
        FinishChange(L.T("Geräteeinstellungen übernommen."));
        return true;
    }
    private void SliderModeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (MidiFields is not null) UpdateSliderHint(); }
    private void UpdateSliderHint()
    {
        var invalidRange = !int.TryParse(SliderMinimum.Text, out var minimum) || !int.TryParse(SliderMaximum.Text, out var maximum)
            || minimum < 0 || maximum > 1023 || maximum <= minimum;
        var invalidMidi = !int.TryParse(MidiChannel.Text, out var channel) || channel is < 1 or > 16
            || !int.TryParse(MidiController.Text, out var controller) || controller is < 0 or > 127;
        MidiFields.Visibility = SliderModeChoice.SelectedIndex == 1 || invalidMidi ? Visibility.Visible : Visibility.Collapsed;
        SliderCalibration.Visibility = SliderModeChoice.SelectedIndex > 0 || invalidRange ? Visibility.Visible : Visibility.Collapsed;
        if (invalidRange) SliderCalibration.IsExpanded = true;
        SliderSharedBrightnessPanel.Visibility = SliderModeChoice.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        SliderModeHint.Text = SliderModeChoice.SelectedIndex switch
        {
            1 => L.T("Steuert einen MIDI-Controller in einer kompatiblen Anwendung."),
            2 => L.T("Ändert die Lautstärke durch Bewegen des Reglers."),
            3 => L.T("Regelt die Helligkeit der GMMK Numpad-Beleuchtung."),
            _ => L.T("Keine Aktion beim Bewegen des Reglers.")
        };
    }
    private void ApplySlider_Click(object sender, RoutedEventArgs e) => ApplyTimingInputs();

    private async void PrepareTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (Device.IsOfflineOnly) { Status(L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar.")); return; }
        if (!IsActiveDeviceConnected) { Status(L.T("Gerät nicht verbunden")); return; }
        if (_readingDevice) return;
        if (!ResolvePendingEdits()) return;
        CommitNames();
        await _sharedBrightness.StopAsync();
        try
        {
            var transfer = new TransferWindow(ProjectService.Clone(_project), offlinePreview: _screenshotMode) { Owner = this };
            transfer.ShowDialog();
            if (transfer.DeviceSaveVerified)
            {
                _confirmedDeviceSaves[Device.Id] = (Fingerprint(_project), DateTime.Now);
                _startupResults.Remove(Device.Id);
                UpdateSourceHint();
                if (transfer.CompletionNotice is { } notice) Status(notice);
            }
            else if (transfer.DeviceStateUncertain)
            {
                _confirmedDeviceSaves.Remove(Device.Id);
                UpdateSourceHint();
            }
            if (transfer.FirmwareGuideRequested) { await CheckFirmwareGuideAsync(force: true, deviceId: Device.Id); return; }
            if (transfer.ImportedProject is not { } imported) return;
            RecordDeviceRead(imported);
            if (!ConfirmReplace()) return;
            AdoptDeviceProject(imported, transfer.ImportedGeneration);
        }
        catch (Exception ex) { ShowError(L.T("Die Konfiguration kann nicht übertragen werden."), ex); }
        finally { StartSharedBrightnessIfReady(); }
    }

    private async void Firmware_Click(object sender, RoutedEventArgs e)
    {
        if (Device.IsOfflineOnly) return;
        if (_readingDevice || _screenshotMode) return;
        await CheckFirmwareGuideAsync(force: true, deviceId: Device.Id);
    }

    private async void RestoreFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (!Device.IsOfflineOnly) await OpenDeviceSetupAsync(Device.Id, SetupOperation.Restore);
    }

    private void AdoptDeviceProject(AppProject project, uint? generation, OnboardInfo? info = null)
    {
        _macroAssigning = false;
        if (project.DeviceId != _project.DeviceId) throw new InvalidDataException(L.T("Die gelesene Konfiguration gehört zu einem anderen Gerät."));
        _project = ProjectService.Clone(project); _projectPath = null; _savedFingerprint = ""; _layerIndex = 0;
        _selection.Clear();
        _undo.Clear(); _redo.Clear(); _macroId = null;
        SetProjectSource("Vom Gerät gelesen" + (generation.HasValue ? $" · Stand {generation.Value}" : "") + $" · {DateTime.Now:HH:mm}");
        RecordDeviceRead(_project, info);
        RefreshAll(); Status(L.T("Konfiguration von Tastatur geladen"));
    }

    private void RecordDeviceRead(AppProject project, OnboardInfo? info = null)
    {
        if (project.DeviceId != Device.Id) throw new InvalidDataException(L.T("Die Konfiguration gehört zu einem anderen Gerät."));
        if (info?.Persistent == false)
        {
            _confirmedDeviceSaves.Remove(Device.Id);
            _startupResults[Device.Id] = new(Device.Id, StartupDeviceState.Loaded, project, info);
        }
        else
        {
            _confirmedDeviceSaves[Device.Id] = (Fingerprint(project), DateTime.Now);
            _startupResults.Remove(Device.Id);
        }
        UpdateSourceHint();
    }

    private void InvalidateDeviceConfirmation(string deviceId)
    {
        _confirmedDeviceSaves.Remove(deviceId);
        UpdateSourceHint();
    }

    private async void ReadFromDevice_Click(object sender, RoutedEventArgs e)
    {
        if (Device.IsOfflineOnly) { Status(L.T("Für dieses Modell kannst du Profile erstellen. Übertragen und Firmwarewechsel sind noch nicht verfügbar.")); return; }
        if (!IsActiveDeviceConnected) { Status(L.T("Gerät nicht verbunden")); return; }
        if (_readingDevice || _screenshotMode) return;
        if (!ResolvePendingEdits()) return;
        CommitNames();
        var deviceId = _project.DeviceId;
        InvalidateDeviceConfirmation(deviceId);
        _readingDevice = true; EditorSurface.IsEnabled = false; ReadDeviceButton.Content = L.T("Wird gelesen …");
        Status(L.T("Passende INGLORIOUS-Firmware wird gesucht. Es werden keine Einstellungen geschrieben …"));
        await _sharedBrightness.StopAsync();
        try
        {
            // Manual reload is separate from the one-time startup read.
            var candidates = (await Task.Run(OnboardClient.FindDevices)).Where(d => d.DeviceId == deviceId).ToList();
            if (candidates.Count == 0) throw new IOException(L.T("Keine passende eigene INGLORIOUS-Firmware erkannt. Ein Gerät mit Original-Firmware kann hier noch nicht ausgelesen werden."));
            var target = candidates.Count == 1 ? candidates[0] : ChooseReadTarget(candidates);
            if (target is null) { Status(L.T("Geräteauswahl abgebrochen. Profil unverändert.")); return; }
            await using var client = OnboardClient.Open(target);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var before = await client.GetInfoAsync(deadline.Token);
            Status(L.T("Gerätestand wird vollständig gelesen und auf unveränderten Stand geprüft …"));
            var data = await client.ReadAsync(deadline.Token);
            var after = await client.GetInfoAsync(deadline.Token);
            if (before != after || after.DeviceId != deviceId || (after.Flags & 4) != 0 || after.StagingBytes != 0 || OnboardProtocol.Crc32(data) != after.ActiveCrc)
                throw new InvalidDataException(L.T("Der Gerätestand hat sich beim Lesen geändert. Bitte erneut lesen."));
            var project = OnboardSerializer.Decode(data);
            if (project.DeviceId != deviceId) throw new InvalidDataException(L.T("Die gelesene Konfiguration passt nicht zum gewählten Gerät."));
            RecordDeviceRead(project, after);
            // Keep the editor frozen until the existing draft has been saved or explicitly discarded.
            if (!ConfirmReplace()) { Status(L.T("Übernahme abgebrochen. Profil unverändert.")); return; }
            AdoptDeviceProject(project, after.Generation, after);
        }
        catch (Exception ex) { ShowError(L.T("Gerätestand konnte nicht übernommen werden. Profil unverändert."), ex); }
        finally { _readingDevice = false; StartSharedBrightnessIfReady(); EditorSurface.IsEnabled = true; ReadDeviceButton.Content = L.T("Von Tastatur laden"); }
    }

    private OnboardDevice? ChooseReadTarget(IReadOnlyList<OnboardDevice> devices)
    {
        var dialog = new Window { Title = L.T("Gerät zum Lesen auswählen"), Owner = this, Width = 670, Height = 420, MinWidth = 540, MinHeight = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#141618") };
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock { Text = L.T("Mehrere passende Geräte gefunden. Wähle das gewünschte Gerät anhand seines USB-Pfads."), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var choice = new ListBox();
        foreach (var device in devices) choice.Items.Add(new ListBoxItem { Tag = device, Content = new TextBlock { Text = DeviceCatalog.Get(device.DeviceId).Name + "\n" + device.Path, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(7) }, ToolTip = device.Path });
        Grid.SetRow(choice, 1); layout.Children.Add(choice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = L.T("Abbrechen"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => dialog.Close(); actions.Children.Add(cancel);
        var accept = new Button { Content = L.T("Gerätestand lesen"), IsEnabled = false, Style = (Style)FindResource("PrimaryButton") }; actions.Children.Add(accept);
        choice.SelectionChanged += (_, _) => accept.IsEnabled = choice.SelectedItem is ListBoxItem;
        accept.Click += (_, _) => dialog.DialogResult = true;
        Grid.SetRow(actions, 2); layout.Children.Add(actions); dialog.Content = layout;
        return dialog.ShowDialog() == true ? (choice.SelectedItem as ListBoxItem)?.Tag as OnboardDevice : null;
    }

    private void RefreshMacros()
    {
        var prior = _refreshing; _refreshing = true;
        MacroChoice.ItemsSource = null;
        MacroChoice.ItemsSource = _project.Macros;
        _macroId = _project.Macros.FirstOrDefault(m => m.Id == _macroId)?.Id ?? _project.Macros.FirstOrDefault()?.Id;
        MacroChoice.SelectedItem = CurrentMacro;
        _refreshing = prior;
        RefreshMacroSteps();
        RefreshBindingChoices();
    }

    private void RefreshMacroSteps(int selectedIndex = -1)
    {
        var prior = _refreshing; _refreshing = true;
        var macro = CurrentMacro;
        MacroName.Text = macro?.Name ?? "";
        MacroName.IsEnabled = macro is not null;
        MacroSteps.ItemsSource = macro?.Steps.Select((s, i) => $"{i + 1:00}     {(s.Kind == "delay" ? L.F($"Pause  ·  {s.DelayMs} ms") : s.Kind == "down" ? L.F($"↓  {BindingCatalog.KeyLabel(s.Key)} drücken") : L.F($"↑  {BindingCatalog.KeyLabel(s.Key)} loslassen"))}").ToList();
        if (macro is not null && macro.Steps.Count > 0) MacroSteps.SelectedIndex = Math.Clamp(selectedIndex, 0, macro.Steps.Count - 1);
        _refreshing = prior;
        RefreshStepFields();
        MacroValidation.Text = macro is null ? L.T("Erstelle ein Makro, um Schritte hinzuzufügen.") : MacroWarnings(macro);
        if (macro is not null)
        {
            var usages = _project.Layers.SelectMany((l, i) => Device.Keys.Where(k => l.Keys[k.Id].Binding == "MACRO:" + macro.Id).Select(k => L.F($"{k.AccessibleLabel} (Ebene {i})"))).ToArray();
            MacroUsage.Text = L.F($"{macro.Steps.Count}/128 Schritte · {macro.Steps.Where(s => s.Kind == "delay").Sum(s => s.DelayMs)} ms Pausen\n") + (usages.Length == 0 ? L.T("Noch keiner Taste zugewiesen.") : L.T("Zugewiesen: ") + string.Join(", ", usages));
        }
        else MacroUsage.Text = L.T("Ein Makro aufnehmen oder mit „Neu“ Schritt für Schritt anlegen.");
    }

    private static string MacroWarnings(MacroDefinition macro)
    {
        if (macro.Steps.Count == 0) return L.T("Noch keine Schritte. Drücken und Lösen bilden gemeinsam einen Tastendruck.");
        var held = new HashSet<string>();
        foreach (var step in macro.Steps)
        {
            if (step.Kind == "down" && !held.Add(step.Key)) return L.F($"{step.Key} wird erneut gedrückt, bevor die Taste gelöst wurde.");
            if (step.Kind == "up" && !held.Remove(step.Key)) return L.F($"{step.Key} wird gelöst, ohne zuvor gedrückt zu sein.");
        }
        return held.Count > 0 ? L.F($"Noch gedrückt: {string.Join(", ", held)}. Füge passende Lösen-Schritte hinzu.") : L.F($"{macro.Steps.Count} Schritte · Alle gedrückten Tasten werden wieder gelöst.");
    }

    private static bool MacroIsBalanced(MacroDefinition macro)
    {
        if (macro.Steps.Count == 0) return false;
        var held = new HashSet<string>();
        foreach (var step in macro.Steps)
        {
            if (step.Kind == "down" && !held.Add(step.Key)) return false;
            if (step.Kind == "up" && !held.Remove(step.Key)) return false;
        }
        return held.Count == 0;
    }

    private void RefreshStepFields()
    {
        var macro = CurrentMacro; var index = MacroSteps.SelectedIndex;
        var step = macro is not null && index >= 0 && index < macro.Steps.Count ? macro.Steps[index] : null;
        StepKeyChoice.IsEnabled = macro is not null;
        StepDelay.IsEnabled = macro is not null;
        if (step is not null)
        {
            StepKeyChoice.SelectedValue = step.Key;
            StepDelay.Text = step.DelayMs.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void MacroChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        _macroId = (MacroChoice.SelectedItem as MacroDefinition)?.Id;
        RefreshMacroSteps(); RefreshInspector();
    }
    private void MacroSteps_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshing) RefreshStepFields(); }

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Macros.Count >= 32) { Status(L.T("Der Entwurfseditor unterstützt maximal 32 Makros pro Profil.")); return; }
        BeginChange();
        var macro = new MacroDefinition { Id = Guid.NewGuid().ToString("N"), Name = L.F($"Mein Makro {_project.Macros.Count + 1}"), Steps = [] };
        _project.Macros.Add(macro); _macroId = macro.Id;
        RefreshMacros(); FinishChange(L.T("Makro erstellt. Ergänze Drücken, Lösen und Pausen."), false);
        MacroName.Focus(); MacroName.SelectAll();
    }

    private static MacroStep CopyMacroStep(MacroStep s) => new() { Kind = s.Kind, Key = s.Key, DelayMs = s.DelayMs };
    private void DuplicateMacro_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro is not { } original) return;
        if (_project.Macros.Count >= 32 || _project.Macros.Sum(m => m.Steps.Count) + original.Steps.Count > 768) { Status(L.T("Nicht genügend Makroplätze oder Schritte für eine Kopie.")); return; }
        BeginChange();
        var copy = new MacroDefinition { Name = (original.Name.Length > 70 ? original.Name[..70] : original.Name) + L.T(" · Kopie"), Steps = original.Steps.Select(CopyMacroStep).ToList() };
        _project.Macros.Add(copy); _macroId = copy.Id; RefreshMacros(); FinishChange(L.T("Makro dupliziert. Die Kopie ist noch keiner Taste zugewiesen."), false);
    }
    private void RecordMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Macros.Count >= 32 && CurrentMacro?.Steps.Count != 0) { Status(L.T("Alle 32 Makroplätze sind belegt.")); return; }
        var recorder = new MacroRecorderWindow { Owner = this };
        if (recorder.ShowDialog() == true && recorder.RecordedSteps is { } steps) AdoptRecordedMacro(steps);
    }
    private void AdoptRecordedMacro(IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count == 0) return;
        var empty = CurrentMacro is { Steps.Count: 0 } ? CurrentMacro : null;
        if ((_project.Macros.Count >= 32 && empty is null) || _project.Macros.Sum(m => m.Steps.Count) + steps.Count > 768) { Status(L.T("Die Aufnahme passt nicht mehr in den gemeinsamen Makrospeicher.")); return; }
        BeginChange();
        var macro = empty ?? new MacroDefinition { Name = L.F($"Aufnahme {_project.Macros.Count + 1}") };
        macro.Steps = steps.Select(CopyMacroStep).ToList();
        if (empty is null) _project.Macros.Add(macro);
        _macroId = macro.Id; _macroAssigning = false; RefreshMacros(); RefreshWorkspaceMode();
        FinishChange(L.T("Aufnahme als Makro übernommen. Prüfe die Schritte und wähle dann eine Auslösetaste."), false);
    }
    private void AddTap_Click(object sender, RoutedEventArgs e)
    {
        var macro = CurrentMacro;
        if (macro is null) { Status(L.T("Erstelle zuerst ein Makro.")); return; }
        if (macro.Steps.Count > 126 || _project.Macros.Sum(m => m.Steps.Count) > 766) { Status(L.T("Für einen Tastendruck werden zwei freie Schritte benötigt.")); return; }
        var key = (StepKeyChoice.SelectedItem as BindingOption)?.Code ?? "KC_A";
        BeginChange(); macro.Steps.Add(new() { Kind = "down", Key = key, DelayMs = 0 }); macro.Steps.Add(new() { Kind = "up", Key = key, DelayMs = 0 });
        RefreshMacroSteps(macro.Steps.Count - 2); FinishChange(L.T("Tastendruck mit passendem Loslassen hinzugefügt."), false);
    }
    private void MoveStep(int delta)
    {
        var macro = CurrentMacro; var index = MacroSteps.SelectedIndex;
        if (macro is null || index < 0 || index + delta < 0 || index + delta >= macro.Steps.Count) return;
        BeginChange(); (macro.Steps[index], macro.Steps[index + delta]) = (macro.Steps[index + delta], macro.Steps[index]);
        RefreshMacroSteps(index + delta); FinishChange(L.T("Makroschritt verschoben."), false);
    }
    private void MoveStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void MoveStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        var macro = CurrentMacro; if (macro is null) return;
        if (MessageBox.Show(this, L.F($"„{macro.Name}“ löschen? Zugewiesene Tasten werden auf „Keine Aktion“ gesetzt. Du kannst dies rückgängig machen."), L.T("Makro löschen"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BeginChange();
        _project.Macros.Remove(macro);
        foreach (var layer in _project.Layers)
            foreach (var assignment in layer.Keys.Values)
                if (assignment.Binding == $"MACRO:{macro.Id}") assignment.Binding = "KC_NO";
        RefreshMacros(); FinishChange(L.T("Makro gelöscht · Rückgängig mit Strg+Z."));
    }

    private void AddStep(string kind)
    {
        var macro = CurrentMacro;
        if (macro is null) { Status(L.T("Erstelle zuerst ein Makro.")); return; }
        if (macro.Steps.Count >= 128) { Status(L.T("Der Entwurfseditor unterstützt maximal 128 Schritte pro Makro.")); return; }
        if (_project.Macros.Sum(m => m.Steps.Count) >= 768) { Status(L.T("Der gemeinsame Makrospeicher mit 768 Schritten ist voll.")); return; }
        var key = (StepKeyChoice.SelectedItem as BindingOption)?.Code ?? "KC_A";
        if (kind == "up") key = macro.Steps.LastOrDefault(s => s.Kind == "down")?.Key ?? key;
        if (kind == "delay" && macro.Steps.Where(s => s.Kind == "delay").Sum(s => s.DelayMs) + 100 > 30000) { Status(L.T("Ein Makro kann insgesamt höchstens 30 Sekunden Pausen enthalten.")); return; }
        BeginChange();
        macro.Steps.Add(new MacroStep { Kind = kind, Key = kind == "delay" ? "" : key, DelayMs = kind == "delay" ? 100 : 0 });
        RefreshMacroSteps(macro.Steps.Count - 1); FinishChange(L.T("Makroschritt hinzugefügt."), false);
    }
    private void AddDown_Click(object sender, RoutedEventArgs e) => AddStep("down");
    private void AddUp_Click(object sender, RoutedEventArgs e) => AddStep("up");
    private void AddDelay_Click(object sender, RoutedEventArgs e) => AddStep("delay");

    private void EditStep_Click(object sender, RoutedEventArgs e)
    {
        var macro = CurrentMacro; var index = MacroSteps.SelectedIndex;
        if (macro is null || index < 0 || index >= macro.Steps.Count) { Status(L.T("Wähle zuerst einen Makroschritt.")); return; }
        var step = macro.Steps[index];
        if (step.Kind == "delay")
        {
            if (!int.TryParse(StepDelay.Text, out var delay) || delay is < 1 or > 10000) { Status(L.T("Pausen müssen zwischen 1 und 10.000 Millisekunden liegen.")); return; }
            if (macro.Steps.Where(s => s.Kind == "delay").Sum(s => s.DelayMs) - step.DelayMs + delay > 30000) { Status(L.T("Ein Makro kann insgesamt höchstens 30 Sekunden Pausen enthalten.")); return; }
            if (step.DelayMs == delay) return;
            BeginChange(); step.DelayMs = delay;
        }
        else
        {
            if (StepKeyChoice.SelectedItem is not BindingOption key) { Status(L.T("Wähle eine Taste für diesen Schritt.")); return; }
            if (step.Key == key.Code) return;
            BeginChange(); step.Key = key.Code;
        }
        RefreshMacroSteps(index); FinishChange(L.T("Makroschritt geändert."), false);
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        var macro = CurrentMacro; var index = MacroSteps.SelectedIndex;
        if (macro is null || index < 0 || index >= macro.Steps.Count) return;
        BeginChange(); macro.Steps.RemoveAt(index); RefreshMacroSteps(index); FinishChange(L.T("Makroschritt entfernt."), false);
    }

    private void AssignMacro_Click(object sender, RoutedEventArgs e)
    {
        var macro = CurrentMacro;
        if (macro is null || macro.Steps.Count == 0) { Status(L.T("Erstelle zuerst ein Makro mit mindestens einem Schritt.")); return; }
        if (!MacroIsBalanced(macro)) { Status(L.T("Ergänze zuerst passende Drücken- und Lösen-Schritte.")); return; }
        if (_activeTab == 2 && !_macroAssigning)
        {
            _macroAssigning = true; _selection.Clear(); RefreshWorkspaceMode(); RenderKeyboard(); RefreshInspector();
            Status(L.T("Wähle die Taste, die dieses Makro auslösen soll.")); return;
        }
        ApplyBinding($"MACRO:{macro.Id}");
        RefreshMacroSteps();
    }

    private void CommitNames()
    {
        var name = ProjectName.Text.Trim();
        if (name.Length > 0 && name != _project.Name) { BeginChange(); _project.Name = name; UpdateDirtyState(); }
        var macro = CurrentMacro; var macroName = MacroName.Text.Trim();
        if (macro is not null && macroName.Length > 0 && macroName != macro.Name)
        {
            BeginChange(); macro.Name = macroName; RefreshMacros(); UpdateDirtyState();
        }
    }
    private void ProjectName_LostFocus(object sender, RoutedEventArgs e) { if (!_refreshing && !IsHelpTakingFocus) CommitNames(); }
    private void MacroName_LostFocus(object sender, RoutedEventArgs e) { if (!_refreshing && !IsHelpTakingFocus) CommitNames(); }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePendingEdits()) return;
        CommitNames();
        if (_undo.Count == 0) return;
        _redo.Push(ProjectService.Clone(_project)); _project = _undo.Pop();
        RefreshAll(); Status(L.T("Letzte Änderung rückgängig gemacht."));
    }
    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!ResolvePendingEdits()) return;
        if (_redo.Count == 0) return;
        _undo.Push(ProjectService.Clone(_project)); _project = _redo.Pop();
        RefreshAll(); Status(L.T("Änderung wiederhergestellt."));
    }

    private void SwitchDevice(string id)
    {
        if (_readingDevice || _startupLoading || _startupFinishing) return;
        if (id == _project.DeviceId) return;
        if (!ResolvePendingEdits()) return;
        _macroAssigning = false;
        _selectedSideLeds.Clear();
        if (_lightingTarget == LightingTarget.SelectedLeds) _lightingTarget = LightingTarget.Keys;
        CommitNames();
        CaptureSession();
        _selection.Clear(); _undo.Clear(); _redo.Clear();
        if (_sessions.TryGetValue(id, out var session))
        {
            RestoreEditorSession(session);
        }
        else
        {
            ApplyOpening(OpenDefaultProject(id, allowDefault: _loadDefaultProjects));
        }
        RefreshAll(); Status(_defaultLoadError ?? StartupNotice ?? Device.Name);
    }

    private void CaptureSession() => _sessions[_project.DeviceId] = new(_project, _savedFingerprint, _projectPath, _layerIndex, _macroId, _selection.ToArray(), _undo.ToArray(), _redo.ToArray(), _sourceDescription, _sourceFingerprint, _defaultLoadError);

    private bool ConfirmReplace()
    {
        if (!IsDirty) return true;
        var result = UnsavedChangesWindow.Ask(this, L.T("Ungespeichertes Profil"), L.F($"Änderungen an „{_project.Name}“ zuerst speichern?"));
        return result == UnsavedChangesChoice.Discard || result == UnsavedChangesChoice.Save && SaveProject(false);
    }

    private bool SaveProject(bool export)
    {
        if (!ResolvePendingEdits()) return false;
        CommitNames();
        var errors = ProjectService.Validate(_project);
        if (errors.Count > 0) { ShowError(L.T("Das Profil kann so noch nicht gespeichert werden:\n\n") + string.Join("\n", errors.Take(8))); return false; }
        try
        {
            var path = _projectPath;
            if (export || path is null)
            {
                var defaultPath = _profileLibrary.SuggestPath(_project.Name);
                if (!_screenshotMode) Directory.CreateDirectory(_profileLibrary.Folder);
                var dialog = new SaveFileDialog { Title = export ? L.T("Profil als JSON exportieren") : L.T("INGLORIOUS-Profil speichern"), Filter = L.T("INGLORIOUS-Profil (*.json)|*.json"), DefaultExt = ".json", AddExtension = true, FileName = Path.GetFileName(path ?? defaultPath), InitialDirectory = Path.GetDirectoryName(path ?? defaultPath), OverwritePrompt = true };
                if (dialog.ShowDialog(this) != true) return false;
                path = dialog.FileName;
            }
            ProjectService.Save(path!, _project);
            RememberProfilePath(path!, activate: !export);
            if (!export) { _projectPath = path; _savedFingerprint = Fingerprint(_project); SetProjectSource("Gespeichertes Profil · nicht mit dem Gerät abgeglichen"); }
            UpdateDirtyState(); RefreshProfiles(); Status(export ? L.T("Profilkopie gespeichert") : L.T("Profil gespeichert"));
            return true;
        }
        catch (Exception ex) { ShowError(L.T("Das Profil konnte nicht gespeichert werden."), ex); return false; }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_readingDevice || !ResolvePendingEdits()) return;
        CommitNames();
        if (!ConfirmReplace()) return;
        ApplyOpening(OpenDefaultProject(Device.Id, allowDefault: false));
        _undo.Clear(); _redo.Clear(); _selection.Clear(); _macroId = null; _layerIndex = 0;
        RefreshAll(); Status(L.T("Neues Profil")); ProjectName.Focus(); ProjectName.SelectAll();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal ? new Size(Width, Height) : RestoreBounds.Size;
        var current = AppPreferencesStore.CaptureWindow(_preferences with { LastDeviceId = Device.Id }, bounds.Width, bounds.Height);
        var settings = new AppSettingsWindow(current, offlinePreview: _screenshotMode, connectedDevices: ConnectedDevices) { Owner = this };
        settings.SetupAction = () => OpenDeviceSetupAsync(owner: settings);
        settings.RestoreSetupAction = () => OpenDeviceSetupAsync(operation: SetupOperation.Restore, owner: settings);
        if (settings.ShowDialog() != true) return;
        _preferences = settings.Preferences;
        Resources["HelpVisibility"] = _preferences.ShowHelp ? Visibility.Visible : Visibility.Collapsed;
        ApplyUiLanguage(_preferences.Language);
        Status(L.T("App-Einstellungen gespeichert"));
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _startupClosed = true;
        StopConnectionMonitor();
        _startupCancellation.Cancel();
        StopLightingPreview();
        await _sharedBrightness.StopAsync();
        if (_screenshotMode) return;
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Size(Width, Height) : RestoreBounds.Size;
            _preferences = AppPreferencesStore.SaveWindowState(Device.Id, bounds.Width, bounds.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Debug.WriteLine("App preferences not saved: " + ex.Message); }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    private void Export_Click(object sender, RoutedEventArgs e) => SaveProject(true);
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_readingDevice) return;
        if (!ResolvePendingEdits()) return;
        CommitNames();
        var dialog = new OpenFileDialog { Title = L.T("INGLORIOUS-Profil öffnen"), Filter = L.T("INGLORIOUS-Profil (*.json)|*.json"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        OpenProfilePath(dialog.FileName);
    }

    private Task InspectBackupAsync()
    {
        CaptureStateText.Text = L.T("Deine Original-Firmware wird bei der Einrichtung auf diesem Computer gesichert.");
        return Task.CompletedTask;
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanDevicesAsync();

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshotMode) return;
        var archive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INGLORIOUS Advanced", "Firmware", "Devices");
        if (!Directory.Exists(archive)) { Status(L.T("Deine Original-Firmware wird bei der Einrichtung auf diesem Computer gesichert.")); return; }
        try { Process.Start(new ProcessStartInfo(archive) { UseShellExecute = true }); }
        catch { Status(L.T("Der Sicherungsordner konnte nicht geöffnet werden.")); }
    }

    private void ShowBackupDialog(string? screenshotPath = null)
    {
        var window = new Window { Owner = this, Title = L.T("Sicherung & Geräteprüfung · INGLORIOUS Advanced"), Width = 870, Height = 760, MinWidth = 680, MinHeight = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var outer = new Grid { Margin = new Thickness(28) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition());
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = L.T("Sicherungsstatus"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = L.T("Lokale Sicherungen und erkannte Geräte"), Foreground = Brush("#A0A7B0"), Margin = new Thickness(0, 6, 0, 16) });
        outer.Children.Add(heading);
        var body = new StackPanel();
        var warning = new Border { Background = Brush("#302328"), BorderBrush = Brush("#66563B"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 16) };
        var warningContent = new StackPanel();
        warningContent.Children.Add(new TextBlock { Text = L.T("Umfang der Sicherung"), Foreground = Brush("#FFA3A8"), FontWeight = FontWeights.SemiBold, FontSize = 14 });
        warningContent.Children.Add(new TextBlock { Text = L.T("Herstellerpakete und ausgelesener interner Speicher werden getrennt geprüft. Externe Profilspeicher sind nicht vollständig enthalten."), TextWrapping = TextWrapping.Wrap, Foreground = Brush("#C9BAA2"), Margin = new Thickness(0, 9, 0, 0), FontSize = 12, LineHeight = 19 });
        warning.Child = warningContent; body.Children.Add(warning);
        var evidence = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        evidence.ColumnDefinitions.Add(new ColumnDefinition()); evidence.ColumnDefinitions.Add(new ColumnDefinition()); evidence.ColumnDefinitions.Add(new ColumnDefinition());
        AddMetric(evidence, 0, _backup?.FileCount.ToString(CultureInfo.InvariantCulture) ?? "—", L.T("Archivierte Dateien"));
        AddMetric(evidence, 1, _backup?.VerifiedCount.ToString(CultureInfo.InvariantCulture) ?? "—", L.T("SHA-256 geprüft"));
        AddMetric(evidence, 2, _backup?.MatchingInternalFlashCaptures.ToString(CultureInfo.InvariantCulture) ?? "—", L.T("Interne Flash-Sicherungen"));
        body.Children.Add(evidence);
        body.Children.Add(new TextBlock { Text = L.T("Gezählt werden Geräte mit zwei übereinstimmenden Auslesungen des internen Speichers."), TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A0A7B0"), FontSize = 10, LineHeight = 16, Margin = new Thickness(0, -7, 0, 18) });
        body.Children.Add(new TextBlock { Text = L.T("PRÜFERGEBNIS"), FontSize = 10, Foreground = Brush("#A0A7B0"), FontWeight = FontWeights.SemiBold });
        var issues = _backup is null ? new[] { L.T("Die Sicherungsprüfung ist derzeit nicht verfügbar.") } : _backup.Issues.Count == 0 ? new[] { L.T("Die geprüften Dateien stimmen mit den gespeicherten Prüfsummen überein.") } : _backup.Issues;
        foreach (var issue in issues.Take(15))
            body.Children.Add(new TextBlock { Text = "• " + issue, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#C3C9D0"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), LineHeight = 17 });
        body.Children.Add(new Border { Height = 1, Background = Brush("#353A40"), Margin = new Thickness(0, 24, 0, 20) });
        body.Children.Add(new TextBlock { Text = L.T("USB-GERÄTE · LIVE ERKANNT"), FontSize = 10, Foreground = Brush("#A0A7B0"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        if (_devices.Count == 0) body.Children.Add(new TextBlock { Text = L.T("Keine unterstützten Geräte über USB erkannt."), Foreground = Brush("#C3C9D0"), Margin = new Thickness(0, 8, 0, 0) });
        foreach (var device in _devices)
        {
            var item = new StackPanel();
            item.Children.Add(new TextBlock { Text = device.Name, FontSize = 13, FontWeight = FontWeights.SemiBold });
            item.Children.Add(new TextBlock { Text = L.F($"USB {device.VendorId}:{device.ProductId} · Release {device.UsbRelease}"), FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = Brush("#C3C9D0"), Margin = new Thickness(0, 7, 0, 0) });
            item.Children.Add(new TextBlock { Text = device.LayoutEvidence + "\n" + device.RevisionStatus, FontSize = 11, Foreground = Brush("#C3C9D0"), TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 6, 0, 0) });
            body.Children.Add(new Border { Background = Brush("#252A2F"), CornerRadius = new CornerRadius(3), Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 0), Child = item });
        }
        if (_backup is not null) body.Children.Add(new TextBlock { Text = L.T("Archiv: ") + _backup.Root, FontSize = 10, Foreground = Brush("#A0A7B0"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 10) });
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); outer.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (_backup is not null && Directory.Exists(_backup.Root))
        {
            var openFolder = new Button { Content = L.T("Sicherungsordner öffnen"), Margin = new Thickness(0, 0, 9, 0), Padding = new Thickness(13, 10, 13, 10) };
            var root = _backup.Root;
            openFolder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo { FileName = root, UseShellExecute = true }); } catch (Exception ex) { ShowError(L.T("Der Sicherungsordner konnte nicht geöffnet werden."), ex); } };
            footer.Children.Add(openFolder);
        }
        var close = new Button { Content = L.T("Schließen"), Padding = new Thickness(14, 8, 14, 8), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => window.Close(); footer.Children.Add(close);
        Grid.SetRow(footer, 2); outer.Children.Add(footer);
        var dialogRoot = new Grid { Background = Brush("#141618") };
        dialogRoot.Children.Add(outer); window.Content = dialogRoot;
        if (screenshotPath is not null)
        {
            outer.Background = Brush("#141618");
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await Task.Delay(150); window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialogRoot.ActualWidth), (int)Math.Ceiling(dialogRoot.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(dialogRoot); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                    using var stream = File.Create(screenshotPath); encoder.Save(stream);
                }
                catch (Exception ex) { Directory.CreateDirectory(Paths.DataRoot); File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                finally { window.Close(); }
            };
        }
        window.ShowDialog();
        Status(_backup is null ? L.T("Sicherungsprüfung derzeit nicht verfügbar.") : L.T("Sicherungsnachweise geprüft"));
    }

    private static void AddMetric(Grid grid, int column, string number, string label)
    {
        var stack = new StackPanel(); stack.Children.Add(new TextBlock { Text = number, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E4E7EB") });
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Brush("#A0A7B0"), Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        var border = new Border { BorderBrush = Brush("#353A40"), BorderThickness = new Thickness(0, 0, column == 2 ? 0 : 1, 0), Padding = new Thickness(10), Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 2 ? 0 : 5, 0), Child = stack };
        Grid.SetColumn(border, column); grid.Children.Add(border);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_startupLoading || _startupFinishing) { _startupCancellation.Cancel(); return; }
        if (_readingDevice) { e.Cancel = true; Status(L.T("Der Gerätestand wird noch gelesen. Bitte warte auf das Prüfergebnis.")); return; }
        if (_screenshotMode) return;
        if (!ResolvePendingEdits()) { e.Cancel = true; return; }
        e.Cancel = !ConfirmCloseProjects();
    }

    private bool ConfirmCloseProjects(Func<string[], UnsavedChangesChoice>? choose = null, Func<bool>? save = null)
    {
        CommitNames(); CaptureSession();
        var dirty = _sessions.Where(s => Fingerprint(s.Value.Project) != s.Value.SavedFingerprint).Select(s => s.Key).ToList();
        if (dirty.Count == 0) return true;
        var names = dirty.Select(id => _sessions[id].Project.Name + " · " + DeviceCatalog.Get(id).Name).ToArray();
        var result = choose?.Invoke(names) ?? UnsavedChangesWindow.Ask(this, L.T("Ungespeicherte Profile"), L.T("Diese geänderten Profile vor dem Beenden speichern?\n\n"), names);
        if (result == UnsavedChangesChoice.Cancel) return false;
        if (result == UnsavedChangesChoice.Discard) return true;
        var originalDevice = _project.DeviceId;
        try
        {
            foreach (var id in dirty)
            {
                SwitchDevice(id);
                if (!(save?.Invoke() ?? SaveProject(false))) return false;
            }
            return true;
        }
        finally { SwitchDevice(originalDevice); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_readingDevice || _startupLoading || _startupFinishing) { e.Handled = true; return; }
        if (e.Key == Key.F1) { Help_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.S) { SaveProject(false); e.Handled = true; }
        else if (e.Key == Key.O) { Open_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.FocusedElement is not TextBox) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Y && Keyboard.FocusedElement is not TextBox) { Redo_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (KeyboardView is null) return;
        var compact = ActualHeight < 850;
        WorkspaceHeading.FontSize = 20;
        
        SafetyBanner.Margin = new Thickness(0, 12, 0, 0);
        KeyboardPanel.Margin = new Thickness(0, compact ? 12 : 18, 0, compact ? 9 : 16);
        KeyboardPanelInner.Margin = compact ? new Thickness(14, 13, 14, 10) : new Thickness(19, 18, 19, 17);
        KeyboardView.Margin = compact ? new Thickness(0, 12, 0, 12) : new Thickness(0, 16, 0, 16);
        KeyboardView.MaxHeight = Math.Clamp(ActualHeight - 350, 280, 480);
    }

    private async Task<string> RunUiSmoke()
    {
        var checks = new List<string>();
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add("PASS " + description); }
        var brandFace = new Typeface(BrandWordmark.FontFamily, BrandWordmark.FontStyle, BrandWordmark.FontWeight, BrandWordmark.FontStretch);
        Check(brandFace.TryGetGlyphTypeface(out var brandGlyph) && brandGlyph.FamilyNames.Values.Contains("IG Preview Oxanium"), "Wordmark resolves the embedded Oxanium face without system fallback");
        Check(brandGlyph is not null && brandGlyph.Weight.ToOpenTypeWeight() == 600 && brandGlyph.StyleSimulations == StyleSimulations.None && brandGlyph.FontUri.AbsoluteUri.EndsWith("inglorious-oxanium-semibold.ttf", StringComparison.OrdinalIgnoreCase), "Wordmark uses the bundled true SemiBold 600 instance without simulated styling");
        RunLightingChecks(Check);
        RunSideSynchronizationChecks(Check);
        RunEffectAndTimingChecks(Check);
        RunWorkflowChecks(Check);
        RunPendingEditsChecks(Check);
        RunNavigationChecks(Check);
        RunAnimationChecks(Check);
        RunNewFeaturesChecks(Check);
        RunProfileOpeningChecks(Check);
        RunStandbyChecks(Check);
        RunHelpChecks(Check);
        RunDeviceSetupChecks(Check);
        AppSettingsWindow.RunOfflineChecks(Check);
        await RunStartupChecksAsync(Check);
        await RunConnectionChecksAsync(Check);
        SwitchDevice("pro-iso");
        var key = Device.Keys.First(k => k.LedIndex is not null).Id;
        _selection.Clear(); _selection.Add(key);
        var original = Assignment(key).Color;
        ApplyColor("#123456");
        Check(Assignment(key).Color == "#123456" && IsDirty, "Per-key color updates project and dirty state");
        Undo_Click(this, new RoutedEventArgs());
        Check(Assignment(key).Color == original, "Undo restores previous color");
        Redo_Click(this, new RoutedEventArgs());
        Check(Assignment(key).Color == "#123456", "Redo reapplies color");
        _selection.Add(Device.Keys.First(k => k.LedIndex is not null && k.Id != key).Id);
        ApplyColor("#224466");
        Check(_selection.All(k => Assignment(k).Color == "#224466"), "Multi-key paint updates all selected LED keys");
        var binding = Assignment(key).Binding;
        ApplyBinding("KC_TRNS");
        Check(Assignment(key).Binding == binding, "Base-layer transparency is rejected");
        _layerIndex = 1;
        ApplyBinding("TG(2)");
        Check(Assignment(key).Binding == "TG(2)", "Layer action can be assigned");
        SwitchDevice("numpad");
        var padKey = Device.Keys.First(k => k.LedIndex is not null).Id;
        _selection.Clear(); _selection.Add(padKey); ApplyColor("#AABBCC");
        SwitchDevice("pro-iso");
        Check(_layerIndex == 1 && Assignment(key).Binding == "TG(2)" && _project.Layers[0].Keys[key].Color == "#224466", "Keyboard draft and active layer survive device switching");
        SwitchDevice("numpad");
        Check(Assignment(padKey).Color == "#AABBCC", "Numpad draft survives device switching");
        NewMacro_Click(this, new RoutedEventArgs());
        AddStep("down"); AddStep("delay"); AddStep("up");
        Check(CurrentMacro?.Steps.Count == 3 && MacroWarnings(CurrentMacro).Contains("Alle gedrückten Tasten", StringComparison.Ordinal), "Macro editor produces balanced down-delay-up sequence");
        AssignMacro_Click(this, new RoutedEventArgs());
        Check(Assignment(padKey).Binding == "MACRO:" + CurrentMacro!.Id, "Macro can be assigned to selected key");
        var encoder = Device.Keys.First(k => k.IsEncoder).Id;
        _selection.Clear(); _selection.Add(encoder); var encoderColor = Assignment(encoder).Color;
        ApplyColor("#FFAABB"); Check(Assignment(encoder).Color == encoderColor, "Encoder without LED ignores RGB painting");
        var encoderBinding = Assignment(encoder).Binding;
        ApplyBinding("MO(2)"); Check(Assignment(encoder).Binding == encoderBinding, "Encoder momentary-layer assignment is rejected");
        ApplySideColor("#446688", LightingTarget.Both);
        Check(Layer.SideLedColor == "#446688" && Layer.ExtraLedColors.Count == 0, "Side-light color updates selected layer without key selection");
        SliderModeChoice.SelectedIndex = 2; SliderMinimum.Text = "12"; SliderMaximum.Text = "1000"; ApplySlider_Click(this, new RoutedEventArgs());
        Check(_project.Slider.Mode == 2 && _project.Slider.Minimum == 12 && _project.Slider.Maximum == 1000, "Slider mode and calibration update the local project");
        SliderMaximum.Text = "5000"; ApplySlider_Click(this, new RoutedEventArgs());
        Check(_project.Slider.Maximum == 1000, "Out-of-range slider calibration is rejected");
        Check(ProjectService.Validate(_project).Count == 0, "Editor-generated project passes backend validation");
        var image = OnboardSerializer.Prepare(_project);
        Check(image.Payload.Length == 3780 && image.LedCount == 31 && image.MacroEvents == 3, "Prepared Numpad binary reports exact size, RGB and macro capacity");
        var decoded = OnboardSerializer.Decode(image.Payload);
        Check(decoded.Slider.Mode == 2 && decoded.Layers[_layerIndex].ExtraLedColors.Values.All(c => c == "#446688"), "Prepared binary preserves slider settings and every side LED color");
        Check(OnboardSerializer.Prepare(decoded).Payload.AsSpan().SequenceEqual(image.Payload), "Prepared binary decodes and re-encodes without data loss");
        var transferPreview = new TransferWindow(ProjectService.Clone(_project));
        Check(transferPreview.ImportedProject is null, "Transfer preview is constructed without device discovery or hardware writes");
        transferPreview.Close();
        var testPath = Path.Combine(Paths.DataRoot, "ui-smoke-roundtrip.json");
        try
        {
            ProjectService.Save(testPath, _project);
            Check(Fingerprint(ProjectService.Load(testPath)) == Fingerprint(_project), "Saved JSON roundtrip preserves colors, layers and macros");
        }
        finally { if (File.Exists(testPath)) File.Delete(testPath); }
        Check(!_backup?.HasVerifiedDeviceDump ?? true, "Archive verification never claims a complete device dump");
        var defaultTests = Path.Combine(Path.GetTempPath(), "inglorious-default-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(defaultTests);
        try
        {
            var savedPad = ProjectService.Create("numpad"); savedPad.Name = "Saved migration";
            var savedKey = DeviceCatalog.Get("numpad").Keys.First(k => k.LedIndex is not null).Id;
            savedPad.Layers[0].Keys[savedKey].Color = "#62F6FF";
            var savedPath = Path.Combine(defaultTests, "numpad.inglorious.json"); ProjectService.Save(savedPath, savedPad);
            var opened = OpenDefaultProject("numpad", savedPath);
            Check(opened.Path == savedPath && opened.Error is null && opened.Project.Name == savedPad.Name && opened.Project.Layers[0].Keys[savedKey].Color == "#62F6FF", "Existing device default loads saved migration colors and project name");
            var missing = OpenDefaultProject("numpad", Path.Combine(defaultTests, "missing.json"));
            Check(missing.Path is null && missing.Error is null && missing.SourceDescription.Contains("Lokale Vorlage"), "Missing default is clearly identified as a local template");
            var malformedPath = Path.Combine(defaultTests, "malformed.json"); File.WriteAllText(malformedPath, "{invalid");
            var failed = OpenDefaultProject("numpad", malformedPath);
            ApplyOpening(failed); RefreshAll();
            Check(failed.Path is null && failed.Error is not null && ProjectSourceHint.Text.Contains("konnte nicht geladen") && File.ReadAllText(malformedPath) == "{invalid", "Invalid default shows persistent visible warning and leaves source file unchanged");
            var wrongPath = Path.Combine(defaultTests, "wrong-device.json"); ProjectService.Save(wrongPath, ProjectService.Create("pro-iso"));
            var wrong = OpenDefaultProject("numpad", wrongPath);
            Check(wrong.Error is not null && wrong.Path is null && wrong.Project.DeviceId == "numpad", "Cross-device default is rejected before editor adoption");
            ApplyOpening(opened); RefreshAll();
            Check(!IsDirty && _sourceDescription.StartsWith("Gespeichertes Profil") && SafetyBanner.Visibility == Visibility.Collapsed, "Loaded default is clean and labeled as a saved local project");
            _selection.Clear(); _selection.Add(savedKey); ApplyColor("#FFE501");
            Check(Fingerprint(_project) != _sourceFingerprint && SafetyBanner.Visibility == Visibility.Collapsed, "Editing a loaded project changes its origin label to local draft");
            SwitchDevice("pro-iso"); SwitchDevice("numpad");
            Check(Assignment(savedKey).Color == "#FFE501" && Fingerprint(_project) != _sourceFingerprint && SafetyBanner.Visibility == Visibility.Collapsed, "In-session edited project and source state take precedence over default reload");
            _project.Macros.Add(new MacroDefinition { Name = "Incomplete draft" });
            Check(ProjectService.Validate(_project).Count > 0, "Incomplete draft fixture is invalid before independent device import");
            AdoptDeviceProject(savedPad, 7);
            Check(_project.Macros.Count == 0 && IsDirty && _sourceDescription.StartsWith("Vom Gerät gelesen · Stand 7") && SafetyBanner.Visibility == Visibility.Collapsed, "Device import accepts decoded project independently of invalid old draft and labels exact source");
        }
        finally { Directory.Delete(defaultTests, true); }
        var dpi = VisualTreeHelper.GetDpi(this);
        checks.Add($"INFO WPF DPI scale: {dpi.DpiScaleX.ToString(CultureInfo.InvariantCulture)} × {dpi.DpiScaleY.ToString(CultureInfo.InvariantCulture)}");
        return string.Join(Environment.NewLine, checks) + Environment.NewLine;
    }

    private void RunLightingChecks(Action<bool, string> check)
    {
        var picker = new ColorPickerWindow("#62F6FF");
        try
        {
        var hex = (TextBox)picker.FindName("HexInput");
        var red = (TextBox)picker.FindName("RedInput");
        var green = (TextBox)picker.FindName("GreenInput");
        var blue = (TextBox)picker.FindName("BlueInput");
        var apply = (Button)picker.FindName("ApplyButton");
        var hue = (Slider)picker.FindName("HueSlider");
        check(picker.SelectedHex == "#62F6FF" && red.Text == "98" && green.Text == "246" && blue.Text == "255", "Color picker initializes exact RGB values");
        hex.Text = "#FF0000"; hue.Value = 120;
        check(picker.SelectedHex == "#00FF00" && hex.Text == "#00FF00" && green.Text == "255", "Hue slider synchronizes color preview, Hex and RGB");
        hue.Value = 240; check(picker.SelectedHex == "#0000FF", "Hue slider reaches exact primary blue");
        hex.Text = "#12"; check(!apply.IsEnabled && picker.SelectedHex == "#0000FF", "Incomplete Hex cannot be accepted and preserves last valid color");
        hex.Text = "#AABBCC"; red.Text = "256";
        check(!apply.IsEnabled && picker.SelectedHex == "#AABBCC", "RGB out of range cannot be accepted");
        red.Text = "17"; check(apply.IsEnabled && picker.SelectedHex == "#11BBCC" && hex.Text == "#11BBCC", "RGB edits update Hex and re-enable valid acceptance");
        hex.Text = "#000000"; hue.Value = 60;
        check(picker.SelectedHex == "#000000", "Changing hue preserves zero brightness");
        hex.Text = "#FFFFFF"; hue.Value = 120;
        check(picker.SelectedHex == "#FFFFFF", "Changing hue preserves zero saturation");
        hex.Text = "#0000FF";
        picker.EditSaturationValue(Key.Left, ModifierKeys.None);
        check(picker.SelectedHex == "#0303FF", "Saturation-value field supports keyboard editing");
        check(!picker.EditSaturationValue(Key.Left, ModifierKeys.Control) && picker.SelectedHex == "#0303FF", "Control shortcut does not edit saturation");
        check(!picker.EditSaturationValue(Key.Up, ModifierKeys.Alt) && picker.SelectedHex == "#0303FF", "Alt shortcut does not edit brightness");
        hex.Text = "#0000FF"; picker.EditSaturationValue(Key.Left, ModifierKeys.Shift);
        check(picker.SelectedHex == "#0D0DFF", "Shift explicitly selects a five-percent saturation step");
        }
        finally { picker.Close(); }
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            SwitchDevice(id);
            var keyState = JsonSerializer.Serialize(Layer.Keys);
            var otherLayer = JsonSerializer.Serialize(_project.Layers[1]);
            var right = Device.RightSideLedIndices.ToDictionary(i => i, SideLedColor);
            SetLightingTarget(LightingTarget.Left); SetTab(0); ApplyLightingColor("#13579B");
            check(Device.LeftSideLedIndices.All(i => SideLedColor(i) == "#13579B") && right.All(p => SideLedColor(p.Key) == p.Value), id + ": left-side edit preserves every right-side LED");
            SetLightingTarget(LightingTarget.Right); ApplyLightingColor("#F0C020");
            check(Device.LeftSideLedIndices.All(i => SideLedColor(i) == "#13579B") && Device.RightSideLedIndices.All(i => SideLedColor(i) == "#F0C020"), id + ": right-side edit preserves left side");
            check(JsonSerializer.Serialize(Layer.Keys) == keyState && JsonSerializer.Serialize(_project.Layers[1]) == otherLayer, id + ": side edits preserve keys and other layers");
            var beforeInvalid = Fingerprint(_project); ApplyLightingColor("invalid");
            check(Fingerprint(_project) == beforeInvalid, id + ": invalid side color leaves project unchanged");
            var decoded = OnboardSerializer.Decode(OnboardSerializer.Prepare(_project).Payload);
            check(Device.LeftSideLedIndices.All(i => decoded.Layers[0].ExtraLedColors[i] == "#13579B") && Device.RightSideLedIndices.All(i => decoded.Layers[0].ExtraLedColors[i] == "#F0C020"), id + ": serialized configuration retains distinct side colors");
            SetLightingTarget(LightingTarget.Both); ApplyLightingColor("#446688");
            Undo_Click(this, new RoutedEventArgs());
            check(Device.LeftSideLedIndices.All(i => SideLedColor(i) == "#13579B") && Device.RightSideLedIndices.All(i => SideLedColor(i) == "#F0C020"), id + ": undo restores distinct colors after changing both sides");
            for (var layer = 0; layer < 4; layer++)
                foreach (var led in Device.NonKeyLedIndices) _project.Layers[layer].ExtraLedColors[led] = $"#{layer + 1:X2}{led + 1:X2}11";
            _layerIndex = 2; RefreshAll();
            var firstLed = Device.LeftSideLedIndices[1]; var secondLed = Device.RightSideLedIndices[^1];
            var beforeLeds = Fingerprint(_project); var beforePayload = OnboardSerializer.Prepare(_project).Payload;
            var fallback = Layer.SideLedColor;
            SelectSideLed(firstLed, toggle: false); SelectSideLed(secondLed, toggle: true); ApplyLightingColor("#ABCDE1");
            var afterPayload = OnboardSerializer.Prepare(_project).Payload;
            var rgbBase = id == "pro-iso" ? 752 : 208;
            var expectedBytes = new[] { firstLed, secondLed }.SelectMany(led => Enumerable.Range(rgbBase + (2 * Device.LedCount + led) * 3, 3)).ToHashSet();
            var changedBytes = Enumerable.Range(0, beforePayload.Length).Where(i => beforePayload[i] != afterPayload[i]).ToHashSet();
            check(changedBytes.SetEquals(expectedBytes) && Layer.SideLedColor == fallback, id + ": editing two LEDs changes exactly six RGB bytes, preserving all other configuration");
            check(OnboardSerializer.Prepare(OnboardSerializer.Decode(afterPayload)).Payload.SequenceEqual(afterPayload), id + ": mixed side colors survive binary decode and re-encode");
            Undo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == beforeLeds, id + ": undo restores every original LED color");
            Redo_Click(this, new RoutedEventArgs()); check(OnboardSerializer.Prepare(_project).Payload.SequenceEqual(afterPayload), id + ": redo restores individual LED colors");
            SelectSideLed(firstLed, toggle: true); SelectSideLed(secondLed, toggle: true);
            var beforeEmpty = Fingerprint(_project); ApplyLightingColor("#102030");
            check(SideIndices(_lightingTarget).Count == 0 && Fingerprint(_project) == beforeEmpty, id + ": empty LED selection does not paint a whole side");
            SetLightingTarget(LightingTarget.Left); SelectSideLed(Device.LeftSideLedIndices[0], toggle: true);
            check(SideIndices(_lightingTarget).Count == Device.LeftSideLedIndices.Count - 1 && !SideIndices(_lightingTarget).Contains(Device.LeftSideLedIndices[0]), id + ": multi-select can remove one LED from a whole-side selection");
            SelectSideLed(firstLed, toggle: false); SwitchDevice(id == "pro-iso" ? "numpad" : "pro-iso");
            check(_selectedSideLeds.Count == 0 && _lightingTarget == LightingTarget.Keys, id + ": device switch clears device-specific LED selection");
            SwitchDevice(id); _layerIndex = 0; RefreshAll();
        }
        SwitchDevice("pro-iso");
        var enter = Device.Keys.Single(k => k.IsIsoEnter);
        var enterButton = _keyButtons[enter.Id];
        check(enterButton.Clip.FillContains(new Point(5, 10)) && !enterButton.Clip.FillContains(new Point(5, 65)) && enterButton.Clip.FillContains(new Point(30, 65)), "ISO Enter hit area includes upper extension and excludes lower notch");
        check(Math.Abs(Canvas.GetLeft(enterButton) - (32 + 13.5 * 51)) < .01, "ISO Enter upper edge aligns with QMK physical layout inside the side-LED gutter");
        for (var mode = 0; mode < 5; mode++)
        {
            SetTab(mode);
            var panels = new[] { ColorPanel, BindingPanel, MacroPanel, LayerPanel, DeviceSettingsPanel };
            check(panels.Count(p => p.Visibility == Visibility.Visible) == 1 && panels[mode].Visibility == Visibility.Visible, "Section " + mode + " shows its own editing controls");
        }
        var oldName = Layer.Name; LayerName.Text = "Arbeit"; RenameLayer_Click(this, new RoutedEventArgs());
        check(Layer.Name == "Arbeit" && ((ComboBoxItem)TargetLayer.Items[0]).Content.ToString()!.Contains("Arbeit"), "Layer rename updates navigation and target labels");
        Undo_Click(this, new RoutedEventArgs()); check(Layer.Name == oldName, "Layer rename can be undone");
        var paletteDirectory = Path.Combine(Path.GetTempPath(), "inglorious-palette-ui-" + Guid.NewGuid().ToString("N"));
        var previousPath = _palettePath;
        try
        {
            _palettePath = Path.Combine(paletteDirectory, "colors.json"); ColorPaletteStore.Save(_palettePath, []); LoadColorPalette();
            ColorHex.Text = "#314159"; SaveColor_Click(this, new RoutedEventArgs()); LoadColorPalette();
            check(_customColors.SequenceEqual(new[] { "#314159" }) && CustomColorSwatches.Children.Count == 1, "Saved custom color reloads into palette without a device write");
            SetTab(0); SetLightingTarget(LightingTarget.Right);
            ((Button)CustomColorSwatches.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(Device.RightSideLedIndices.All(i => SideLedColor(i) == "#314159"), "Saved color applies to the selected side");
            ClearSelection_Click(this, new RoutedEventArgs());
            check(_lightingTarget == LightingTarget.Keys && SelectionText.Text == "Keine Taste ausgewählt", "Clear selection also clears selected side lighting");
            var menu = ((Button)CustomColorSwatches.Children[0]).ContextMenu;
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); LoadColorPalette();
            check(_customColors.Count == 0 && CustomColorSwatches.Children.Count == 0, "Removing a saved color persists across palette reloads");
        }
        finally { _palettePath = previousPath; LoadColorPalette(); if (Directory.Exists(paletteDirectory)) Directory.Delete(paletteDirectory, true); }
        _sessions.Clear(); ApplyOpening(OpenDefaultProject("pro-iso", allowDefault: false));
        _lightingTarget = LightingTarget.Keys; _activeTab = 0; RefreshAll();
    }

    private void RunSideSynchronizationChecks(Action<bool, string> check)
    {
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            ApplyOpening(OpenDefaultProject(id, allowDefault: false)); _layerIndex = 1;
            for (var layer = 0; layer < 4; layer++)
                foreach (var led in Device.NonKeyLedIndices) _project.Layers[layer].ExtraLedColors[led] = $"#{layer + 1:X2}{led + 1:X2}11";
            RefreshAll(); SetLightingTarget(LightingTarget.Left);
            var before = Fingerprint(_project);
            var otherLayer = JsonSerializer.Serialize(_project.Layers[2]);
            var keyColors = JsonSerializer.Serialize(Layer.Keys);
            var fallback = Layer.SideLedColor;
            var originalLeft = Device.LeftSideLedIndices.Select(SideLedColor).ToArray();
            SideSyncToggle.IsChecked = true; SideSyncToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(Layer.SynchronizeSides && Device.RightSideLedIndices.Select(SideLedColor).SequenceEqual(originalLeft) && Device.LeftSideLedIndices.Select(SideLedColor).SequenceEqual(originalLeft), id + ": enabling sync copies left pattern to matching right positions");
            check(JsonSerializer.Serialize(_project.Layers[2]) == otherLayer && JsonSerializer.Serialize(Layer.Keys) == keyColors && Layer.SideLedColor == fallback, id + ": enabling sync preserves keys, other layers and fallback color");
            var enabled = Fingerprint(_project);
            Undo_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == before && SideSyncToggle.IsChecked == false, id + ": one undo restores flag and former right-side colors");
            Redo_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == enabled && SideSyncToggle.IsChecked == true, id + ": redo restores synchronization without a second mutation");
            var left = Device.LeftSideLedIndices[1]; var right = Device.RightSideLedIndices[1];
            SelectSideLed(right, toggle: false);
            check(SideIndices(_lightingTarget).ToHashSet().SetEquals(new[] { left, right }), id + ": selecting a right LED highlights its matching left LED");
            var payloadBefore = OnboardSerializer.Prepare(_project).Payload;
            ApplyLightingColor("#CCBBAA");
            var payloadAfter = OnboardSerializer.Prepare(_project).Payload;
            var rgbBase = id == "pro-iso" ? 752 : 208;
            var expectedBytes = new[] { left, right }.SelectMany(led => Enumerable.Range(rgbBase + (Device.LedCount + led) * 3, 3)).ToHashSet();
            check(Enumerable.Range(0, payloadBefore.Length).Where(i => payloadBefore[i] != payloadAfter[i]).ToHashSet().SetEquals(expectedBytes), id + ": synchronized edit changes only the two corresponding RGB triples");
            SelectSideLed(left, toggle: true);
            var deselected = Fingerprint(_project); ApplyLightingColor("#112233");
            check(SideIndices(_lightingTarget).Count == 0 && Fingerprint(_project) == deselected, id + ": toggling the counterpart deselects the entire pair");
            _layerIndex = 2; RefreshAll();
            check(SideSyncToggle.IsChecked == false && JsonSerializer.Serialize(Layer) == otherLayer, id + ": switching layers shows that layer's independent sync setting");
            _layerIndex = 1; RefreshAll();
            check(SideSyncToggle.IsChecked == true, id + ": sync setting survives layer switching");
            var decoded = OnboardSerializer.Decode(OnboardSerializer.Prepare(_project).Payload);
            check(decoded.Layers.All(l => !l.SynchronizeSides) && decoded.Layers[1].ExtraLedColors[left] == "#CCBBAA" && decoded.Layers[1].ExtraLedColors[right] == "#CCBBAA", id + ": device import preserves actual colors without inventing an editor sync preference");
            var colorsBeforeDisable = JsonSerializer.Serialize(Layer.ExtraLedColors);
            SetSideSynchronization(false);
            check(JsonSerializer.Serialize(Layer.ExtraLedColors) == colorsBeforeDisable, id + ": disabling synchronization leaves colors unchanged");
            SelectSideLed(left, toggle: false); ApplyLightingColor("#123456");
            check(SideLedColor(left) == "#123456" && SideLedColor(right) == "#CCBBAA", id + ": disabled sync allows independent editing again");
        }
        _sessions.Clear(); ApplyOpening(OpenDefaultProject("pro-iso", allowDefault: false));
        _lightingTarget = LightingTarget.Keys; _selectedSideLeds.Clear(); _activeTab = 0; RefreshAll();
    }

    private void RunEffectAndTimingChecks(Action<bool, string> check)
    {
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            ApplyOpening(OpenDefaultProject(id, allowDefault: false)); _layerIndex = 2;
            _lightingTarget = LightingTarget.Keys; _showLightingEffects = true; _effectForSelection = false; _activeTab = 0;
            RefreshAll();
            var before = Fingerprint(_project);
            var colors = JsonSerializer.Serialize(Layer.Keys) + JsonSerializer.Serialize(Layer.ExtraLedColors);
            var otherLayer = JsonSerializer.Serialize(_project.Layers[0]);
            EffectChoice.SelectedValue = 3; EffectSpeed.Value = 201; EffectBrightness.Value = 123; EffectReverse.IsChecked = true; EffectTarget.SelectedIndex = 0;
            ApplyEffect_Click(this, new RoutedEventArgs());
            check(Layer.LightingEffect is { Mode: 3, Speed: 201, Brightness: 123, Target: 1, Reverse: true } && IsDirty, id + ": global effect controls persist exact parameters in the selected layer");
            check(JsonSerializer.Serialize(Layer.Keys) + JsonSerializer.Serialize(Layer.ExtraLedColors) == colors && JsonSerializer.Serialize(_project.Layers[0]) == otherLayer, id + ": global effect preserves individual colors and other layers");
            var applied = Fingerprint(_project); Undo_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == before, id + ": global effect is one undo step");
            Redo_Click(this, new RoutedEventArgs()); check(Fingerprint(_project) == applied, id + ": redo restores the global effect");

            var key = Device.Keys.First(k => k.LedIndex.HasValue); SelectKey(key.Id);
            check(_showLightingEffects && _effectForSelection && EffectTargetPanel.Visibility == Visibility.Collapsed && EffectScopeHint.Text.Contains("globalen Effekt"), id + ": selecting a key keeps the effects editor and exposes inheritance");
            EffectChoice.SelectedValue = 19; EffectSpeed.Value = 99; EffectBrightness.Value = 77; EffectReverse.IsChecked = true;
            ApplyEffect_Click(this, new RoutedEventArgs());
            check(Layer.LedEffects.Count == 1 && Layer.LedEffects[key.LedIndex!.Value] is { Mode: 19, Speed: 99, Brightness: 77, Reverse: false } && Layer.LightingEffect.Mode == 3, id + ": per-key effect has independent parameters and leaves the global effect intact");
            SetEffectScope(false); EffectChoice.SelectedValue = 5; ApplyEffect_Click(this, new RoutedEventArgs());
            check(Layer.LedEffects[key.LedIndex!.Value].Mode == 19 && Layer.LightingEffect.Mode == 5, id + ": changing global effect preserves per-key overrides");
            SetEffectScope(true); InheritEffect_Click(this, new RoutedEventArgs());
            check(Layer.LedEffects.Count == 0 && !InheritEffectButton.IsEnabled && (int)EffectChoice.SelectedValue == 5, id + ": selected key can return to global inheritance");
            Undo_Click(this, new RoutedEventArgs()); check(Layer.LedEffects[key.LedIndex!.Value].Mode == 19, id + ": undo restores a removed per-key override");
            _selection.Clear(); _selection.Add(Device.Keys.First(k => k.IsEncoder).Id); RefreshInspector();
            var beforeNoLed = Fingerprint(_project); ApplyEffect_Click(this, new RoutedEventArgs());
            check(!ApplyEffectButton.IsEnabled && Fingerprint(_project) == beforeNoLed, id + ": encoder without an LED cannot receive a lighting effect");

            var left = Device.LeftSideLedIndices[1]; var right = Device.RightSideLedIndices[1];
            SelectSideLed(left, toggle: false); EffectChoice.SelectedValue = 16; EffectSpeed.Value = 202; EffectBrightness.Value = 188; EffectReverse.IsChecked = true;
            ApplyEffect_Click(this, new RoutedEventArgs());
            check(Layer.LedEffects[left] is { Mode: 16, Speed: 202, Brightness: 188, Reverse: true } && !Layer.LedEffects.ContainsKey(right), id + ": individual side LED accepts its own effect without changing the opposite side");
            SetSideSynchronization(true);
            check(JsonSerializer.Serialize(Layer.LedEffects[left]) == JsonSerializer.Serialize(Layer.LedEffects[right]) && !ReferenceEquals(Layer.LedEffects[left], Layer.LedEffects[right]), id + ": side synchronization copies the left effect without object aliasing");
            SelectSideLed(right, toggle: false); EffectChoice.SelectedValue = 1; EffectSpeed.Value = 170; ApplyEffect_Click(this, new RoutedEventArgs());
            check(Layer.LedEffects[left].Mode == 1 && Layer.LedEffects[right].Mode == 1 && Layer.LedEffects[left].Speed == 170 && Layer.LedEffects[right].Speed == 170, id + ": synchronized side effects update the matching pair from either side");
            InheritEffect_Click(this, new RoutedEventArgs());
            check(!Layer.LedEffects.ContainsKey(left) && !Layer.LedEffects.ContainsKey(right), id + ": synchronized inheritance clears both overrides together");
            SetTab(4); PollingChoice.SelectedValue = "4"; DebounceInput.Text = "9"; ApplyDeviceSettings_Click(this, new RoutedEventArgs());
            check(_project.DeviceSettings.UsbPollingIntervalMs == 4 && _project.DeviceSettings.DebounceMs == 9 && DeviceSettingsPanel.Visibility == Visibility.Visible, id + ": timing controls update the project-wide settings");
            var beforeInvalid = Fingerprint(_project); DebounceInput.Text = "41"; ApplyDeviceSettings_Click(this, new RoutedEventArgs());
            check(Fingerprint(_project) == beforeInvalid, id + ": invalid debounce input cannot alter the project");
            Undo_Click(this, new RoutedEventArgs());
            check(_project.DeviceSettings.UsbPollingIntervalMs == 1 && _project.DeviceSettings.DebounceMs == 5 && PollingChoice.SelectedValue?.ToString() == "1" && DebounceInput.Text == "5", id + ": timing undo restores settings and visible controls");
            Redo_Click(this, new RoutedEventArgs());
            var payload = OnboardSerializer.Prepare(_project).Payload; var decoded = OnboardSerializer.Decode(payload);
            check(decoded.Layers[2].LightingEffect.Mode == 5 && decoded.Layers[2].LedEffects[key.LedIndex!.Value].Mode == 19 && decoded.Layers[2].SynchronizeSides && decoded.DeviceSettings.UsbPollingIntervalMs == 4 && decoded.DeviceSettings.DebounceMs == 9, id + ": generated device configuration preserves effects, overrides, side sync and timing");
            check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(payload), id + ": complete editor configuration roundtrips through device format unchanged");
        }
        _sessions.Clear(); ApplyOpening(OpenDefaultProject("pro-iso", allowDefault: false));
        _lightingTarget = LightingTarget.Keys; _selectedSideLeds.Clear(); _activeTab = 0; _showLightingEffects = false; _effectForSelection = false; RefreshAll();
    }
}
