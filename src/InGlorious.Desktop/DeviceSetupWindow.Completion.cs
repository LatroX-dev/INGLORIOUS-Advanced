using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class DeviceSetupWindow
{
    private readonly StackPanel _completion = new() { Margin = new(0, 18, 0, 0), IsVisible = false };
    private readonly Button _lightingButton = new() { Name = "SetupLightingTest", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
    private readonly TextBlock _lightingStatus = Text(13), _typingHint = Text(13);
    private readonly TextBlock _numLockHint = new() { Text = L.T("Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly TextBox _typingTest = new() { Name = "SetupTypingTest", MinHeight = 40, MaxLength = 32, Margin = new(0, 8, 0, 0) };
    private string? _returnedHidPath;
    private bool _lightingRunning;
    private bool CompletionTestsVisible => _progress?.Stage == SetupStage.Complete && _writeAttempted && _operation != SetupOperation.Restore;
    private bool LightingReady => CompletionTestsVisible && !_closed && !_busy && !_lightingRunning && !string.IsNullOrWhiteSpace(_returnedHidPath);
    private bool TypingCompletes => CompletionTestsVisible && !_closed && !_busy && !_lightingRunning &&
        string.Equals((_typingTest.Text ?? "").Trim(), DeviceId == "numpad" ? "1234567890" : "INGLORIOUS", StringComparison.OrdinalIgnoreCase);

    private void InitializeCompletionControls()
    {
        _lightingButton.Content = MainWindow.ButtonContent("Beleuchtung testen"); _lightingButton.Classes.Add("primary");
        _lightingButton.Margin = new(0, 0, 12, 0);
        var lightingActions = new WrapPanel();
        lightingActions.Children.Add(_lightingButton); lightingActions.Children.Add(_deviceTestButton);
        _completion.Children.Add(lightingActions);
        AutomationProperties.SetName(_lightingButton, L.T("Beleuchtung testen"));
        AutomationProperties.SetName(_deviceTestButton, L.T("Gerät testen"));
        _completion.Children.Add(new TextBlock { Text = L.T("Dauert etwa 21 Sekunden."), Margin = new(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        _completion.Children.Add(new TextBlock { Text = L.T("Während des Lichttests keine Tasten drücken."), Margin = new(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        _lightingStatus.Margin = new(0, 8, 0, 0); _completion.Children.Add(_lightingStatus);
        _typingHint.Margin = new(0, 18, 0, 0); _completion.Children.Add(_typingHint); _completion.Children.Add(_numLockHint); _completion.Children.Add(_typingTest);
        AutomationProperties.SetName(_typingTest, L.T("Hier Tasten testen"));
        _lightingButton.Click += async (_, _) => await RunCompletionLightingAsync();
        _typingTest.TextChanged += (_, _) => { if (!_offlinePreview && TypingCompletes) Close(); };
        InitializeCompletionOptions();
    }

    private void RenderCompletion()
    {
        RenderCompletionOptions();
        _completion.IsVisible = CompletionTestsVisible ? true : false;
        _lightingButton.IsEnabled = !_offlinePreview && LightingReady;
        _typingTest.IsEnabled = !_offlinePreview && !_lightingRunning;
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
        _lightingStatus.Text = L.T(DeviceId == "numpad"
            ? "Alle LEDs leuchten weiß, dann die Ziffern 1 bis 0 nacheinander. Rote Tropfen fallen von oben und füllen die Beleuchtung von unten auf. Zum Schluss leuchten alle LEDs rot, dann kehrt die normale Beleuchtung zurück."
            : "Alle LEDs leuchten weiß, dann die Buchstaben von INGLORIOUS nacheinander. Rote Tropfen fallen von oben und füllen die Beleuchtung von unten auf. Zum Schluss leuchten alle LEDs rot, dann kehrt die normale Beleuchtung zurück.");
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
