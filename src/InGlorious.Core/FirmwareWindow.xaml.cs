using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace InGlorious.Core;

public partial class FirmwareWindow : Window
{
    private enum Step { Overview, Preparing, Unplug, Connect, Ready, Running, Reconnect, Complete, Failed }
    private readonly string _deviceId;
    private readonly bool _offlinePreview;
    private readonly FirmwareOperation _operation;
    private readonly FirmwareService _service = new();
    private FirmwarePlan? _plan;
    private bool _busy, _preparing, _executionAttempted;
    private bool _probing, _bootloaderReady, _closed;
    private bool? _coreRunning;
    private bool _advancing, _coreStatusKnown;
    private Task<bool?>? _coreCheckTask;
    private readonly Func<Task<bool?>> _readCoreStatus = () => Task.Run(FirmwareService.IsGloriousCoreRunning);
    private UsbConnectionTracker? _disconnectTracker;
    private bool _checkingDisconnect;
    private long? _firstAbsentAt;
    private readonly Func<Task<IReadOnlyList<string>>> _readUsbInstances = DeviceScanner.ScanPresentUsbInstanceIdsAsync;
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _coreTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _disconnectTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _availabilityRevision;
    private Step _step = Step.Overview;
    private FirmwareOperation Operation => _operation;
    private bool CanExecuteConfirmed => !_closed && !_busy && !_preparing && _coreRunning == false && _bootloaderReady && _step == Step.Ready && _plan is not null && _plan.DeviceId == _deviceId && _plan.Operation == Operation && !VersionBlocksUpdate && ConfirmationCheck.IsChecked == true;
    private bool CanContinue => !_busy && !_preparing && !_advancing && _coreStatusKnown && !_executionAttempted && !VersionBlocksUpdate && (_step == Step.Overview || CanExecuteConfirmed);
    private bool CanObserveDisconnect => !_closed && !_busy && !_preparing && !_advancing && !_executionAttempted && _coreRunning == false && _step == Step.Unplug && _disconnectTracker is not null && _plan is not null && _plan.DeviceId == _deviceId && _plan.Operation == Operation;

    public FirmwareWindow(string deviceId, bool offlinePreview = false, FirmwareOperation initialOperation = FirmwareOperation.Update)
    {
        if (!BoardDescriptorRegistry.TryGet(deviceId, out var board) || !board.HasApprovedFirmwareManagement)
            throw new System.IO.InvalidDataException(L.T("Firmwareinstallation ist für dieses Modell noch nicht verfügbar."));
        _ = FirmwareService.DeviceName(deviceId);
        _deviceId = deviceId; _offlinePreview = offlinePreview; _operation = initialOperation;
        _readNormalConnection = ReadNormalModeAsync;
        _checkBootloader = plan => _service.CheckBootloaderAsync(plan);
        _coreRunning = offlinePreview ? false : null;
        _coreStatusKnown = offlinePreview;
        InitializeComponent();
        FlowTitle.Text = Operation switch {
            FirmwareOperation.Update => L.T("Firmware aktualisieren"),
            FirmwareOperation.InstallFromOriginal => L.T("INGLORIOUS einrichten"),
            _ => L.T("Original-Firmware wiederherstellen")
        };
        Title = FlowTitle.Text + " · INGLORIOUS Advanced";
        DeviceTitle.Text = "\"" + FirmwareService.DeviceName(deviceId) + "\"";
        VersionText.Text = Operation switch {
            FirmwareOperation.Update => FirmwareRelease.DisplayVersion,
            FirmwareOperation.InstallFromOriginal => "Glorious → " + FirmwareRelease.DisplayVersion,
            _ => L.T("Zurück zur gesicherten Original-Firmware")
        };
        OriginalBackupNotice.Visibility = Operation == FirmwareOperation.InstallFromOriginal ? Visibility.Visible : Visibility.Collapsed;
        ScopeText.Text = Operation switch {
            FirmwareOperation.Update => L.T("Deine Einstellungen bleiben erhalten. Die App sichert den aktuellen Zustand vor dem Update."),
            FirmwareOperation.InstallFromOriginal => L.T("INGLORIOUS ersetzt die Original-Firmware und startet mit Standardwerten. Übertrage danach dein Profil."),
            _ => L.T("Die Original-Firmware ersetzt INGLORIOUS und seine Einstellungen. Externe Profilspeicher sind nicht vollständig gesichert.")
        };
        KeyCombination.Text = _deviceId == "pro-iso" ? L.T("Leertaste + B") : L.T("Plus (+) + Enter");
        _probeTimer.Tick += async (_, _) => await ProbeBootloaderAsync();
        _coreTimer.Tick += async (_, _) => await RefreshCoreStatusAsync();
        _disconnectTimer.Tick += async (_, _) => await ObserveDisconnectAsync();
        _reconnectTimer.Tick += async (_, _) => await ObserveReconnectAsync();
        RenderStep();
    }

