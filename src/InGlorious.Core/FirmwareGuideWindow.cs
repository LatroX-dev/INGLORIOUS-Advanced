using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InGlorious.Core;

public enum FirmwareGuideState { Unverified, Original, UpdateRequired, Ready, NewerVersion }
public enum FirmwareGuideAction { None, OpenFirmwareManager }
public sealed record FirmwareGuideDevice(string DeviceId, FirmwareGuideState State, bool LocalFilesVerified = false, string AvailabilityMessage = "")
{
    public FirmwareRuntimeVersion? RuntimeVersion { get; init; }
    public bool CanOpenManager => BoardDescriptorRegistry.TryGet(DeviceId, out var board) && board.HasApprovedFirmwareManagement && LocalFilesVerified && (State is FirmwareGuideState.Original or FirmwareGuideState.UpdateRequired);
    public FirmwareOperation Operation => State == FirmwareGuideState.Original ? FirmwareOperation.InstallFromOriginal : FirmwareOperation.Update;
    public string Status => L.T(State switch
    {
        FirmwareGuideState.Original => "Glorious-Firmware erkannt",
        FirmwareGuideState.UpdateRequired => "INGLORIOUS erkannt · Aktualisierung erforderlich",
        FirmwareGuideState.Ready => "Firmware ist aktuell",
        FirmwareGuideState.NewerVersion => "Neuere Firmware installiert",
        _ => "Gerät nicht eindeutig erkannt"
    });
    public static FirmwareGuideState Classify(OnboardInfo? info, string expectedDeviceId, OnboardFirmwareVersion? firmwareVersion)
    {
        if (!BoardDescriptorRegistry.TryGet(expectedDeviceId, out _) || info is null || info.DeviceId != expectedDeviceId || !info.Persistent || (info.Flags & 4) != 0)
            return FirmwareGuideState.Unverified;
        if (firmwareVersion is null) return FirmwareGuideState.Unverified;
        if (firmwareVersion.DeviceId != expectedDeviceId || firmwareVersion.BuildId == 0) return FirmwareGuideState.Unverified;
        var comparison = firmwareVersion.Version.CompareTo(FirmwareRelease.CurrentVersion);
        return comparison < 0 ? FirmwareGuideState.UpdateRequired : comparison > 0 ? FirmwareGuideState.NewerVersion : FirmwareGuideState.Ready;
    }
}

/// <summary>Displays local evidence and instructions. No device or process calls occur in this window.</summary>
public sealed class FirmwareGuideWindow : Window
{
    private readonly IReadOnlyList<FirmwareGuideDevice> _devices;
    private readonly ComboBox _deviceChoice = new();
    private readonly TextBlock _state = new(), _availability = new(), _nextStep = new(), _instruction = new(), _actionNote = new(), _heading = new(), _deviceName = new();
    private readonly StackPanel _detailsContent = new();
    private readonly Expander _details = new();
    private readonly FirmwareChangelogPanel _changelog = new();
    private readonly Button _open = new();
    public FirmwareGuideAction Action { get; private set; }
    public string? SelectedDeviceId => (_deviceChoice.SelectedItem as ComboBoxItem)?.Tag as string;
    public FirmwareOperation RequestedOperation => Selected.Operation;
    private FirmwareGuideDevice Selected => _devices.First(device => device.DeviceId == SelectedDeviceId);

