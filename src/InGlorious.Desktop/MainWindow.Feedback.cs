using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using InGlorious.Core;
using System.Diagnostics;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private bool _feedbackInvitationOpen;
    private Window? _settingsEditOwner;
    private sealed record DeferredFeedbackInvitation(string DeviceId, DeviceFeedbackTrigger Trigger, OnboardFirmwareVersion? Version);
    private readonly Dictionary<string, DeferredFeedbackInvitation> _deferredFeedbackInvitations = [];

    private bool DeferFeedbackWhileEditingSettings(string deviceId, DeviceFeedbackTrigger trigger, OnboardFirmwareVersion? version)
    {
        if (_settingsEditOwner is null) return false;
        if (DeviceCatalog.All.Any(device => device.Id == deviceId && device.IsExperimentalFirmware) && Enum.IsDefined(trigger))
            _deferredFeedbackInvitations.TryAdd(deviceId, new(deviceId, trigger, version));
        return true;
    }

    private async Task FinishSettingsFeedbackAsync(Window owner)
    {
        if (!ReferenceEquals(_settingsEditOwner, owner)) return;
        _settingsEditOwner = null;
        var pending = _deferredFeedbackInvitations.Values.ToArray(); _deferredFeedbackInvitations.Clear();
        foreach (var invitation in pending)
            await MaybeOfferExperimentalFeedbackAsync(invitation.DeviceId, invitation.Trigger, invitation.Version);
    }

    private async Task MaybeOfferExperimentalFeedbackAsync(string deviceId, DeviceFeedbackTrigger trigger,
        OnboardFirmwareVersion? version = null, Window? owner = null)
    {
        if (Program.Offline || _feedbackInvitationOpen || _busy || _firmwareFlow || _startupLoading || _startupWindowClosed || _allowClose) return;
        // A live Settings dialog owns a preference fingerprint. Do not change it behind the editor.
        if (DeferFeedbackWhileEditingSettings(deviceId, trigger, version)) return;
        var device = DeviceCatalog.All.FirstOrDefault(candidate => candidate.Id == deviceId);
        if (device is null) return;
        try
        {
            // Claim before showing: choosing Later, closing the popup or restarting must not repeat it.
            if (!ExperimentalDeviceFeedback.TryMarkOffered(deviceId, trigger, succeeded: true, offline: Program.Offline, out var preferences)) return;
            if (preferences is not null) _preferences = preferences;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Feedback invitation preference could not be saved: {0}", exception);
            return;
        }
        var dialogOwner = owner is { IsVisible: true } ? owner : this;
        var priorEnabled = _root.IsEnabled;
        _feedbackInvitationOpen = _busy = true; _root.IsEnabled = false;
        try
        {
            var choice = await CreateExperimentalFeedbackPrompt(device).ShowDialog<int>(dialogOwner);
            if (choice == 1 && !_startupWindowClosed && !_allowClose)
                await new DeviceFeedbackWindow(deviceId, version).ShowDialog(dialogOwner);
        }
        catch (Exception exception) { Trace.TraceError("Feedback invitation could not be displayed: {0}", exception); }
        finally { _feedbackInvitationOpen = _busy = false; if (!_startupWindowClosed) _root.IsEnabled = priorEnabled; }
    }

    private Window CreateExperimentalFeedbackPrompt(DeviceDefinition device)
    {
        var prompt = CreatePrompt("Rückmeldung & Kontakt", "\"" + device.Name + "\"\n\n" + DeviceFeedbackContext.Invitation(device), "Später", "Rückmeldung geben");
        var buttons = ((Control)prompt.Content!).GetLogicalDescendants().OfType<Button>().ToArray();
        buttons[1].Classes.Add("primary");
        return prompt;
    }

    private async Task RunExperimentalFeedbackInvitationChecksAsync(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Feedback invitation checks require offline mode.");
        var preferences = _preferences;
        MaybeOfferExperimentalFeedbackAsync("gmmk3-75-iso", DeviceFeedbackTrigger.Installation).GetAwaiter().GetResult();
        MaybeOfferExperimentalFeedbackAsync("gmmk3-75-iso", DeviceFeedbackTrigger.ConfigurationTransfer).GetAwaiter().GetResult();
        check(ReferenceEquals(preferences, _preferences) && !_feedbackInvitationOpen,
            "Experimental feedback: offline setup and transfer never load or save invitation preferences, or open a popup");
        var settingsOwner = new Window(); var unrelatedOwner = new Window();
        var preferenceFixture = System.IO.Path.Combine(AppContext.BaseDirectory, "offline-feedback-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AppPreferencesStore.Save(new AppPreferences(), preferenceFixture);
            var edit = AppPreferencesStore.BeginEdit(preferenceFixture);
            var originalBytes = File.ReadAllBytes(preferenceFixture);
            _settingsEditOwner = settingsOwner;
            check(DeferFeedbackWhileEditingSettings("gmmk3-75-iso", DeviceFeedbackTrigger.Installation, null) &&
                DeferFeedbackWhileEditingSettings("gmmk3-75-iso", DeviceFeedbackTrigger.ConfigurationTransfer, null) &&
                _deferredFeedbackInvitations.Count == 1 && originalBytes.SequenceEqual(File.ReadAllBytes(preferenceFixture)),
                "Experimental feedback: a live Settings editor defers and deduplicates invitations without modifying its fingerprint");
            await FinishSettingsFeedbackAsync(unrelatedOwner);
            check(ReferenceEquals(_settingsEditOwner, settingsOwner) && _deferredFeedbackInvitations.Count == 1,
                "Experimental feedback: closing another modal cannot flush the Settings owner's invitation");
            AppPreferencesStore.Save(edit, edit.Preferences with { Language = "en", ShowHelp = false });
            await FinishSettingsFeedbackAsync(settingsOwner);
            check(_settingsEditOwner is null && _deferredFeedbackInvitations.Count == 0 && !_feedbackInvitationOpen,
                "Experimental feedback: closing Settings releases deferred invitations; offline checks still never show a popup");
            check(ExperimentalDeviceFeedback.TryMarkOffered("gmmk3-75-iso", DeviceFeedbackTrigger.Installation, true, false, out var persisted, preferenceFixture) &&
                persisted is { Language: "en", ShowHelp: false } && persisted.FeedbackRequestedModels.Contains("gmmk3-75-iso"),
                "Experimental feedback: claiming after Settings saves preserves newly chosen preferences and their concurrency protection");
        }
        finally
        {
            _settingsEditOwner = null; _deferredFeedbackInvitations.Clear(); settingsOwner.Close(); unrelatedOwner.Close();
            if (File.Exists(preferenceFixture)) File.Delete(preferenceFixture);
        }
        var language = L.CurrentLanguage;
        try
        {
            foreach (var supported in L.SupportedLanguages)
            {
                L.SetLanguage(supported.Code);
                var model = DeviceCatalog.Get("gmmk3-75-iso");
                var prompt = CreateExperimentalFeedbackPrompt(model);
                try
                {
                    var body = (Control)prompt.Content!;
                    var buttons = body.GetLogicalDescendants().OfType<Button>().ToArray();
                    check(prompt.Title == L.T("Rückmeldung & Kontakt") && buttons.Length == 2 &&
                        buttons[0].Content?.ToString() == L.T("Später") && buttons[0].IsCancel && buttons[0].IsDefault &&
                        buttons[1].Content?.ToString() == L.T("Rückmeldung geben") && !buttons[1].IsDefault && buttons[1].Classes.Contains("primary"),
                        "Experimental feedback: optional popup has a safe Later default and explicit feedback action: " + supported.Code);
                    check(body.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text is { } message &&
                        message.Contains(model.Name, StringComparison.Ordinal) && message.Contains(DeviceFeedbackContext.Invitation(model), StringComparison.Ordinal)),
                        "Experimental feedback: popup clearly identifies the model and asks for voluntary feedback: " + supported.Code);
                }
                finally { prompt.Close(); }

                var feedback = new DeviceFeedbackWindow(model.Id);
                try
                {
                    // ScrollViewer presenters are created only after the window enters the visual tree.
                    // The fixture stays transparent and never activates or appears in the taskbar.
                    feedback.ShowActivated = false; feedback.Opacity = 0; feedback.Show();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var body = (Control)feedback.Content!;
                    var controls = body.GetLogicalDescendants().OfType<Control>().ToArray();
                    T Named<T>(string name) where T : Control => controls.OfType<T>().Single(control => control.Name == name);
                    var outcome = Named<ComboBox>("FeedbackResult");
                    var preview = Named<TextBox>("FeedbackPreview");
                    var details = Named<StackPanel>("FeedbackProblemDetails");
                    var steps = Named<TextBox>("FeedbackSteps"); var expected = Named<TextBox>("FeedbackExpected"); var actual = Named<TextBox>("FeedbackActual");
                    var github = Named<Button>("OpenFeedbackGitHub"); var email = Named<Button>("OpenFeedbackEmail"); var copy = Named<Button>("CopyFeedbackReport");
                    check(outcome.SelectedIndex == 0 && !preview.IsVisible && !details.IsVisible && !github.IsEnabled && !email.IsEnabled && !copy.IsEnabled,
                        "Feedback report: opening does not preselect success, reveal a report or enable external actions: " + supported.Code);
                    outcome.SelectedIndex = 2;
                    steps.Text = "Press the selected key."; expected.Text = "The selected key lights red."; actual.Text = "A different key lights red.";
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    check(details.IsVisible && preview.IsVisible && github.IsEnabled && copy.IsEnabled &&
                        preview.Text!.Contains("## Steps to reproduce", StringComparison.Ordinal) && preview.Text.Contains(steps.Text, StringComparison.Ordinal) &&
                        preview.Text.Contains(expected.Text, StringComparison.Ordinal) && preview.Text.Contains(actual.Text, StringComparison.Ordinal) &&
                        preview.Text.Contains("- Layout: ISO", StringComparison.Ordinal) && preview.Text.Contains("- Firmware: Not checked", StringComparison.Ordinal),
                        "Feedback report: selected problem includes the supplied reproduction details and honest device metadata: " + supported.Code);
                    body.Measure(new Size(760, 600)); body.Arrange(new Rect(0, 0, 760, 600));
                    foreach (var control in new Control[] { steps, expected, actual, github, email, copy })
                    {
                        if (!control.IsVisible) continue;
                        var point = control.TranslatePoint(default, body);
                        check(point is { } position && position.X >= 0 && position.Y >= 0 && position.X + control.Bounds.Width <= 760 && position.Y + control.Bounds.Height <= 600,
                            "Feedback report: problem fields and actions fit at minimum size: " + supported.Code + "/" + control.Name + "/" + point + "/" + control.Bounds);
                    }
                    outcome.SelectedIndex = 1;
                    check(!details.IsVisible && !preview.Text!.Contains("## Steps to reproduce", StringComparison.Ordinal) && steps.Text == "Press the selected key.",
                        "Feedback report: success hides problem details without deleting the typed values: " + supported.Code);
                    outcome.SelectedIndex = 2;
                    check(details.IsVisible && preview.Text!.Contains("A different key lights red.", StringComparison.Ordinal),
                        "Feedback report: returning to a problem restores its details: " + supported.Code);
                    outcome.SelectedIndex = 0;
                    check(!preview.IsVisible && !github.IsEnabled && !email.IsEnabled && !copy.IsEnabled && actual.Text == "A different key lights red.",
                        "Feedback report: clearing the result disables actions and preserves the user's inputs: " + supported.Code);
                    outcome.SelectedIndex = 2;
                    Named<TextBox>("FeedbackComment").Text = new string('界', DeviceFeedbackDraft.MaximumCommentLength);
                    foreach (var field in new[] { steps, expected, actual }) field.Text = new string('界', DeviceFeedbackDetails.MaximumFieldLength);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var notice = Named<TextBlock>("FeedbackStatus"); var footer = Named<StackPanel>("FeedbackFooter");
                    var formScroll = Named<ScrollViewer>("FeedbackFormScroll"); var formBody = Named<Grid>("FeedbackBody");
                    check(notice.IsVisible && copy.IsEnabled && notice.Text == L.T("Der Bericht ist zu lang für einen Link. Kopiere ihn und füge ihn in GitHub oder deine E-Mail ein."),
                        "Feedback report: long problem reports keep an explicit copy route and show the link limit: " + supported.Code);
                    foreach (var size in new[] { new Size(760, 600), new Size(1000, 500) })
                    {
                        body.Measure(size); body.Arrange(new Rect(default, size));
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        body.Measure(size); body.Arrange(new Rect(default, size));
                        foreach (var action in new Control[] { notice, github, email, copy, Named<Button>("CloseFeedback") })
                        {
                            if (!action.IsVisible) continue;
                            var position = action.TranslatePoint(default, body);
                            check(position is { } point && point.X >= 0 && point.Y >= 0 && point.X + action.Bounds.Width <= size.Width && point.Y + action.Bounds.Height <= size.Height,
                                $"Feedback report: notice and every action stay visible on small workareas: {supported.Code}/{size}/{action.Name}");
                        }
                        var formTop = formBody.TranslatePoint(default, body); var footerTop = footer.TranslatePoint(default, body);
                        check(formTop is { } formPosition && footerTop is { } footerPosition && formPosition.Y + formBody.Bounds.Height <= footerPosition.Y + 1 &&
                            formScroll.Bounds.Height > 100 && formScroll.Bounds.Height <= formBody.Bounds.Height + 1,
                            $"Feedback report: only the bounded form can scroll; it never overlaps the fixed footer: {supported.Code}/{size}");
                    }
                }
                finally { feedback.Close(); }
            }
        }
        finally { L.SetLanguage(language); }
    }

    private Control BuildDeviceSupportActions()
    {
        var hasDeviceTest = !_state.Device.IsOfflineOnly;
        var actions = new Grid { Name = "DeviceSupportActions", ColumnDefinitions = new(hasDeviceTest ? "*,*,*" : "*,*"), ColumnSpacing = 24 };
        var column = 0;
        if (hasDeviceTest)
        {
            var test = AsyncButton("Gerät testen", () => OpenDeviceTestAsync(_state.Device.Id)); test.Name = "DeviceTestButton";
            var testSection = DeviceActionSection(L.T("Tasten und Eingaben prüfen."), test);
            Grid.SetColumn(testSection, column++); actions.Children.Add(testSection);
        }
        var import = AsyncButton("CORE-Einstellungen importieren", ImportCoreSettingsAsync);
        var importSection = DeviceActionSection(L.T("Gespeicherte Farben und Belegungen aus Glorious CORE übernehmen."), import);
        Grid.SetColumn(importSection, column++); actions.Children.Add(importSection);
        var button = AsyncButton("Rückmeldung & Kontakt", OpenFeedbackAsync);
        button.Name = "DeviceFeedbackContact";
        var feedbackSection = DeviceActionSection(DeviceFeedbackContext.Invitation(_state.Device), button, "DeviceFeedbackHint");
        Grid.SetColumn(feedbackSection, column); actions.Children.Add(feedbackSection);
        return actions;
    }

    private Control DeviceActionSection(string description, Button button, string? hintName = null)
    {
        var section = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 10 };
        var hint = Text(description, true, 12); hint.Name = hintName;
        section.Children.Add(hint);
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
