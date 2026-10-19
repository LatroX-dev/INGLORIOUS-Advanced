using System.IO;
using System.Windows;
using System.Windows.Automation;

namespace InGlorious.Core;

public partial class MainWindow
{
    private sealed record HardwareSwitchReading(string Path, OnboardHardwareControlStatus? Os,
        OnboardHardwareProfileStatus? Profile, Guid? InstallationId);
    private sealed record HardwareProfileDraft(EditorSession Session, OnboardProfileBinding Binding);
    private readonly Dictionary<string, OnboardProfileBinding> _hardwareProfileBindings = [];
    private readonly Dictionary<string, OnboardProfileBinding> _hardwareRefreshObserved = [];
    private readonly HashSet<OnboardProfileBinding> _hardwareRefreshRetried = [];
    private readonly Dictionary<(string Device, Guid Installation, byte Slot), HardwareProfileDraft> _hardwareProfileDrafts = [];
    private HardwareSwitchReading? _hardwareSwitchReading;
    private string? _hardwareSwitchDevice;
    private DateTime _hardwareSwitchNextRead;
    private bool _hardwareSwitchReadingActive;
    private Func<string, CancellationToken, Task<HardwareSwitchReading>> _readHardwareSwitches = ReadHardwareSwitchesAsync;

    private bool CanReadHardwareSwitches => _knobStatusPauseDepth == 0 && IsVisible && WindowState != WindowState.Minimized &&
        !_startupClosed && !_startupLoading && !_startupFinishing && !_readingDevice && !_checkingFirmwareGuide && !_scanning &&
        IsEnabled && EditorSurface.IsEnabled && IsActiveDeviceConnected && HardwareControlCatalog.HasOsSwitch(Device.Id) &&
        !OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

    private static Task<HardwareSwitchReading> ReadHardwareSwitchesAsync(string deviceId, CancellationToken token) => Task.Run(async () =>
    {
        var matches = OnboardClient.FindDevices().Where(device => device.DeviceId == deviceId).ToArray();
        token.ThrowIfCancellationRequested();
        if (matches.Length != 1) throw new IOException("No unique hardware-switch endpoint.");
        await using var client = OnboardClient.Open(matches[0]);
        var profile = await client.GetHardwareProfileStatusAsync(cancellationToken: token);
        if (profile is not null)
            return new HardwareSwitchReading(matches[0].Path, null, profile, await client.GetInstallationIdAsync(token));
        return new HardwareSwitchReading(matches[0].Path, await client.GetHardwareControlStatusAsync(token), null, null);
    }, token);

    private async Task RefreshHardwareSwitchesAsync(bool allowOfflineFixture = false)
    {
        if (_screenshotMode && !allowOfflineFixture || _knobStatusPauseDepth != 0) return;
        RefreshHardwareSwitchView();
        if (!CanReadHardwareSwitches || _knobStatusReading || DateTime.UtcNow < _hardwareSwitchNextRead) return;
        var deviceId = Device.Id; var revision = _connectionRevision;
        _knobStatusReading = true; _hardwareSwitchReadingActive = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _knobStatusCompletion = completion.Task;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _knobStatusCancellation = cancellation;
        try
        {
            var reading = await _readHardwareSwitches(deviceId, cancellation.Token);
            if (!CanReadHardwareSwitches || Device.Id != deviceId || revision != _connectionRevision || cancellation.IsCancellationRequested) return;
            if (reading.Profile is { } p && p.DeviceId != deviceId || reading.Os is { } os && os.DeviceId != deviceId)
                throw new InvalidDataException("Hardware-switch model mismatch.");
            _hardwareSwitchDevice = deviceId; _hardwareSwitchReading = reading;
            _hardwareSwitchNextRead = DateTime.UtcNow.AddSeconds(reading.Profile is null && reading.Os is null ? 15 : 1.5);
            if (reading.Profile is { ActiveSlot: > 0, SwitchPending: false, UploadOpen: false } profile && reading.InstallationId is { } installation)
            {
                var binding = profile.Bind(installation);
                if (!_hardwareProfileBindings.TryGetValue(deviceId, out var current) || current != binding)
                {
                    // A physical switch never retargets a draft. The refresh path
                    // captures its explicit old slot before adopting the new one.
                    if (!HasPendingHardwareEditorInput && _hardwareRefreshObserved.GetValueOrDefault(deviceId) != binding)
                    {
                        _hardwareRefreshObserved[deviceId] = binding;
                        _configurationRefreshPending.Add(deviceId);
                    }
                    else if (HasPendingHardwareEditorInput)
                        RequireDeviceConfigurationRead(deviceId, L.T("Beende zuerst deine Eingabe. Danach wird das gewählte Tastaturprofil geladen."));
                    TransferButton.IsEnabled = false;
                }
            }
            RefreshHardwareSwitchView();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("Hardware switches: " + error.Message);
            // A busy read must not blink or reset a previously confirmed position.
            if (Device.Id == deviceId && revision == _connectionRevision) _hardwareSwitchNextRead = DateTime.UtcNow.AddSeconds(3);
        }
        finally
        {
            _knobStatusCancellation = null; _knobStatusReading = false; _hardwareSwitchReadingActive = false; completion.TrySetResult();
        }
    }

