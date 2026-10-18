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
        Title = L.T("Rückmeldung & Kontakt");
        var area = SystemParameters.WorkArea;
        Width = Math.Min(area.Height < 640 ? 1060 : 960, area.Width); Height = Math.Min(720, area.Height);
        MinWidth = Math.Min(760, area.Width); MinHeight = Math.Min(600, area.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        FontFamily = new("Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
        var heading = new StackPanel { Margin = new(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = context.DeviceName, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        heading.Children.Add(Text(L.T("Modell, Versionen und System werden ergänzt. Persönliche Dateien und Gerätekennungen bleiben ausgeschlossen.")));
        heading.Children.Add(Text(L.T("GitHub-Rückmeldungen sind öffentlich. Prüfe den Text vor dem Absenden.")));
        root.Children.Add(heading);
        var body = new Grid(); body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new()); Grid.SetRow(body, 1); root.Children.Add(body);
        var form = new StackPanel { Margin = new(0, 0, 16, 0) };
        body.Children.Add(new ScrollViewer { Name = "FeedbackFormScroll", Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var outcome = new ComboBox { Name = "FeedbackResult", ItemsSource = new[] { L.T("Bitte wählen"), L.T("Funktioniert gut"), L.T("Problem melden") }, SelectedIndex = initialResult is null ? 0 : (int)initialResult + 1, Margin = new(0, 0, 0, 8) };
        form.Children.Add(outcome);
        TextBox Field(StackPanel parent, string label, string name, int limit, double height)
        {
            var text = Text(L.T(label)); text.Margin = new(0, 0, 0, 4); parent.Children.Add(text);
            var input = new TextBox { Name = name, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, MaxLength = limit, Margin = new(0, 0, 0, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(input, L.T(label)); parent.Children.Add(input); return input;
        }
        var comment = Field(form, "Kommentar (optional)", "FeedbackComment", DeviceFeedbackDraft.MaximumCommentLength, 58);
        var detailsPanel = new StackPanel { Name = "FeedbackProblemDetails", Visibility = Visibility.Collapsed }; form.Children.Add(detailsPanel);
        var steps = Field(detailsPanel, "Schritte zum Problem", "FeedbackSteps", DeviceFeedbackDetails.MaximumFieldLength, 44);
        var expected = Field(detailsPanel, "Erwartetes Verhalten", "FeedbackExpected", DeviceFeedbackDetails.MaximumFieldLength, 44);
        var actual = Field(detailsPanel, "Tatsächliches Verhalten", "FeedbackActual", DeviceFeedbackDetails.MaximumFieldLength, 44);
        var report = new Grid(); report.RowDefinitions.Add(new() { Height = GridLength.Auto }); report.RowDefinitions.Add(new()); Grid.SetColumn(report, 1); body.Children.Add(report);
        var previewLabel = Text(L.T("Vorschau")); previewLabel.Margin = new(0, 0, 0, 6); report.Children.Add(previewLabel);
        var preview = new TextBox { Name = "FeedbackPreview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(preview, L.T("Vorschau")); Grid.SetRow(preview, 1); report.Children.Add(preview);
        var footer = new StackPanel { Margin = new(0, 10, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        var status = Text(""); status.Foreground = (Brush)FindResource("Accent"); status.Margin = new(0, 0, 0, 6); footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        Button Action(string name, string label) => new() { Name = name, Content = L.T(label), IsEnabled = false, Margin = new(0, 0, 8, 0) };
        var github = Action("OpenFeedbackGitHub", "Auf GitHub öffnen"); var email = Action("OpenFeedbackEmail", "E-Mail öffnen");
        email.Visibility = SupportContact.Email is null ? Visibility.Collapsed : Visibility.Visible;
        var copy = Action("CopyFeedbackReport", "Bericht kopieren");
        var close = new Button { Content = L.T("Schließen") }; close.Click += (_, _) => Close();
        actions.Children.Add(github); actions.Children.Add(email); actions.Children.Add(copy); actions.Children.Add(close);
        DeviceFeedbackDraft? draft = null;
        void RefreshPreview()
        {
            status.Text = ""; status.Visibility = Visibility.Collapsed;
            detailsPanel.Visibility = outcome.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (outcome.SelectedIndex < 1) { draft = null; github.IsEnabled = email.IsEnabled = copy.IsEnabled = false; preview.Visibility = Visibility.Collapsed; preview.Text = ""; return; }
            draft = DeviceFeedbackDraft.Create(context, (DeviceFeedbackResult)(outcome.SelectedIndex - 1), comment.Text, new(steps.Text, expected.Text, actual.Text));
            preview.Text = draft.Title + "\n\n" + draft.Body; preview.Visibility = Visibility.Visible;
            github.IsEnabled = copy.IsEnabled = true; email.IsEnabled = SupportContact.Email is not null;
            if (draft.GitHubUri.AbsoluteUri.Length > 6000 || draft.EmailUri?.AbsoluteUri.Length > 6000)
            { status.Text = L.T("Der Bericht ist zu lang für einen Link. Kopiere ihn und füge ihn in GitHub oder deine E-Mail ein."); status.Visibility = Visibility.Visible; }
        }
        outcome.SelectionChanged += (_, _) => RefreshPreview();
        foreach (var input in new[] { comment, steps, expected, actual }) input.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();
        void Open(Uri? uri)
        {
            if (uri is null) return;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { status.Text = L.T("Der Link konnte nicht geöffnet werden. Prüfe deine Standard-App für Browser oder E-Mail."); status.Visibility = Visibility.Visible; }
        }
        github.Click += (_, _) => { if (draft is not null) Open(draft.GitHubUri.AbsoluteUri.Length <= 6000 ? draft.GitHubUri : new Uri(SupportContact.IssueUrl)); };
        email.Click += (_, _) => { if (draft?.EmailUri is { } uri) Open(uri.AbsoluteUri.Length <= 6000 ? uri : SupportContact.EmailUri); };
        copy.Click += (_, _) =>
        {
            if (draft is null) return;
            try { Clipboard.SetText(draft.Title + "\n\n" + draft.Body); }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = L.T("Kopieren nicht möglich. Markiere und kopiere den Text in der Vorschau."); status.Visibility = Visibility.Visible; }
        };
        var surface = new Border { Background = Background, Child = root };
        surface.SizeChanged += (_, args) => root.Margin = new(args.NewSize.Height < 560 ? 12 : 20);
        Content = surface;
    }
}