    private async Task PrepareAsync()
    {
        if (_offlinePreview || _busy || _preparing || _executionAttempted || _coreRunning != false || _step != Step.Overview || VersionBlocksUpdate) return;
        var revision = ++_availabilityRevision;
        StopDisconnectObservation();
        StopReconnectObservation(); _firmwareVerified = false; _evidenceDirectory = null; _completedTarget = null;
        _plan = null; ConfirmationCheck.IsChecked = false; LogOutput.Clear(); _lastLogMessage = null; LogExpander.IsExpanded = false;
        _preparing = true; _step = Step.Preparing; StatusText.Text = ""; RenderStep();
        try
        {
            // Only local files, hashes and a read-only plan. Never bootloader entry or flashing.
            var availability = await Task.Run(() => _service.CheckAvailability(_deviceId, Operation));
            if (revision != _availabilityRevision) return;
            if (!availability.Available) throw new InvalidOperationException(availability.Message);
            var plan = await _service.PrepareAsync(_deviceId, Operation, new Progress<string>(AppendServiceLog), installedFirmware: _runtimeVersionHint);
            if (revision != _availabilityRevision) return;
            // Capture a fresh, unique normal-mode connection for this run only. This is
            // navigation evidence, separate from the service's firmware identity checks.
            try
            {
                var devices = await DeviceScanner.ScanAsync();
                if (_closed || revision != _availabilityRevision) return;
                ArmDisconnectObservation(devices);
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            if (_closed || revision != _availabilityRevision) return;
            var bootloaderAvailable = false;
            if (_disconnectTracker is null)
            {
                // Recovery must remain possible when the device cannot boot normally.
                // Only the existing pinned read-only check can recognize this state.
                try { bootloaderAvailable = (await Task.Run(() => _service.CheckBootloaderAsync(plan))).Available; }
                catch (Exception ex) { AppendLog(ex.Message); }
            }
            if (_closed || revision != _availabilityRevision) return;
            await CompletePreparationAsync(plan, bootloaderAvailable, revision);
        }
        catch (Exception ex)
        {
            if (revision != _availabilityRevision) return;
            _plan = null; _step = Step.Failed;
            StatusText.Text = L.T("Die Vorbereitung konnte nicht abgeschlossen werden. Am Gerät wurde nichts geändert.");
            AppendLog(ex.Message); LogExpander.IsExpanded = true;
        }
        finally { if (revision == _availabilityRevision) { _preparing = false; RenderStep(); } }
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offlinePreview || _closed || !CanContinue) return;
        _advancing = true; RenderStep();
        try { await ContinueAsync(); }
        finally { _advancing = false; if (!_closed) RenderStep(); }
    }

    private async Task ContinueAsync()
    {
        // The unplug step has no manual bypass, including queued/programmatic clicks.
        if (_offlinePreview || _closed || _step is Step.Unplug or Step.Reconnect) return;
        if (_executionAttempted) return;
        // Re-read before advancing or installing; a previous process snapshot never authorizes a write.
        var clickRevision = _availabilityRevision; var clickStep = _step;
        await RefreshCoreStatusAsync(advanceWhenClosed: false);
        if (_closed || clickRevision != _availabilityRevision || clickStep != _step || _busy || _preparing || _coreRunning != false) return;
        if (_step == Step.Overview) { await PrepareAsync(); return; }
        if (!CanExecuteConfirmed) return;
        // Only the explicit install click after a separate, unchecked-by-default acknowledgement writes.
        _probeTimer.Stop(); _coreTimer.Stop(); ++_availabilityRevision;
        var plan = _plan!; _plan = null; _executionAttempted = true; _firmwareVerified = false; _busy = true; _step = Step.Running;
        StatusText.Text = L.T("Die App prüft und sichert das Gerät. Danach überträgt sie die Firmware. Bitte warten."); RenderStep();
        try
        {
            var result = await _service.ExecuteAsync(plan, explicitlyConfirmed: true, new Progress<string>(AppendServiceLog));
            _step = result.Verified ? Step.Reconnect : Step.Failed;
            StatusText.Text = result.Verified
                ? L.T("Nach dem Wiederanschließen geht es automatisch weiter.")
                : FirmwareMessages.ForUser(result.Message);
            _evidenceDirectory = result.EvidenceDirectory;
            StartReconnectObservation(result);
            if (!result.Verified) LogExpander.IsExpanded = true;
            if (result.Verified) AppendServiceLog(L.T("Die Firmware wurde geprüft. Verbinde das Gerät jetzt ohne gedrückte Tasten neu."));
            else AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            _step = Step.Failed; StatusText.Text = FirmwareMessages.ForUser(ex.Message);
            AppendLog(ex.Message); LogExpander.IsExpanded = true;
        }
        finally { _busy = false; RenderStep(); }
    }

    private void SetStep(Step step)
    {
        _step = step; StatusText.Text = "";
        StepScroll.ScrollToTop(); RenderStep();
    }

