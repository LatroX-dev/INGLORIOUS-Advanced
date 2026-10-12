using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace InGlorious.Core;

public partial class MainWindow
{
    private bool _helpOpen;
    private bool IsHelpTakingFocus => _helpOpen || Keyboard.FocusedElement == HelpButton;
    private string ContextHelpTopic => _activeTab switch
    {
        0 => "lighting",
        1 => _editingSlider && Device.Id == "numpad" ? "device" : "bindings",
        2 => "macros", 3 => "layers", 4 => "device", 5 => "profiles", _ => "start"
    };
    private HelpWindow CreateHelpWindow(string? topic = null) => new(topic ?? ContextHelpTopic) { Owner = this };
    private void Help_Click(object sender, RoutedEventArgs e) => OpenHelp();
    private void OpenHelp(Action<HelpWindow>? show = null)
    {
        if (_helpOpen) return;
        _helpOpen = true;
        try
        {
            var window = CreateHelpWindow();
            if (show is null) window.ShowDialog(); else show(window);
        }
        finally { _helpOpen = false; }
    }

    private void RunHelpChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Help checks require offline UI-smoke mode.");
        var helpVisibility = Resources["HelpVisibility"];
        try
        {
            Resources["HelpVisibility"] = Visibility.Collapsed;
            check(HelpButton.IsVisible && HelpButton.ToolTip?.ToString() == L.T("Anleitung") && AutomationProperties.GetName(HelpButton) == L.T("Anleitung"),
                "Help: header question button remains accessible when optional hints are hidden");
            ResetWorkflowFixture("pro-iso");
            foreach (var (area, topic) in new[] { (0, "lighting"), (1, "bindings"), (2, "macros"), (3, "layers"), (4, "device"), (5, "profiles") })
            { SetTab(area); check(ContextHelpTopic == topic, "Help: area " + area + " opens its matching article"); }
            ResetWorkflowFixture("numpad"); ShowBindingTarget(true);
            check(ContextHelpTopic == "device", "Help: slider settings open the article explaining slider functions");
            ShowBindingTarget(false);
            check(ContextHelpTopic == "bindings", "Help: returning to keys restores the bindings article");
            SetTab(0); SetStandbyEditor(); check(ContextHelpTopic == "device", "Help: standby settings are part of the Device article context");
            SetLightingEditor(true); SetEffectScope(false); GlobalEffectEnabled.IsChecked = true; EffectSpeed.Value = 177; EffectChoice.SelectedValue = 3;
            ProjectName.Text = "Uncommitted profile name";
            var fingerprint = Fingerprint(_project); var inputs = EffectInputFingerprint(); var undo = _undo.Count;
            var pendingPrompts = 0; _pendingEditDecisionOverride = () => { pendingPrompts++; return PendingEditChoice.Discard; };
            OpenHelp(window =>
            {
                check(window.SelectedTopicId == "lighting", "Help: the real open path uses the current editor context");
                ProjectName_LostFocus(ProjectName, new RoutedEventArgs());
                MacroName_LostFocus(MacroName, new RoutedEventArgs());
                foreach (var topic in UserGuide.Topics)
                {
                    window.SelectTopic(topic.Id);
                    check(window.SelectedTopicId == topic.Id && window.TopicHeading.Text == L.T(topic.Title), "Help: topic selection renders " + topic.Id);
                    var paragraphs = window.Article.Children.OfType<TextBlock>().Select(block => block.Text).ToArray();
                    check(topic.Sections.All(section => string.IsNullOrWhiteSpace(section.Heading) || paragraphs.Contains(L.T(section.Heading))) && paragraphs.Length > 1,
                        "Help: structured headings and article text are available for " + topic.Id);
                }
                window.SelectTopic("missing-topic");
                check(window.SelectedTopicId == "start" && window.CloseButton.IsCancel, "Help: unknown topic falls back to introduction and Escape closes safely");
                check(window.TopicList.Items.Count == UserGuide.Topics.Count && window.ArticleScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                    "Help: every topic is one flat navigation entry and articles wrap without horizontal scrolling");
                window.Close();
            });
            check(!_helpOpen && pendingPrompts == 0 && Fingerprint(_project) == fingerprint && EffectInputFingerprint() == inputs && HasPendingEffect && _undo.Count == undo && ProjectName.Text == "Uncommitted profile name",
                "Help: opening, changing topics and closing preserve pending inputs, profile names and undo without prompting");
        }
        finally
        {
            Resources["HelpVisibility"] = helpVisibility; _pendingEditDecisionOverride = null;
            DiscardPendingEdits(); ResetWorkflowFixture("pro-iso");
        }
    }
}
