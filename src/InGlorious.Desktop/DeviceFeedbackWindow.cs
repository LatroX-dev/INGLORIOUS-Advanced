using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed class DeviceFeedbackWindow : Window
{
    public DeviceFeedbackWindow(string deviceId, OnboardFirmwareVersion? version = null, DeviceFeedbackResult? initialResult = null)
    {
        var context = DeviceFeedbackContext.Create(deviceId, version);
        Title = L.T("Rückmeldung & Kontakt"); Width = 960; Height = 720; MinWidth = 760; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Brush.Parse("#141618"); Foreground = Brush.Parse("#E4E7EB");
        FontFamily = new("Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
        var heading = new StackPanel { Margin = new(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = context.DeviceName, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new(0, 0, 0, 6) });
        heading.Children.Add(Text(L.T("Modell, Versionen und System werden ergänzt. Persönliche Dateien und Gerätekennungen bleiben ausgeschlossen.")));
        heading.Children.Add(Text(L.T("GitHub-Rückmeldungen sind öffentlich. Prüfe den Text vor dem Absenden.")));
        root.Children.Add(heading);
        var body = new Grid { Name = "FeedbackBody" }; body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new()); Grid.SetRow(body, 1); root.Children.Add(body);
        var form = new StackPanel();
        var formScroll = new ScrollViewer { Name = "FeedbackFormScroll", Content = form, Margin = new(0, 0, 16, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(formScroll);
        var outcome = new ComboBox { Name = "FeedbackResult", ItemsSource = new[] { L.T("Bitte wählen"), L.T("Funktioniert gut"), L.T("Problem melden") }, SelectedIndex = initialResult is null ? 0 : (int)initialResult + 1, Margin = new(0, 0, 0, 8) };
        form.Children.Add(outcome);
        TextBox Field(StackPanel parent, string label, string name, int limit, double height)
        {
            var text = Text(L.T(label)); text.Margin = new(0, 0, 0, 4); parent.Children.Add(text);
            var input = new TextBox { Name = name, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, MaxLength = limit, Margin = new(0, 0, 0, 8) };
            Avalonia.Automation.AutomationProperties.SetName(input, L.T(label)); parent.Children.Add(input); return input;
        }
        var comment = Field(form, "Kommentar (optional)", "FeedbackComment", DeviceFeedbackDraft.MaximumCommentLength, 58);
        var detailsPanel = new StackPanel { Name = "FeedbackProblemDetails", IsVisible = false }; form.Children.Add(detailsPanel);
        var steps = Field(detailsPanel, "Schritte zum Problem", "FeedbackSteps", DeviceFeedbackDetails.MaximumFieldLength, 44);
        var expected = Field(detailsPanel, "Erwartetes Verhalten", "FeedbackExpected", DeviceFeedbackDetails.MaximumFieldLength, 44);
        var actual = Field(detailsPanel, "Tatsächliches Verhalten", "FeedbackActual", DeviceFeedbackDetails.MaximumFieldLength, 44);
        var report = new Grid(); report.RowDefinitions.Add(new() { Height = GridLength.Auto }); report.RowDefinitions.Add(new()); Grid.SetColumn(report, 1); body.Children.Add(report);
        var previewLabel = Text(L.T("Vorschau")); previewLabel.Margin = new(0, 0, 0, 6); report.Children.Add(previewLabel);
        var preview = new TextBox { Name = "FeedbackPreview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Avalonia.Automation.AutomationProperties.SetName(preview, L.T("Vorschau")); Grid.SetRow(preview, 1); report.Children.Add(preview);
        var footer = new StackPanel { Name = "FeedbackFooter", Margin = new(0, 10, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        var status = Text(""); status.Name = "FeedbackStatus"; status.Foreground = Brush.Parse("#EF646B"); status.Margin = new(0, 0, 0, 6); footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        Button Action(string name, string label) => new() { Name = name, Content = L.T(label), IsEnabled = false, Margin = new(0, 0, 8, 0) };
        var github = Action("OpenFeedbackGitHub", "Auf GitHub öffnen"); var email = Action("OpenFeedbackEmail", "E-Mail öffnen");
        email.IsVisible = SupportContact.Email is not null;
        var copy = Action("CopyFeedbackReport", "Bericht kopieren");
        var close = new Button { Name = "CloseFeedback", Content = L.T("Schließen") }; close.Click += (_, _) => Close();
        actions.Children.Add(github); actions.Children.Add(email); actions.Children.Add(copy); actions.Children.Add(close);
        DeviceFeedbackDraft? draft = null;
        void RefreshPreview()
        {
            status.Text = ""; status.IsVisible = false;
            detailsPanel.IsVisible = outcome.SelectedIndex == 2;
            if (outcome.SelectedIndex < 1) { draft = null; github.IsEnabled = email.IsEnabled = copy.IsEnabled = false; preview.IsVisible = false; preview.Text = ""; return; }
            draft = DeviceFeedbackDraft.Create(context, (DeviceFeedbackResult)(outcome.SelectedIndex - 1), comment.Text, new(steps.Text ?? "", expected.Text ?? "", actual.Text ?? ""));
            preview.Text = draft.Title + "\n\n" + draft.Body; preview.IsVisible = true;
            github.IsEnabled = copy.IsEnabled = true; email.IsEnabled = SupportContact.Email is not null;
            if (draft.GitHubUri.AbsoluteUri.Length > 6000 || draft.EmailUri?.AbsoluteUri.Length > 6000)
            { status.Text = L.T("Der Bericht ist zu lang für einen Link. Kopiere ihn und füge ihn in GitHub oder deine E-Mail ein."); status.IsVisible = true; }
        }
        outcome.SelectionChanged += (_, _) => RefreshPreview();
        foreach (var input in new[] { comment, steps, expected, actual }) input.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();
        async Task OpenAsync(Uri? uri)
        {
            if (uri is null) return;
            try { if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException(); }
            catch { status.Text = L.T("Der Link konnte nicht geöffnet werden. Prüfe deine Standard-App für Browser oder E-Mail."); status.IsVisible = true; }
        }
        github.Click += async (_, _) => { if (draft is not null) await OpenAsync(draft.GitHubUri.AbsoluteUri.Length <= 6000 ? draft.GitHubUri : new Uri(SupportContact.IssueUrl)); };
        email.Click += async (_, _) => { if (draft?.EmailUri is { } uri) await OpenAsync(uri.AbsoluteUri.Length <= 6000 ? uri : SupportContact.EmailUri); };
        copy.Click += async (_, _) =>
        {
            if (draft is null) return;
            try { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard is null) throw new InvalidOperationException(); await clipboard.SetTextAsync(draft.Title + "\n\n" + draft.Body); }
            catch (Exception) { status.Text = L.T("Kopieren nicht möglich. Markiere und kopiere den Text in der Vorschau."); status.IsVisible = true; }
        };
        var frame = new Border { Background = Background, Child = root };
        frame.SizeChanged += (_, _) =>
        {
            var compact = frame.Bounds.Height < 560;
            root.Margin = new(compact ? 12 : 20);
            heading.Margin = new(0, 0, 0, compact ? 8 : 12);
            comment.Height = compact ? 48 : 58;
            foreach (var input in new[] { steps, expected, actual }) input.Height = compact ? 36 : 44;
        };
        Content = frame;
        Opened += (_, _) => FitAvailableWorkArea();
    }

    private void FitAvailableWorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var availableWidth = Math.Max(1, Math.Floor(screen.WorkingArea.Width / screen.Scaling) - 32);
        var availableHeight = Math.Max(1, Math.Floor(screen.WorkingArea.Height / screen.Scaling) - 40);
        var constrained = Width > availableWidth || Height > availableHeight;
        MinWidth = Math.Min(760, availableWidth); MinHeight = Math.Min(600, availableHeight);
        Width = Math.Min(availableWidth, availableHeight < 600 ? 1060 : Width);
        Height = Math.Min(Height, availableHeight);
        if (constrained)
            Position = new PixelPoint(screen.WorkingArea.X + Math.Max(0, (int)((screen.WorkingArea.Width - Width * screen.Scaling) / 2)),
                screen.WorkingArea.Y + Math.Max(0, (int)((screen.WorkingArea.Height - (Height + 32) * screen.Scaling) / 2)));
    }
}