    private void ArmDisconnectObservation(IReadOnlyList<DetectedDevice> devices)
    {
        StopDisconnectObservation();
        var selected = devices.Where(device => device.DeviceId == _deviceId).Select(device => device.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_runtimeVersionHint is { } hint && (selected.Length != 1 || !string.Equals(selected[0], hint.UsbInstanceId, StringComparison.OrdinalIgnoreCase)))
            _runtimeVersionHint = null;
        if (selected.Length != 1) return;
        var tracker = new UsbConnectionTracker(selected[0]);
        tracker.ObserveSnapshot(selected);
        _disconnectTracker = tracker;
    }

    private void ApplyPreparedConnection(FirmwarePlan plan, bool bootloaderAvailable)
    {
        if (_coreRunning != false)
        {
            StopDisconnectObservation(); _plan = null; _bootloaderReady = false; ConfirmationCheck.IsChecked = false;
            SetStep(Step.Overview); return;
        }
        if (_disconnectTracker is not null)
        {
            _plan = plan; SetStep(Step.Unplug);
            StatusText.Text = L.T("Nach dem Trennen der USB-Verbindung geht es automatisch weiter.");
            if (!_offlinePreview) _disconnectTimer.Start();
        }
        else if (bootloaderAvailable)
        {
            _plan = plan; EnterConnectionStep();
            ApplyBootloaderState(true);
        }
        else
        {
            _plan = null; SetStep(Step.Overview);
            StatusText.Text = L.T("Verbinde das Gerät ohne gedrückte Tasten und starte die Vorbereitung erneut.");
        }
    }

    private async Task CompletePreparationAsync(FirmwarePlan plan, bool bootloaderAvailable, int revision)
    {
        // Preparation may take several seconds. A process snapshot from its start
        // must not advance the wizard if CORE has since been opened.
        bool? running = null;
        try { running = await ReadCoreStatusAsync(); }
        catch (Exception ex) { AppendLog(ex.Message); }
        if (_closed || revision != _availabilityRevision) return;
        ApplyCoreStatus(running);
        ApplyPreparedConnection(plan, bootloaderAvailable);
    }

    private void StopDisconnectObservation()
    {
        _disconnectTimer.Stop(); _disconnectTracker = null; _firstAbsentAt = null;
    }

    private void EnterConnectionStep()
    {
        StopDisconnectObservation();
        _bootloaderReady = false; ConfirmationCheck.IsChecked = false;
        SetStep(Step.Connect);
        if (!_offlinePreview) _probeTimer.Start();
    }

    private async Task ObserveDisconnectAsync()
    {
        if (_offlinePreview || _checkingDisconnect) return;
        if (!CanObserveDisconnect) { _firstAbsentAt = null; return; }
        _checkingDisconnect = true;
        var tracker = _disconnectTracker!; var plan = _plan; var revision = _availabilityRevision;
        bool IsCurrent() => CanObserveDisconnect && revision == _availabilityRevision && ReferenceEquals(tracker, _disconnectTracker) && ReferenceEquals(plan, _plan);
        try
        {
            UsbConnectionState state;
            try
            {
                var instances = await _readUsbInstances();
                if (!IsCurrent()) return;
                state = tracker.ObserveSnapshot(instances);
            }
            catch
            {
                if (!IsCurrent()) return;
                state = tracker.ObserveFailure();
            }
            if (ApplyDisconnectState(state, Environment.TickCount64))
            {
                AppendServiceLog(L.T("Die USB-Verbindung wurde getrennt."));
                // Automatic navigation ends at connection help. Readiness and the explicit
                // install acknowledgement still go through the separate firmware service.
                EnterConnectionStep();
                await ProbeBootloaderAsync();
            }
        }
        finally
        {
            if (!IsCurrent() && ReferenceEquals(tracker, _disconnectTracker)) _firstAbsentAt = null;
            _checkingDisconnect = false;
        }
    }

    private bool ApplyDisconnectState(UsbConnectionState state, long now)
    {
        if (state != UsbConnectionState.Disconnected)
        {
            _firstAbsentAt = null;
            StatusText.Text = state == UsbConnectionState.Connected
                ? L.T("Nach dem Trennen der USB-Verbindung geht es automatisch weiter.")
                : L.T("Die USB-Verbindung konnte nicht geprüft werden. Verbinde das Gerät normal und gehe mit „Zurück“ zur Vorbereitung.");
            return false;
        }
        // Require two separate successful absence snapshots with time between them.
        if (_firstAbsentAt is null) { _firstAbsentAt = now; return false; }
        return now - _firstAbsentAt.Value >= 750;
    }

    private async Task RefreshCoreStatusAsync(bool advanceWhenClosed = true)
    {
        if (_offlinePreview || _closed || _busy || _preparing || (_executionAttempted && _step != Step.Reconnect) || _step is not (Step.Overview or Step.Unplug or Step.Connect or Step.Ready or Step.Reconnect)) return;
        var wasRunning = _coreRunning == true;
        var revision = _availabilityRevision;
        // A background poll does not change visible control state. A click shares the pending
        // read instead of being dropped or starting another query.
        try
        {
            var running = await ReadCoreStatusAsync();
            if (_closed || revision != _availabilityRevision) return;
            ApplyCoreStatus(running);
        }
        catch (Exception ex)
        {
            if (!_closed && revision == _availabilityRevision) { ApplyCoreStatus(null); AppendLog(ex.Message); }
        }
        if (!_closed && !_advancing && revision == _availabilityRevision && advanceWhenClosed && wasRunning && _coreRunning == false && _step == Step.Overview)
            await PrepareAsync();
    }

