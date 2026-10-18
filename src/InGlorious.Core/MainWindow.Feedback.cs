using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _feedbackInvitationOpen;
    private (string DeviceId, OnboardFirmwareVersion? Version)? _pendingSetupFeedback;

    private void OfferFeedbackAfterSetup(string deviceId, OnboardFirmwareVersion? version, Window? owner)
    {
        if (owner is AppSettingsWindow)
        {
            // Settings holds an optimistic edit session. Persist the prompt marker only after it closes.
            _pendingSetupFeedback = (deviceId, version);
            return;
        }
        MaybeOfferExperimentalFeedback(deviceId, DeviceFeedbackTrigger.Installation, version);
    }

    private void OfferPendingSetupFeedback()
    {
        var pending = _pendingSetupFeedback;
        _pendingSetupFeedback = null;
        if (pending is { } installed)
            MaybeOfferExperimentalFeedback(installed.DeviceId, DeviceFeedbackTrigger.Installation, installed.Version);
    }

    private void MaybeOfferExperimentalFeedback(string deviceId, DeviceFeedbackTrigger trigger, OnboardFirmwareVersion? version = null)
    {
        if (_screenshotMode || _startupClosed || _readingDevice || _checkingFirmwareGuide || _feedbackInvitationOpen || !IsVisible) return;
        _feedbackInvitationOpen = true;
        try
        {
            if (!ExperimentalDeviceFeedback.TryMarkOffered(deviceId, trigger, succeeded: true, offline: _screenshotMode, out var saved)) return;
            _preferences = saved!;
            var dialog = CreateExperimentalFeedbackInvitation(DeviceCatalog.Get(deviceId));
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && !_startupClosed)
                new DeviceFeedbackWindow(deviceId, version) { Owner = this }.ShowDialog();
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Feedback invitation skipped: {0}", error); }
        finally { _feedbackInvitationOpen = false; }
    }

    internal static Window CreateExperimentalFeedbackInvitation(DeviceDefinition device)
    {
        var dialog = new Window
        {
            Title = L.T("Rückmeldung & Kontakt"), Width = 560, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("WindowBackground"), Foreground = (Brush)Application.Current.FindResource("Text")
        };
        var body = new StackPanel { Margin = new(24) };
        body.Children.Add(new TextBlock { Text = device.Name, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = DeviceFeedbackContext.Invitation(device), Foreground = (Brush)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 24), LineHeight = 21 });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var later = new Button { Name = "FeedbackLater", Content = L.T("Später"), IsCancel = true, IsDefault = true, MinWidth = 90, Margin = new(0, 0, 10, 0) };
        var feedback = new Button { Name = "GiveFeedback", Content = L.T("Rückmeldung geben"), MinWidth = 120, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        later.Click += (_, _) => dialog.Close();
        feedback.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(later); actions.Children.Add(feedback); body.Children.Add(actions); dialog.Content = body;
        return dialog;
    }

    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        _firmwareAssessments.TryGetValue(Device.Id, out var assessment);
        new DeviceFeedbackWindow(Device.Id, assessment?.RuntimeVersion?.Report) { Owner = this }.ShowDialog();
    }
}
