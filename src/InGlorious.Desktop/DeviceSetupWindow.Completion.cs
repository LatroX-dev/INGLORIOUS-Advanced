using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class DeviceSetupWindow
{
    private readonly Grid _completion = new() { Name = "SetupCompletion", RowDefinitions = new("*,Auto"), RowSpacing = 14, IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _testCards = new() { Name = "SetupTestCards", ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
    private readonly Button _lightingButton = new() { Name = "SetupLightingTest", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
    private readonly TextBlock _lightingStatus = Text(13), _typingHint = Text(13);
    private readonly TextBlock _numLockHint = new() { Text = L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private string? _returnedHidPath;
    private string _completionInput = "";
    private bool _lightingRunning, _completionWasVisible;
    private string CompletionTarget => DeviceId == "numpad" ? "1234567890" : "INGLORIOUS";
    private bool CompletionTestsVisible => _progress?.Stage == SetupStage.Complete && _writeAttempted && _operation != SetupOperation.Restore;
    private bool LightingReady => CompletionTestsVisible && !_closed && !_busy && !_lightingRunning && !string.IsNullOrWhiteSpace(_returnedHidPath);
    private bool TypingCompletes => CompletionTestsVisible && !_closed && !_busy && !_lightingRunning &&
        string.Equals(_completionInput, CompletionTarget, StringComparison.OrdinalIgnoreCase);

    private bool CanReceiveCompletionInput(bool activeWindow) => CompletionTestsVisible && !_closed && !_busy && !_lightingRunning &&
        activeWindow && IsEnabled && !OwnedWindows.Any(window => window.IsVisible);

    private bool ReceiveCompletionText(string? text, bool activeWindow)
    {
        if (!CanReceiveCompletionInput(activeWindow) || string.IsNullOrEmpty(text)) return false;
        _completionInput = DeviceTypingTest.AdvanceCompletionPrefix(_completionInput, text, CompletionTarget);
        if (!_offlinePreview && TypingCompletes) Close();
        return true;
    }

    private void ReceiveCompletionKey(KeyEventArgs e, bool activeWindow, bool released)
    {
        if (!CanReceiveCompletionInput(activeWindow) || !DeviceTypingTest.IsTestNavigationKey(e.Key.ToString())) return;
        if (!released)
        {
            if (e.Key == Key.Back && _completionInput.Length > 0) _completionInput = _completionInput[..^1];
            else if (e.Key is Key.Delete or Key.Escape) _completionInput = "";
        }
        e.Handled = true;
    }

    private void InitializeCompletionControls()
    {
        _lightingButton.Content = MainWindow.ButtonContent("Beleuchtung testen"); _lightingButton.Classes.Add("primary");
        _deviceTestButton.HorizontalAlignment = HorizontalAlignment.Left;
        var lighting = new StackPanel { Spacing = 8 };
        lighting.Children.Add(_lightingButton);
        lighting.Children.Add(new TextBlock { Text = L.T("Dauert etwa 21 Sekunden."), TextWrapping = TextWrapping.Wrap });
        lighting.Children.Add(new TextBlock { Text = L.T("Während des Lichttests keine Tasten drücken."), TextWrapping = TextWrapping.Wrap });
        lighting.Children.Add(_lightingStatus);
        var typing = new StackPanel { Spacing = 8 };
        typing.Children.Add(_deviceTestButton);
        typing.Children.Add(_typingHint); typing.Children.Add(_numLockHint);
        _numLockHint.Margin = new(0);
        var lightingCard = CompletionCard(lighting); var typingCard = CompletionCard(typing);
        _testCards.Children.Add(lightingCard); Grid.SetColumn(typingCard, 1); _testCards.Children.Add(typingCard);
        _completion.Children.Add(_testCards);
        AutomationProperties.SetName(_lightingButton, L.T("Beleuchtung testen"));
        AutomationProperties.SetName(_deviceTestButton, L.T("Gerät testen"));
        _lightingButton.Click += async (_, _) => await RunCompletionLightingAsync();
        _flowBody.Focusable = true;
        AddHandler(KeyDownEvent, (_, e) => ReceiveCompletionKey(e, IsActive, released: false), RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => ReceiveCompletionKey(e, IsActive, released: true), RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, (_, e) =>
        {
            if (ReceiveCompletionText(e.Text, IsActive)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _completionInput = "";
        InitializeCompletionOptions();
    }

    private static Border CompletionCard(Control content) => new()
    {
        Background = Palette("Surface"), BorderBrush = Palette("Stroke"), BorderThickness = new(1),
        CornerRadius = new(6), Padding = new(12), Child = content
    };

    private void RenderCompletion()
    {
        RenderCompletionOptions();
        _completion.IsVisible = CompletionTestsVisible;
        if (!CompletionTestsVisible || _busy || _lightingRunning) _completionInput = "";
        if (CompletionTestsVisible && !_completionWasVisible && !_offlinePreview) _flowBody.Focus();
        _completionWasVisible = CompletionTestsVisible;
        _lightingButton.IsEnabled = !_offlinePreview && LightingReady;
        _lightingStatus.IsVisible = !string.IsNullOrWhiteSpace(_lightingStatus.Text) && !_lightingRunning;
        _numLockHint.IsVisible = DeviceId == "numpad";
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
            _service.MatchesRuntimePort(session, device.Path, normal.VendorId, normal.ProductId)).ToArray();
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
        return hid[0].Path;
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
        _lightingStatus.Text = "";
        try
        {
            // MainWindow keeps shared brightness paused for the entire modal firmware flow.
            var root = await ReadVerifiedReturnedRuntimeAsync(session);
            var board = BoardDescriptorRegistry.Get(session.DeviceId!);
            await new FirmwareLightingTestService().BlinkAsync(session.DeviceId!, candidate => candidate.Path == root &&
                _service!.MatchesRuntimePort(session, candidate.Path, BoardDescriptorRegistry.RuntimeVendorId, board.RuntimeProductId),
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
            if (!_closed) { Render(); if (!_offlinePreview && TypingCompletes) Close(); }
        }
    }
}