    private async Task<bool?> ReadCoreStatusAsync()
    {
        var query = _coreCheckTask ??= _readCoreStatus();
        try { return await query; }
        finally { if (ReferenceEquals(_coreCheckTask, query)) _coreCheckTask = null; }
    }

    private void ApplyCoreStatus(bool? running)
    {
        if (_coreStatusKnown && _coreRunning == running) return;
        _coreStatusKnown = true;
        _coreRunning = running;
        if (running != false)
        {
                ConfirmationCheck.IsChecked = false; _firstAbsentAt = null; _firstReconnectedAt = null; _bootloaderReady = false;
            if (_step == Step.Ready) SetStep(Step.Connect);
        }
        RenderStep();
    }

    private async Task ProbeBootloaderAsync()
    {
        if (_offlinePreview || _closed || _probing || _busy || _coreRunning != false || _plan is null || _plan.DeviceId != _deviceId || _plan.Operation != Operation || _step is not (Step.Connect or Step.Ready)) return;
        _probing = true; var revision = _availabilityRevision; var plan = _plan;
        try
        {
            var state = await _checkBootloader(plan);
            if (_closed || _coreRunning != false || revision != _availabilityRevision || !ReferenceEquals(plan, _plan) || _step is not (Step.Connect or Step.Ready)) return;
            ApplyBootloaderState(state.Available);
            // Missing/wrong devices stay on the connection step; do not flood the log on every poll.
            if (!state.Available) { StatusText.Text = FirmwareMessages.ForUser(state.Message); AppendLog(state.Message); }
            else if (!VersionBlocksUpdate) AppendServiceLog(L.T("Update-Modus erkannt. Du kannst den Vorgang jetzt bestätigen."));
        }
        catch (Exception ex)
        {
            if (_closed || revision != _availabilityRevision) return;
            ApplyBootloaderState(false); StatusText.Text = FirmwareMessages.ForUser(ex.Message); AppendLog(ex.Message);
        }
        finally { _probing = false; if (!_closed) RenderStep(); }
    }

    private void ApplyBootloaderState(bool ready)
    {
        if (ready && _coreRunning != false) return;
        _bootloaderReady = ready;
        if (!_bootloaderReady) ConfirmationCheck.IsChecked = false;
        if (_bootloaderReady && _step == Step.Connect) SetStep(Step.Ready);
        else if (!_bootloaderReady && _step == Step.Ready)
        {
            SetStep(Step.Connect);
        }
        StatusText.Text = VersionBlocksUpdate ? "" : _bootloaderReady
            ? L.Format("\"{0}\" ist für die Installation bereit.", FirmwareService.DeviceName(_deviceId))
            : L.Format("Warte auf \"{0}\" …", FirmwareService.DeviceName(_deviceId));
        RenderStep();
    }

