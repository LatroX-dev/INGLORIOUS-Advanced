using System.Windows;

namespace InGlorious.Core;

public partial class MainWindow
{
    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        _firmwareAssessments.TryGetValue(Device.Id, out var assessment);
        new DeviceFeedbackWindow(Device.Id, assessment?.RuntimeVersion?.Report) { Owner = this }.ShowDialog();
    }
}