    private bool HasPendingHardwareEditorInput => HasPendingEdits || ProjectName.Text != _project.Name ||
        CurrentMacro is { } macro && MacroName.Text != macro.Name;

    private bool HardwareProfileAwaitingRefresh => _hardwareSwitchDevice == Device.Id &&
        _hardwareSwitchReading is { Profile: { } status, InstallationId: { } identity } &&
        (status.SwitchPending || status.UploadOpen || status.ActiveSlot == 0 ||
         !_hardwareProfileBindings.TryGetValue(Device.Id, out var binding) || binding != status.Bind(identity));

    private void RefreshHardwareSwitchView()
    {
        if (HardwareSwitchHost is null) return;
        if (_hardwareSwitchDevice != Device.Id || !IsActiveDeviceConnected)
        {
            _hardwareSwitchReading = null; _hardwareSwitchDevice = Device.Id; _hardwareSwitchNextRead = default;
        }
        HardwareSwitchHost.Visibility = IsActiveDeviceConnected && HardwareControlCatalog.HasOsSwitch(Device.Id) && _activeTab != 5
            ? Visibility.Visible : Visibility.Collapsed;
        var reading = _hardwareSwitchReading;
        var mac = reading?.Profile?.MacPosition ?? reading?.Os?.MacPosition;
        HardwareOsValue.Text = mac is null ? "—" : mac.Value ? "Mac" : "Windows";
        HardwareProfilePosition.Visibility = reading?.Profile is null ? Visibility.Collapsed : Visibility.Visible;
        HardwareProfileValue.Text = reading?.Profile is { PhysicalSlot: > 0 } profile ? L.Format("Profil {0}", profile.PhysicalSlot) : L.T("Profil —");
        var pending = reading?.Profile is { SwitchPending: true } or { OsPending: true } || reading?.Os?.Pending == true;
        HardwareSwitchPending.Text = pending ? L.T("Zum Wechseln alle Tasten loslassen.") : "";
        if (!pending && reading?.Profile is { ActiveSlot: > 0 } active && _hardwareProfileBindings.TryGetValue(Device.Id, out var editing) && editing.Slot != active.ActiveSlot)
            HardwareSwitchPending.Text = L.Format("In Bearbeitung: Profil {0}", editing.Slot);
        HardwareSwitchPending.Visibility = HardwareSwitchPending.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(HardwareOsPosition, L.T("Stellung des Windows/Mac-Schalters an deiner Tastatur.") + " " + HardwareOsValue.Text);
        AutomationProperties.SetName(HardwareProfilePosition, L.T("Stellung des Profilschalters an deiner Tastatur.") + " " + HardwareProfileValue.Text);
    }

    private void RememberHardwareProfileSnapshot(OnboardHardwareProfileSnapshot snapshot)
    {
        _hardwareRefreshRetried.RemoveWhere(binding => binding.DeviceId == snapshot.Status.DeviceId);
        _hardwareProfileBindings[snapshot.Status.DeviceId] = snapshot.Binding;
        if (!snapshot.Status.StorageHealthy) _confirmedDeviceSaves.Remove(snapshot.Status.DeviceId);
        if (Device.Id == snapshot.Status.DeviceId)
        {
            _hardwareSwitchDevice = Device.Id;
            _hardwareSwitchReading = new("", null, snapshot.Status, snapshot.InstallationId);
        }
    }

    private void RetryHardwareProfileRead(string deviceId)
    {
        if (_hardwareRefreshObserved.TryGetValue(deviceId, out var binding) && _hardwareRefreshRetried.Add(binding))
        {
            _hardwareRefreshObserved.Remove(deviceId);
            _hardwareSwitchNextRead = DateTime.UtcNow.AddSeconds(3);
        }
    }

