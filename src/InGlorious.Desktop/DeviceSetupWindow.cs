using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using InGlorious.Core;

namespace InGlorious.Desktop;

/// <summary>Device-bound firmware workflow. Offline previews create no service and never access hardware.</summary>
public sealed partial class DeviceSetupWindow : Window
{
    private readonly DeviceSetupService? _service;
    private readonly bool _offlinePreview;
    private readonly string? _preferredDeviceId;
    private readonly string? _preferredInstanceId;
    private readonly SetupOperation _operation;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SetupSession? _session;
    private SetupProgress? _progress;
    private bool _busy, _observing, _closed, _started, _error, _writeAttempted, _outcomeUncertain;
    private int _revision, _versionRevision;
    private bool _readingVersion;
    private OnboardFirmwareVersion? _runtimeVersion;
    private DeviceSetupFirmwareSequence _firmwareSequence = new();
    private readonly List<Window> _owners = [];
    private readonly ComboBox _devices = new() { Name = "SetupDevices", MinHeight = 36, Margin = new(0, 10, 0, 12) };
    private readonly TextBlock _flowTitle = Text(24), _deviceName = Text(13), _version = Text(13), _step = Text(13), _heading = Text(22), _description = Text(13), _status = Text(13), _experimental = Text(12), _backup = Text(13);
    private readonly ProgressBar _stepProgress = new() { Name = "SetupStepProgress", Maximum = 5, Height = 3, MinHeight = 0, Margin = new(0, 18, 0, 0), IsHitTestVisible = false };
    private readonly Border _summary = new() { CornerRadius = new(6), Padding = new(16), Margin = new(0, 0, 0, 16) };
    private readonly Border _keyPanel = new() { Name = "SetupKeyCombination", CornerRadius = new(6), Padding = new(20), Margin = new(0, 0, 0, 16), BorderThickness = new(1) };
    private readonly StackPanel _keyCaps = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly CheckBox _modelConfirmation = new() { Name = "SetupModelConfirmed", Margin = new(0, 18, 0, 0) };
    private readonly Button _search = new() { Name = "SetupSearch", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _action = new() { Name = "SetupAction", MinWidth = 155, Margin = new(12, 0, 0, 0) };
    private readonly Button _close = new() { Name = "SetupClose", MinWidth = 100 };
    private readonly ProgressBar _activity = new() { Height = 4, MinHeight = 0, IsIndeterminate = true, Margin = new(0, 12, 0, 0), IsVisible = false };
    private readonly TextBlock _keepOpen = Text(12);
    private readonly StackPanel _driverHelp = new() { Margin = new(0, 14, 0, 0), IsVisible = false };
    public string? DeviceId => _session?.DeviceId;
    public bool Completed => _progress?.Stage == SetupStage.Complete && _writeAttempted;
    public bool DeviceStateMayHaveChanged => _writeAttempted || _outcomeUncertain;

    private static IBrush Palette(string key) => new SolidColorBrush(Color.Parse(key switch
    { "WindowBackground" => "#141618", "Text" => "#E4E7EB", "Muted" => "#A0A7B0", "Accent" => "#EF646B", "Surface" => "#1C1F22", "SurfaceRaised" => "#22262A", "Stroke" => "#353A40", "Experimental" => "#E9B86B", _ => throw new ArgumentException("Unknown palette color.") }));
    private static TextBlock Text(double size, string text = "") => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };

