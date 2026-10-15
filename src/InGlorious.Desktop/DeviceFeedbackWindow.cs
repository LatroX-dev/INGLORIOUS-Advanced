using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed class DeviceFeedbackWindow : Window
{
    public DeviceFeedbackWindow(string deviceId, OnboardFirmwareVersion? version = null, DeviceFeedbackResult? initialResult = null)
    {
        var context = DeviceFeedbackContext.Create(deviceId, version);
        Title = L.T("Rückmeldung & Kontakt"); Width = 700; Height = 720; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
        var modelName = Text(context.DeviceName); modelName.FontSize = 18; modelName.FontWeight = FontWeight.SemiBold; body.Children.Add(modelName);
        body.Children.Add(Text(L.T("Modell und Versionen werden ergänzt. Keine Dateien oder Gerätekennungen.")));
        body.Children.Add(Text(L.T("GitHub-Rückmeldungen sind öffentlich. Prüfe den Text vor dem Absenden.")));
        var outcome = new ComboBox { Name = "FeedbackResult", ItemsSource = new[] { L.T("Bitte wählen"), L.T("Funktioniert gut"), L.T("Problem melden") }, SelectedIndex = initialResult is null ? 0 : (int)initialResult + 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(outcome);
        body.Children.Add(Text(L.T("Kommentar (optional)")));
        var comment = new TextBox { Name = "FeedbackComment", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 85, MaxLength = DeviceFeedbackDraft.MaximumCommentLength };
        Avalonia.Automation.AutomationProperties.SetName(comment, L.T("Kommentar (optional)")); body.Children.Add(comment);
        body.Children.Add(Text(L.T("Vorschau")));
        var preview = new TextBox { Name = "FeedbackPreview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 190, MaxHeight = 250, IsVisible = false };
        Avalonia.Automation.AutomationProperties.SetName(preview, L.T("Vorschau")); body.Children.Add(preview);
        var status = Text(""); status.Foreground = Brushes.IndianRed; body.Children.Add(status);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var github = new Button { Name = "OpenFeedbackGitHub", Content = L.T("Auf GitHub öffnen"), IsEnabled = false, Margin = new Thickness(0, 0, 10, 10) };
        var email = new Button { Name = "OpenFeedbackEmail", Content = L.T("E-Mail öffnen"), IsEnabled = false, IsVisible = SupportContact.Email is not null, Margin = new Thickness(0, 0, 10, 10) };
        var close = new Button { Content = L.T("Schließen"), Margin = new Thickness(0, 0, 0, 10) }; close.Click += (_, _) => Close();
        actions.Children.Add(github); actions.Children.Add(email); actions.Children.Add(close); body.Children.Add(actions);
        DeviceFeedbackDraft? draft = null;
        void RefreshPreview()
        {
            status.Text = "";
            if (outcome.SelectedIndex < 1) { draft = null; github.IsEnabled = email.IsEnabled = false; preview.IsVisible = false; preview.Text = ""; return; }
            draft = DeviceFeedbackDraft.Create(context, (DeviceFeedbackResult)(outcome.SelectedIndex - 1), comment.Text);
            preview.Text = draft.Title + "\n\n" + draft.Body; preview.IsVisible = true; github.IsEnabled = true; email.IsEnabled = SupportContact.Email is not null;
        }
        outcome.SelectionChanged += (_, _) => RefreshPreview(); comment.TextChanged += (_, _) => RefreshPreview(); RefreshPreview();
        async Task OpenAsync(Uri? uri)
        {
            if (uri is null) return;
            try { if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException(); }
            catch { status.Text = L.T("Der Link konnte nicht geöffnet werden. Prüfe deine Standard-App für Browser oder E-Mail."); }
        }
        github.Click += async (_, _) => await OpenAsync(draft?.GitHubUri); email.Click += async (_, _) => await OpenAsync(draft?.EmailUri);
        Content = new ScrollViewer { Content = body };
    }
}
