using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace InGlorious.Core;

/// <summary>A local, opaque RGB colour editor. It has no device or project dependencies.</summary>
public sealed class ColorPickerWindow : Window
{
    private readonly Grid _svSurface;
    private readonly Canvas _selectionCanvas = new() { IsHitTestVisible = false };
    private readonly Grid _selectionRing;
    private readonly SolidColorBrush _hueBrush = new();
    private readonly Slider _hueSlider;
    private readonly TextBox _hexInput;
    private readonly TextBox _redInput;
    private readonly TextBox _greenInput;
    private readonly TextBox _blueInput;
    private readonly Border _newPreview;
    private readonly TextBlock _svReadout;
    private readonly TextBlock _error;
    private readonly Button _applyButton;
    private double _hue;
    private double _saturation;
    private double _value;
    private Color _color;
    private bool _syncing;
    private bool _inputsValid = true;
    private readonly string _palettePath;
    private IReadOnlyList<string> _favoriteColors = [];
    private string? _favoriteError;
    private readonly WrapPanel _favoriteSwatches = new();
    private readonly TextBlock _favoriteStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 5, 0, 0) };
    private readonly Button _saveFavorite = new();
    private readonly Button _deleteFavorite = new();
    public bool FavoritesChanged { get; private set; }

    public string SelectedHex { get; private set; }

    public ColorPickerWindow(string initialHex, string? palettePath = null)
    {
        if (!TryParseHex(initialHex, out var initialColor))
            throw new ArgumentException(L.T("Die Ausgangsfarbe muss sechs Hex-Ziffern enthalten, z. B. #62F6FF."), nameof(initialHex));

        SelectedHex = ToHex(initialColor);
        _palettePath = palettePath ?? ColorPaletteStore.DefaultPath;
        Title = L.T("Farbe auswählen");
        Width = 560;
        Height = Math.Min(780, SystemParameters.WorkArea.Height - 40);
        MinWidth = 480;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush("#141618");
        Foreground = Brush("#E4E7EB");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;
        NameScope.SetNameScope(this, new NameScope());

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = new Border { Background = Background, Child = root };
        root.Children.Add(Text(L.T("Farbe auswählen"), 18, "#E4E7EB", FontWeights.SemiBold));

        _svSurface = Named("SaturationValueSurface", new Grid
        {
            Background = _hueBrush,
            ClipToBounds = true,
            Focusable = true,
            Cursor = Cursors.Cross,
            ToolTip = L.T("Sättigung: links/rechts. Helligkeit: oben/unten. Pfeiltasten ändern um 1 %, mit Umschalt um 5 %.")
        });
        KeyboardNavigation.SetTabIndex(_svSurface, 0);
        AutomationProperties.SetName(_svSurface, L.T("Sättigung und Helligkeit"));
        AutomationProperties.SetHelpText(_svSurface, L.T("Pfeiltasten: Sättigung links/rechts, Helligkeit oben/unten. Umschalt für größere Schritte."));
        _svSurface.Children.Add(new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0))
        });
        _svSurface.Children.Add(new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1))
        });
        _selectionRing = new Grid { Width = 14, Height = 14, IsHitTestVisible = false };
        _selectionRing.Children.Add(new Ellipse { Stroke = Brushes.Black, StrokeThickness = 3, Margin = new Thickness(1) });
        _selectionRing.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 1, Margin = new Thickness(1) });
        _selectionCanvas.Children.Add(_selectionRing);
        _svSurface.Children.Add(_selectionCanvas);
        var fieldBorder = new Border
        {
            BorderBrush = Brush("#49535E"), BorderThickness = new Thickness(1),
            Child = _svSurface, Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetRow(fieldBorder, 1);
        root.Children.Add(fieldBorder);
        _svSurface.SizeChanged += (_, _) => UpdateSelectionPosition();
        _svSurface.IsKeyboardFocusWithinChanged += (_, _) => fieldBorder.BorderBrush = Brush(_svSurface.IsKeyboardFocusWithin ? "#EF646B" : "#49535E");
        _svSurface.MouseLeftButtonDown += (_, e) =>
        {
            _svSurface.Focus();
            _svSurface.CaptureMouse();
            PickFromPoint(e.GetPosition(_svSurface));
            e.Handled = true;
        };
        _svSurface.MouseMove += (_, e) =>
        {
            if (_svSurface.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
                PickFromPoint(e.GetPosition(_svSurface));
        };
        _svSurface.MouseLeftButtonUp += (_, e) =>
        {
            if (!_svSurface.IsMouseCaptured) return;
            PickFromPoint(e.GetPosition(_svSurface));
            _svSurface.ReleaseMouseCapture();
            e.Handled = true;
        };
        _svSurface.PreviewKeyDown += Surface_KeyDown;

        var hueArea = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        hueArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        hueArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(hueArea, 2);
        root.Children.Add(hueArea);
        _hueSlider = Named("HueSlider", new Slider
        {
            Minimum = 0, Maximum = 359.999, SmallChange = 1, LargeChange = 15,
            IsMoveToPointEnabled = true, Height = 26, Margin = new Thickness(0, 5, 0, 0),
            Template = CreateHueTemplate(), ToolTip = L.T("Farbton · Pfeiltasten ändern um 1°, Bild auf/ab um 15°")
        });
        KeyboardNavigation.SetTabIndex(_hueSlider, 1);
        AutomationProperties.SetName(_hueSlider, L.T("Farbton in Grad"));
        _hueSlider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _hue = e.NewValue;
            UpdateFromHsv();
        };
        var hueLabel = new Label { Content = L.T("_Farbton"), Target = _hueSlider, Padding = new Thickness(0), Foreground = Brush("#A0A7B0") };
        hueArea.Children.Add(hueLabel);
        _svReadout = Text("", 11, "#A0A7B0");
        _svReadout.HorizontalAlignment = HorizontalAlignment.Right;
        _svReadout.VerticalAlignment = VerticalAlignment.Center;
        hueArea.Children.Add(_svReadout);
        Grid.SetRow(_hueSlider, 1);
        hueArea.Children.Add(_hueSlider);

        var previews = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(previews, 3);
        root.Children.Add(previews);
        previews.Children.Add(Preview(L.T("Ausgangsfarbe"), initialColor, new Thickness(0, 0, 6, 0), out _));
        var newPreviewArea = Preview(L.T("Neue Farbe"), initialColor, new Thickness(6, 0, 0, 0), out var newPreview);
        _newPreview = newPreview;
        Grid.SetColumn(newPreviewArea, 1);
        previews.Children.Add(newPreviewArea);

        var inputs = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) inputs.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(inputs, 4);
        root.Children.Add(inputs);
        _hexInput = Input("HexInput", 7, L.T("Hex-Farbwert"), 2);
        _hexInput.FontFamily = new FontFamily("Consolas");
        _redInput = Input("RedInput", 3, L.T("Rot, 0 bis 255"), 3);
        _greenInput = Input("GreenInput", 3, L.T("Grün, 0 bis 255"), 4);
        _blueInput = Input("BlueInput", 3, L.T("Blau, 0 bis 255"), 5);
        AddInput(inputs, "_Hex", _hexInput, 0);
        AddInput(inputs, "_R", _redInput, 1);
        AddInput(inputs, "_G", _greenInput, 2);
        AddInput(inputs, "_B", _blueInput, 3);
        _hexInput.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            if (TryParseHex(_hexInput.Text, out var color)) SetColor(color, _hexInput);
            else SetValidity(false, L.T("Hex: sechs Ziffern eingeben, z. B. #62F6FF."));
        };
        foreach (var input in new[] { _redInput, _greenInput, _blueInput })
            input.TextChanged += (_, _) => RgbEdited(input);
        foreach (var input in new[] { _hexInput, _redInput, _greenInput, _blueInput })
            input.LostKeyboardFocus += (_, _) => { if (_inputsValid) Synchronize(); };

        var favorites = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        favorites.Children.Add(Text(L.T("Favoriten"), 12, "#E4E7EB", FontWeights.SemiBold));
        Named("FavoriteSwatches", _favoriteSwatches);
        favorites.Children.Add(new ScrollViewer { Content = _favoriteSwatches, MaxHeight = 84, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 6, 0, 0) });
        var favoriteActions = new WrapPanel();
        Named("SaveFavoriteButton", _saveFavorite); _saveFavorite.Content = L.T("Als Favorit speichern"); _saveFavorite.Margin = new Thickness(0, 0, 6, 0);
        Named("DeleteFavoriteButton", _deleteFavorite); _deleteFavorite.Content = L.T("Favorit löschen");
        _saveFavorite.Click += (_, _) => ChangeFavorite(remove: false);
        _deleteFavorite.Click += (_, _) => ChangeFavorite(remove: true);
        favoriteActions.Children.Add(_saveFavorite); favoriteActions.Children.Add(_deleteFavorite);
        favorites.Children.Add(favoriteActions); favorites.Children.Add(_favoriteStatus);
        Grid.SetRow(favorites, 5); root.Children.Add(favorites);

        _error = Text("", 11, "#EF646B");
        _error.TextWrapping = TextWrapping.Wrap;
        _error.MinHeight = 18;
        _error.Margin = new Thickness(0, 7, 0, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        Grid.SetRow(_error, 6);
        root.Children.Add(_error);

        var footer = new Border { BorderBrush = Brush("#353A40"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 10, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("Abbrechen"), IsCancel = true, Padding = new Thickness(15, 8, 15, 8), MinWidth = 96 };
        KeyboardNavigation.SetTabIndex(cancel, 6);
        _applyButton = Named("ApplyButton", new Button { Content = L.T("Übernehmen"), IsDefault = true, Padding = new Thickness(15, 8, 15, 8), MinWidth = 106, Margin = new Thickness(8, 0, 0, 0) });
        if (TryFindResource("PrimaryButton") is Style primaryStyle) _applyButton.Style = primaryStyle;
        else { _applyButton.Background = Brush("#EF646B"); _applyButton.Foreground = Brush("#241317"); }
        KeyboardNavigation.SetTabIndex(_applyButton, 7);
        _applyButton.Click += (_, _) => { if (_inputsValid) DialogResult = true; };
        actions.Children.Add(cancel);
        actions.Children.Add(_applyButton);
        footer.Child = actions;
        Grid.SetRow(footer, 7);
        root.Children.Add(footer);

        SetColor(initialColor);
        LoadFavorites();
        Loaded += (_, _) => { UpdateSelectionPosition(); _svSurface.Focus(); };
        Closed += (_, _) => { if (_svSurface.IsMouseCaptured) _svSurface.ReleaseMouseCapture(); };
    }

    private void LoadFavorites()
    {
        var loaded = ColorPaletteStore.Load(_palettePath); _favoriteColors = loaded.Colors; _favoriteError = loaded.Error;
        RenderFavorites();
    }
    private void RenderFavorites()
    {
        _favoriteSwatches.Children.Clear();
        foreach (var color in _favoriteColors)
        {
            var button = new Button { Width = 34, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), Background = Brush(color), Tag = color, ToolTip = color };
            AutomationProperties.SetName(button, L.T("Gespeicherte Farbe ") + color);
            button.Click += (_, _) => { if (TryParseHex(color, out var chosen)) SetColor(chosen); };
            _favoriteSwatches.Children.Add(button);
        }
        _favoriteStatus.Text = _favoriteError ?? L.T("Für alle Geräte und Profile. Änderungen an Favoriten werden sofort gespeichert.");
        UpdateFavoriteState();
    }
    private void UpdateFavoriteState()
    {
        var exists = _favoriteColors.Contains(SelectedHex, StringComparer.Ordinal);
        _saveFavorite.IsEnabled = _inputsValid && _favoriteError is null && !exists && _favoriteColors.Count < ColorPaletteStore.MaximumColors;
        _deleteFavorite.IsEnabled = _inputsValid && _favoriteError is null && exists;
        foreach (Button button in _favoriteSwatches.Children)
        {
            var selected = _inputsValid && Equals(button.Tag, SelectedHex);
            button.BorderBrush = Brush(selected ? "#FFFFFF" : "#59616A"); button.BorderThickness = new Thickness(selected ? 3 : 1);
        }
    }
    private void ChangeFavorite(bool remove)
    {
        if (!_inputsValid || _favoriteError is not null) return;
        try
        {
            _favoriteColors = remove ? ColorPaletteStore.Remove(_palettePath, SelectedHex) : ColorPaletteStore.Add(_palettePath, SelectedHex);
            FavoritesChanged = true; RenderFavorites();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _favoriteStatus.Text = ex.Message; }
    }

    private T Named<T>(string name, T element) where T : FrameworkElement
    {
        element.Name = name;
        RegisterName(name, element);
        return element;
    }

    private TextBox Input(string name, int maxLength, string accessibleName, int tabIndex)
    {
        var input = Named(name, new TextBox
        {
            MaxLength = maxLength, Height = 36, Padding = new Thickness(9, 7, 9, 7),
            Background = Brush("#15181B"), Foreground = Brush("#E4E7EB"),
            BorderBrush = Brush("#414A53"), CaretBrush = Brush("#EF646B"),
            VerticalContentAlignment = VerticalAlignment.Center, ToolTip = accessibleName
        });
        AutomationProperties.SetName(input, accessibleName);
        KeyboardNavigation.SetTabIndex(input, tabIndex);
        return input;
    }

    private static void AddInput(Grid host, string label, TextBox input, int column)
    {
        var stack = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) };
        stack.Children.Add(new Label { Content = label, Target = input, Foreground = Brush("#A0A7B0"), Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(input);
        Grid.SetColumn(stack, column);
        host.Children.Add(stack);
    }

    private static StackPanel Preview(string label, Color color, Thickness margin, out Border chip)
    {
        var stack = new StackPanel { Margin = margin };
        stack.Children.Add(Text(label, 11, "#A0A7B0"));
        chip = new Border { Background = new SolidColorBrush(color), Height = 32, BorderBrush = Brush("#59616A"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(chip);
        return stack;
    }

    private void RgbEdited(TextBox origin)
    {
        if (_syncing) return;
        if (!byte.TryParse(_redInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(_greenInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(_blueInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var b))
        {
            SetValidity(false, L.T("RGB: für Rot, Grün und Blau jeweils 0 bis 255 eingeben."));
            return;
        }
        SetColor(Color.FromRgb(r, g, b), origin);
    }

    private void SetColor(Color color, TextBox? origin = null)
    {
        _color = color;
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        _value = max;
        _saturation = max == 0 ? 0 : delta / max;
        // Preserve the chosen hue when RGB is grey or black; these colours have no hue.
        if (delta > 0)
        {
            var sector = max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
            _hue = (sector * 60 + 360) % 360;
        }
        Synchronize(origin);
    }

    private void PickFromPoint(Point point)
    {
        if (_svSurface.ActualWidth <= 0 || _svSurface.ActualHeight <= 0) return;
        _saturation = Math.Clamp(point.X / _svSurface.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(point.Y / _svSurface.ActualHeight, 0, 1);
        UpdateFromHsv();
    }

    private void Surface_KeyDown(object sender, KeyEventArgs e)
        => e.Handled = EditSaturationValue(e.Key, Keyboard.Modifiers);

    internal bool EditSaturationValue(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return false;
        var step = (modifiers & ModifierKeys.Shift) != 0 ? 0.05 : 0.01;
        switch (key)
        {
            case Key.Left: _saturation = Math.Clamp(_saturation - step, 0, 1); break;
            case Key.Right: _saturation = Math.Clamp(_saturation + step, 0, 1); break;
            case Key.Up: _value = Math.Clamp(_value + step, 0, 1); break;
            case Key.Down: _value = Math.Clamp(_value - step, 0, 1); break;
            default: return false;
        }
        UpdateFromHsv();
        return true;
    }

    private void UpdateFromHsv()
    {
        _color = FromHsv(_hue, _saturation, _value);
        Synchronize();
    }

    private void Synchronize(TextBox? origin = null)
    {
        _syncing = true;
        try
        {
            SelectedHex = ToHex(_color);
            if (origin != _hexInput) _hexInput.Text = SelectedHex;
            if (origin != _redInput) _redInput.Text = _color.R.ToString(CultureInfo.InvariantCulture);
            if (origin != _greenInput) _greenInput.Text = _color.G.ToString(CultureInfo.InvariantCulture);
            if (origin != _blueInput) _blueInput.Text = _color.B.ToString(CultureInfo.InvariantCulture);
            _hueSlider.Value = _hue;
            _hueBrush.Color = FromHsv(_hue, 1, 1);
            _newPreview.Background = new SolidColorBrush(_color);
            _svReadout.Text = $"{Math.Round(_hue):0}°  ·  S {Math.Round(_saturation * 100):0} %  ·  V {Math.Round(_value * 100):0} %";
            SetValidity(true);
            UpdateSelectionPosition();
        }
        finally { _syncing = false; }
    }

    private void SetValidity(bool valid, string message = "")
    {
        _inputsValid = valid;
        _error.Text = message;
        _applyButton.IsEnabled = valid;
        UpdateFavoriteState();
    }

    private void UpdateSelectionPosition()
    {
        Canvas.SetLeft(_selectionRing, Math.Clamp(_saturation * _svSurface.ActualWidth - 7, 0, Math.Max(0, _svSurface.ActualWidth - 14)));
        Canvas.SetTop(_selectionRing, Math.Clamp((1 - _value) * _svSurface.ActualHeight - 7, 0, Math.Max(0, _svSurface.ActualHeight - 14)));
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var value = text?.Trim();
        if (value?.StartsWith('#') == true) value = value[1..];
        if (value?.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var sector = ((hue % 360) + 360) % 360 / 60;
        var chroma = value * saturation;
        var intermediate = chroma * (1 - Math.Abs(sector % 2 - 1));
        var offset = value - chroma;
        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, intermediate, 0d),
            1 => (intermediate, chroma, 0d),
            2 => (0d, chroma, intermediate),
            3 => (0d, intermediate, chroma),
            4 => (intermediate, 0d, chroma),
            _ => (chroma, 0d, intermediate)
        };
        static byte Component(double channel) => (byte)Math.Clamp((int)Math.Round(channel * 255, MidpointRounding.AwayFromZero), 0, 255);
        return Color.FromRgb(Component(r + offset), Component(g + offset), Component(b + offset));
    }

    private static TextBlock Text(string value, double size, string color, FontWeight? weight = null) => new()
    {
        Text = value, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal
    };

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static ControlTemplate CreateHueTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid>
            <Border Height="12" Margin="5,0" VerticalAlignment="Center" BorderBrush="#59616A" BorderThickness="1">
              <Border.Background><LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                <GradientStop Color="#FF0000" Offset="0"/><GradientStop Color="#FFFF00" Offset="0.166667"/>
                <GradientStop Color="#00FF00" Offset="0.333333"/><GradientStop Color="#00FFFF" Offset="0.5"/>
                <GradientStop Color="#0000FF" Offset="0.666667"/><GradientStop Color="#FF00FF" Offset="0.833333"/>
                <GradientStop Color="#FF0000" Offset="1"/>
              </LinearGradientBrush></Border.Background>
            </Border>
            <Track x:Name="PART_Track" Height="24" Orientation="Horizontal" IsDirectionReversed="False"
                   Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
              <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False" Opacity="0"/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Width="10" Height="24" Cursor="SizeWE"><Thumb.Template><ControlTemplate TargetType="Thumb">
                <Border Background="#E4E7EB" BorderBrush="#141618" BorderThickness="2" CornerRadius="2"/>
              </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False" Opacity="0"/></Track.IncreaseRepeatButton>
            </Track>
            <Border x:Name="FocusOutline" IsHitTestVisible="False" BorderBrush="Transparent" BorderThickness="1"/>
          </Grid>
          <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True">
            <Setter TargetName="FocusOutline" Property="BorderBrush" Value="#EF646B"/>
          </Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
}