    public DeviceSetupWindow(string? preferredDeviceId = null, SetupOperation operation = SetupOperation.Install, bool offlinePreview = false,
        string? preferredInstanceId = null)
    {
        _preferredDeviceId = preferredDeviceId; _preferredInstanceId = preferredInstanceId; _operation = operation; _offlinePreview = offlinePreview || Program.Offline;
        _service = _offlinePreview ? null : new DeviceSetupService();
        Title = OperationTitle + " · INGLORIOUS Advanced";
        Width = 760; Height = 710; MinWidth = 680; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Palette("WindowBackground"); Foreground = Palette("Text"); FontSize = 13; UseLayoutRounding = true;
        using (var icon = AssetLoader.Open(new Uri("avares://INGLORIOUS Advanced/Assets/inglorious.ico"))) Icon = new WindowIcon(icon);
        var root = new Grid { Background = Background, Margin = new(24), RowDefinitions = new("Auto,*,Auto") };
        var header = new StackPanel { Margin = new(0, 0, 0, 20) };
        _flowTitle.Text = OperationTitle; _flowTitle.FontWeight = FontWeight.SemiBold;
        _deviceName.Foreground = Palette("Muted"); _deviceName.Margin = new(0, 5, 0, 0);
        _stepProgress.Foreground = _activity.Foreground = Palette("Accent"); _stepProgress.Background = _activity.Background = Palette("Stroke");
        header.Children.Add(_flowTitle); header.Children.Add(_deviceName); header.Children.Add(_experimental); header.Children.Add(_stepProgress); root.Children.Add(header);
        var body = new StackPanel();
        _step.Foreground = Palette("Accent"); _step.FontWeight = FontWeight.SemiBold; _step.Margin = new(0, 0, 0, 8);
        _heading.FontWeight = FontWeight.SemiBold;
        _description.Foreground = Palette("Muted"); _description.LineHeight = 21; _description.Margin = new(0, 10, 0, 14);
        var summaryContent = new StackPanel(); summaryContent.Children.Add(_version); summaryContent.Children.Add(_backup);
        _summary.Background = Palette("Surface"); _summary.Child = summaryContent;
        _keyPanel.Background = Palette("SurfaceRaised"); _keyPanel.BorderBrush = Palette("Stroke"); _keyPanel.Child = _keyCaps;
        InitializeCompletionControls();
        foreach (var element in new Control[] { _step, _heading, _description, _devices, _keyPanel, _summary, _modelConfirmation, _activity, _status, _completion, _driverHelp }) body.Children.Add(element);
        var scroll = new ScrollViewer { Content = body, Padding = new(0, 0, 10, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        _version.FontWeight = FontWeight.SemiBold;
        _experimental.Foreground = Palette("Experimental"); _experimental.Margin = new(0, 10, 0, 0);
        _backup.LineHeight = 21; _backup.Margin = new(0, 8, 0, 0);
        _status.Foreground = Palette("Muted"); _status.Margin = new(0, 14, 0, 14); _status.LineHeight = 21;
        _keepOpen.Text = L.T("INGLORIOUS Advanced nicht schließen."); _keepOpen.Foreground = Palette("Accent"); _keepOpen.FontWeight = FontWeight.SemiBold; _keepOpen.Margin = new(0, 0, 0, 14);
        _driverHelp.Children.Add(Text(13, L.T("Wähle in Zadig nur das Gerät im Update-Modus und installiere dafür WinUSB.")));
        var driverLink = new Button { Content = L.T("USB-Treiber einrichten (Zadig)"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 8, 0, 0), IsEnabled = !_offlinePreview };
        driverLink.Click += async (_, _) => { if (!_offlinePreview && !_busy && !_closed) await Launcher.LaunchUriAsync(new Uri("https://zadig.akeo.ie/")); };
        _driverHelp.Children.Add(driverLink);
        _modelConfirmation.Content = Text(13, L.T("Das erkannte Modell stimmt mit meinem Gerät überein."));
        _modelConfirmation.Foreground = Palette("Text");
        _search.Content = L.T("Erneut prüfen"); _close.Content = L.T("Schließen"); _action.Classes.Add("primary");
        var footer = new Grid { Margin = new(0, 20, 0, 0), RowDefinitions = new("Auto,Auto"), ColumnDefinitions = new("*,Auto,Auto") };
        Grid.SetColumnSpan(_keepOpen, 3); footer.Children.Add(_keepOpen);
        _search.MinHeight = _close.MinHeight = _action.MinHeight = 38;
        Grid.SetRow(_search, 1); footer.Children.Add(_search); Grid.SetRow(_close, 1); Grid.SetColumn(_close, 1); footer.Children.Add(_close);
        Grid.SetRow(_action, 1); Grid.SetColumn(_action, 2); footer.Children.Add(_action); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = new Border { Background = Background, Child = root };
        AutomationProperties.SetName(_devices, L.T("Gerät auswählen"));
        _devices.SelectionChanged += async (_, _) => { _error = false; _status.Text = ""; _runtimeVersion = null; _versionRevision++; Render(); await ReadSelectedVersionAsync(); };
        _modelConfirmation.IsCheckedChanged += (_, _) => Render();
        _search.Click += async (_, _) => await DiscoverAsync(); _action.Click += async (_, _) => await ActAsync();
        _close.Click += (_, _) => Close(); Closing += WindowClosing;
        Closed += (_, _) => { _closed = true; _revision++; _versionRevision++; _timer.Stop(); foreach (var owner in _owners) owner.Closing -= WindowClosing; _owners.Clear(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) { e.Handled = true; Close(); } };
        _timer.Tick += async (_, _) => await ObserveAsync();
        Opened += async (_, _) =>
        {
            if (_started) return; _started = true;
            for (var owner = Owner as Window; owner is not null; owner = owner.Owner as Window) { owner.Closing += WindowClosing; _owners.Add(owner); }
            if (!_offlinePreview) { await DiscoverAsync(); if (!_closed) _timer.Start(); }
        };
        Render();
    }

    private string OperationTitle => L.T(_operation switch { SetupOperation.Install => "Gerät einrichten", SetupOperation.Update => "Firmware aktualisieren", _ => "Original-Firmware wiederherstellen" });
    private SetupDevice? SelectedDevice => _session?.Device ?? _devices.SelectedItem as SetupDevice;
    private string? SelectedTargetVersion => (_session?.DeviceId ?? _runtimeVersion?.DeviceId ??
        (SelectedDevice?.CandidateDeviceIds.Count == 1 ? SelectedDevice.CandidateDeviceIds[0] : null)) is { } id
        ? SelectedDevice?.CandidateDeviceIds.Contains(id) == true && BoardDescriptorRegistry.TryGet(id, out _) ? DeviceSetupService.TargetFirmwareVersionFor(id) : null : null;
    internal static bool MatchesSelectedRuntime(OnboardDevice candidate, SetupDevice selected) =>
        selected.IsRuntime && selected.CandidateDeviceIds.Contains(candidate.DeviceId) &&
        BoardDescriptorRegistry.TryGet(candidate.DeviceId, out var board) &&
        new DeviceSetupService().MatchesRuntimePort(selected, candidate.Path, BoardDescriptorRegistry.RuntimeVendorId, board.RuntimeProductId);
    private bool CanAct => !_offlinePreview && ActionReady;
    private bool UpdateVersionReady => _operation != SetupOperation.Update ||
        (_runtimeVersion is { } report && SelectedDevice?.CandidateDeviceIds.Contains(report.DeviceId) == true &&
         FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(report.DeviceId), out var target) && report.Version < target &&
         (_session?.InstalledFirmwareVersion is not { } captured || FirmwareSemanticVersion.TryParse(captured, out var version) && version == report.Version));
    private bool ActionReady => !_busy && !_readingVersion && !_observing && !_closed && !_error && !_outcomeUncertain && !_writeAttempted && !_firmwareSequence.Started && UpdateVersionReady && (_session is null
        ? SelectedDevice?.HasFirmwareCandidate == true
        : HasResolvedModel && _progress is { CanCapture: true } && _modelConfirmation.IsChecked == true);
    private bool HasResolvedModel => _session?.DeviceId is { } id && _session.Device.CandidateDeviceIds.Contains(id) && _progress?.DeviceId == id;

    private void ApplyDevices(IReadOnlyList<SetupDevice> devices)
    {
        var eligible = devices.Where(device => device.IsRuntime == (_operation != SetupOperation.Install) &&
            (_preferredInstanceId is null || device.InstanceId == _preferredInstanceId) &&
            (_preferredDeviceId is null || device.CandidateDeviceIds.Contains(_preferredDeviceId))).ToArray();
        _devices.ItemsSource = eligible;
        // Even a preferred model cannot choose between two physical devices silently.
        _devices.SelectedItem = eligible.Length == 1 ? eligible[0] : null;
        _devices.IsVisible = eligible.Length > 1 ? true : false;
        if (eligible.Length == 0) _status.Text = L.T(_operation == SetupOperation.Install ? "Kein Gerät mit Original-Firmware verbunden." : "Kein Gerät mit INGLORIOUS verbunden.");
        Render();
    }

    private async Task ReadSelectedVersionAsync()
    {
        var selected = SelectedDevice;
        if (_offlinePreview || _closed || _session is not null || selected?.IsRuntime != true || !selected.HasFirmwareCandidate) { _readingVersion = false; Render(); return; }
        int revision = _versionRevision;
        _readingVersion = true; Render();
        try
        {
            var report = await Task.Run(async () =>
            {
                if (DeviceSetupService.IsGloriousCoreRunning() is not false)
                    throw new SetupException(L.T("Schließe Glorious CORE, bevor du fortfährst."), recoverable: true);
                var matches = OnboardClient.FindDevices().Where(device => selected.CandidateDeviceIds.Contains(device.DeviceId) &&
                    MatchesSelectedRuntime(device, selected)).ToArray();
                if (matches.Length != 1) throw new SetupException(L.T("Gerät nicht eindeutig erkannt."));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var client = OnboardClient.Open(matches[0]);
                var info = await client.GetInfoAsync(timeout.Token);
                if (!info.Persistent || (info.Flags & 4) != 0 || info.StagingBytes != 0)
                    throw new SetupException(L.T("Die Firmware-Version konnte nicht geprüft werden."));
                return await client.GetFirmwareVersionAsync(timeout.Token)
                    ?? throw new SetupException(L.T("Die Firmware-Version konnte nicht geprüft werden."));
            });
            if (!_closed && revision == _versionRevision && ReferenceEquals(selected, SelectedDevice))
            {
                _runtimeVersion = report;
                if (_operation == SetupOperation.Update && !UpdateVersionReady) _status.Text = L.T("Die Firmware ist bereits aktuell oder neuer als die angebotene Version.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed && revision == _versionRevision)
            {
                Debug.WriteLine("Setup version read failed: " + exception);
                if (_operation == SetupOperation.Update) { _error = true; _status.Text = DeviceSetupService.ForUser(exception); }
            }
        }
        finally { if (!_closed && revision == _versionRevision) { _readingVersion = false; Render(); } }
    }

    private async Task DiscoverAsync()
    {
        if (_offlinePreview || _service is null || _busy || _readingVersion || _observing || _closed || _session is not null) return;
        _busy = true; _error = false; _status.Text = L.T("Geräte werden gesucht …"); Render();
        try { var devices = await _service.ListDevicesAsync(); if (!_closed) { _status.Text = ""; ApplyDevices(devices); } }
        catch (Exception ex) { if (!_closed) ShowError(ex); }
        finally { _busy = false; if (!_closed) Render(); }
    }

    // The final confirmation stays stable. Capture and write each recheck the live device before I/O.
    private bool NeedsObservation => _session?.Stage is SetupStage.Disconnect or SetupStage.ConnectBootloader or SetupStage.Reconnect;

    private async Task ObserveAsync()
    {
        if (_offlinePreview || _service is null || _session is null || _busy || _observing || _closed || !NeedsObservation) return;
        var session = _session; var revision = _revision; _observing = true;
        try
        {
            var progress = await _service.ObserveAsync(session);
            if (_closed || revision != _revision || !ReferenceEquals(session, _session)) return;
            if (progress.Stage == SetupStage.Complete && _operation != SetupOperation.Restore)
            {
                try { _returnedHidPath = await ReadVerifiedReturnedRuntimeAsync(session); }
                catch (Exception exception)
                {
                    if (_closed || revision != _revision || !ReferenceEquals(session, _session)) return;
                    // A normal USB descriptor is not confirmation of the flashed runtime.
                    session.Stage = SetupStage.Reconnect;
                    ApplyProgress(new(SetupStage.Reconnect, exception is SetupException ? exception.Message
                        : L.T("Die USB-Verbindung konnte nicht geprüft werden. Die App versucht es erneut."), session.DeviceId));
                    return;
                }
                if (_closed || revision != _revision || !ReferenceEquals(session, _session)) return;
            }
            ApplyProgress(progress);
        }
        catch (Exception ex) { if (!_closed && revision == _revision) ShowError(ex); }
        finally { _observing = false; if (!_closed) Render(); }
    }

    private async Task ActAsync()
    {
        if (!CanAct || _service is null) return;
        _busy = true; _error = false; Render();
        try
        {
            if (_session is null)
            {
                _session = await _service.BeginAsync(SelectedDevice!, _operation); _revision++;
                await CaptureCoreImportAsync();
                _devices.IsEnabled = false;
                ApplyProgress(new(_session.Stage, L.T("Trenne das Gerät vom USB-Kabel."), _session.DeviceId));
            }
            else if (_progress is { CanCapture: true } && _modelConfirmation.IsChecked == true)
            {
                var session = _session;
                var deviceId = session.DeviceId!;
                var revision = _revision;
                var result = await _firmwareSequence.RunAsync(session, _operation, deviceId, explicitlyConfirmed: true,
                    () => !_closed && _busy && revision == _revision && ReferenceEquals(session, _session),
                    async () =>
                    {
                        _status.Text = BackupActivity; Render();
                        var captured = await _service.CaptureAsync(session, Log());
                        if (!UpdateVersionReady)
                        {
                            session.Stage = SetupStage.Blocked;
                            throw new SetupException(L.T("Die Firmware-Version hat sich geändert. Starte das Update erneut."));
                        }
                        return captured;
                    },
                    verified => ApplyProgress(verified),
                    async () =>
                    {
                        _status.Text = WriteActivity; Render();
                        return _operation switch
                        {
                            SetupOperation.Install => await _service.InstallAsync(session, explicitlyConfirmed: true, log: Log()),
                            SetupOperation.Update => await _service.UpdateAsync(session, explicitlyConfirmed: true, log: Log()),
                            SetupOperation.Restore => await _service.RestoreAsync(session, explicitlyConfirmed: true, log: Log()),
                            _ => throw new InvalidOperationException("Unknown firmware operation.")
                        };
                    });
                ApplyProgress(result);
                _writeAttempted = session.WriteAttempted;
                _outcomeUncertain = session.OutcomeUncertain;
            }
        }
        catch (Exception ex)
        {
            _writeAttempted = _session?.WriteAttempted == true;
            _outcomeUncertain = _session?.OutcomeUncertain == true;
            ShowError(ex);
            if (_firmwareSequence.Started || _writeAttempted || _outcomeUncertain || _session?.Stage == SetupStage.Blocked) { _progress = new(SetupStage.Blocked, _status.Text ?? "", _session?.DeviceId); _timer.Stop(); }
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private IProgress<string> Log()
    {
        var revision = _revision;
        return new Progress<string>(message =>
        {
            if (!_closed && _busy && revision == _revision)
            {
                _status.Text = message;
                UpdateStatusVisibility();
            }
        });
    }

    private bool IsFirmwareOperationRunning => _busy && _firmwareSequence.Started &&
        _progress?.Stage is SetupStage.ReadyToBackup or SetupStage.ReadyToInstall;

    private void UpdateStatusVisibility() => _status.IsVisible = !IsFirmwareOperationRunning || _error ||
        (_status.Text != _description.Text && _status.Text != _heading.Text && _status.Text != _keepOpen.Text);

    private void ApplyProgress(SetupProgress progress)
    {
        if (_progress?.Stage != progress.Stage) { _modelConfirmation.IsChecked = false; }
        _progress = progress; _error = false;
        _status.Text = progress.Stage == SetupStage.Reconnect && progress.Message == L.T("Verbinde das Gerät ohne gedrückte Tasten neu.")
            ? L.T("Nach dem Wiederanschließen geht es automatisch weiter.") : progress.Message;
        if (progress.Stage is SetupStage.Complete or SetupStage.Blocked) _timer.Stop();
        Render();
    }

    private void ShowError(Exception exception)
    {
        Debug.WriteLine("Device setup failed: " + exception); _error = true;
        _status.Text = _outcomeUncertain ? FirmwareMessages.ForExecutionFailure() : DeviceSetupService.ForUser(exception);
        if (_firmwareSequence.Started && !_writeAttempted && !_outcomeUncertain)
            _status.Text += "\n" + L.T("Schließe diesen Dialog, verbinde das Gerät ohne gedrückte Tasten neu und starte den Vorgang erneut.");
    }

    private string CombinedAction => L.T(_operation switch
    {
        SetupOperation.Install => "Sichern und installieren",
        SetupOperation.Update => "Sichern und aktualisieren",
        _ => "Sichern und wiederherstellen"
    });
    private string BackupActivity => L.T(_operation == SetupOperation.Install
        ? "Die Original-Firmware wird gesichert und geprüft …" : "Der aktuelle Zustand und die Original-Sicherung werden geprüft …");
    private string WriteActivity => L.T(_operation switch
    {
        SetupOperation.Install => "Firmware wird installiert …",
        SetupOperation.Update => "Firmware wird aktualisiert …",
        _ => "Original-Firmware wird wiederhergestellt …"
    });

    private void Render()
    {
        var stage = _progress?.Stage; var device = SelectedDevice;
        _heading.Text = stage switch
        {
            SetupStage.Disconnect => L.T("USB-Kabel trennen"), SetupStage.ConnectBootloader => L.T("Im Update-Modus verbinden"),
            SetupStage.ReadyToBackup or SetupStage.ReadyToInstall => L.T(_operation switch { SetupOperation.Install => "Installation bestätigen", SetupOperation.Update => "Update bestätigen", _ => "Wiederherstellung bestätigen" }),
            SetupStage.Reconnect => L.T("Jetzt normal neu verbinden"), SetupStage.Complete => L.T(_operation switch { SetupOperation.Install => "Einrichtung abgeschlossen", SetupOperation.Update => "Update abgeschlossen", _ => "Wiederherstellung abgeschlossen" }),
            SetupStage.Blocked => L.T(_operation switch { SetupOperation.Install => "Einrichtung angehalten", SetupOperation.Update => "Update angehalten", _ => "Wiederherstellung angehalten" }),
            _ => L.T(_operation switch { SetupOperation.Install => "Deine Tastatur mit INGLORIOUS nutzen", SetupOperation.Update => "Update verfügbar", _ => "Zurück zu Glorious CORE" })
        };
        var stepNumber = stage switch { null => 1, SetupStage.Disconnect => 2, SetupStage.ConnectBootloader => 3, SetupStage.ReadyToBackup or SetupStage.ReadyToInstall => 4, _ => 5 };
        _step.Text = L.Format("Schritt {0} von 5", stepNumber);
        _step.IsVisible = _stepProgress.IsVisible = stage == SetupStage.Blocked ? false : true;
        _stepProgress.Value = stepNumber;
        var resolvedModel = DeviceCatalog.All.FirstOrDefault(model => model.Id == _session?.DeviceId && device?.CandidateDeviceIds.Contains(model.Id) == true);
        var productName = resolvedModel?.Name ?? device?.Name;
        _deviceName.Text = productName is null ? L.T("Kein Gerät verbunden") : "\"" + productName + "\"";
        _version.Text = _operation switch
        {
            SetupOperation.Install => SelectedTargetVersion is { } installVersion ? "INGLORIOUS " + installVersion : "INGLORIOUS",
            SetupOperation.Update when _runtimeVersion is { } report && FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(report.DeviceId), out var target) && report.Version < target
                => "INGLORIOUS " + report.Version + " → " + target,
            SetupOperation.Update or SetupOperation.Restore when _runtimeVersion is { } report => "INGLORIOUS " + report.Version,
            _ => _readingVersion ? L.T("Firmware-Version wird gelesen …") : ""
        };
        var experimental = resolvedModel?.IsExperimentalFirmware ?? device?.IsExperimental == true;
        _experimental.Text = L.T("Experimentelle Unterstützung: noch nicht vollständig geprüft.");
        _experimental.IsVisible = experimental ? true : false;
        ((TextBlock)_modelConfirmation.Content!).Text = L.T("Das erkannte Modell stimmt mit meinem Gerät überein.") + " " + L.T(experimental && _operation != SetupOperation.Restore
            ? "Ich möchte die experimentelle Firmware auf diesem Gerät installieren."
            : "Ich möchte die Firmware auf diesem Gerät ändern.");
        _description.Text = stage switch
        {
            null => L.T("Verbinde das Gerät normal per USB."),
            SetupStage.Disconnect => L.Format("Trenne nur \"{0}\" vom USB-Kabel. Andere Geräte bleiben angeschlossen.", device?.Name ?? ""),
            SetupStage.ConnectBootloader => L.T("Halte die unten angezeigte Tastenkombination gedrückt und stecke das USB-Kabel am selben Anschluss wieder ein. Lass die Tasten nach etwa 3 Sekunden los."),
            SetupStage.ReadyToBackup or SetupStage.ReadyToInstall => _operation switch
            {
                SetupOperation.Install => L.T("Die App sichert zuerst deine Original-Firmware und installiert danach INGLORIOUS.") + "\n" + L.T("INGLORIOUS ersetzt die Original-Firmware und startet mit Standardwerten. Übertrage danach dein Profil."),
                SetupOperation.Update => L.T("Die App sichert zuerst den aktuellen Zustand und aktualisiert danach die Firmware. Deine Einstellungen bleiben erhalten."),
                _ => L.T("Die App sichert zuerst den aktuellen Zustand und stellt danach die Original-Firmware wieder her.") + "\n" + L.T("Die Original-Firmware ersetzt INGLORIOUS und seine Einstellungen.")
            },
            SetupStage.Reconnect => L.Format("Trenne \"{0}\" einmal vom USB-Kabel und stecke es ohne gedrückte Tasten wieder ein.", productName ?? L.T("Gerät")),
            SetupStage.Complete => L.T("Prüfe jetzt die Tasten und die Beleuchtung."),
            _ => L.T("Es wird nichts automatisch wiederholt.")
        };
        RenderKeyCombination(stage == SetupStage.ConnectBootloader ? device : null);
        _backup.Text = _operation == SetupOperation.Install
            ? L.T("Die Original-Firmware wird vor der Installation zweimal gesichert. Die geprüfte Sicherung bleibt auf diesem Computer und ermöglicht die Wiederherstellung.") + "\n" + L.T("Zusätzliche Profilspeicher und Bluetooth-Einstellungen sind nicht Teil dieser Sicherung.")
            : _operation == SetupOperation.Update ? L.T("Deine Einstellungen und die Original-Sicherung bleiben erhalten.")
            : L.T("Die Wiederherstellung verwendet ausschließlich die passende Original-Sicherung dieses Geräts.");
        _backup.IsVisible = stage is null or SetupStage.ReadyToBackup or SetupStage.ReadyToInstall ? true : false;
        _version.IsVisible = string.IsNullOrEmpty(_version.Text) ? false : true;
        _summary.IsVisible = _backup.IsVisible;
        _modelConfirmation.IsVisible = stage == SetupStage.ReadyToBackup ? true : false;
        _modelConfirmation.IsEnabled = !_busy && !_offlinePreview;
        _devices.IsEnabled = !_busy && _session is null;
        _devices.IsVisible = _session is null && _devices.Items.Count > 1 ? true : false;
        _search.IsVisible = _session is null ? true : false;
        _search.IsEnabled = !_busy && !_readingVersion && !_offlinePreview;
        _action.Content = stage switch
        {
            SetupStage.Disconnect => L.T("Warte auf USB-Trennung …"),
            SetupStage.ConnectBootloader => L.T("Warte auf Gerät …"),
            SetupStage.Reconnect => L.T("Warte auf die USB-Verbindung …"),
            SetupStage.ReadyToBackup or SetupStage.ReadyToInstall => CombinedAction,
            _ => L.T(_operation switch { SetupOperation.Install => "Einrichtung starten", SetupOperation.Update => "Update vorbereiten", _ => "Wiederherstellung vorbereiten" })
        };
        _action.IsVisible = stage is SetupStage.Complete or SetupStage.Blocked ? false : true;
        _action.IsEnabled = CanAct;
        _close.IsEnabled = !_busy && !_lightingRunning; _close.Content = L.T(stage == SetupStage.Complete ? "Fertig" : "Schließen");
        _activity.IsVisible = _busy || _readingVersion || stage == SetupStage.Reconnect;
        _activity.Margin = stage == SetupStage.Reconnect ? new(0, 0, 0, 18) : new(0, 12, 0, 0);
        _status.Margin = stage == SetupStage.Reconnect ? new(0, 0, 0, 14) : new(0, 14, 0, 14);
        if (IsFirmwareOperationRunning)
        {
            _heading.Text = stage == SetupStage.ReadyToBackup ? BackupActivity : WriteActivity;
            _description.Text = L.T("USB angeschlossen und INGLORIOUS Advanced geöffnet lassen.");
            _summary.IsVisible = _modelConfirmation.IsVisible = false;
            _action.Content = L.T("Bitte warten …");
        }
        _driverHelp.IsVisible = _status.Text == L.T("Für den Update-Modus fehlt der USB-Treiber.") ? true : false;
        _keepOpen.IsVisible = !IsFirmwareOperationRunning && stage is not (SetupStage.Complete or SetupStage.Blocked);
        if (device is { HasFirmwareCandidate: false })
        {
            _heading.Text = L.T("Gerät erkannt");
            _description.Text = L.T("Für dieses Modell ist noch keine INGLORIOUS-Firmware verfügbar.");
            _version.Text = "";
            _version.IsVisible = _step.IsVisible = _stepProgress.IsVisible = _summary.IsVisible = _backup.IsVisible = _keepOpen.IsVisible = false;
        }
        _status.Foreground = Palette(_error ? "Accent" : "Muted");
        UpdateStatusVisibility();
        RenderCompletion();
    }

    private static string[] BootKeyLabels(SetupDevice? device)
    {
        if (device is null || device.CandidateDeviceIds.Count == 0) return [];
        if (device.CandidateDeviceIds.All(id => id == "numpad"))
            return L.T("Plus (+) + Enter").Split(" + ", StringSplitOptions.None);
        if (device.IsRuntime || device.CandidateDeviceIds.All(id => id == "gmmk3-pro-100-iso" || id.StartsWith("pro-", StringComparison.Ordinal) || id.StartsWith("gmmk2-", StringComparison.Ordinal)))
            return L.T("Leertaste + B").Split(" + ", StringSplitOptions.None);
        if (device.CandidateDeviceIds.All(id => id.StartsWith("gmmk3-", StringComparison.Ordinal))) return ["Esc"];
        return [];
    }

    private void RenderKeyCombination(SetupDevice? device)
    {
        var labels = BootKeyLabels(device);
        _keyPanel.IsVisible = labels.Length == 0 ? false : true;
        AutomationProperties.SetName(_keyPanel, BootInstruction(device));
        // Polling may render repeatedly. Keep the same visual elements while the combination is unchanged.
        var signature = string.Join("\u001f", labels);
        if (Equals(_keyCaps.Tag, signature)) return;
        _keyCaps.Tag = signature; _keyCaps.Children.Clear();
        for (var index = 0; index < labels.Length; index++)
        {
            if (index > 0) _keyCaps.Children.Add(new TextBlock { Text = "+", FontSize = 23, Foreground = Palette("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 16, 0) });
            var label = Text(24, index == 0 && device?.CandidateDeviceIds.All(id => id == "numpad") == true ? "+" : labels[index]);
            label.FontWeight = FontWeight.SemiBold; label.Foreground = Palette("Accent"); label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
            var cap = new Border { MinWidth = 72, MinHeight = 64, Padding = new(22, 12, 22, 14), CornerRadius = new(6), Background = Palette("Surface"), BorderBrush = Palette("Stroke"), BorderThickness = new(1, 1, 1, 4), Child = label };
            AutomationProperties.SetName(cap, labels[index]); _keyCaps.Children.Add(cap);
        }
    }

    private static string BootInstruction(SetupDevice? device)
    {
        if (device is null || !device.HasFirmwareCandidate) return "";
        if (device.CandidateDeviceIds.All(id => id == "numpad")) return L.T("Halte Plus und Enter beim Einstecken gedrückt. Lass die Tasten nach etwa 3 Sekunden los.");
        if (device.IsRuntime || device.CandidateDeviceIds.All(id => id == "gmmk3-pro-100-iso")) return L.T("Halte Leertaste und B beim Einstecken gedrückt. Lass die Tasten nach etwa 3 Sekunden los.");
        if (device.CandidateDeviceIds.All(id => id.StartsWith("gmmk3-", StringComparison.Ordinal))) return L.T("Halte Esc beim Einstecken gedrückt. Lass die Taste nach etwa 3 Sekunden los.");
        if (device.CandidateDeviceIds.All(id => id.StartsWith("pro-", StringComparison.Ordinal) || id.StartsWith("gmmk2-", StringComparison.Ordinal))) return L.T("Halte Leertaste und B beim Einstecken gedrückt. Lass die Tasten nach etwa 3 Sekunden los.");
        return L.T("Gerät nicht eindeutig erkannt.");
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy || _lightingRunning)
        {
            e.Cancel = true;
            _status.Text = L.T("INGLORIOUS Advanced nicht schließen.");
            UpdateStatusVisibility();
        }
    }
}
