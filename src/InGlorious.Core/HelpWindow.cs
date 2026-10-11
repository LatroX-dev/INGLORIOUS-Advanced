using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InGlorious.Core;

/// <summary>A local, read-only guide. It receives no editor, device or file-service reference.</summary>
public sealed class HelpWindow : Window
{
    internal ListBox TopicList { get; } = new() { Name = "GuideTopics", TabIndex = 0, BorderThickness = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    internal StackPanel Article { get; } = new() { Margin = new(26, 22, 26, 22) };
    internal ScrollViewer ArticleScroll { get; } = new() { Name = "GuideArticle", Focusable = true, TabIndex = 1, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    internal TextBlock TopicHeading { get; } = new() { FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 22) };
    internal Button CloseButton { get; } = new() { Name = "CloseGuide", IsCancel = true, TabIndex = 2, MinWidth = 105, Padding = new(18, 8, 18, 8) };
    public string SelectedTopicId => (TopicList.SelectedItem as ListBoxItem)?.Tag is GuideTopic topic ? topic.Id : "start";

    public HelpWindow(string initialTopic = "start")
    {
        Title = L.T("Anleitung") + " · INGLORIOUS Advanced";
        Width = 900; Height = 680; MinWidth = 680; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        FontFamily = new("Segoe UI"); FontSize = 14; UseLayoutRounding = true;
        var root = new Grid { Background = Background };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new Border { Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Stroke"), BorderThickness = new(0, 0, 0, 1), Padding = new(22, 16, 22, 16) };
        heading.Child = new TextBlock { Text = L.T("Anleitung"), FontSize = 20, FontWeight = FontWeights.SemiBold };
        root.Children.Add(heading);

        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new(205) }); body.ColumnDefinitions.Add(new());
        var navigation = new DockPanel { Margin = new(12, 22, 12, 16) };
        var topicsLabel = new TextBlock { Text = L.T("Themen"), FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new(10, 0, 10, 12) };
        DockPanel.SetDock(topicsLabel, Dock.Top); navigation.Children.Add(topicsLabel); navigation.Children.Add(TopicList);
        var navigationBorder = new Border { Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Stroke"), BorderThickness = new(0, 0, 1, 0), Child = navigation };
        body.Children.Add(navigationBorder);
        AutomationProperties.SetName(TopicList, L.T("Themen"));
        ScrollViewer.SetHorizontalScrollBarVisibility(TopicList, ScrollBarVisibility.Disabled);
        foreach (var topic in UserGuide.Topics)
        {
            var item = new ListBoxItem { Tag = topic, Padding = new(10, 10, 10, 10), Margin = new(0, 0, 0, 3),
                Content = new TextBlock { Text = L.T(topic.Title), TextWrapping = TextWrapping.Wrap, FontSize = 13 } };
            AutomationProperties.SetName(item, L.T(topic.Title)); TopicList.Items.Add(item);
        }
        ArticleScroll.Content = Article; Grid.SetColumn(ArticleScroll, 1); body.Children.Add(ArticleScroll);
        Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new Border { BorderBrush = (Brush)FindResource("Stroke"), BorderThickness = new(0, 1, 0, 0), Padding = new(20, 12, 20, 12) };
        CloseButton.Content = L.T("Schließen"); CloseButton.HorizontalAlignment = HorizontalAlignment.Right;
        CloseButton.Click += (_, _) => Close(); footer.Child = CloseButton;
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        TopicList.SelectionChanged += (_, _) => RenderTopic();
        SelectTopic(initialTopic);
        Loaded += (_, _) => TopicList.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    internal void SelectTopic(string topicId)
    {
        var items = TopicList.Items.Cast<ListBoxItem>().ToArray();
        TopicList.SelectedItem = items.FirstOrDefault(item => ((GuideTopic)item.Tag).Id == topicId)
            ?? items.FirstOrDefault(item => ((GuideTopic)item.Tag).Id == "start") ?? items.FirstOrDefault();
    }

    private void RenderTopic()
    {
        Article.Children.Clear();
        if ((TopicList.SelectedItem as ListBoxItem)?.Tag is not GuideTopic topic) return;
        TopicHeading.Text = L.T(topic.Title); Article.Children.Add(TopicHeading);
        AutomationProperties.SetName(ArticleScroll, L.T(topic.Title));
        foreach (var section in topic.Sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Heading))
                Article.Children.Add(new TextBlock { Text = L.T(section.Heading), FontSize = 16, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Accent"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
            foreach (var paragraph in L.T(section.Body).Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Article.Children.Add(new TextBlock { Text = paragraph, FontSize = 14, LineHeight = 23,
                    TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 13) });
            Article.Children.Add(new Border { Height = 8 });
        }
        ArticleScroll.ScrollToTop();
    }
}