    public FirmwareGuideWindow(IReadOnlyList<FirmwareGuideDevice> devices, string? selectedDeviceId = null)
    {
        if (devices.Count == 0 || devices.Any(device => !BoardDescriptorRegistry.TryGet(device.DeviceId, out _)) || devices.Select(device => device.DeviceId).Distinct().Count() != devices.Count)
            throw new ArgumentException("A unique supported device assessment is required.", nameof(devices));
        _devices = devices;
        Width = 720; Height = Math.Min(550, SystemParameters.WorkArea.Height - 40);
        MinWidth = 680; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        var grid = new Grid(); Content = new Border { Background = Background, Padding = new(24), Child = grid };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new(0, 0, 0, 18) }; grid.Children.Add(heading);
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/inglorious-mark.png")), Width = 40, Height = 40, Margin = new(0, 0, 14, 0) });
        _heading.FontSize = 24; _heading.FontWeight = FontWeights.SemiBold; _heading.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(_heading); heading.Children.Add(brand);
        _deviceName.FontSize = 14; _deviceName.Margin = new(0, 16, 0, 0); heading.Children.Add(_deviceName);
        _deviceChoice.MinHeight = 36;
        _deviceChoice.Visibility = devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var device in devices) _deviceChoice.Items.Add(new ComboBoxItem { Content = DeviceCatalog.Get(device.DeviceId).Name, Tag = device.DeviceId });
        heading.Children.Add(_deviceChoice);
        var content = new StackPanel(); var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new(0, 0, 12, 0) };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        _state.FontSize = 13; _state.Foreground = (Brush)FindResource("Muted"); _state.TextWrapping = TextWrapping.Wrap; _state.Margin = new(0, 0, 0, 16); content.Children.Add(_state);
        _nextStep.FontSize = 20; _nextStep.FontWeight = FontWeights.SemiBold; _nextStep.TextWrapping = TextWrapping.Wrap; _nextStep.Margin = new(0, 0, 0, 8); content.Children.Add(_nextStep);
        _instruction.FontSize = 14; _instruction.TextWrapping = TextWrapping.Wrap; _instruction.Margin = new(0, 0, 0, 12); content.Children.Add(_instruction);
        content.Children.Add(_changelog);
        _actionNote.FontSize = 12; _actionNote.TextWrapping = TextWrapping.Wrap; _actionNote.Foreground = (Brush)FindResource("Muted"); _actionNote.Margin = new(0, 0, 0, 12); content.Children.Add(_actionNote);
        _availability.TextWrapping = TextWrapping.Wrap; _availability.Margin = new(0, 0, 0, 12); content.Children.Add(_availability);
        _details.Header = L.T("Sicherung und weitere Hinweise"); _details.Foreground = (Brush)FindResource("Text"); _details.IsExpanded = false;
        _detailsContent.Margin = new(0, 12, 0, 0); _details.Content = _detailsContent; content.Children.Add(_details);
        var footer = new StackPanel { Margin = new(0, 18, 0, 0) }; Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        var close = new Button { Content = L.T("Schließen"), IsCancel = true, IsDefault = true, MinWidth = 110, Margin = new(0, 0, 10, 0) }; close.Click += (_, _) => Close(); actions.Children.Add(close);
        _open.Style = (Style)FindResource("PrimaryButton"); _open.Margin = new(0); actions.Children.Add(_open);
        _open.Click += (_, _) => { if (Selected.CanOpenManager) { Action = FirmwareGuideAction.OpenFirmwareManager; DialogResult = true; } };
        NameScope.SetNameScope(this, new NameScope()); RegisterName("GuideOpenManager", _open); RegisterName("GuideDeviceChoice", _deviceChoice); RegisterName("GuideStatus", _state);
        RegisterName("GuideNextStep", _nextStep); RegisterName("GuideInstruction", _instruction); RegisterName("GuideDetails", _details);
        RegisterName("GuideChangelog", _changelog);
        _deviceChoice.SelectionChanged += (_, _) => Refresh();
        _deviceChoice.SelectedItem = _deviceChoice.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selectedDeviceId) ?? _deviceChoice.Items[0];
    }
    private TextBlock Text(string text, double size, bool bold = false, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0), VerticalAlignment = VerticalAlignment.Center
    };
    private void Detail(string message)
    {
        _detailsContent.Children.Add(Text(message, 12, margin: new(0, 0, 0, 12)));
    }
    private void Refresh()
    {
        var device = Selected;
        if (!DeviceCatalog.Get(device.DeviceId).HasApprovedFirmwareManagement)
        {
            _changelog.ShowChanges(device.DeviceId, null, FirmwareRelease.CurrentVersion);
            _heading.Text = L.T("Gerätestatus"); Title = _heading.Text + " · INGLORIOUS Advanced";
            _deviceName.Text = "\"" + DeviceCatalog.Get(device.DeviceId).Name + "\"";
            _state.Text = device.RuntimeVersion is { } verified ? "INGLORIOUS " + verified.Report.Version : L.T("Version noch nicht geprüft");
            _open.IsEnabled = false; _open.Visibility = Visibility.Collapsed;
            _nextStep.Text = L.T("Firmware"); _instruction.Text = L.T("Firmwareinstallation ist für dieses Modell noch nicht verfügbar.");
            _actionNote.Visibility = _availability.Visibility = _details.Visibility = Visibility.Collapsed;
            return;
        }
        _details.Visibility = Visibility.Visible;
        _changelog.ShowChanges(device.DeviceId, device.State == FirmwareGuideState.UpdateRequired && device.RuntimeVersion?.Report.DeviceId == device.DeviceId
            ? device.RuntimeVersion.Report.Version : null, FirmwareRelease.CurrentVersion);
        _state.Text = device.RuntimeVersion is { } runtime ? "INGLORIOUS " + runtime.Report.Version + " · " + device.Status : device.Status;
        _open.IsEnabled = device.CanOpenManager;
        _heading.Text = L.T(device.State switch { FirmwareGuideState.Original => "INGLORIOUS einrichten", FirmwareGuideState.UpdateRequired => "Firmware aktualisieren", _ => "Gerätestatus" });
        Title = _heading.Text + " · INGLORIOUS Advanced";
        _deviceName.Text = "\"" + DeviceCatalog.Get(device.DeviceId).Name + "\"";
        _open.Content = L.T(device.State == FirmwareGuideState.Original ? "Einrichtung starten" : "Update vorbereiten");
        _open.Visibility = device.CanOpenManager ? Visibility.Visible : Visibility.Collapsed;
        _detailsContent.Children.Clear(); _details.IsExpanded = false;
        var backupDetail = device.LocalFilesVerified
            ? L.T("Lokale Werkzeuge und Originaldateien sind vorhanden und ihre Prüfsummen stimmen. Der Firmware-Manager prüft das konkrete Gerät nochmals vor jeder Änderung.")
            : string.IsNullOrWhiteSpace(device.AvailabilityMessage) ? L.T("Ohne eindeutige Geräteprüfung und passende lokale Original-Sicherung wird hier keine Firmware-Installation freigegeben.") : FirmwareMessages.ForUser(device.AvailabilityMessage);
        var needsSetup = device.State is FirmwareGuideState.Original or FirmwareGuideState.UpdateRequired;
        _nextStep.Text = L.T(device.State switch
        {
            FirmwareGuideState.Original => "Einrichtung starten",
            FirmwareGuideState.UpdateRequired => "Update vorbereiten",
            FirmwareGuideState.NewerVersion => "Kein Update erforderlich",
            FirmwareGuideState.Ready => "Dein Gerät ist bereit",
            _ => "Gerät anschließen"
        });
        _instruction.Text = L.T(device.State switch
        {
            FirmwareGuideState.Original => "Vor der Installation sichert und prüft die App deine Original-Firmware. Die Sicherung bleibt auf deinem Computer. Unter „Gerät“ → „Original-Firmware wiederherstellen“ kannst du jederzeit zurückwechseln.",
            FirmwareGuideState.UpdateRequired => "Eine neuere Firmware-Version ist verfügbar.",
            FirmwareGuideState.Ready => "Die installierte Firmware entspricht der aktuellen Version.",
            FirmwareGuideState.NewerVersion => "Die installierte Firmware ist neuer als die Version dieser App. Es wird kein Downgrade angeboten.",
            _ => "Schließe das Gerät per USB an und öffne diese Anleitung erneut, um den Status zu prüfen."
        });
        _actionNote.Text = L.T("Im nächsten Fenster wird zuerst geprüft. Es wird noch nichts installiert.");
        _actionNote.Visibility = device.CanOpenManager ? Visibility.Visible : Visibility.Collapsed;
        _availability.Text = L.T("Die benötigten Installationsdateien sind hier nicht verfügbar. Mehr dazu unter „Sicherung und weitere Hinweise“.");
        _availability.Visibility = needsSetup && !device.LocalFilesVerified ? Visibility.Visible : Visibility.Collapsed;
        Detail(device.State is FirmwareGuideState.Ready or FirmwareGuideState.NewerVersion
            ? L.T("Die Firmware-Version wurde direkt vom Gerät gelesen.")
            : backupDetail);
        Detail(L.T("Externe Original-Profilspeicher sind nicht vollständig gesichert."));
        if (needsSetup) Detail(L.T("Die Installation ersetzt die Original-Firmware. Beim Update bleiben die INGLORIOUS-Einstellungen erhalten. Die App prüft den passenden Ausgangsstand vor jeder Änderung."));
        Detail(L.T(SharedBrightnessService.Notice));
    }
}
