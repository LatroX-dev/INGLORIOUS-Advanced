using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace InGlorious.Core;

public sealed class KeyBindingSelectionWindow : Window
{
    private readonly Dictionary<int, CheckBox> _layers = [];
    internal IReadOnlyDictionary<int, CheckBox> LayerChoices => _layers;
    internal CheckBox SliderChoice { get; }
    internal Button ConfirmButton { get; }
    public KeyBindingSelection? Selection { get; private set; }

    public KeyBindingSelectionWindow(bool importing, IReadOnlyList<(int Index, string Name)> layers, bool canIncludeSlider)
    {
        if (layers.Count is < 1 or > 4 || layers.Any(layer => layer.Index is < 0 or > 3) || layers.Select(layer => layer.Index).Distinct().Count() != layers.Count)
            throw new ArgumentException("One to four distinct layer indices are required.", nameof(layers));
        Title = L.T(importing ? "Tastenbelegung importieren" : "Tastenbelegung exportieren");
        Width = 560; MinWidth = 440; SizeToContent = SizeToContent.Height; MaxHeight = Math.Min(680, SystemParameters.WorkArea.Height);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text");
        FontFamily = new("Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        var root = new Grid { Margin = new(24) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock
        {
            Text = L.T(importing ? "Nur die ausgewählten Ebenen werden ersetzt. Farben und Effekte bleiben erhalten." : "Wähle die Ebenen für den Export."),
            Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 18)
        });
        var content = new StackPanel();
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var choices = new StackPanel { Margin = new(13, 5, 13, 5) };
        content.Children.Add(new Border { Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Stroke"), BorderThickness = new(1), CornerRadius = new(3), Child = choices });
        var style = CreateChoiceStyle();
        foreach (var (index, name) in layers.OrderBy(layer => layer.Index))
        {
            var label = L.F($"Ebene {index} · {name}");
            var choice = CreateChoice(label, style); choice.IsChecked = true;
            _layers.Add(index, choice); choices.Children.Add(choice);
        }
        SliderChoice = CreateChoice(L.T("Schieberegler einbeziehen"), style);
        SliderChoice.IsChecked = false;
        var slider = new StackPanel { Visibility = canIncludeSlider ? Visibility.Visible : Visibility.Collapsed, Margin = new(0, 16, 0, 0) };
        slider.Children.Add(SliderChoice);
        slider.Children.Add(new TextBlock { Text = L.T("Gilt für alle Ebenen."), Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new(28, 0, 0, 0), FontSize = 12 });
        content.Children.Add(slider);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        var cancel = new Button { Content = L.T("Abbrechen"), IsCancel = true, MinWidth = 100, Margin = new(0, 0, 10, 0) }; footer.Children.Add(cancel);
        ConfirmButton = new Button { Content = L.T(importing ? "Übernehmen" : "Exportieren …"), IsDefault = true, MinWidth = 112, Style = (Style)FindResource("PrimaryButton") };
        UiIcons.SetKind(ConfirmButton, importing ? "import" : "export"); footer.Children.Add(ConfirmButton);
        foreach (var choice in _layers.Values)
        {
            choice.Checked += (_, _) => UpdateChoice(); choice.Unchecked += (_, _) => UpdateChoice();
        }
        ConfirmButton.Click += (_, _) =>
        {
            var selected = _layers.Where(layer => layer.Value.IsChecked == true).Select(layer => layer.Key).ToArray();
            if (selected.Length == 0) return;
            Selection = new(selected, canIncludeSlider && SliderChoice.IsChecked == true);
            DialogResult = true;
        };
        UpdateChoice();
    }

    private void UpdateChoice() => ConfirmButton.IsEnabled = _layers.Values.Any(choice => choice.IsChecked == true);

    private static CheckBox CreateChoice(string label, Style style)
    {
        var choice = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Style = style, MinHeight = 40 };
        System.Windows.Automation.AutomationProperties.SetName(choice, label);
        return choice;
    }

    internal static Style CreateChoiceStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="CheckBox">
          <Setter Property="Foreground" Value="{DynamicResource Text}"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="FocusVisualStyle" Value="{DynamicResource KeyboardFocus}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
            <Border Background="Transparent" Padding="0,8"><DockPanel LastChildFill="True">
              <Border x:Name="Box" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="18" Height="18" VerticalAlignment="Center" Margin="0,0,10,0" BorderThickness="1" CornerRadius="2" BorderBrush="{DynamicResource Stroke}" Background="{DynamicResource WindowBackground}">
                <TextBlock x:Name="Tick" Visibility="Collapsed" Text="✓" Foreground="#241317" FontSize="13" FontWeight="Bold" HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ContentPresenter VerticalAlignment="Center"/>
            </DockPanel></Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True"><Setter TargetName="Box" Property="Background" Value="{DynamicResource Accent}"/><Setter TargetName="Tick" Property="Visibility" Value="Visible"/></Trigger>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
}
