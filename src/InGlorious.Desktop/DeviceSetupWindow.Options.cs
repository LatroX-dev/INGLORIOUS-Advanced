using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class DeviceSetupWindow
{
    private CoreImportPreview? _coreImportPreview;
    private readonly Button _coreImportButton = new() { Content = MainWindow.ButtonContent("CORE-Einstellungen importieren") };
    private readonly Button _deviceTestButton = new() { Name = "SetupDeviceTest", Content = MainWindow.ButtonContent("Gerät testen"), MinHeight = 38 };
    private readonly Button _feedbackButton = new() { Content = MainWindow.ButtonContent("Rückmeldung & Kontakt") };
    private readonly Grid _completionOptions = new() { ColumnDefinitions = new("*,*"), ColumnSpacing = 24 };
    private Control? _coreImportSection, _feedbackSection;
    public CoreImportSelection? ImportedCoreSelection { get; private set; }

    private void InitializeCompletionOptions()
    {
        _completion.Children.Add(new Separator { Margin = new(0, 18, 0, 16) });
        _coreImportSection = CompletionOption("Gespeicherte Farben und Belegungen aus Glorious CORE übernehmen.", _coreImportButton);
        _feedbackSection = CompletionOption("Deine Rückmeldung hilft uns, INGLORIOUS Advanced zu verbessern.", _feedbackButton);
        _completionOptions.Children.Add(_coreImportSection); _completionOptions.Children.Add(_feedbackSection);
        _completion.Children.Add(_completionOptions);
        Avalonia.Automation.AutomationProperties.SetName(_coreImportButton, L.T("CORE-Einstellungen importieren"));
        Avalonia.Automation.AutomationProperties.SetName(_feedbackButton, L.T("Rückmeldung & Kontakt"));
        _coreImportButton.Click += async (_, _) =>
        {
            if (!LightingReady || _coreImportPreview is null || DeviceId is not { } id) return;
            var import = new CoreImportWindow(ProjectService.Create(id), _coreImportPreview);
            var copy = await import.ShowDialog<AppProject?>(this);
            if (copy is not null) ImportedCoreSelection = import.Selection;
        };
        _feedbackButton.Click += async (_, _) =>
        {
            if (!LightingReady || DeviceId is not { } id) return;
            await new DeviceFeedbackWindow(id, _runtimeVersion).ShowDialog(this);
        };
        _deviceTestButton.Click += async (_, _) =>
        {
            if (!LightingReady || _session is not { } session) return;
            _lightingRunning = true; Render();
            try
            {
                var path = await ReadVerifiedReturnedRuntimeAsync(session);
                var matches = (await Task.Run(OnboardClient.FindDevices)).Where(device => device.DeviceId == session.DeviceId && device.Path == path).ToArray();
                if (matches.Length != 1) throw new IOException("Returned device changed.");
                await new DeviceTestWindow(session.DeviceId!, matches[0]).ShowDialog(this);
            }
            catch { if (!_closed) _lightingStatus.Text = L.T("Der Gerätetest konnte nicht gestartet werden. Prüfe die USB-Verbindung."); }
            finally { _lightingRunning = false; if (!_closed) Render(); }
        };
    }

    private static Control CompletionOption(string description, Button button)
    {
        var section = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 10 };
        var introduction = Text(12, L.T(description)); introduction.Foreground = Palette("Muted");
        section.Children.Add(introduction);
        button.MinHeight = 38; button.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(button, 1); section.Children.Add(button);
        return section;
    }

    private async Task CaptureCoreImportAsync()
    {
        _coreImportPreview = null;
        if (_offlinePreview || _operation != SetupOperation.Install || DeviceId is not { } id || CoreSettingsImport.DefaultFolder is not { } folder || !Directory.Exists(folder)) return;
        try { _coreImportPreview = await Task.Run(() => CoreSettingsImport.ReadFolder(folder, id)); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { }
    }

    private void RenderCompletionOptions()
    {
        var hasCoreImport = _coreImportPreview is not null;
        _coreImportButton.IsVisible = hasCoreImport;
        if (_coreImportSection is not null) _coreImportSection.IsVisible = hasCoreImport;
        if (_feedbackSection is not null)
        {
            Grid.SetColumn(_feedbackSection, hasCoreImport ? 1 : 0);
            Grid.SetColumnSpan(_feedbackSection, hasCoreImport ? 1 : 2);
        }
        _deviceTestButton.IsVisible = DeviceId is { } id && DeviceCatalog.All.Any(device => device.Id == id && !device.IsOfflineOnly);
        _coreImportButton.IsEnabled = _deviceTestButton.IsEnabled = _feedbackButton.IsEnabled = !_offlinePreview && LightingReady;
    }
}