    private void RenderStep()
    {
        RenderInstalledVersion();
        var checksCore = _step is Step.Overview or Step.Unplug or Step.Connect or Step.Ready or Step.Reconnect;
        CoreNotice.Visibility = checksCore && _coreStatusKnown && _coreRunning != false ? Visibility.Visible : Visibility.Collapsed;
        CoreNotice.Text = _coreRunning == true
            ? L.T("Glorious CORE läuft noch. Sobald es geschlossen ist, geht es automatisch weiter.")
            : L.T("Der Anwendungsstatus konnte nicht geprüft werden. Erneut prüfen.");
        KeepOpenNotice.Visibility = Operation == FirmwareOperation.Update && !VersionBlocksUpdate && _step is not (Step.Complete or Step.Failed) ? Visibility.Visible : Visibility.Collapsed;
        var stepNumber = _step switch { Step.Overview or Step.Preparing => 1, Step.Unplug => 2, Step.Connect => 3, Step.Ready or Step.Running => 4, _ => 5 };
        StepNumber.Text = L.Format("Schritt {0} von 5", stepNumber);
        StepNumber.Visibility = _step == Step.Failed ? Visibility.Collapsed : Visibility.Visible;
        StepProgress.Value = stepNumber; StepProgress.Visibility = StepNumber.Visibility;
        StepTitle.Text = _step switch {
            Step.Overview or Step.Ready when UpdateVersionMissing => L.T("Gerät nicht eindeutig erkannt"),
            Step.Overview when VersionBlocksUpdate => L.T("Kein Update erforderlich"),
            Step.Overview when _coreRunning == true => L.T("Glorious CORE schließen"),
            Step.Overview when !_coreStatusKnown => L.T("Anwendungsstatus wird geprüft …"),
            Step.Overview => Operation switch {
                FirmwareOperation.Update => L.T("Update verfügbar"),
                FirmwareOperation.InstallFromOriginal => L.T("Deine Tastatur mit INGLORIOUS nutzen"),
                _ => L.T("Zurück zu Glorious CORE")
            },
            Step.Preparing => L.T("Wird automatisch vorbereitet …"),
            Step.Unplug => L.T("USB-Kabel trennen"),
            Step.Connect => L.T("Tasten halten und USB einstecken"),
            Step.Ready when VersionBlocksUpdate => L.T("Kein Update erforderlich"),
            Step.Ready => L.T("Gerät erkannt"),
            Step.Running => L.T("Firmware wird installiert …"),
            Step.Reconnect => L.T("Jetzt normal neu verbinden"),
            Step.Complete => L.T("Tasten und Beleuchtung prüfen"), _ => L.T("Vorgang angehalten")
        };
        StepDescription.Text = _step switch {
            Step.Overview or Step.Ready when UpdateVersionMissing => L.T("Die Firmware-Version konnte nicht geprüft werden."),
            Step.Overview when VersionBlocksUpdate => L.T("Die installierte Firmware ist gleich oder neuer. Es wird kein Update gestartet."),
            Step.Overview => L.T("INGLORIOUS Advanced nicht schließen."),
            Step.Preparing => L.T("Die App prüft automatisch Firmware, Sicherung und benötigte Werkzeuge."),
            Step.Unplug => L.Format("Trenne nur \"{0}\" vom USB-Kabel. Andere Geräte bleiben angeschlossen.", FirmwareService.DeviceName(_deviceId)),
            Step.Connect => L.T("Halte die unten angezeigte Tastenkombination gedrückt und stecke das USB-Kabel am selben Anschluss wieder ein. Lass die Tasten nach etwa 3 Sekunden los."),
            Step.Ready when VersionBlocksUpdate => L.T("Kein Update erforderlich"),
            Step.Ready => L.T("Lass die Tasten los. Du kannst die Installation jetzt bestätigen."),
            Step.Running => L.T("USB nicht trennen. Die App bestätigt den Abschluss nach der Prüfung."),
            Step.Reconnect => L.Format("Trenne \"{0}\" einmal vom USB-Kabel und stecke es ohne gedrückte Tasten wieder ein.", FirmwareService.DeviceName(_deviceId)),
            Step.Complete => Operation == FirmwareOperation.RestoreOriginal
                ? L.T("Prüfe die Tasten und die Beleuchtung. Für die weitere Einrichtung der Original-Firmware verwende Glorious CORE.")
                : L.T("Starte den Beleuchtungstest. Prüfe danach die Tasten im Eingabefeld. Für Ziffern muss am GMMK Numpad Num Lock eingeschaltet sein."),
            _ => L.T("Es wird nichts automatisch wiederholt. Die Ursache und der nächste Schritt stehen im Protokoll.")
        };
        ConnectionSteps.Visibility = _step == Step.Connect ? Visibility.Visible : Visibility.Collapsed;
        ConfirmationPanel.Visibility = _step is Step.Overview or Step.Ready ? Visibility.Visible : Visibility.Collapsed;
        AcknowledgementPanel.Visibility = _step == Step.Ready && !VersionBlocksUpdate ? Visibility.Visible : Visibility.Collapsed;
        ConnectionHelp.Visibility = _step == Step.Connect ? Visibility.Visible : Visibility.Collapsed;
        TypingPanel.Visibility = _step == Step.Complete && Operation != FirmwareOperation.RestoreOriginal ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = _step is Step.Preparing or Step.Connect or Step.Running or Step.Reconnect ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.Content = _step switch {
            Step.Overview when _coreRunning != false => L.T("Erneut prüfen"),
            Step.Overview => Operation switch {
                FirmwareOperation.Update => L.T("Update vorbereiten"),
                FirmwareOperation.InstallFromOriginal => L.T("Einrichtung starten"),
                _ => L.T("Wiederherstellung vorbereiten")
            },
            Step.Unplug => L.T("Warte auf USB-Trennung …"),
            Step.Connect => L.T("Warte auf Gerät …"),
            Step.Reconnect => L.T("Warte auf die USB-Verbindung …"),
            _ => Operation switch {
            FirmwareOperation.Update => L.T("Update jetzt installieren"), FirmwareOperation.InstallFromOriginal => L.T("INGLORIOUS jetzt installieren"), _ => L.T("Original jetzt wiederherstellen")
        }};
        ContinueButton.Visibility = _step == Step.Reconnect || !VersionBlocksUpdate && _step is Step.Overview or Step.Unplug or Step.Connect or Step.Ready ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.IsEnabled = !_offlinePreview && CanContinue;
        ConfirmationCheck.IsEnabled = _coreRunning == false && !_advancing && !VersionBlocksUpdate;
        BackButton.Visibility = !_executionAttempted && _step is Step.Unplug or Step.Connect or Step.Ready or Step.Failed ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = !_busy && !_preparing;
        CloseButton.Content = _step == Step.Overview ? L.T("Später") : _step == Step.Complete ? L.T("Fertig") : L.T("Schließen");
        CloseButton.IsEnabled = !_busy && !_lightingTestRunning;
        LightingTestButton.Visibility = _step == Step.Complete && Operation != FirmwareOperation.RestoreOriginal ? Visibility.Visible : Visibility.Collapsed;
        LightingTestButton.IsEnabled = !_offlinePreview && !_busy && !_lightingTestRunning && _firmwareVerified && _completedTarget is not null;
        TypingTest.IsEnabled = !_lightingTestRunning;
        TypingTestHint.Text = L.T(_deviceId == "numpad"
            ? "Tippe 1234567890, um abzuschließen, oder klicke auf „Fertig“."
            : "Schreibe INGLORIOUS, um abzuschließen, oder klicke auf „Fertig“.");
        var hasReport = !string.IsNullOrWhiteSpace(_evidenceDirectory ?? _service?.DiagnosticDirectory);
        EvidenceButton.Visibility = hasReport ? Visibility.Visible : Visibility.Collapsed;
        EvidenceButton.IsEnabled = !_offlinePreview && !_busy && hasReport;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _preparing || _executionAttempted || _step is not (Step.Unplug or Step.Connect or Step.Ready or Step.Failed)) return;
        _probeTimer.Stop(); StopDisconnectObservation(); ++_availabilityRevision; _bootloaderReady = false; ConfirmationCheck.IsChecked = false;
        var needsNormalConnection = _step is Step.Connect or Step.Ready;
        _plan = null; SetStep(Step.Overview);
        if (needsNormalConnection) StatusText.Text = L.T("Verbinde das Gerät ohne gedrückte Tasten und starte die Vorbereitung erneut.");
    }
    private void ConfirmationCheck_Changed(object sender, RoutedEventArgs e) { if (ContinueButton is not null) RenderStep(); }
    internal void ShowOfflineStep(string step)
    {
        if (!_offlinePreview) throw new InvalidOperationException("Only an offline preview can select a synthetic step.");
        StopDisconnectObservation();
        StopReconnectObservation(); _firmwareVerified = false; _evidenceDirectory = null; _completedTarget = null;
        _step = step switch { "unplug" => Step.Unplug, "connect" => Step.Connect, "confirm" or "ready" => Step.Ready, "running" => Step.Running, "reconnect" => Step.Reconnect, "complete" => Step.Complete, "failed" => Step.Failed, _ => Step.Overview };
        _plan = null; _busy = _preparing = false; _bootloaderReady = _step == Step.Ready; _executionAttempted = _step is Step.Failed or Step.Reconnect or Step.Complete; ConfirmationCheck.IsChecked = false;
        StatusText.Text = L.T("Vorschau: Datei- und Geräteprüfungen sowie alle Prozessaufrufe sind deaktiviert.");
        RenderStep();
    }
    internal void ShowOfflineCoreStatus(bool? running)
    {
        if (!_offlinePreview) throw new InvalidOperationException("Only an offline preview can select a synthetic process state.");
        ApplyCoreStatus(running);
    }
    internal void RunOfflineChecks(Action<bool, string> check)
    {
        if (!_offlinePreview) throw new InvalidOperationException("Offline checks require a preview.");
        if (Operation == FirmwareOperation.Update)
            SetRuntimeVersionHint(new(new(_deviceId, new(0, 7, 0), 1), "USB\\VID_FEED&PID_" + (_deviceId == "pro-iso" ? "1C01" : "1C02") + "\\fixture", DateTimeOffset.UtcNow));
        else check(_runtimeVersionHint is null && !VersionBlocksUpdate,
            _deviceId + ": original installation and restore do not require an INGLORIOUS runtime version");
        check(FlowTitle.Text == (Operation == FirmwareOperation.InstallFromOriginal ? L.T("INGLORIOUS einrichten") : Operation == FirmwareOperation.Update ? L.T("Firmware aktualisieren") : L.T("Original-Firmware wiederherstellen")), _deviceId + ": setup, update and restore have separate fixed headings");
        ShowOfflineStep("overview"); ApplyCoreStatus(false);
        check(CoreNotice.Visibility == Visibility.Collapsed && StepTitle.Text != L.T("Glorious CORE schließen") && !Equals(ContinueButton.Content, L.T("Glorious CORE ist geschlossen · Weiter")), _deviceId + ": absent CORE shows no close instruction or redundant confirmation");
        ApplyCoreStatus(true);
        check(CoreNotice.Visibility == Visibility.Visible && StepTitle.Text == L.T("Glorious CORE schließen") && !_busy && _plan is null, _deviceId + ": detected CORE alone shows the close instruction, never writes");
        ApplyCoreStatus(null);
        check(StepTitle.Text != L.T("Glorious CORE schließen") && CoreNotice.Text == L.T("Der Anwendungsstatus konnte nicht geprüft werden. Erneut prüfen."), _deviceId + ": an unknown process state does not claim CORE is running");
        ApplyCoreStatus(false);
        check(CoreNotice.Visibility == Visibility.Collapsed && StepTitle.Text != L.T("Glorious CORE schließen"), _deviceId + ": closing CORE removes the instruction immediately");
        foreach (var step in new[] { "overview", "unplug", "connect", "ready", "running", "reconnect", "complete", "failed" })
        {
            ShowOfflineStep(step); _plan = new FirmwarePlan(_deviceId, Operation, "offline fixture");
            ContinueButton_Click(this, new RoutedEventArgs());
            check(!_busy && _plan is not null && !ContinueButton.IsEnabled, _deviceId + ": offline " + step + " cannot prepare or execute firmware");
        }
        ShowOfflineStep("connect"); _plan = new FirmwarePlan(_deviceId, Operation, "offline fixture");
        ConfirmationCheck.IsChecked = true;
        check(!CanExecuteConfirmed && AcknowledgementPanel.Visibility == Visibility.Collapsed, _deviceId + ": waiting for the device cannot authorize a write");
        ConfirmationCheck.IsChecked = false;
        ApplyBootloaderState(true);
        check(_step == Step.Ready && !CanExecuteConfirmed && !_busy && _plan is not null, _deviceId + ": automatic detection only offers confirmation, never writes");
        check(!CanExecuteConfirmed, _deviceId + ": double-clicking preparation cannot confirm a write");
        ConfirmationCheck.IsChecked = true;
        check(CanExecuteConfirmed, _deviceId + ": write gate requires matching plan, detected bootloader and separate acknowledgement");
        ApplyCoreStatus(true);
        check(!CanExecuteConfirmed && ConfirmationCheck.IsChecked == false && !ConfirmationCheck.IsEnabled, _deviceId + ": CORE reopening revokes an acknowledged installation");
        ApplyCoreStatus(false);
        check(!CanExecuteConfirmed && ConfirmationCheck.IsChecked == false, _deviceId + ": CORE closing never restores a previous write acknowledgement");
        ConfirmationCheck.IsChecked = true;
        ApplyCoreStatus(null);
        check(!CanExecuteConfirmed && ConfirmationCheck.IsChecked == false, _deviceId + ": a failed process query cannot authorize an installation");
        ApplyCoreStatus(false); ConfirmationCheck.IsChecked = true;
        ApplyBootloaderState(false);
        check(_step == Step.Connect && ConfirmationCheck.IsChecked == false && !CanExecuteConfirmed, _deviceId + ": losing the device revokes confirmation and returns to connection help");
        ApplyBootloaderState(true); ConfirmationCheck.IsChecked = true;
        var wrong = Operation == FirmwareOperation.Update ? FirmwareOperation.RestoreOriginal : FirmwareOperation.Update;
        _plan = new FirmwarePlan(_deviceId, wrong, "wrong operation");
        check(!CanExecuteConfirmed, _deviceId + ": wrong operation plan cannot satisfy the fixed flow");
        _plan = new FirmwarePlan(_deviceId == "numpad" ? "pro-iso" : "numpad", Operation, "wrong device");
        check(!CanExecuteConfirmed, _deviceId + ": wrong device plan cannot satisfy the fixed flow");
        BackButton_Click(this, new RoutedEventArgs());
        check(_step == Step.Overview && !_bootloaderReady && ConfirmationCheck.IsChecked == false && !CanExecuteConfirmed && !_probeTimer.IsEnabled, _deviceId + ": Back requires fresh preparation and invalidates readiness");
        check(_step == Step.Overview && _plan is null && ConfirmationCheck.IsChecked == false && !CanExecuteConfirmed, _deviceId + ": Back discards plan and acknowledgement");
        check(StepDescription.Text.Contains("INGLORIOUS Advanced") && StepNumber.Text == L.Format("Schritt {0} von 5", 1), _deviceId + ": first step keeps this application open and shows progress");
        ShowOfflineStep("unplug");
        check(StepDescription.Text.Contains("\"" + FirmwareService.DeviceName(_deviceId) + "\"") && ConnectionSteps.Visibility == Visibility.Collapsed, _deviceId + ": unplug step names the full quoted device without later instructions");
        DetectedDevice Fixture(string id, string instance) => new(id, "Test device", "1234", "5678", "0001", instance, "", "");
        const string selectedInstance = @"USB\VID_1234&PID_5678\runtime-a";
        ArmDisconnectObservation([]);
        check(_disconnectTracker is null && !CanContinue, _deviceId + ": missing normal-mode baseline cannot enable a manual bypass");
        ApplyPreparedConnection(new FirmwarePlan(_deviceId, Operation, "offline fixture"), bootloaderAvailable: false);
        check(_step == Step.Overview && _plan is null && !_disconnectTimer.IsEnabled, _deviceId + ": missing baseline returns to preparation instead of an unrecoverable wait");
        ApplyPreparedConnection(new FirmwarePlan(_deviceId, Operation, "offline fixture"), bootloaderAvailable: true);
        check(_step == Step.Ready && _plan is not null && ConfirmationCheck.IsChecked == false && !CanExecuteConfirmed && !_executionAttempted, _deviceId + ": a verified bootloader offers confirmation without an additional full-read version probe");
        ShowOfflineStep("unplug");
        ArmDisconnectObservation([Fixture(_deviceId, selectedInstance), Fixture(_deviceId, @"USB\VID_1234&PID_5678\runtime-b")]);
        check(_disconnectTracker is null, _deviceId + ": ambiguous identical models cannot arm automatic navigation");
        ArmDisconnectObservation([Fixture(_deviceId, selectedInstance), Fixture("other-model", @"USB\VID_ABCD&PID_9876\unrelated")]);
        check(_disconnectTracker?.ObservedConnected == true && _disconnectTracker.InstanceId == selectedInstance && !_disconnectTimer.IsEnabled, _deviceId + ": fresh selected runtime connection is observed without a private pin or offline timer");
        check(!CanContinue && !ContinueButton.IsEnabled && Equals(ContinueButton.Content, L.T("Warte auf USB-Trennung …")), _deviceId + ": connected USB shows a disabled waiting button");
        check(!ApplyDisconnectState(UsbConnectionState.Disconnected, 1000) && !ApplyDisconnectState(UsbConnectionState.Disconnected, 1749), _deviceId + ": a brief USB absence does not advance");
        check(!ApplyDisconnectState(UsbConnectionState.Connected, 1800) && _firstAbsentAt is null, _deviceId + ": reappearing USB connection clears the pending absence");
        check(!ApplyDisconnectState(UsbConnectionState.Disconnected, 2000) && !ApplyDisconnectState(UsbConnectionState.Unknown, 3000) && _firstAbsentAt is null, _deviceId + ": query failure cancels pending disconnection evidence");
        check(!CanContinue && !ContinueButton.IsEnabled, _deviceId + ": unknown USB state cannot enable Continue");
        check(!ApplyDisconnectState(UsbConnectionState.Disconnected, 4000) && ApplyDisconnectState(UsbConnectionState.Disconnected, 4750), _deviceId + ": two spaced successful absence snapshots permit only connection guidance");
        EnterConnectionStep();
        check(_step == Step.Connect && _disconnectTracker is null && _firstAbsentAt is null && !CanExecuteConfirmed && !_executionAttempted && !_probeTimer.IsEnabled, _deviceId + ": detected disconnection clears tracking and cannot authorize or execute firmware");
        BackButton_Click(this, new RoutedEventArgs());
        check(_step == Step.Overview && _plan is null && _disconnectTracker is null && !_disconnectTimer.IsEnabled, _deviceId + ": Back cannot reuse an old absence and requires a new preparation");
        ShowOfflineStep("reconnect");
        check(ContinueButton.Visibility == Visibility.Visible && !CanContinue && !ContinueButton.IsEnabled && !CanExecuteConfirmed && BackButton.Visibility == Visibility.Collapsed, _deviceId + ": reconnect cannot be skipped manually or start another installation");
        check(!ApplyReconnectState(true, 1000) && !ApplyReconnectState(true, 1749), _deviceId + ": normal USB return requires two spaced positive observations");
        check(!ApplyReconnectState(false, 1800) && _firstReconnectedAt is null && !ApplyReconnectState(true, 2000) && ApplyReconnectState(true, 2750), _deviceId + ": missing or unknown connection resets the normal-return evidence");
        ShowOfflineStep("complete");
        check(ContinueButton.Visibility == Visibility.Collapsed && BackButton.Visibility == Visibility.Collapsed, _deviceId + ": completion offers no repeated write");
        ShowOfflineStep("failed");
        check(ContinueButton.Visibility == Visibility.Collapsed && BackButton.Visibility == Visibility.Collapsed, _deviceId + ": a failed write cannot be retried in place");
        _executionAttempted = false; RenderStep(); BackButton_Click(this, new RoutedEventArgs());
        check(_step == Step.Overview && _plan is null, _deviceId + ": local preparation failure can return without enabling a write");
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner is not null) Owner.Closing += Owner_Closing;
        if (!_offlinePreview) { _coreTimer.Start(); await RefreshCoreStatusAsync(advanceWhenClosed: false); }
    }
    private void Window_Closed(object? sender, EventArgs e) { _closed = true; _probeTimer.Stop(); _coreTimer.Stop(); StopDisconnectObservation(); StopReconnectObservation(); ++_availabilityRevision; if (Owner is not null) Owner.Closing -= Owner_Closing; }
    private void Owner_Closing(object? sender, CancelEventArgs e)
    {
        if (_lightingTestRunning) { e.Cancel = true; _lightingTestCancellation?.Cancel(); return; }
        if (_busy) { e.Cancel = true; Activate(); StatusText.Text = L.T("Der Vorgang läuft noch. Anwendung geöffnet und USB angeschlossen lassen."); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_lightingTestRunning) { e.Cancel = true; _lightingTestCancellation?.Cancel(); return; }
        if (_busy) { e.Cancel = true; StatusText.Text = L.T("Der Vorgang läuft noch. Auf das Prüfergebnis warten; der Prozess wird nicht abgebrochen."); }
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e) { if (!_busy && !_lightingTestRunning) Close(); }
}
