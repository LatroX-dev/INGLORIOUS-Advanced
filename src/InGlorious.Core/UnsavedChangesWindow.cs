using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InGlorious.Core;

public enum UnsavedChangesChoice { Cancel, Save, Discard }

public sealed class UnsavedChangesWindow : Window
{
    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;
    internal Button SaveButton { get; }
    internal Button DiscardButton { get; }
    internal Button CancelButton { get; }

    public UnsavedChangesWindow(string heading, string message, IEnumerable<string>? profiles = null, string? saveCaption = null)
    {
        Title = heading; Width = 610; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        var shell = new Border { BorderBrush = (Brush)FindResource("Stroke"), BorderThickness = new(1), Background = Background };
        var root = new StackPanel(); shell.Child = root; Content = shell;

        var header = new Grid { Background = (Brush)FindResource("Surface"), Margin = new(0) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(22, 12, 20, 12) };
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/inglorious-mark.png")), Width = 30, Height = 30, Margin = new(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "INGLORIOUS", FontFamily = new("Bahnschrift SemiCondensed"), FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(brand);
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        var close = new Button { Content = "×", FontSize = 24, Width = 36, Height = 34, Padding = new(0), Margin = new(0, 0, 12, 0), Style = (Style)FindResource("QuietButton"), ToolTip = L.T("Schließen") };
        System.Windows.Automation.AutomationProperties.SetName(close, L.T("Schließen"));
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); header.Children.Add(close); root.Children.Add(header);
        root.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Stroke") });

        var body = new StackPanel { Margin = new(24, 23, 24, 22) }; root.Children.Add(body);
        body.Children.Add(new TextBlock { Text = heading, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message.Trim(), Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new(0, 10, 0, 0) });
        var names = (profiles ?? []).ToArray();
        if (names.Length > 0)
        {
            var list = new StackPanel();
            foreach (var name in names)
            {
                var item = new Border { BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new(2, 0, 0, 0), Padding = new(12, 7, 10, 7), Margin = new(0, 2, 0, 2) };
                item.Child = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
                list.Children.Add(item);
            }
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 18, 0, 0) });
        }
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(18, 0, 18, 20) };
        CancelButton = CreateButton(L.T("Abbrechen"), UnsavedChangesChoice.Cancel);
        CancelButton.IsCancel = true; CancelButton.IsDefault = true;
        DiscardButton = CreateButton(L.T("Verwerfen"), UnsavedChangesChoice.Discard);
        SaveButton = CreateButton(saveCaption ?? L.T("Speichern"), UnsavedChangesChoice.Save);
        SaveButton.Style = (Style)FindResource("PrimaryButton");
        UiIcons.SetKind(DiscardButton, "delete"); UiIcons.SetKind(SaveButton, saveCaption is null ? "save" : "check");
        actions.Children.Add(CancelButton); actions.Children.Add(DiscardButton); actions.Children.Add(SaveButton); root.Children.Add(actions);
        Loaded += (_, _) => CancelButton.Focus();
    }

    private Button CreateButton(string caption, UnsavedChangesChoice choice)
    {
        var button = new Button { Content = caption, MinWidth = 112, MinHeight = 38, Padding = new(16, 9, 16, 9), Margin = new(6, 4, 6, 0) };
        button.Click += (_, _) => { Choice = choice; Close(); };
        return button;
    }

    public static UnsavedChangesChoice Ask(Window owner, string heading, string message, IEnumerable<string>? profiles = null, string? saveCaption = null)
    {
        var dialog = new UnsavedChangesWindow(heading, message, profiles, saveCaption) { Owner = owner };
        dialog.ShowDialog(); return dialog.Choice;
    }

    public static void ShowInformation(Window owner, string heading, string message)
    {
        var dialog = new UnsavedChangesWindow(heading, message) { Owner = owner };
        dialog.SaveButton.Visibility = dialog.DiscardButton.Visibility = Visibility.Collapsed;
        dialog.CancelButton.Content = L.T("Schließen");
        dialog.CancelButton.Style = (Style)dialog.FindResource("PrimaryButton");
        dialog.ShowDialog();
    }
}
