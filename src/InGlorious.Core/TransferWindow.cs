using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InGlorious.Core;

/// <summary>Confirmed configuration transfer. Firmware installation is never part of this window.</summary>
public sealed partial class TransferWindow : Window
{
    private readonly OnboardImage _image;
    private readonly AppProject _project;
    private readonly Grid _root;
    private readonly TextBlock _deviceStatus;
    private readonly TextBlock _transferStatus;
    private readonly Button _transferButton;
    private readonly Button _findButton;
    private readonly Button _readButton;
    private readonly Button _importButton;
    private readonly ComboBox _targetChoice;
    private readonly Button _closeButton;
    private readonly Button _firmwareGuideButton;
    private readonly ProgressBar _progress;
    private bool _deviceSearchCompleted, _deviceSearchFailed;
    private bool _busy, _closed;
    private bool _outcomeUncertain, _verificationInvalidated;
    private bool _recoveryMode, _reconnectRequired, _closeAfterOperation;
    private bool _progressActive;
    private int _progressRevision;
    private readonly bool _offlinePreview;
    private AppProject? _readProject;
    private OnboardInfo? _currentInfo;
    private OnboardExtensions? _currentExtensions;
    private sealed record VerifiedTarget(OnboardDevice Device, OnboardInfo Info, string Label, OnboardExtensions? Extensions = null);
    private VerifiedTarget? SelectedTarget => _targetChoice.SelectedItem as VerifiedTarget;
    private async Task<OnboardExtensions?> ReadRequiredExtensionsAsync(OnboardClient client)
    {
        if (!_image.RequiresScreensaver) return null;
        try { return await client.GetExtensionsAsync(); }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            // An unknown capability keeps upload blocked; the confirmed device remains readable.
            return null;
        }
    }
    public AppProject? ImportedProject { get; private set; }
    public bool DeviceSaveVerified { get; private set; }
    public bool FirmwareGuideRequested { get; private set; }
    public bool DeviceStateUncertain => _outcomeUncertain || _verificationInvalidated;
    public uint? ImportedGeneration => _currentInfo?.Generation;
    public string? CompletionNotice { get; private set; }

    public TransferWindow(AppProject project, string? screenshotPath = null, bool offlinePreview = false)
    {
        _offlinePreview = offlinePreview || screenshotPath is not null;
        _project = ProjectService.Clone(project);
        _image = OnboardSerializer.Prepare(_project);
        Title = L.T("Auf Tastatur übertragen · INGLORIOUS Advanced");
        Width = 620; Height = 400; MinWidth = 540; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        Foreground = ColorBrush("#E4E7EB");
        _root = new Grid { Background = ColorBrush("#141618") };
        Content = _root;
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(layout);
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(Label(project.Name, 20, "#E4E7EB", FontWeights.SemiBold));
        header.Children.Add(Label(DeviceCatalog.Get(project.DeviceId).Name, 13, "#A0A7B0", margin: new Thickness(0, 4, 0, 0)));
        layout.Children.Add(header);
        var body = new StackPanel();
        body.Children.Add(Label(L.T("Der bisherige Gerätestand wird automatisch gesichert."), 13, "#A0A7B0", margin: new Thickness(0, 0, 0, 16)));
        _deviceStatus = Label(L.T("Passendes Gerät wird gesucht …"), 13, "#E4E7EB", margin: new Thickness(0, 0, 0, 10));
        body.Children.Add(_deviceStatus);
        _targetChoice = new ComboBox { ItemTemplate = (DataTemplate)Application.Current.FindResource("OptionLabelTemplate"), Visibility = Visibility.Collapsed, FontSize = 13, MinHeight = 34, Margin = new Thickness(0, 0, 0, 10) };
        _targetChoice.SelectionChanged += (_, _) => UpdateTarget(); body.Children.Add(_targetChoice);
        var deviceActions = new WrapPanel();
        _findButton = new Button { Content = L.T("Erneut suchen"), FontSize = 12, MinHeight = 32, Margin = new Thickness(0, 0, 8, 8), Visibility = Visibility.Collapsed };
        _findButton.Click += FindDevices_Click; deviceActions.Children.Add(_findButton);
        _readButton = new Button { Content = L.T("Gerätestand prüfen"), IsEnabled = false, FontSize = 12, MinHeight = 32, Margin = new Thickness(0, 0, 8, 8), Visibility = Visibility.Collapsed };
        _readButton.Click += ReadDevice_Click; deviceActions.Children.Add(_readButton);
        _importButton = new Button { Content = L.T("Als Profil öffnen"), IsEnabled = false, FontSize = 12, MinHeight = 32, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        _importButton.Click += (_, _) =>
        {
            if (!_offlinePreview && !_busy && !_closed && _recoveryMode && !_outcomeUncertain && _readProject is not null)
            { ImportedProject = ProjectService.Clone(_readProject); Close(); }
        };
        deviceActions.Children.Add(_importButton);
        body.Children.Add(deviceActions);
        _firmwareGuideButton = new Button { Content = L.T("Gerätestatus prüfen"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        UiIcons.SetKind(_firmwareGuideButton, "chip");
        _firmwareGuideButton.Click += (_, _) => { if (!_offlinePreview && !_busy && !_closed && !_outcomeUncertain) { FirmwareGuideRequested = true; Close(); } };
        body.Children.Add(_firmwareGuideButton);
        _transferStatus = Label("", 13, "#A0A7B0"); body.Children.Add(_transferStatus);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 12, 0, 0), Foreground = ColorBrush("#EF646B"), Background = ColorBrush("#353A40"), Visibility = Visibility.Collapsed };
        body.Children.Add(_progress);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _closeButton = new Button { Content = L.T("Schließen"), IsCancel = true, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
        _closeButton.Click += (_, _) => Close(); actions.Children.Add(_closeButton);
        _transferButton = new Button { Content = L.T("Auf Tastatur übertragen"), IsEnabled = false, Style = (Style)Application.Current.FindResource("PrimaryButton"), MinHeight = 36 };
        _transferButton.Click += Transfer_Click; actions.Children.Add(_transferButton);
        var footer = new Border { Child = actions, BorderBrush = ColorBrush("#353A40"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _transferStatus.Text = L.T("Der Gerätezugriff läuft noch. Bitte warte auf das Prüfergebnis."); } };
        System.ComponentModel.CancelEventHandler ownerClosing = (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            _transferStatus.Text = L.T("Die Konfiguration wird noch übertragen oder geprüft. Bitte warte auf das Ergebnis.");
            Activate();
        };
        Loaded += (_, _) => { if (Owner is not null) Owner.Closing += ownerClosing; };
        Closed += (_, _) => { _closed = true; _progressActive = false; if (Owner is not null) Owner.Closing -= ownerClosing; };
        if (_offlinePreview) _deviceStatus.Text = L.T("Offline-Vorschau · keine Geräteprüfung");
        if (screenshotPath is not null) Loaded += async (_, _) => await RenderAndClose(screenshotPath);
        else if (!_offlinePreview) Loaded += (_, _) => FindDevices_Click(this, new RoutedEventArgs());
        UpdateControls();
    }

    private async void FindDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview || _busy || _closed || _outcomeUncertain) return;
        ClearConfirmation(); _recoveryMode = false;
        SetBusy(true, L.T("Passendes Gerät wird gesucht …"));
        try
        {
            var candidates = await Task.Run(OnboardClient.FindDevices);
            var matches = new List<VerifiedTarget>(); var failed = false;
            foreach (var device in candidates.Where(d => d.DeviceId == _image.DeviceId))
            {
                try
                {
                    await using var client = OnboardClient.Open(device);
                    var info = await client.GetInfoAsync();
                    var extensions = await ReadRequiredExtensionsAsync(client);
                    matches.Add(new(device, info, DeviceCatalog.Get(device.DeviceId).Name + " (" + (matches.Count + 1) + ")", extensions));
                }
                catch (Exception ex) { failed = true; System.Diagnostics.Trace.TraceError(ex.ToString()); }
            }
            SetDiscoveredTargets(matches, failed);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); SetDiscoveredTargets([], failed: true); }
        finally { SetBusy(false); }
    }

    private void SetDiscoveredTargets(IReadOnlyList<VerifiedTarget> targets, bool failed = false)
    {
        ClearConfirmation(); _recoveryMode = false; _deviceSearchCompleted = true; _deviceSearchFailed = failed;
        var matching = targets.Where(target => target.Device.DeviceId == _image.DeviceId && target.Info.DeviceId == _image.DeviceId).ToArray();
        _targetChoice.ItemsSource = matching;
        _targetChoice.SelectedIndex = matching.Length == 1 ? 0 : -1;
        _transferStatus.Text = "";
        UpdateTarget();
    }

    internal void ShowOfflineTargetPreview()
    {
        if (!_offlinePreview) throw new InvalidOperationException("Offline preview required.");
        var device = new OnboardDevice(_image.DeviceId, DeviceCatalog.Get(_image.DeviceId).Name, "offline-preview");
        var info = new OnboardInfo(_image.DeviceId, 249, _image.UsedBytes, 1, _image.Crc32, 0, _image.SchemaVersion);
        SetDiscoveredTargets([new(device, info, DeviceCatalog.Get(device.DeviceId).Name, new OnboardExtensions(1, 3, 21000))]);
        _deviceStatus.Text = L.T("Offline-Vorschau · keine Geräteprüfung");
    }

    private void UpdateTarget()
    {
        ClearConfirmation();
        var target = SelectedTarget;
        _currentInfo = target?.Info; _currentExtensions = target?.Extensions;
        if (!_outcomeUncertain) _recoveryMode = false;
        _deviceStatus.Text = target is null
            ? _targetChoice.Items.Count > 1 ? L.T("Mehrere passende Geräte gefunden. Wähle ein Gerät.")
                : _deviceSearchFailed ? L.T("Die Geräteprüfung ist fehlgeschlagen. Prüfe die USB-Verbindung und suche erneut.")
                : L.T("Kein passendes Gerät gefunden. Prüfe die USB-Verbindung.")
            : !target.Info.Persistent ? L.T("Das Gerät kann die Einstellungen gerade nicht speichern. Prüfe die USB-Verbindung.")
                : UnsupportedFeature(target.Info) ?? L.T("Bereit zur Übertragung");
        UpdateControls();
    }

    private void UpdateControls()
    {
        var active = !_offlinePreview && !_busy && !_closed;
        _findButton.Visibility = _deviceSearchCompleted && (_targetChoice.Items.Count != 1 || _deviceSearchFailed || DeviceStateUncertain || _currentInfo?.Persistent == false) ? Visibility.Visible : Visibility.Collapsed;
        _findButton.IsEnabled = active && !_outcomeUncertain;
        _targetChoice.Visibility = _targetChoice.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _targetChoice.IsEnabled = active && !_outcomeUncertain;
        _readButton.Visibility = _recoveryMode ? Visibility.Visible : Visibility.Collapsed;
        _readButton.IsEnabled = active && _recoveryMode && SelectedTarget is not null;
        _importButton.Visibility = _recoveryMode && _readProject is not null && !DeviceSaveVerified ? Visibility.Visible : Visibility.Collapsed;
        _importButton.IsEnabled = active && _recoveryMode && !_outcomeUncertain && _readProject is not null;
        _transferButton.IsEnabled = active && !_outcomeUncertain && SelectedTarget is not null && _currentInfo?.Persistent == true && UnsupportedFeature(_currentInfo) is null;
        var needsFirmware = _currentInfo is not null ? UnsupportedFeature(_currentInfo) is not null : _deviceSearchCompleted && _targetChoice.Items.Count == 0;
        _firmwareGuideButton.Visibility = needsFirmware && DeviceCatalog.Get(_image.DeviceId).HasApprovedFirmwareManagement ? Visibility.Visible : Visibility.Collapsed;
        _firmwareGuideButton.IsEnabled = active && !_outcomeUncertain;
        _firmwareGuideButton.Content = _currentInfo is not null ? L.T("Update öffnen") : L.T("Gerätestatus prüfen");
        _deviceStatus.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
        _closeButton.IsEnabled = !_busy;
    }
    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        if (text is not null) _transferStatus.Text = text;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy && !_progressActive;
        UpdateControls();
    }

    private async void ReadDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview || _busy || _closed || !_recoveryMode) return;
        var target = SelectedTarget; if (target is null) return;
        var recoveryRead = _recoveryMode;
        ClearConfirmation(invalidated: true);
        SetBusy(true, L.T("Aktuelle Konfiguration wird vollständig gelesen und geprüft …"));
        try
        {
            await using var client = OnboardClient.Open(target.Device);
            var data = await client.ReadAsync();
            var info = await client.GetInfoAsync();
            _currentExtensions = await ReadRequiredExtensionsAsync(client);
            if (info.ActiveCrc != OnboardProtocol.Crc32(data) || info.BlobSize != data.Length || info.SchemaVersion != OnboardSerializer.Get16(data, 4))
                throw new InvalidDataException(L.T("Die Gerätekonfiguration hat sich nach dem Lesen geändert. Bitte erneut prüfen."));
            AcceptReadResult(data, info);
            if (DeviceSaveVerified && recoveryRead)
            {
                if (_reconnectRequired && info.SupportsTiming) await FinishReconnectAsync(client, info.Generation, info.ActiveCrc);
                CompleteVerifiedOperation();
            }
            else _transferStatus.Text = info.Persistent
                ? L.T("Der Gerätestand unterscheidet sich vom Profil. Du kannst ihn als Profil öffnen oder erneut übertragen.")
                : L.T("Der Gerätestand konnte nicht bestätigt werden. Prüfe die USB-Verbindung und versuche es erneut.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(ex.ToString()); ClearConfirmation(invalidated: true);
            _transferStatus.Text = L.T("Der Gerätestand konnte nicht bestätigt werden. Prüfe die USB-Verbindung und versuche es erneut.");
        }
        finally { FinishOperation(); }
    }

    private async void Transfer_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview || _busy || _closed) return;
        var target = SelectedTarget;
        if (target is null || _currentInfo?.Persistent != true || _outcomeUncertain) return;
        if (UnsupportedFeature(_currentInfo) is { } unsupported) { _transferStatus.Text = unsupported; return; }
        ClearConfirmation(invalidated: true); _reconnectRequired = false;
        SetBusy(true, L.T("Bisheriger Gerätestand wird vor der Übertragung gesichert …"));
        var applyStarted = false;
        var revision = ++_progressRevision;
        try
        {
            await using var client = OnboardClient.Open(target.Device);
            var backupInfo = await client.GetInfoAsync();
            _currentExtensions = await ReadRequiredExtensionsAsync(client);
            if (UnsupportedFeature(backupInfo) is { } unavailable) throw new InvalidDataException(unavailable);
            var previous = await client.ReadAsync();
            var afterBackupInfo = await client.GetInfoAsync();
            if (!backupInfo.Persistent || !afterBackupInfo.Persistent || backupInfo.DeviceId != _image.DeviceId || afterBackupInfo.DeviceId != _image.DeviceId ||
                backupInfo.Generation != afterBackupInfo.Generation || backupInfo.ActiveCrc != afterBackupInfo.ActiveCrc || backupInfo.SchemaVersion != afterBackupInfo.SchemaVersion || backupInfo.BlobSize != afterBackupInfo.BlobSize ||
                previous.Length != backupInfo.BlobSize || OnboardProtocol.Crc32(previous) != backupInfo.ActiveCrc)
                throw new InvalidDataException(L.T("Der Gerätestand hat sich während der Sicherung geändert. Bitte erneut prüfen."));
            var previousProject = OnboardSerializer.Decode(previous);
            _reconnectRequired = backupInfo.SupportsTiming && (_image.RequiresTiming || previousProject.DeviceSettings.UsbPollingIntervalMs != _project.DeviceSettings.UsbPollingIntervalMs);
            var directory = Path.Combine(Paths.DataRoot, "device-backups"); Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, $"{target.Device.DeviceId}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
            // Persist and re-open the lossless raw configuration before any staging or COMMIT.
            await using (var backupFile = new FileStream(stem + ".igcfg.bin", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await backupFile.WriteAsync(previous); backupFile.Flush(flushToDisk: true);
            }
            var storedBackup = await File.ReadAllBytesAsync(stem + ".igcfg.bin");
            if (!storedBackup.AsSpan().SequenceEqual(previous)) throw new IOException(L.T("Die lokale Sicherungsdatei stimmt nach dem erneuten Lesen nicht mit dem Gerätestand überein."));
            ProjectService.Save(stem + ".json", previousProject);
            _progressActive = true; _progress.IsIndeterminate = false; _progress.Value = 0;
            var progress = new Progress<int>(value => ReportProgress(value, revision));
            applyStarted = true;
            var result = await client.ApplyAsync(_image, progress, expectedGeneration: backupInfo.Generation);
            _progressActive = false;
            AcceptSavedResult(result, afterBackupInfo);
            if (_reconnectRequired) await FinishReconnectAsync(client, result.Generation, result.Crc32);
            CompleteVerifiedOperation();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(ex.ToString()); RejectTransferResult(applyStarted);
            _transferStatus.Text = applyStarted
                ? L.T("Das Ergebnis ist unklar. Prüfe zuerst den Gerätestand.")
                : L.T("Die Übertragung wurde nicht gestartet. Versuche es erneut.");
        }
        finally { _progressActive = false; FinishOperation(); }
    }

    private void ReportProgress(int value, int revision)
    {
        if (!_progressActive || !_busy || _closed || revision != _progressRevision) return;
        _progress.Value = Math.Clamp(value, 0, 100);
        _transferStatus.Text = value < 70 ? L.F($"Das Profil wird übertragen … {value} %")
            : value < 80 ? L.T("Dauerhaft speichern … dieser Schritt kann bis zu zwei Minuten dauern. Bitte angeschlossen lassen.")
            : L.T("Die gespeicherten Einstellungen werden geprüft …");
    }
    private async Task FinishReconnectAsync(OnboardClient client, uint generation, uint crc)
    {
        try { await client.ReconnectUsbAsync(generation, crc); SetReconnectAcknowledged(true); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(ex.ToString());
            // Full persistence/readback already succeeded. A missing ACK is not a failed save.
            SetReconnectAcknowledged(false);
        }
    }
    private void SetReconnectAcknowledged(bool acknowledged) => CompletionNotice = acknowledged ? null
        : L.T("Bitte verbinde das USB-Kabel erneut, damit die neue Abfragerate aktiv wird.");
    private void CompleteVerifiedOperation()
    {
        _closeAfterOperation = DeviceSaveVerified && !DeviceStateUncertain && _currentInfo?.Persistent == true;
        if (_closeAfterOperation) { _progress.Value = 100; _transferStatus.Text = L.T("Die Übertragung ist bestätigt."); }
    }
    private bool CanAutoClose => !_offlinePreview && !_busy && !_closed && _closeAfterOperation && DeviceSaveVerified && !DeviceStateUncertain && _currentInfo?.Persistent == true;
    private void FinishOperation()
    {
        // Called from the outer finally, after await-using has disposed the transport.
        SetBusy(false);
        if (CanAutoClose) Close();
    }

    private void ClearConfirmation(bool invalidated = false)
    {
        DeviceSaveVerified = false; _closeAfterOperation = false; CompletionNotice = null;
        _readProject = null; _verificationInvalidated |= invalidated;
    }
    private void AcceptReadResult(byte[] data, OnboardInfo info)
    {
        var project = OnboardSerializer.Decode(data);
        if (project.DeviceId != _project.DeviceId || info.DeviceId != _project.DeviceId) throw new InvalidDataException(L.T("Der Gerätestand gehört zu einem anderen Gerät."));
        _readProject = project; _currentInfo = info;
        DeviceSaveVerified = info.Persistent && (info.Flags & 4) == 0 && data.AsSpan().SequenceEqual(_image.Payload);
        _verificationInvalidated = !DeviceSaveVerified; _outcomeUncertain = false;
    }
    private void AcceptSavedResult(OnboardTransferResult result, OnboardInfo previousInfo)
    {
        if (!result.ReadbackVerified || result.Crc32 != _image.Crc32 || result.Bytes != _image.UsedBytes || previousInfo.DeviceId != _image.DeviceId || !previousInfo.Persistent)
            throw new IOException(L.T("Der Vergleich nach dem Speichern wurde nicht bestätigt."));
        _readProject = ProjectService.Clone(_project);
        _currentInfo = previousInfo with { Generation = result.Generation, ActiveCrc = result.Crc32, SchemaVersion = _image.SchemaVersion, BlobSize = result.Bytes, Flags = (byte)(previousInfo.Flags & ~4), StagingBytes = 0 };
        DeviceSaveVerified = true; _verificationInvalidated = false; _outcomeUncertain = false;
    }
    private void RejectTransferResult(bool applyStarted)
    {
        ClearConfirmation(invalidated: true); _outcomeUncertain = applyStarted;
        _recoveryMode |= applyStarted;
    }

    private string? UnsupportedFeature(OnboardInfo info) =>
        _image.RequiresScreensaver && _currentExtensions?.SupportsScreensaver != true ? L.T("Der Screensaver benötigt ein Firmware-Update. Es wird nichts übertragen.") :
        _image.RequiresStandby && !info.SupportsStandby ? L.T("Standby benötigt ein Firmware-Update. Es wird nichts übertragen.") :
        _image.RequiresColorSequences && !info.SupportsColorSequences ? L.T("Eigene Farbfolgen benötigen ein Firmware-Update. Es wird nichts übertragen.") :
        _image.SchemaVersion >= 2 && !info.SupportsEffects && !info.SupportsTiming ? L.T("Für dieses Konfigurationsformat ist ein Firmware-Update erforderlich.") :
        _image.RequiresEffects && !info.SupportsEffects ? L.T("Für die Beleuchtungseinstellungen ist ein Firmware-Update erforderlich.") :
        _image.RequiresTiming && !info.SupportsTiming ? L.T("Für USB-Abfrageintervall und Entprellzeit ist ein Firmware-Update erforderlich.") : null;

    private static Brush ColorBrush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static TextBlock Label(string text, double size, string color, FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, Foreground = ColorBrush(color), FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0), LineHeight = size * 1.45
    };
    private async Task RenderAndClose(string path)
    {
        try
        {
            await Task.Delay(150); UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_root.ActualWidth), (int)Math.Ceiling(_root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(_root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path); encoder.Save(stream);
        }
        catch (Exception ex) { Directory.CreateDirectory(Paths.DataRoot); File.WriteAllText(Path.Combine(Paths.DataRoot, "screenshot-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
}
