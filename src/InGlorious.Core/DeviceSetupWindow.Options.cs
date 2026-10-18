using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

public sealed partial class DeviceSetupWindow
{
    private CoreImportPreview? _coreImportPreview;
    private readonly Button _coreImportButton = new() { Content = L.T("CORE-Einstellungen importieren"), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
    private readonly Button _deviceTestButton = new() { Content = L.T("Gerät testen"), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
    private readonly Button _feedbackButton = new() { Content = L.T("Rückmeldung & Kontakt"), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
    private readonly Grid _coreImportSection = new(), _feedbackSection = new();
    private readonly TextBlock _feedbackHint = Text(12);
    public CoreImportSelection? ImportedCoreSelection { get; private set; }

    private void InitializeCompletionOptions()
    {
        UiIcons.SetKind(_deviceTestButton, "keyboard"); UiIcons.SetKind(_coreImportButton, "import"); UiIcons.SetKind(_feedbackButton, "message");
        var actions = new Grid { Margin = new(0, 12, 0, 0) };
        actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
        Grid.SetIsSharedSizeScope(actions, true);
        void AddSection(Grid section, string description, Button button)
        {
            section.RowDefinitions.Add(new() { Height = GridLength.Auto, SharedSizeGroup = "CompletionOptionHint" });
            section.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var hint = ReferenceEquals(section, _feedbackSection) ? _feedbackHint : Text(12);
            hint.Text = L.T(description); hint.Foreground = (Brush)FindResource("Muted"); hint.Margin = new(0, 0, 0, 6); hint.LineHeight = 17;
            section.Children.Add(hint); Grid.SetRow(button, 1); section.Children.Add(button); actions.Children.Add(section);
        }
        _coreImportSection.Margin = new(0, 0, 24, 0);
        AddSection(_coreImportSection, "Gespeicherte Farben und Belegungen aus Glorious CORE übernehmen.", _coreImportButton);
        AddSection(_feedbackSection, "Deine Rückmeldung hilft uns, INGLORIOUS Advanced zu verbessern.", _feedbackButton);
        _completion.Children.Add(new Border { BorderBrush = (Brush)FindResource("Stroke"), BorderThickness = new(0, 1, 0, 0), Margin = new(0, 12, 0, 0), Child = actions });
        _coreImportButton.Click += (_, _) =>
        {
            if (!LightingReady || _coreImportPreview is null || DeviceId is not { } id) return;
            var import = new CoreImportWindow(ProjectService.Create(id), _coreImportPreview) { Owner = this };
            if (import.ShowDialog() == true) ImportedCoreSelection = import.Selection;
        };
        _feedbackButton.Click += (_, _) =>
        {
            if (!LightingReady || DeviceId is not { } id) return;
            new DeviceFeedbackWindow(id, _runtimeVersion) { Owner = this }.ShowDialog();
        };
        _deviceTestButton.Click += async (_, _) =>
        {
            if (!LightingReady || _session is not { } session) return;
            _lightingRunning = true; Render();
            try
            {
                var root = await ReadVerifiedReturnedRuntimeAsync(session);
                var matches = (await Task.Run(OnboardClient.FindDevices)).Where(device => device.DeviceId == session.DeviceId &&
                    string.Equals(device.UsbInstanceId, root, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new IOException("Returned device changed.");
                new DeviceTestWindow(session.DeviceId!, matches[0]) { Owner = this }.ShowDialog();
            }
            catch { if (!_closed) _lightingStatus.Text = L.T("Der Gerätetest konnte nicht gestartet werden. Prüfe die USB-Verbindung."); }
            finally { _lightingRunning = false; if (!_closed) Render(); }
        };
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
        _feedbackHint.Text = DeviceCatalog.All.FirstOrDefault(model => model.Id == DeviceId) is { } model
            ? DeviceFeedbackContext.Invitation(model) : L.T("Deine Rückmeldung hilft uns, INGLORIOUS Advanced zu verbessern.");
        _coreImportButton.Visibility = _coreImportPreview is null ? Visibility.Collapsed : Visibility.Visible;
        _coreImportSection.Visibility = _coreImportButton.Visibility;
        Grid.SetColumn(_feedbackSection, _coreImportPreview is null ? 0 : 1);
        Grid.SetColumnSpan(_feedbackSection, _coreImportPreview is null ? 2 : 1);
        _deviceTestButton.Visibility = DeviceId is not null ? Visibility.Visible : Visibility.Collapsed;
        _coreImportButton.IsEnabled = _deviceTestButton.IsEnabled = _feedbackButton.IsEnabled = !_offlinePreview && LightingReady;
    }
}
