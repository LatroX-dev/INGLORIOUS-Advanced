using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private Control BuildDeviceSupportActions()
    {
        var hasDeviceTest = _state.Device.Id == RetainedLoaderLayout.DeviceId;
        var actions = new Grid { Name = "DeviceSupportActions", ColumnDefinitions = new(hasDeviceTest ? "*,*,*" : "*,*"), ColumnSpacing = 24 };
        var column = 0;
        if (hasDeviceTest)
        {
            var test = AsyncButton("Gerät testen", () => OpenDeviceTestAsync(_state.Device.Id)); test.Name = "DeviceTestButton";
            var testSection = DeviceActionSection("Tasten, Drehknopf und LEDs einzeln prüfen.", test);
            Grid.SetColumn(testSection, column++); actions.Children.Add(testSection);
        }
        var import = AsyncButton("CORE-Einstellungen importieren", ImportCoreSettingsAsync);
        var importSection = DeviceActionSection("Gespeicherte Farben und Belegungen aus Glorious CORE übernehmen.", import);
        Grid.SetColumn(importSection, column++); actions.Children.Add(importSection);
        var button = AsyncButton("Rückmeldung & Kontakt", OpenFeedbackAsync);
        button.Name = "DeviceFeedbackContact";
        var feedbackSection = DeviceActionSection("Deine Rückmeldung hilft uns, INGLORIOUS Advanced zu verbessern.", button);
        Grid.SetColumn(feedbackSection, column); actions.Children.Add(feedbackSection);
        return actions;
    }

    private Control DeviceActionSection(string description, Button button)
    {
        var section = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 10 };
        section.Children.Add(Text(L.T(description), true, 12));
        button.MinHeight = 38; button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        Grid.SetRow(button, 1); section.Children.Add(button);
        return section;
    }

    private Task OpenFeedbackAsync()
    {
        _firmwareAssessments.TryGetValue(_state.Device.Id, out var assessment);
        return new DeviceFeedbackWindow(_state.Device.Id, assessment?.Version).ShowDialog(this);
    }
}
