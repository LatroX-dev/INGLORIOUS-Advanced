using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
namespace InGlorious.Core;
public sealed class CoreImportWindow : Window
{
    public AppProject? ImportedProject { get; private set; }
    public CoreImportSelection? Selection { get; private set; }
    public CoreImportWindow(AppProject current, CoreImportPreview? initial = null)
    {
        var source = ProjectService.Clone(current);
        Title = L.T("CORE-Einstellungen importieren"); Width = 650; Height = 680; MinWidth = 500; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Background = (Brush)FindResource("WindowBackground"); Foreground = (Brush)FindResource("Text"); FontFamily = new("Segoe UI"); FontSize = 13;
        var body = new StackPanel { Margin = new Thickness(24) };
        TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        body.Children.Add(Text(DeviceCatalog.Get(source.DeviceId).Name));
        body.Children.Add(Text(L.T("Übernimmt passende Einstellungen in eine ungespeicherte Profilkopie. Die Tastatur bleibt unverändert.")));
        var folder = new Button { Content = L.T("CORE-Ordner auswählen …"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) }; body.Children.Add(folder);
        var status = Text(""); body.Children.Add(status);
        var colors = new CheckBox { Name = "CoreImportColors", Content = L.T("Einzelfarben und Helligkeit (statisch)"), Margin = new Thickness(0, 0, 0, 10) };
        var bindings = new CheckBox { Name = "CoreImportBindings", Content = L.T("Einfache Tastenbelegungen"), Margin = new Thickness(0, 0, 0, 14) };
        colors.Foreground = bindings.Foreground = Foreground;
        body.Children.Add(colors); body.Children.Add(bindings);
        var details = new TextBox { Name = "CoreImportPreview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, Margin = new Thickness(0, 0, 0, 14), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(details);
        body.Children.Add(Text(L.T("Die aktive CORE-Ebene wird zur Basisebene der Kopie. Andere Ebenen, Makros, Effekte, Seitenlichter und Geräteeinstellungen werden nicht importiert. Nicht zugeordnete Werte bleiben wie im aktuellen Profil.")));
        body.Children.Add(Text(L.T("Prüfe die Kopie. Mit „Übertragen“ speicherst du sie später auf der Tastatur.")));
        var apply = new Button { Name = "ApplyCoreImport", Content = L.T("Profilkopie öffnen"), IsEnabled = false, Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = L.T("Abbrechen") }; cancel.Click += (_, _) => Close();
        var actions = new WrapPanel(); actions.Children.Add(apply); actions.Children.Add(cancel); body.Children.Add(actions);
        CoreImportPreview? preview = null;
        void Update() { apply.IsEnabled = preview is not null && (colors.IsChecked == true && preview.Colors.Count > 0 || bindings.IsChecked == true && preview.Bindings.Count > 0); details.Text = preview?.DescribeChanges(colors.IsChecked == true, bindings.IsChecked == true) ?? ""; }
        void Set(CoreImportPreview value)
        {
            preview = value;
            status.Text = L.F($"CORE-Ebene {value.ActiveCoreLayer + 1}: {value.Colors.Count} Farben, {value.Bindings.Count} Belegungen. Ausgelassen: {value.OmittedColors} Farben und {value.OmittedBindings} Belegungen.");
            colors.IsEnabled = value.Colors.Count > 0; colors.IsChecked = colors.IsEnabled;
            bindings.IsEnabled = value.Bindings.Count > 0; bindings.IsChecked = bindings.IsEnabled; Update();
        }
        void Read(string? path)
        {
            preview = null; colors.IsChecked = bindings.IsChecked = false; colors.IsEnabled = bindings.IsEnabled = false; Update();
            try { if (path is null || !Directory.Exists(path)) { status.Text = L.T("Keine passenden CORE-Einstellungen gefunden."); return; } Set(CoreSettingsImport.ReadFolder(path, source.DeviceId)); }
            catch (InvalidDataException ex) { status.Text = ex.Message; }
            catch { status.Text = L.T("Die CORE-Dateien konnten nicht gelesen werden. Schließe CORE oder wähle einen anderen Ordner."); }
        }
        colors.Checked += (_, _) => Update(); colors.Unchecked += (_, _) => Update(); bindings.Checked += (_, _) => Update(); bindings.Unchecked += (_, _) => Update();
        folder.Click += (_, _) => { var picker = new OpenFolderDialog { Title = L.T("CORE-Ordner auswählen …"), Multiselect = false }; if (picker.ShowDialog(this) == true) Read(picker.FolderName); };
        apply.Click += (_, _) => { if (apply.IsEnabled && preview is not null) { Selection = new(preview, colors.IsChecked == true, bindings.IsChecked == true); ImportedProject = Selection.ApplyToCopy(source); DialogResult = true; } };
        if (initial is not null) Set(initial); else Read(CoreSettingsImport.DefaultFolder);
        Content = new ScrollViewer { Content = body, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