    private void CaptureHardwareProfileDraft(string deviceId, EditorSession session)
    {
        if (_hardwareProfileBindings.TryGetValue(deviceId, out var binding))
            _hardwareProfileDrafts[(deviceId, binding.InstallationId, binding.Slot)] = new(session, binding);
    }

    private bool ApplyHardwareProfileRefresh(StartupDeviceResult result, string activeId, bool preserveActive,
        bool untouchedAutomaticFallback = false)
    {
        var snapshot = result.ProfileSnapshot!; var id = result.DeviceId;
        var incoming = snapshot.Binding;
        _hardwareProfileBindings.TryGetValue(id, out var previous);
        _sessions.TryGetValue(id, out var session);
        if (previous is null && session is not null &&
            (!untouchedAutomaticFallback || Fingerprint(session.Project) != session.SavedFingerprint || id == activeId && preserveActive))
        {
            // With no device binding, only the untouched startup fallback can be replaced automatically.
            // Pending text is not in the project fingerprint, and an intentionally opened file may be clean.
            RequireDeviceConfigurationRead(id, id == activeId && preserveActive
                ? L.T("Beende zuerst deine Eingabe. Danach wird das gewählte Tastaturprofil geladen.")
                : L.T("Die Konfiguration auf der Tastatur hat sich geändert. Deine Änderungen bleiben erhalten. Lade zuerst den aktuellen Gerätestand."));
            return false;
        }
        if (id == activeId && preserveActive && previous is not null && previous != incoming)
        {
            RequireDeviceConfigurationRead(id, L.T("Beende zuerst deine Eingabe. Danach wird das gewählte Tastaturprofil geladen."));
            return false;
        }
        if (session is not null) CaptureHardwareProfileDraft(id, session);
        var key = (id, incoming.InstallationId, incoming.Slot);
        if (_hardwareProfileDrafts.TryGetValue(key, out var draft))
        {
            if (draft.Binding != incoming && Fingerprint(draft.Session.Project) != draft.Session.SavedFingerprint)
            {
                // Keep both the edited snapshot and its old precondition. Saving
                // stays blocked until the user explicitly resolves the conflict.
                _sessions[id] = draft.Session; _hardwareProfileBindings[id] = draft.Binding;
                RequireDeviceConfigurationRead(id, L.T("Die Konfiguration auf der Tastatur hat sich geändert. Deine Änderungen bleiben erhalten. Lade zuerst den aktuellen Gerätestand."));
                return id == activeId;
            }
            if (draft.Binding == incoming)
            {
                _sessions[id] = draft.Session;
                RememberHardwareProfileSnapshot(snapshot); MarkDeviceConfigurationRead(result.Project!, result.Info);
                return id == activeId;
            }
        }
        var project = ProjectService.Clone(result.Project!); var fingerprint = Fingerprint(project);
        _sessions[id] = new(project, fingerprint, null, session?.Layer ?? 0, null, session?.Selection ?? [], [], [],
            L.Format("Profil {0} von Tastatur gelesen", incoming.Slot), fingerprint, null);
        RememberHardwareProfileSnapshot(snapshot); MarkDeviceConfigurationRead(project, result.Info);
        if (snapshot.Status.StorageHealthy) _confirmedDeviceSaves[id] = (fingerprint, DateTime.Now);
        return id == activeId;
    }

    private bool SaveHardwareProfileDraft((string Device, Guid Installation, byte Slot) key, HardwareProfileDraft draft)
    {
        try
        {
            var path = draft.Session.Path;
            if (path is null)
            {
                var suggested = _profileLibrary.SuggestPath(draft.Session.Project.Name + " - " + L.Format("Profil {0}", key.Slot));
                Directory.CreateDirectory(_profileLibrary.Folder);
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = L.T("INGLORIOUS-Profil speichern"), Filter = L.T("INGLORIOUS-Profil (*.json)|*.json"),
                    DefaultExt = ".json", AddExtension = true, FileName = Path.GetFileName(suggested),
                    InitialDirectory = Path.GetDirectoryName(suggested), OverwritePrompt = true
                };
                if (dialog.ShowDialog(this) != true) return false;
                path = dialog.FileName;
            }
            ProjectService.Save(path, draft.Session.Project);
            _hardwareProfileDrafts[key] = draft with { Session = draft.Session with
                { Path = path, SavedFingerprint = Fingerprint(draft.Session.Project) } };
            RememberProfilePath(path, activate: false);
            return true;
        }
        catch (Exception error) { ShowError(L.T("Das Profil konnte nicht gespeichert werden."), error); return false; }
    }
}
