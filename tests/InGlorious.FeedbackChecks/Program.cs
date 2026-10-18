using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        foreach (var model in DeviceCatalog.All.Select(device => device.Id))
        {
            var testWindow = new DeviceTestWindow(model, null, preview: true);
            typeof(DeviceTestWindow).GetMethod("RunOfflineChecks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(testWindow, [new Action<bool, string>(Check)]);
            Check(!Descendants(testWindow).OfType<TextBox>().Any(), "key testing needs no input field for " + model);
            CheckRoutedTyping(testWindow, model);
            if (args.Length == 2 && args[0] == "--device-previews" && model is "numpad" or "pro-iso" or "gmmk3-pro-100-iso")
            {
                var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
                var panel = (FrameworkElement)testWindow.Content; panel.Measure(new Size(1012, 640)); panel.Arrange(new Rect(0, 0, 1012, 640)); panel.UpdateLayout();
                var image = new RenderTargetBitmap(1012, 640, 96, 96, PixelFormats.Pbgra32); image.Render(panel);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, "device-test-" + model + ".png")); encoder.Save(file);
            }
            testWindow.Close();
            CheckTypingStopped(testWindow, model);
        }
        foreach (var language in L.SupportedLanguages.Where(language => language.Code != "en"))
        foreach (var model in new[] { "numpad", "gmmk3-pro-100-iso" })
        {
            L.SetLanguage(language.Code);
            var testWindow = new DeviceTestWindow(model, null, preview: true);
            typeof(DeviceTestWindow).GetMethod("RunOfflineChecks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(testWindow, [new Action<bool, string>(Check)]);
            Check(!Descendants(testWindow).OfType<TextBox>().Any(), "localized key testing needs no input field: " + language.Code + " " + model);
            testWindow.Close();
        }
        L.SetLanguage("en");
        CheckCompletionInput();
        Check(DeviceTypingTest.KeyLabel("Home") == "Home" && DeviceTypingTest.KeyLabel("Left") == "←" &&
            DeviceTypingTest.KeyLabel("NumPad0") == "0" && DeviceTypingTest.KeyLabel("D7") == "7", "local key test labels navigation and digits");
        var pad = DeviceCatalog.Get("numpad");
        foreach (var pair in new[] { ("Home", "KC_P7"), ("Left", "KC_P4"), ("Clear", "KC_P5"), ("Insert", "KC_P0"), ("Delete", "KC_PDOT"), ("Return", "KC_PENT"), ("NumPad7", "KC_P7"), ("Add", "KC_PPLS") })
            Check(DeviceTypingTest.MatchingDisplayKeys(pad, pair.Item1).Single() == pad.Keys.Single(key => key.DefaultBinding == pair.Item2).Id, "standalone keypad OS hints follow standard Num Lock and operator legends: " + pair.Item1);
        foreach (var name in new[] { "D7", "Fn", "VolumeUp", "VolumeMute", "Unknown" })
            Check(DeviceTypingTest.MatchingDisplayKeys(pad, name).Count == 0, "keypad OS hints never invent physical source, Fn or knob hits: " + name);
        var full = DeviceCatalog.Get("gmmk3-pro-100-iso");
        var pause = full.Keys.Single(key => key.DefaultBinding == "KC_PAUS");
        Check(pause.DisplayLabel == "Pause" && pause.AccessibleLabel == "Pause" && DeviceTypingTest.KeyLabel("Pause") == "Pause" &&
            BindingCatalog.All.Single(binding => binding.Code == "KC_PAUS").Label.StartsWith("Pause", StringComparison.Ordinal) && L.T("Pause") == "Delay",
            "English Pause key legend, accessible name, input label and binding stay separate from macro delay");
        Check(DeviceTypingTest.MatchingDisplayKeys(full, "Home").Single() == full.Keys.Single(key => key.DefaultBinding == "KC_HOME").Id &&
            DeviceTypingTest.MatchingDisplayKeys(full, "Return").Count == 0, "full keyboards keep navigation separate and do not guess between two Enter keys");
        Check(DeviceTypingTest.MatchingDisplayKeys(full, "Oem5").Count == 0 && DeviceTypingTest.KeyLabel("Oem5") == L.T("Zeichentaste"), "OEM events have a readable generic label and never guess a layout position");
        Check(DeviceTypingTest.MatchingTextDisplayKeys(full, "#").Single() == full.Keys.Single(key => key.DisplayLabel == "#").Id &&
            DeviceTypingTest.MatchingTextDisplayKeys(full, "7").Count == 0 && DeviceTypingTest.MatchingTextDisplayKeys(full, "\r").Count == 0,
            "actual symbol input follows a unique displayed legend but never guesses duplicate digits or control input");
        L.SetLanguage("de");
        Check(DeviceTypingTest.KeyLabel("Home") == "Pos1" && DeviceTypingTest.KeyLabel("Y") == "Y", "local input labels translate functions without changing typed letters");
        foreach (var source in new[] { "Tipptest", "Drücke die Tasten, die du prüfen möchtest.",
            "Die Eingabe kann auch von anderen angeschlossenen Tastaturen kommen.", "Für Ziffern Num-Lock einschalten. Standardbelegung: Fn + 0.",
            "Zuletzt gedrückt: {0}", "Noch keine Taste gedrückt.", "Tasten und Eingaben prüfen.",
            "Gelb zeigt die empfangene Eingabe. Bei eigenen Belegungen kann die Position abweichen.", "Zeichentaste", "Pause-Taste" })
            foreach (var language in L.SupportedLanguages.Where(language => language.Code != "de"))
                Check(L.Catalog.TryGetValue(source, out var translated) && !string.IsNullOrWhiteSpace(translated.GetValueOrDefault(language.Code)), "typing-test language coverage " + language.Code);
        L.SetLanguage("en");
        var id = "gmmk3-pro-100-iso";
        var context = DeviceFeedbackContext.Create(id, new(id, new(0, 11, 2), 0x000B0201));
        Check(context.DeviceId == id && context.DeviceName == DeviceCatalog.Get(id).Name && context.FirmwareVersion == "0.11.2", "exact model and reported version");
        Check(DeviceFeedbackContext.Create(id).FirmwareVersion == "Not checked", "unknown firmware is never inferred from available release");
        Check(DeviceFeedbackContext.Create(id, new("gmmk3-100-iso", new(0, 11, 2), 1)).FirmwareVersion == "Not checked", "foreign version is ignored");
        Check(typeof(DeviceFeedbackContext).GetProperties().Select(p => p.Name).Order().SequenceEqual(new[] { "AppArchitecture", "AppVersion", "BundledFirmwareVersion", "DeviceId", "DeviceName", "FirmwareBuildId", "FirmwareVersion", "Layout", "OperatingSystem", "SupportStatus", "UiLanguage" }), "metadata whitelist excludes identifiers and user data");
        Check(context.Layout == "ISO" && context.SupportStatus == "Tested" && context.UiLanguage == "en" && context.FirmwareBuildId == "000B0201" &&
            context.BundledFirmwareVersion == DeviceSetupService.TargetFirmwareVersionFor(id) && context.AppArchitecture.Length > 0,
            "report distinguishes actual firmware identity from bundled target and includes layout, support and app architecture");
        Check(DeviceFeedbackContext.Create(id, new(id, new(0, 11, 2), 0)) is { FirmwareVersion: "Not checked", FirmwareBuildId: "Not checked" },
            "unverified zero-build firmware is not reported as installed");
        Check(context.AppVersion.Length > 0 && context.OperatingSystem.Length > 0, "app and OS version are populated");
        foreach (var model in DeviceCatalog.All)
        {
            var modelContext = DeviceFeedbackContext.Create(model.Id);
            Check(modelContext.DeviceName == model.Name, "exact feedback name " + model.Id);
            Check(modelContext.SupportStatus == (model.IsOfflineOnly ? "Offline profile only" : model.IsExperimentalFirmware ? "Experimental" : "Tested") &&
                modelContext.BundledFirmwareVersion == (model.IsOfflineOnly || !model.HasApprovedFirmwareManagement ? "Not available" : DeviceSetupService.TargetFirmwareVersionFor(model.Id)),
                "support and available firmware metadata reflect actual model capability " + model.Id);
            Check(DeviceFeedbackContext.Invitation(model) == L.T(model.IsExperimentalFirmware
                ? "Dein Modell wird experimentell unterstützt. Deine Rückmeldung zu Tasten und Beleuchtung hilft uns besonders."
                : "Deine Rückmeldung hilft uns, INGLORIOUS Advanced zu verbessern."),
                "feedback invitation uses the actual support status " + model.Id);
        }
        var comment = "Colors & keys? #1\n日本語 / ü + 100%";
        var draft = DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Problem, comment);
        var query = draft.GitHubUri.Query.TrimStart('?').Split('&').Select(item => item.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        Check(draft.GitHubUri.Scheme == "https" && draft.GitHubUri.Host == "github.com" && draft.GitHubUri.AbsolutePath.EndsWith("/INGLORIOUS-Advanced/issues/new", StringComparison.Ordinal), "fixed GitHub destination");
        Check(query.Count == 3 && query["body"] == draft.Body && query["title"] == draft.Title && query["template"] == "device-feedback.md", "URI preserves exactly the reviewed draft, including Unicode");
        Check(draft.Body.Contains(comment) && draft.Body.Contains("Result: Problem"), "comment and outcome retained");
        Check(DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Success, null).Body.EndsWith("No comment."), "comment remains optional");
        Check(DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Success, "a\r\nb\rc").Body.EndsWith("a\nb\nc"), "newlines normalized");
        var details = new DeviceFeedbackDetails(" Open app\r\nPress key ", "Red\rBlue", "No light");
        var detailed = DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Problem, null, details);
        Check(detailed.Body.Contains("## Steps to reproduce\n\nOpen app\nPress key") && detailed.Body.Contains("## Expected behavior\n\nRed\nBlue") &&
            detailed.Body.Contains("## Actual behavior\n\nNo light"), "optional problem details are normalized and represented in the reviewed report");
        Check(!DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Success, null, details).Body.Contains("## Steps to reproduce"), "success feedback stays concise without problem sections");
        foreach (var oversize in new[] { new DeviceFeedbackDetails(new('x', 501)), new DeviceFeedbackDetails(Expected: new('x', 501)), new DeviceFeedbackDetails(Actual: new('x', 501)) })
        {
            var rejected = false; try { DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Problem, "", oversize); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "every issue detail has a bounded length without silent truncation");
        }
        var escaped = DeviceFeedbackDraft.Create(context, DeviceFeedbackResult.Problem, "", new("&title=foreign#anchor", "日本語", "ü"));
        Check(escaped.GitHubUri.Query.Split('&').Length == 3 && Uri.UnescapeDataString(escaped.GitHubUri.Query).Contains("&title=foreign#anchor"),
            "user-provided problem text stays inside the encoded reviewed body");
        CheckExperimentalFeedbackPolicy();
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
        foreach (var source in new[] { "Rückmeldung & Kontakt", "Bitte wählen", "Funktioniert gut", "Problem melden", "Kommentar (optional)", "Auf GitHub öffnen", "E-Mail öffnen", "Modell und Versionen werden ergänzt. Keine Dateien oder Gerätekennungen.", "GitHub-Rückmeldungen sind öffentlich. Prüfe den Text vor dem Absenden.", "Der Link konnte nicht geöffnet werden. Prüfe deine Standard-App für Browser oder E-Mail.",
            "Dein Modell wird experimentell unterstützt. Deine Rückmeldung zu Tasten und Beleuchtung hilft uns besonders.", "Rückmeldung geben", "Später",
            "Schritte zum Problem", "Erwartetes Verhalten", "Tatsächliches Verhalten", "Bericht kopieren",
            "Modell, Versionen und System werden ergänzt. Persönliche Dateien und Gerätekennungen bleiben ausgeschlossen.",
            "Kopieren nicht möglich. Markiere und kopiere den Text in der Vorschau.",
            "Der Bericht ist zu lang für einen Link. Kopiere ihn und füge ihn in GitHub oder deine E-Mail ein." })
            foreach (var language in L.SupportedLanguages.Where(l => l.Code != "de"))
                Check(L.Catalog.TryGetValue(source, out var translations) && !string.IsNullOrWhiteSpace(translations.GetValueOrDefault(language.Code)), "translated feedback " + language.Code);

        var window = new DeviceFeedbackWindow(id);
        var outcome = Find<ComboBox>(window, "FeedbackResult"); var input = Find<TextBox>(window, "FeedbackComment");
        var preview = Find<TextBox>(window, "FeedbackPreview");
        var githubButton = Find<Button>(window, "OpenFeedbackGitHub"); var emailButton = Find<Button>(window, "OpenFeedbackEmail");
        var copyButton = Find<Button>(window, "CopyFeedbackReport");
        var problemPanel = Find<StackPanel>(window, "FeedbackProblemDetails");
        var stepsInput = Find<TextBox>(window, "FeedbackSteps");
        var expectedInput = Find<TextBox>(window, "FeedbackExpected");
        var actualInput = Find<TextBox>(window, "FeedbackActual");
        Check(!copyButton.IsEnabled && problemPanel.Visibility == Visibility.Collapsed,
            "no draft or problem form is selected by default");
        Check(outcome.SelectedIndex == 0 && !githubButton.IsEnabled && !emailButton.IsEnabled, "no default success or destination launch before preview");
        Check(emailButton.Visibility == (SupportContact.Email is null ? Visibility.Collapsed : Visibility.Visible), "email visibility follows validated public contact");
        outcome.SelectedIndex = 2; input.Text = "A key does not light up.";
        Check(githubButton.IsEnabled && preview.IsReadOnly && preview.Text.Contains("Firmware: Not checked") && preview.Text.Contains(input.Text), "complete preview precedes explicit external action");
        stepsInput.Text = "Turn the knob"; expectedInput.Text = "Lights dim"; actualInput.Text = "No change";
        Check(problemPanel.Visibility == Visibility.Visible && copyButton.IsEnabled && preview.Text.Contains("## Steps to reproduce\n\nTurn the knob") &&
            preview.Text.Contains("## Expected behavior\n\nLights dim") && preview.Text.Contains("## Actual behavior\n\nNo change"),
            "problem fields update the exact read-only preview without opening a destination");
        foreach (var field in new[] { stepsInput, expectedInput, actualInput })
            Check(field.MaxLength == DeviceFeedbackDetails.MaximumFieldLength, "issue input limit matches report validation");
        input.Text = "Changed comment";
        Check(preview.Text.Contains("Changed comment") && !preview.Text.Contains("A key does not light up."), "comment edit refreshes exact draft immediately");
        outcome.SelectedIndex = 0;
        Check(!githubButton.IsEnabled && !emailButton.IsEnabled && preview.Text == "", "clearing outcome clears draft and disables external actions");
        Check(!copyButton.IsEnabled && problemPanel.Visibility == Visibility.Collapsed, "clearing outcome also hides issue details and disables copying");
        outcome.SelectedIndex = 1;
        Check(preview.Text.Contains("Result: Working well") && !preview.Text.Contains("Result: Problem"), "new preview includes updated outcome");
        Check(problemPanel.Visibility == Visibility.Collapsed && !preview.Text.Contains("Turn the knob") && !preview.Text.Contains("## Steps to reproduce"),
            "success hides issue fields and excludes previous problem details from the outgoing report");
        outcome.SelectedIndex = 2;
        Check(stepsInput.Text == "Turn the knob" && preview.Text.Contains("Turn the knob"), "returning to problem preserves the user's unsent details");
        input.Text = new('日', DeviceFeedbackDraft.MaximumCommentLength);
        Check(preview.Text.Contains(input.Text) && githubButton.IsEnabled && copyButton.IsEnabled &&
            Descendants(window).OfType<TextBlock>().Any(text => text.Text == L.T("Der Bericht ist zu lang für einen Link. Kopiere ihn und füge ihn in GitHub oder deine E-Mail ein.")),
            "long Unicode reports keep the entire text and offer an explicit copy path without automatic clipboard or network use");
        input.Text = "Changed comment";
        using var rendered = new MemoryStream();
        var visual = (FrameworkElement)window.Content; visual.Measure(new Size(744, 560)); visual.Arrange(new Rect(0, 0, 744, 560)); visual.UpdateLayout();
        foreach (var control in new FrameworkElement[] { outcome, input, stepsInput, expectedInput, actualInput, preview, githubButton, emailButton, copyButton })
        {
            var position = control.TranslatePoint(new Point(), visual);
            Check(position.Y >= 0 && position.Y + control.ActualHeight <= 560 && position.X >= 0 && position.X + control.ActualWidth <= 744,
                "feedback controls fit the supported minimum window without page scrolling: " + control.Name);
        }
        var bitmap = new RenderTargetBitmap(744, 560, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(rendered);
        Check(rendered.Length > 1000, "offline feedback panel renders");
        if (args.Length == 2 && args[0] == "--preview") File.WriteAllBytes(Path.GetFullPath(args[1]), rendered.ToArray());
        window.Close();
        var completed = new DeviceFeedbackWindow(id, new(id, new(0, 11, 2), 1), DeviceFeedbackResult.Success);
        Check(Find<ComboBox>(completed, "FeedbackResult").SelectedIndex == 1 && Find<TextBox>(completed, "FeedbackPreview").Text.Contains("Firmware: 0.11.2"), "success screen renders exact preview before deliberate opening"); completed.Close();
        foreach (var language in L.SupportedLanguages)
        {
            L.SetLanguage(language.Code);
            var invite = (Window)typeof(MainWindow).GetMethod("CreateExperimentalFeedbackInvitation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [DeviceCatalog.All.First(model => model.IsExperimentalFirmware)])!;
            Check(Find<Button>(invite, "FeedbackLater") is { IsCancel: true, IsDefault: true } later && Equals(later.Content, L.T("Später")) &&
                Equals(Find<Button>(invite, "GiveFeedback").Content, L.T("Rückmeldung geben")),
                "feedback invitation is translated and defers by default without opening any external destination: " + language.Code);
            invite.Close();
            var localizedReport = new DeviceFeedbackWindow(id, initialResult: DeviceFeedbackResult.Problem);
            Check(localizedReport.Width <= SystemParameters.WorkArea.Width && localizedReport.Height <= SystemParameters.WorkArea.Height &&
                localizedReport.MinWidth <= SystemParameters.WorkArea.Width && localizedReport.MinHeight <= SystemParameters.WorkArea.Height,
                "feedback window bounds fit the current work area: " + language.Code);
            Find<TextBox>(localizedReport, "FeedbackComment").Text = new('日', DeviceFeedbackDraft.MaximumCommentLength);
            var localizedContent = (FrameworkElement)localizedReport.Content;
            localizedContent.Measure(new Size(744, 560)); localizedContent.Arrange(new Rect(0, 0, 744, 560)); localizedContent.UpdateLayout();
            foreach (var control in Descendants(localizedReport).OfType<FrameworkElement>().Where(element => element is Button or TextBox or ComboBox && element.Visibility == Visibility.Visible))
            {
                var position = control.TranslatePoint(new Point(), localizedContent);
                Check(position.Y >= 0 && position.Y + control.ActualHeight <= 560 && position.X >= 0 && position.X + control.ActualWidth <= 744,
                    "localized feedback controls remain inside the minimum client area: " + language.Code + " " + control.Name);
            }
            var bottomInput = Find<TextBox>(localizedReport, "FeedbackActual"); var actionButton = Find<Button>(localizedReport, "OpenFeedbackGitHub");
            Check(bottomInput.TranslatePoint(new Point(0, bottomInput.ActualHeight), localizedContent).Y <= actionButton.TranslatePoint(new Point(), localizedContent).Y,
                "long-report footer does not overlap the issue form: " + language.Code);
            localizedContent.Measure(new Size(1000, 500)); localizedContent.Arrange(new Rect(0, 0, 1000, 500)); localizedContent.UpdateLayout();
            foreach (var action in Descendants(localizedReport).OfType<Button>())
            {
                var position = action.TranslatePoint(new Point(), localizedContent);
                Check(position.Y >= 0 && position.Y + action.ActualHeight <= 500 && position.X >= 0 && position.X + action.ActualWidth <= 1000,
                    "all feedback actions remain visible on a short work area: " + language.Code);
            }
            var formScroll = Find<ScrollViewer>(localizedReport, "FeedbackFormScroll");
            Check(formScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && formScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                formScroll.ActualHeight > 100 && formScroll.TranslatePoint(new Point(0, formScroll.ActualHeight), localizedContent).Y <= actionButton.TranslatePoint(new Point(), localizedContent).Y,
                "short work area confines optional scrolling to the form above fixed actions: " + language.Code);
            formScroll.ScrollToEnd(); localizedContent.UpdateLayout();
            var lastFieldPosition = bottomInput.TranslatePoint(new Point(), localizedContent);
            Check(lastFieldPosition.Y >= 0 && lastFieldPosition.Y + bottomInput.ActualHeight <= actionButton.TranslatePoint(new Point(), localizedContent).Y,
                "last problem field is reachable without moving the footer: " + language.Code);
            localizedReport.Close();
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, checks = _checks, hardwareAccess = false, networkAccess = false, externalLaunches = 0 }));
    }

    private static void CheckExperimentalFeedbackPolicy()
    {
        var directory = Path.GetFullPath(Path.Combine("workspace", "checks", "feedback-policy-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(directory, "app-settings.json");
        try
        {
            foreach (var device in DeviceCatalog.All)
                Check(ExperimentalDeviceFeedback.ShouldOffer(new(), device.Id, DeviceFeedbackTrigger.Installation, true, false) == device.IsExperimentalFirmware,
                    "only experimental models can receive a post-success invitation: " + device.Id);
            var model = DeviceCatalog.All.First(device => device.IsExperimentalFirmware).Id;
            Check(!ExperimentalDeviceFeedback.TryMarkOffered(model, DeviceFeedbackTrigger.Installation, true, true, out _, path) &&
                !ExperimentalDeviceFeedback.TryMarkOffered(model, DeviceFeedbackTrigger.ConfigurationTransfer, false, false, out _, path) &&
                !ExperimentalDeviceFeedback.TryMarkOffered(model, (DeviceFeedbackTrigger)999, true, false, out _, path) && !Directory.Exists(directory),
                "offline, failed and unknown operations neither invite nor create preference files");
            Directory.CreateDirectory(directory);
            var preferences = new AppPreferences { Language = "fr", ShowHelp = false };
            AppPreferencesStore.Save(preferences, path);
            Check(ExperimentalDeviceFeedback.TryMarkOffered(model, DeviceFeedbackTrigger.Installation, true, false, out var updated, path) &&
                updated is { Language: "fr", ShowHelp: false } && updated.FeedbackRequestedModels.SequenceEqual([model]),
                "once-only claim uses latest preferences and persists only the model");
            Check(!ExperimentalDeviceFeedback.TryMarkOffered(model, DeviceFeedbackTrigger.ConfigurationTransfer, true, false, out _, path),
                "installation invitation is not repeated after configuration or restart");
            var saved = AppPreferencesStore.Load(path).Preferences;
            Check(saved.FeedbackRequestedModels.SequenceEqual([model]) && AppPreferencesStore.SaveWindowState("numpad", 1280, 820, path).FeedbackRequestedModels.SequenceEqual([model]),
                "seen model survives reload and window-state saves");
            var copy = saved.ClonePreferences(); copy.FeedbackRequestedModels[0] = "numpad";
            Check(saved.FeedbackRequestedModels[0] == model, "editing a preferences clone cannot change the original once-only markers");
            File.WriteAllText(path, "{invalid");
            var next = DeviceCatalog.All.First(device => device.IsExperimentalFirmware && device.Id != model).Id;
            var rejected = false;
            try { ExperimentalDeviceFeedback.TryMarkOffered(next, DeviceFeedbackTrigger.Installation, true, false, out _, path); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(path) == "{invalid", "automatic invitation never repairs malformed preference data");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class OfflinePresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static KeyEventArgs RaiseKey(UIElement target, Key key, bool released = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new OfflinePresentationSource(), 0, key)
        { RoutedEvent = released ? Keyboard.PreviewKeyUpEvent : Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        // Use the same handled state for the bubbling stage, just as routed keyboard input does.
        args.RoutedEvent = released ? Keyboard.KeyUpEvent : Keyboard.KeyDownEvent;
        target.RaiseEvent(args);
        return args;
    }

    private static void CheckRoutedTyping(DeviceTestWindow window, string model)
    {
        var close = Find<Button>(window, "CloseDeviceTest"); var last = Find<TextBlock>(window, "DeviceTypingLastKey");
        var clicks = 0; close.Click += (_, _) => clicks++;
        FocusManager.SetFocusedElement(window, close);
        Check(ReferenceEquals(FocusManager.GetFocusedElement(window), close), "offline test sets logical focus to Done: " + model);
        foreach (var key in new[] { Key.Return, Key.Space, Key.Tab, Key.Home, Key.Delete, Key.Escape })
        {
            var down = RaiseKey(close, key); var up = RaiseKey(close, key, released: true);
            Check(down.Handled && up.Handled && clicks == 0 && last.Text == L.Format("Zuletzt gedrückt: {0}", DeviceTypingTest.KeyLabel(key.ToString())),
                "window test captures navigation press/release without activating focused Done: " + model + " " + key);
        }
        var symbolKey = RaiseKey(close, Key.Oem5);
        Check(!symbolKey.Handled && last.Text == L.Format("Zuletzt gedrückt: {0}", L.T("Zeichentaste")),
            "printable OEM key remains available for OS text composition: " + model);
        var composition = new TextComposition(InputManager.Current, close, "#");
        var text = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        close.RaiseEvent(text);
        Check(text.Handled && last.Text == L.Format("Zuletzt gedrückt: {0}", "#") && clicks == 0,
            "window receives composed symbols without a text box or button activation: " + model);
        var diagram = Find<Canvas>(window, "DeviceTestKeyboard");
        Check(diagram.Children.OfType<Button>().All(key => !key.Focusable), "clicking diagram keys cannot steal test focus: " + model);
        RaiseKey(close, Key.NumPad7);
    }

    private static void CheckTypingStopped(DeviceTestWindow window, string model)
    {
        var label = Find<TextBlock>(window, "DeviceTypingLastKey"); var before = label.Text;
        RaiseKey(window, Key.A);
        var composition = new TextComposition(InputManager.Current, window, "private input");
        window.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
        Check(label.Text == before, "closing the test stops recording new input immediately: " + model);
    }

    private static void CheckCompletionInput()
    {
        Check(DeviceTypingTest.AdvanceCompletionPrefix("", "ing", "INGLORIOUS") == "ing" &&
            DeviceTypingTest.AdvanceCompletionPrefix("ing", "LORIOUS", "INGLORIOUS").Equals("INGLORIOUS", StringComparison.OrdinalIgnoreCase),
            "completion accepts separately delivered text and any letter case");
        Check(DeviceTypingTest.AdvanceCompletionPrefix("", "unrelated input INGLINGLORIOUS", "INGLORIOUS") == "INGLORIOUS" &&
            DeviceTypingTest.AdvanceCompletionPrefix("INGL", "wrong", "INGLORIOUS") == "",
            "completion keeps only a matching prefix and recovers after mistakes or overlapping starts");
        Check(DeviceTypingTest.AdvanceCompletionPrefix("", new string('I', 10000), "INGLORIOUS") == "I" &&
            DeviceTypingTest.AdvanceCompletionPrefix("", "private text", "") == "",
            "completion never retains unrelated text and remains bounded by the expected word");
        Check(DeviceTypingTest.AdvanceCompletionPrefix("123", "4567890", "1234567890") == "1234567890" &&
            DeviceTypingTest.AdvanceCompletionPrefix("123", "\r\n", "1234567890") == "",
            "numpad completion uses the full expected digit sequence and rejects control characters");
        foreach (var key in new[] { "Return", "Enter", "Space", "Tab", "Escape", "Left", "Right", "Up", "Down", "Home", "End", "Prior", "PageUp", "Next", "PageDown", "Back", "Backspace", "Delete" })
            Check(DeviceTypingTest.IsTestNavigationKey(key), "both platforms suppress test navigation activation: " + key);
        foreach (var key in new[] { "I", "Oem5", "D1", "NumPad7", "LeftShift" })
            Check(!DeviceTypingTest.IsTestNavigationKey(key), "printable/modifier keys remain available to text composition: " + key);
    }
}
