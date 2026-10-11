using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private void RunHelpChecks(Action<bool, string> check)
    {
        if (!Program.Offline) throw new InvalidOperationException("Help checks require offline mode.");
        var language = L.CurrentLanguage; var preferences = _preferences;
        var page = _page; var standby = _showStandby; var sliderEditor = _editingSlider; var pending = _pendingApply;
        var before = EditorState.Fingerprint(_state.Project); var path = _state.Path;
        var dirty = _state.Dirty; var undo = _state.CanUndo; var redo = _state.CanRedo;
        var selectedKeys = _state.SelectedKeys.ToArray(); var selectedSides = _state.SelectedSideLeds.ToArray();
        var applyCalls = 0;
        Func<bool> uncommitted = () => { applyCalls++; return false; };
        try
        {
            _preferences = _preferences with { ShowHelp = false }; Build();
            var button = _root.GetLogicalDescendants().OfType<Button>().Single(c => c.Name == "HelpButton");
            check(button.IsVisible && Equals(button.Content, "?") && Equals(ToolTip.GetTip(button), L.T("Anleitung")) &&
                Avalonia.Automation.AutomationProperties.GetName(button) == L.T("Anleitung"),
                "help question mark remains visible and accessible when inline hints are disabled");
            _page = "Profile"; Build();
            var name = _root.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "ProfileName");
            name.Text = "Uncommitted help name";
            _pendingApply = uncommitted;
            OpenHelpAsync(guide =>
            {
                try
                {
                    name.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent) { OldFocusedElement = name });
                    check(IsHelpTakingFocus && name.Text == "Uncommitted help name" && EditorState.Fingerprint(_state.Project) == before,
                        "actual help opening preserves uncommitted profile name when focus leaves its editor");
                    check(ReferenceEquals(_pendingApply, uncommitted) && applyCalls == 0, "actual help opening bypasses pending-edit decisions");
                }
                finally { guide.Close(); }
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            check(_helpWindow is null, "closing help releases its window without rebuilding the editor");
            _editingSlider = false;
            foreach (var (source, id) in new[] { ("Beleuchtung", "lighting"), ("Tastenbelegung", "bindings"), ("Makros", "macros"),
                ("Ebenen", "layers"), ("Profile", "profiles"), ("Gerät", "device"), ("", "start") })
            { _page = source; _showStandby = false; check(CurrentGuideTopic() == id, "help opens the active editor topic: " + id); }
            _page = "Beleuchtung"; _showStandby = true;
            check(CurrentGuideTopic() == "standby", "standby editor opens its dedicated help topic");
            var priorSliderEditor = _editingSlider;
            try
            {
                _page = "Tastenbelegung"; _editingSlider = true;
                check(CurrentGuideTopic() == "device", "slider editor opens the regulator and USB help topic");
            }
            finally { _editingSlider = priorSliderEditor; }

            foreach (var supported in L.SupportedLanguages)
            {
                L.SetLanguage(supported.Code);
                var guide = CreateUserGuide("lighting");
                try
                {
                    var root = (Grid)guide.Content!;
                    var topics = root.GetLogicalDescendants().OfType<ListBox>().Single(c => c.Name == "GuideTopics");
                    var article = root.GetLogicalDescendants().OfType<StackPanel>().Single(c => c.Name == "GuideArticle");
                    var scroll = root.GetLogicalDescendants().OfType<ScrollViewer>().Single(c => c.Name == "GuideArticleScroll");
                    var close = root.GetLogicalDescendants().OfType<Button>().Single(c => c.Name == "GuideClose");
                    check(guide.Title == L.T("Anleitung") && guide.CanResize && guide.MinWidth == 680 && guide.MinHeight == 480 &&
                        topics.Items.Count == UserGuide.Topics.Count && ((GuideTopic)topics.SelectedItem!).Id == "lighting",
                        "localized help has all topics, contextual selection and bounded resizable layout: " + supported.Code);
                    check(close.IsCancel && close.IsDefault && Equals(close.Content, L.T("Schließen")), "help has localized close and safe Escape/default action: " + supported.Code);
                    foreach (var topic in UserGuide.Topics)
                    {
                        topics.SelectedItem = topic;
                        var texts = article.Children.OfType<TextBlock>().ToArray();
                        check(texts[0].Text == L.T(topic.Title) && texts.Length >= topic.Sections.Count + 1 && texts.All(t => t.TextWrapping == TextWrapping.Wrap && !string.IsNullOrWhiteSpace(t.Text)),
                            "topic switch shows translated wrapped headings and paragraphs: " + supported.Code + "/" + topic.Id);
                        root.Measure(new Size(680, 480)); root.Arrange(new Rect(0, 0, 680, 480));
                        check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && scroll.Bounds.Width > 0,
                            "minimum help layout keeps a vertically scrollable article without horizontal overflow: " + supported.Code + "/" + topic.Id);
                    }
                    var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                    guide.RaiseEvent(escape);
                    check(escape.Handled, "Escape is handled by the help window: " + supported.Code);
                }
                finally { guide.Close(); }
            }
            var fallback = CreateUserGuide("unknown-topic");
            try
            {
                var topics = ((Grid)fallback.Content!).GetLogicalDescendants().OfType<ListBox>().Single(c => c.Name == "GuideTopics");
                check(ReferenceEquals(topics.SelectedItem, UserGuide.Topics[0]), "unknown help topic selects the first article");
            }
            finally { fallback.Close(); }
            check(applyCalls == 0 && ReferenceEquals(_pendingApply, uncommitted), "opening, browsing and closing help never evaluates or clears pending edits");
            check(EditorState.Fingerprint(_state.Project) == before && _state.Path == path && _state.Dirty == dirty && _state.CanUndo == undo && _state.CanRedo == redo &&
                _state.SelectedKeys.SetEquals(selectedKeys) && _state.SelectedSideLeds.SetEquals(selectedSides),
                "help preserves profile bytes, file association, dirty state, history and selections");
        }
        finally
        {
            L.SetLanguage(language); _preferences = preferences; _page = page; _showStandby = standby; _editingSlider = sliderEditor;
            Build(); _pendingApply = pending;
        }
    }
}
