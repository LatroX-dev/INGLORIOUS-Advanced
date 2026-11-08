using InGlorious.Core;

namespace InGlorious.Core;

public sealed partial class DeviceSetupWindow
{
    private bool _automaticUpdateRunning;

    private async Task RunAutomaticUpdateAsync(SetupSession session, OnboardFirmwareVersion expectedVersion)
    {
        if (_service is null || _operation != SetupOperation.Update) throw new InvalidOperationException();
        _automaticUpdateRunning = true;
        var revision = _revision;
        bool Current() => !_closed && _busy && revision == _revision && ReferenceEquals(session, _session);
        Render();
        try
        {
            async Task<OnboardClient> OpenBoundRuntime()
            {
                if (!Current()) throw new SetupException(L.T("Starte die Einrichtung erneut."));
                var matches = (await Task.Run(OnboardClient.FindDevices)).Where(device => device.DeviceId == expectedVersion.DeviceId &&
                    string.Equals(device.UsbInstanceId, session.Device.InstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !_service.MatchesRuntimePort(session.Device, matches[0].Path,
                        BoardDescriptorRegistry.RuntimeVendorId, BoardDescriptorRegistry.Get(expectedVersion.DeviceId).RuntimeProductId))
                    throw new SetupException(L.T("Gerät nicht eindeutig erkannt."));
                return OnboardClient.Open(matches[0]);
            }

            var progress = await _service.StartAutomaticUpdateAsync(session, expectedVersion, explicitlyConfirmed: true,
                async () =>
                {
                    await using var client = await OpenBoundRuntime();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    if (!await client.CanEnterBootloaderAsync(expectedVersion, timeout.Token))
                        throw new SetupException(L.T("Die Firmware-Version hat sich geändert. Starte das Update erneut."));
                    return await DeviceSetupService.ReadAutomaticRuntimeConfigurationAsync(client, expectedVersion.DeviceId, timeout.Token);
                },
                async () =>
                {
                    await using var client = await OpenBoundRuntime();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await client.EnterBootloaderAsync(expectedVersion, explicitlyConfirmed: true, timeout.Token,
                        expectedConfigurationCrc: OnboardProtocol.Crc32(session.ExpectedRuntimeConfiguration!),
                        expectedHardwareProfile: session.ExpectedHardwareProfile);
                });
            ApplyProgress(progress);
            while (Current() && session.Stage == SetupStage.ConnectBootloader)
            {
                await Task.Delay(300);
                progress = await _service.ObserveAsync(session);
                ApplyProgress(progress);
            }
            if (!Current() || !progress.CanCapture)
                throw new SetupException(progress.Message);
            var result = await _firmwareSequence.RunAsync(session, SetupOperation.Update, expectedVersion.DeviceId, true, Current,
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
                ApplyProgress,
                async () =>
                {
                    _status.Text = WriteActivity; Render();
                    return await _service.UpdateAsync(session, explicitlyConfirmed: true, log: Log());
                });
            _writeAttempted = session.WriteAttempted;
            _outcomeUncertain = session.OutcomeUncertain;
            ApplyProgress(result);
        }
        finally { _automaticUpdateRunning = false; }
    }

    private void RenderAutomaticUpdate()
    {
        _stepProgress.Maximum = 5;
        if (_operation != SetupOperation.Update || !(_automaticUpdateAvailable || _session?.AutomaticUpdate == true)) return;
        var stage = _progress?.Stage;
        var step = stage switch { null => 1, SetupStage.Disconnect or SetupStage.ConnectBootloader => 2,
            SetupStage.ReadyToBackup or SetupStage.ReadyToInstall => 3, _ => 4 };
        _stepProgress.Maximum = 4; _stepProgress.Value = step;
        _step.Text = L.Format("Schritt {0} von {1}", step, 4);
        if (_session?.AutomaticUpdate == true) _modelConfirmation.Visibility = System.Windows.Visibility.Collapsed;
        if (stage is null && !_automaticUpdateRunning)
        {
            _description.Text = L.T("Die App sichert deine Einstellungen, aktualisiert die Firmware und startet das Gerät neu. Lass das USB-Kabel angeschlossen.");
            _action.Content = CombinedAction;
        }
        if (_automaticUpdateRunning && stage is null or SetupStage.Disconnect or SetupStage.ConnectBootloader)
        {
            _heading.Text = L.T("Update-Modus wird gestartet …");
            _description.Text = L.T("USB angeschlossen und INGLORIOUS Advanced geöffnet lassen.");
            RenderKeyCombination(null);
            _summary.Visibility = _modelConfirmation.Visibility = System.Windows.Visibility.Collapsed;
            _action.Content = L.T("Bitte warten …");
        }
        if (stage == SetupStage.Reconnect && _session?.AutomaticRestartRequested == true)
        {
            _heading.Text = L.T("Die Tastatur startet neu …");
            _description.Text = L.T("USB angeschlossen und INGLORIOUS Advanced geöffnet lassen.");
        }
        if (_automaticUpdateRunning && stage is SetupStage.ReadyToBackup or SetupStage.ReadyToInstall)
        {
            _heading.Text = stage == SetupStage.ReadyToBackup ? BackupActivity : WriteActivity;
            _description.Text = L.T("USB angeschlossen und INGLORIOUS Advanced geöffnet lassen.");
            _summary.Visibility = System.Windows.Visibility.Collapsed;
            _action.Content = L.T("Bitte warten …");
        }
    }
}

