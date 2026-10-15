using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

public sealed class DeviceFeedbackWindow : Window
{
    public DeviceFeedbackWindow(string deviceId, OnboardFirmwareVersion? version = null, DeviceFeedbackResult? initialResult = null)
    {
        var context = DeviceFeedbackContext.Create(deviceId, version);
        Title = L.T("Rückmeldung & Kontakt"); Width = 700; Height = 720; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        var body = new StackPanel { Margin = new Thickness(24) };
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var modelName = Text(context.DeviceName); modelName.FontSize = 18; modelName.FontWeight = FontWeights.SemiBold; body.Children.Add(modelName);
        body.Children.Add(Text(L.T("Modell und Versionen werden ergänzt. Keine Dateien oder Gerätekennungen.")));
        body.Children.Add(Text(L.T("GitHub-Rückmeldungen sind öffentlich. Prüfe den Text vor dem Absenden.")));
        var outcome = new ComboBox { Name = "FeedbackResult", ItemsSource = new[] { L.T("Bitte wählen"), L.T("Funktioniert gut"), L.T("Problem melden") }, SelectedIndex = initialResult is null ? 0 : (int)initialResult + 1, Margin = new Thickness(0, 0, 0, 12) };
        body.Children.Add(outcome);
        body.Children.Add(Text(L.T("Kommentar (optional)")));
        var comment = new TextBox { Name = "FeedbackComment", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 85, MaxLength = DeviceFeedbackDraft.MaximumCommentLength, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(comment, L.T("Kommentar (optional)"));
        body.Children.Add(comment);
        body.Children.Add(Text(L.T("Vorschau")));
        var preview = new TextBox { Name = "FeedbackPreview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 190, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(preview, L.T("Vorschau")); body.Children.Add(preview);
        var status = Text(""); status.Foreground = Brushes.IndianRed; body.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var github = new Button { Name = "OpenFeedbackGitHub", Content = L.T("Auf GitHub öffnen"), IsEnabled = false, Margin = new Thickness(0, 0, 10, 10) };
        var email = new Button { Name = "OpenFeedbackEmail", Content = L.T("E-Mail öffnen"), IsEnabled = false, Visibility = SupportContact.Email is null ? Visibility.Collapsed : Visibility.Visible, Margin = new Thickness(0, 0, 10, 10) };
        var close = new Button { Content = L.T("Schließen"), Margin = new Thickness(0, 0, 0, 10) }; close.Click += (_, _) => Close();
        actions.Children.Add(github); actions.Children.Add(email); actions.Children.Add(close); body.Children.Add(actions);
        DeviceFeedbackDraft? draft = null;
        void RefreshPreview()
        {
            status.Text = "";
            if (outcome.SelectedIndex < 1) { draft = null; github.IsEnabled = email.IsEnabled = false; preview.Visibility = Visibility.Collapsed; preview.Text = ""; return; }
            draft = DeviceFeedbackDraft.Create(context, (DeviceFeedbackResult)(outcome.SelectedIndex - 1), comment.Text);
            preview.Text = draft.Title + "\n\n" + draft.Body; preview.Visibility = Visibility.Visible; github.IsEnabled = true; email.IsEnabled = SupportContact.Email is not null;
        }
        outcome.SelectionChanged += (_, _) => RefreshPreview(); comment.TextChanged += (_, _) => RefreshPreview(); RefreshPreview();
        void Open(Uri? uri)
        {
            if (uri is null) return;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { status.Text = L.T("Der Link konnte nicht geöffnet werden. Prüfe deine Standard-App für Browser oder E-Mail."); }
        }
        github.Click += (_, _) => Open(draft?.GitHubUri); email.Click += (_, _) => Open(draft?.EmailUri);
        Content = new ScrollViewer { Background = Background, Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
