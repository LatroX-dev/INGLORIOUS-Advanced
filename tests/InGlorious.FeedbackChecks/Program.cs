using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InGlorious.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static T Find<T>(DependencyObject parent, string name) where T : FrameworkElement => Descendants(parent).OfType<T>().Single(x => x.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new App(); app.InitializeComponent(); // Resources only; never starts the application or hardware services.
        L.SetLanguage("en");
        var id = "gmmk3-pro-100-iso";
        var context = DeviceFeedbackContext.Create(id, new(id, new(0, 11, 2), 0x000B0201));
        Check(context.DeviceId == id && context.DeviceName == DeviceCatalog.Get(id).Name && context.FirmwareVersion == "0.11.2", "exact model and reported version");
        Check(DeviceFeedbackContext.Create(id).FirmwareVersion == "Not checked", "unknown firmware is never inferred from available release");
        Check(DeviceFeedbackContext.Create(id, new("gmmk3-100-iso", new(0, 11, 2), 1)).FirmwareVersion == "Not checked", "foreign version is ignored");
        Check(typeof(DeviceFeedbackContext).GetProperties().Select(p => p.Name).Order().SequenceEqual(new[] { "AppVersion", "DeviceId", "DeviceName", "FirmwareVersion", "OperatingSystem" }), "metadata whitelist excludes identifiers and user data");
        Check(context.AppVersion.Length > 0 && context.OperatingSystem.Length > 0, "app and OS version are populated");
        foreach (var model in DeviceCatalog.All)
            Check(DeviceFeedbackContext.Create(model.Id).DeviceName == model.Name, "exact feedback name " + model.Id);
        var comment = "Colors & keys? #1\n日本語 / ü + 100%";
        var draft = DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Problem, comment);
        var query = draft.GitHubUri.Query.TrimStart('?').Split('&').Select(item => item.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        Check(draft.GitHubUri.Scheme == "https" && draft.GitHubUri.Host == "github.com" && draft.GitHubUri.AbsolutePath.EndsWith("/INGLORIOUS-Advanced/issues/new", StringComparison.Ordinal), "fixed GitHub destination");
        Check(query.Count == 3 && query["body"] == draft.Body && query["title"] == draft.Title && query["template"] == "device-feedback.md", "URI preserves exactly the reviewed draft, including Unicode");
        Check(draft.Body.Contains(comment) && draft.Body.Contains("Result: Problem"), "comment and outcome retained");
        Check(DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Success, null).Body.EndsWith("No comment."), "comment remains optional");
        Check(DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Success, "a\r\nb\rc").Body.EndsWith("a\nb\nc"), "newlines normalized");
        foreach (var invalid in new[] { "", "not-an-email", "Name <test@example.org>", "test@example.org?bcc=other@example.org", "test@example.org\r\nBcc:other@example.org", "test@example.org#bad", "mailto:test@example.org", " test@example.org", "test@example.org ", "test@localhost" })
            Check(SupportContact.ValidateEmail(invalid) is null && draft.CreateEmailUri(invalid) is null, "unsafe/unconfigured email rejected");
        Check(SupportContact.ValidateEmail("support+feedback@example.org") == "support+feedback@example.org", "valid public mailbox accepted");
        var email = draft.CreateEmailUri("support@example.org")!;
        Check(email.Scheme == "mailto" && Uri.UnescapeDataString(email.Query).Contains(draft.Body), "email uses same reviewed text");
        try { DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Success, new('x', DeviceFeedbackDraft.MaximumCommentLength + 1)); throw new Exception("oversize comment accepted"); } catch (ArgumentException) { _checks++; }
        try { DeviceFeedbackDraft.Create(context, (DeviceFeedbackResult)42, ""); throw new Exception("invalid outcome accepted"); } catch (ArgumentOutOfRangeException) { _checks++; }
        Check(SupportContact.PublicEmail == "inglorious@latrox.dev" && SupportContact.EmailEnabled && SupportContact.Email == SupportContact.PublicEmail, "configured public contact is enabled without exposing forwarding destination");
        Check(SupportContact.EmailUri is { Scheme: "mailto", Query.Length: 0, Fragment.Length: 0 } && SupportContact.EmailUri.AbsoluteUri == "mailto:" + SupportContact.Email,
            "general contact opens only the public address without metadata or prefilled content");
        var settings = new AppSettingsWindow(new AppPreferences(), offlinePreview: true, connectedDevices: []);
        var contactButton = Find<Button>(settings, "ContactEmailButton");
        Check(contactButton.Visibility == Visibility.Visible && !contactButton.IsEnabled && (string)contactButton.ToolTip == SupportContact.Email,
            "settings contact remains visible without a keyboard; offline checks cannot open external apps");
        settings.Close();
        foreach (var source in new[] { "Rückmeldung & Kontakt", "Bitte wählen", "Funktioniert gut", "Problem melden", "Kommentar (optional)", "Auf GitHub öffnen", "E-Mail öffnen", "Modell und Versionen werden ergänzt. Keine Dateien oder Gerätekennungen.", "GitHub-Rückmeldungen sind öffentlich. Prüfe den Text vor dem Absenden.", "Der Link konnte nicht geöffnet werden. Prüfe deine Standard-App für Browser oder E-Mail." })
            foreach (var language in L.SupportedLanguages.Where(l => l.Code != "de"))
                Check(L.Catalog.TryGetValue(source, out var translations) && !string.IsNullOrWhiteSpace(translations.GetValueOrDefault(language.Code)), "translated feedback " + language.Code);

        var window = new DeviceFeedbackWindow(id);
        var outcome = Find<ComboBox>(window, "FeedbackResult"); var input = Find<TextBox>(window, "FeedbackComment");
        var preview = Find<TextBox>(window, "FeedbackPreview");
        var githubButton = Find<Button>(window, "OpenFeedbackGitHub"); var emailButton = Find<Button>(window, "OpenFeedbackEmail");
        Check(outcome.SelectedIndex == 0 && !githubButton.IsEnabled && !emailButton.IsEnabled, "no default success or destination launch before preview");
        Check(emailButton.Visibility == (SupportContact.Email is null ? Visibility.Collapsed : Visibility.Visible), "email visibility follows validated public contact");
        outcome.SelectedIndex = 2; input.Text = "A key does not light up.";
        Check(githubButton.IsEnabled && preview.IsReadOnly && preview.Text.Contains("Firmware: Not checked") && preview.Text.Contains(input.Text), "complete preview precedes explicit external action");
        input.Text = "Changed comment";
        Check(preview.Text.Contains("Changed comment") && !preview.Text.Contains("A key does not light up."), "comment edit refreshes exact draft immediately");
        outcome.SelectedIndex = 0;
        Check(!githubButton.IsEnabled && !emailButton.IsEnabled && preview.Text == "", "clearing outcome clears draft and disables external actions");
        outcome.SelectedIndex = 1;
        Check(preview.Text.Contains("Result: Working well") && !preview.Text.Contains("Result: Problem"), "new preview includes updated outcome");
        using var rendered = new MemoryStream();
        var visual = (FrameworkElement)window.Content; visual.Measure(new Size(660, 900)); visual.Arrange(new Rect(0, 0, 660, 900)); visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(660, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(rendered);
        Check(rendered.Length > 1000, "offline feedback panel renders");
        if (args.Length == 2 && args[0] == "--preview") File.WriteAllBytes(Path.GetFullPath(args[1]), rendered.ToArray());
        window.Close();
        var completed = new DeviceFeedbackWindow(id, new(id, new(0, 11, 2), 1), DeviceFeedbackResult.Success);
        Check(Find<ComboBox>(completed, "FeedbackResult").SelectedIndex == 1 && Find<TextBox>(completed, "FeedbackPreview").Text.Contains("Firmware: 0.11.2"), "success screen renders exact preview before deliberate opening"); completed.Close();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, checks = _checks, hardwareAccess = false, networkAccess = false, externalLaunches = 0 }));
    }
}
