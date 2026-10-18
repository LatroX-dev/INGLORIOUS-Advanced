using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InGlorious.Core;

public sealed partial class DeviceSetupWindow
{
    private readonly StackPanel _completion = new() { Name = "SetupCompletion", Margin = new(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _lightingButton = new() { Name = "SetupLightingTest", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
    private readonly TextBlock _lightingStatus = Text(13), _typingHint = Text(13), _numLockHint = Text(12);
    private string _typingSequence = "";
    private string? _returnedUsbInstanceId;
    private bool _lightingRunning, _completionWasVisible;
    private bool CompletionTestsVisible => _progress?.Stage == SetupStage.Complete && _writeAttempted && _operation != SetupOperation.Restore;
    private bool LightingReady => CompletionTestsVisible && !_closed && !_busy && !_lightingRunning && !string.IsNullOrWhiteSpace(_returnedUsbInstanceId);
    private bool CompletionTypingReady => CompletionTestsVisible && !_closed && !_busy && !_lightingRunning;
    private string ExpectedTypingSequence => DeviceId == "numpad" ? "1234567890" : "INGLORIOUS";
    private bool TypingCompletes => CompletionTypingReady && string.Equals(_typingSequence, ExpectedTypingSequence, StringComparison.OrdinalIgnoreCase);
    private bool CompletionInputActive => CompletionTypingReady && IsActive && IsEnabled && !OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

    private void RecordCompletionText(string text)
    {
        if (!CompletionTypingReady) return;
        _typingSequence = DeviceTypingTest.AdvanceCompletionPrefix(_typingSequence, text, ExpectedTypingSequence);
        if (!_offlinePreview && TypingCompletes) Close();
    }

    private void InitializeCompletionControls()
    {
        _lightingButton.Content = L.T("Beleuchtung testen"); _lightingButton.Style = (Style)FindResource("PrimaryButton");
        UiIcons.SetKind(_lightingButton, "light");
        var tests = new Grid { Name = "SetupTestCards" };
        tests.ColumnDefinitions.Add(new()); tests.ColumnDefinitions.Add(new());
        var lighting = new StackPanel();
        var typing = new StackPanel();
        Border Card(StackPanel content) => new() { Child = content, Background = (Brush)FindResource("Surface"), CornerRadius = new(6), Padding = new(14) };
        var lightingCard = Card(lighting); lightingCard.Margin = new(0, 0, 6, 0);
        var typingCard = Card(typing); typingCard.Margin = new(6, 0, 0, 0); Grid.SetColumn(typingCard, 1);
        tests.Children.Add(lightingCard); tests.Children.Add(typingCard);
        lighting.Children.Add(_lightingButton); typing.Children.Add(_deviceTestButton);
        _completion.Children.Add(tests);
        lighting.Children.Add(new TextBlock { Text = L.T("Dauert etwa 21 Sekunden."), Margin = new(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        _lightingStatus.Margin = new(0, 6, 0, 0); lighting.Children.Add(_lightingStatus);
        AutomationProperties.SetLiveSetting(_lightingStatus, AutomationLiveSetting.Polite);
        _typingHint.Margin = new(0, 8, 0, 0); typing.Children.Add(_typingHint);
        _numLockHint.Text = L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0.");
        _numLockHint.Margin = new(0, 6, 0, 0); _numLockHint.Foreground = (Brush)FindResource("Muted"); typing.Children.Add(_numLockHint);
        _lightingButton.Click += async (_, _) => await RunCompletionLightingAsync();
        _flowBody.Focusable = true;
        PreviewTextInput += (_, e) =>
        {
            if (!CompletionInputActive) return;
            RecordCompletionText(e.Text); e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!CompletionInputActive) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Back && _typingSequence.Length > 0) _typingSequence = _typingSequence[..^1];
            else if (key is Key.Escape or Key.Delete) _typingSequence = "";
            if (DeviceTypingTest.IsTestNavigationKey(key.ToString())) e.Handled = true;
        };
        PreviewKeyUp += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (CompletionInputActive && DeviceTypingTest.IsTestNavigationKey(key.ToString())) e.Handled = true;
        };
        Deactivated += (_, _) => _typingSequence = "";
        InitializeCompletionOptions();
    }

    private void RenderCompletion()
    {
        RenderCompletionOptions();
        _completion.Visibility = CompletionTestsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (CompletionTestsVisible && !_completionWasVisible && !_offlinePreview) _flowBody.Focus();
        _completionWasVisible = CompletionTestsVisible;
        _lightingButton.IsEnabled = !_offlinePreview && LightingReady;
        if (!CompletionTypingReady) _typingSequence = "";
        if (string.IsNullOrWhiteSpace(_lightingStatus.Text)) _lightingStatus.Text = L.T("Während des Lichttests keine Tasten drücken.");
        _numLockHint.Visibility = DeviceId == "numpad" ? Visibility.Visible : Visibility.Collapsed;
        _typingHint.Text = L.T(DeviceId == "numpad" ? "Tippe 1234567890, um abzuschließen, oder klicke auf „Fertig“."
            : "Schreibe INGLORIOUS, um abzuschließen, oder klicke auf „Fertig“.");
    }

    private async Task<string> ReadVerifiedReturnedRuntimeAsync(SetupSession session)
    {
        if (_offlinePreview || _service is null || session.DeviceId is not { } id || _operation == SetupOperation.Restore)
            throw new InvalidOperationException("A completed INGLORIOUS setup session is required.");
        if (DeviceSetupService.IsGloriousCoreRunning() is not false)
            throw new SetupException(L.T("Schließe Glorious CORE, bevor du fortfährst."), recoverable: true);
        var devices = await _service.ListDevicesAsync();
        var normal = SelectReturnedDevice(devices, session.Device, id);
        if (normal is null) throw new SetupException(L.T("Die USB-Verbindung konnte nicht geprüft werden. Die App versucht es erneut."));
        var hid = (await Task.Run(OnboardClient.FindDevices)).Where(device => device.DeviceId == id &&
            string.Equals(device.UsbInstanceId, normal.InstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hid.Length != 1) throw new SetupException(L.T("Die Firmware-Version konnte nicht geprüft werden."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var client = OnboardClient.Open(hid[0]);
        var info = await client.GetInfoAsync(timeout.Token);
        var extensions = await client.GetExtensionsAsync(timeout.Token);
        var version = await client.GetFirmwareVersionAsync(timeout.Token);
        if (!IsExpectedReturnedRuntime(id, info, extensions, version))
            throw new SetupException(L.T("Die erwartete Firmware-Version wurde noch nicht bestätigt."));
        if (DeviceSetupService.IsGloriousCoreRunning() is not false)
            throw new SetupException(L.T("Schließe Glorious CORE, bevor du fortfährst."), recoverable: true);
        _runtimeVersion = version;
        return normal.InstanceId;
    }

    private static SetupDevice? SelectReturnedDevice(IReadOnlyList<SetupDevice> devices, SetupDevice original, string id)
    {
        if (!BoardDescriptorRegistry.TryGet(id, out var board)) return null;
        var matches = devices.Where(device => device.IsRuntime && device.CandidateDeviceIds.Count == 1 && device.CandidateDeviceIds[0] == id &&
            device.VendorId == BoardDescriptorRegistry.RuntimeVendorId && device.ProductId == board.RuntimeProductId &&
            DeviceSetupService.SamePort(device.LocationPaths, original.LocationPaths)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsExpectedReturnedRuntime(string id, OnboardInfo info, OnboardExtensions extensions, OnboardFirmwareVersion? version) =>
        BoardDescriptorRegistry.TryGet(id, out _) && info.DeviceId == id && info.Persistent && (info.Flags & 4) == 0 && info.StagingBytes == 0 &&
        info.SupportsEffects && info.SupportsTiming && info.SupportsColorSequences && info.SupportsRuntimeBrightness && info.SupportsStandby &&
        extensions.SupportsLightingTest && version is not null && version.DeviceId == id && version.BuildId != 0 &&
        FirmwareSemanticVersion.TryParse(DeviceSetupService.TargetFirmwareVersionFor(id), out var target) && version.Version == target;

    private async Task RunCompletionLightingAsync()
    {
        if (_offlinePreview || !LightingReady || _session is not { } session) return;
        _lightingRunning = true; Render();
        _lightingStatus.Text = L.T("Während des Lichttests keine Tasten drücken.");
        try
        {
            // MainWindow keeps shared brightness paused for the entire modal firmware flow.
            var root = await ReadVerifiedReturnedRuntimeAsync(session);
            await new FirmwareLightingTestService().BlinkAsync(session.DeviceId!, root,
                () => Task.FromResult(DeviceSetupService.IsGloriousCoreRunning()), CancellationToken.None);
            if (!_closed) _lightingStatus.Text = L.T("Prüfe, ob die Tasten und Seitenleisten aufgeleuchtet haben.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine("Setup lighting test failed: " + exception);
            if (!_closed) _lightingStatus.Text = exception is FirmwareLightingTestException lighting ? lighting.UserMessage
                : exception is SetupException ? exception.Message : L.T("Der Beleuchtungstest konnte nicht abgeschlossen werden. Prüfe die USB-Verbindung.");
        }
        finally
        {
            _lightingRunning = false;
            if (!_closed) Render();
        }
    }
}
