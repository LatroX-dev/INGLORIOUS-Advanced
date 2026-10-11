using System.Windows;
using System.Windows.Controls;

namespace InGlorious.Core;

public sealed class OsActionsWindow : Window
{
    public OsAction? SelectedAction { get; private set; }
    public OsActionsWindow(bool macrosOnly)
    {
        Title = L.T("Aktionen für das Betriebssystem"); Width = 610; Height = 660; MinWidth = 510; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("WindowBackground");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var cancel = new Button { Content = L.T("Abbrechen"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; buttons.Children.Add(cancel);
        var accept = new Button { Content = macrosOnly ? L.T("Als Makro hinzufügen") : L.T("Zuweisen"), IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") }; buttons.Children.Add(accept);
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = L.T("Aktionen für das Betriebssystem"), FontSize = 21, Margin = new Thickness(0, 0, 0, 18) });
        header.Children.Add(new TextBlock { Text = L.T("Zielsystem"), Margin = new Thickness(0, 0, 0, 6) });
        var os = new ComboBox { ItemsSource = new[] { new Choice("windows", "Windows"), new Choice("macos", "macOS"), new Choice("linux", "Linux · GNOME") }, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = OsActionCatalog.CurrentSystem };
        header.Children.Add(os);
        header.Children.Add(new TextBlock { Text = L.T("Tastaturlayout des Betriebssystems"), Margin = new Thickness(0, 12, 0, 6) });
        var inputLanguage = System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage.TwoLetterISOLanguageName;
        var layout = new ComboBox { ItemsSource = new[] { new Choice("de", "Deutsch · QWERTZ"), new Choice("en", "English · QWERTY"), new Choice("fr", "Français · AZERTY") }, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = inputLanguage is "de" or "fr" ? inputLanguage : "en" };
        header.Children.Add(layout);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), FontSize = 12 }; header.Children.Add(hint);
        var list = new ListBox { DisplayMemberPath = "Label" }; root.Children.Add(list);
        void Refresh()
        {
            var selected = (list.SelectedItem as OsAction)?.Id;
            var actions = OsActionCatalog.For((string)os.SelectedValue, (string)layout.SelectedValue).Where(a => !macrosOnly || a.DirectBinding is null).ToList();
            list.ItemsSource = actions; list.SelectedItem = actions.FirstOrDefault(a => a.Id == selected) ?? actions.FirstOrDefault();
            hint.Text = L.T("Wird als Taste oder Tastenkombination im Gerät gespeichert. INGLORIOUS muss dafür nicht laufen.") + " " + L.T("Geänderte System-Shortcuts und das gewählte Tastaturlayout müssen dazu passen.");
        }
        os.SelectionChanged += (_, _) => Refresh(); layout.SelectionChanged += (_, _) => Refresh(); Refresh();
        accept.Click += (_, _) => { if (list.SelectedItem is not OsAction action) return; SelectedAction = action; DialogResult = true; };
    }
    private sealed record Choice(string Id, string Label);
}
