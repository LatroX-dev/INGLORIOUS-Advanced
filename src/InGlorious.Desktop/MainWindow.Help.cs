using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    private Window? _helpWindow;
    private bool IsHelpTakingFocus => _helpWindow is not null || FocusManager?.GetFocusedElement() is Control { Name: "HelpButton" };

    private string CurrentGuideTopic() => _page switch
    {
        "Beleuchtung" => "lighting",
        "Tastenbelegung" => _editingSlider ? "device" : "bindings",
        "Makros" => "macros",
        "Ebenen" => "layers",
        "Profile" => "profiles",
        "Gerät" => "device",
        _ => "start"
    };

    private Task OpenHelpAsync() => OpenHelpAsync(null);

    private async Task OpenHelpAsync(Func<Window, Task>? show)
    {
        // Reading help must not apply, discard, or even evaluate a pending editor action.
        if (Program.Offline && show is null) return;
        if (_helpWindow is { } existing) { existing.Activate(); return; }
        var window = CreateUserGuide(CurrentGuideTopic());
        _helpWindow = window;
        try { if (show is null) await window.ShowDialog(this); else await show(window); }
        finally { _helpWindow = null; }
    }

    private Window CreateUserGuide(string topicId)
    {
        var window = new Window
        {
            Title = L.T("Anleitung"), Width = 900, Height = 680, MinWidth = 680, MinHeight = 480,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#141618"), Foreground = Brush("#E4E7EB")
        };
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(20) };
        root.Children.Add(Text(L.T("Anleitung"), size: 22));
        var layout = new Grid { Name = "GuideLayout", ColumnDefinitions = new("180,*"), ColumnSpacing = 20, Margin = new Thickness(0, 18, 0, 16) };
        Grid.SetRow(layout, 1); root.Children.Add(layout);
        var topics = new ListBox
        {
            Name = "GuideTopics", ItemsSource = UserGuide.Topics,
            Background = Brush("#1C1F22"), BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<GuideTopic>((topic, _) => new TextBlock
            {
                Text = L.T(topic?.Title ?? ""), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(3, 7), FontSize = 13
            })
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(topics, ScrollBarVisibility.Disabled);
        Avalonia.Automation.AutomationProperties.SetName(topics, L.T("Anleitung"));
        layout.Children.Add(topics);
        var article = new StackPanel { Name = "GuideArticle", Spacing = 12, Margin = new Thickness(0, 0, 16, 0) };
        var scroll = new ScrollViewer { Name = "GuideArticleScroll", Content = article, Focusable = true, TabIndex = 1, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll);
        void ShowTopic(GuideTopic topic)
        {
            article.Children.Clear();
            var title = Text(L.T(topic.Title), size: 21); title.Name = "GuideArticleTitle"; title.FontWeight = FontWeight.SemiBold;
            title.Foreground = Brush("#EF646B"); article.Children.Add(title);
            foreach (var section in topic.Sections)
            {
                if (!string.IsNullOrWhiteSpace(section.Heading))
                {
                    var heading = Text(L.T(section.Heading), size: 15); heading.FontWeight = FontWeight.SemiBold;
                    heading.Margin = new Thickness(0, 9, 0, 0); article.Children.Add(heading);
                }
                foreach (var paragraph in L.T(section.Body).Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries))
                    article.Children.Add(new TextBlock { Text = paragraph, FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap });
            }
            scroll.Offset = default;
        }
        topics.SelectionChanged += (_, _) => { if (topics.SelectedItem is GuideTopic selected) ShowTopic(selected); };
        topics.SelectedItem = UserGuide.Topics.FirstOrDefault(topic => topic.Id == topicId) ?? UserGuide.Topics.First();
        var close = new Button { Name = "GuideClose", Content = L.T("Schließen"), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100 };
        close.Click += (_, _) => window.Close(); Grid.SetRow(close, 2); root.Children.Add(close);
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; window.Close(); } };
        window.Content = root;
        return window;
    }
}
