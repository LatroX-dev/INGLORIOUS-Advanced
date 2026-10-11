using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace InGlorious.Core;

public partial class MainWindow
{
    private readonly ProfileLibrary _profileLibrary = new(Paths.DataRoot);
    private IReadOnlyList<ProfileEntry> _profileEntries = [];
    private sealed record ProfileRow(ProfileEntry Entry, string Device, string Details, string State)
    { public string Name => Entry.Name; public string Path => Entry.Path; }

    private void RefreshProfiles()
    {
        if (ProfileList is null) return;
        if (!_screenshotMode)
        {
            _profileEntries = _profileLibrary.Discover(out var warning);
            ProfileLibraryHint.Text = warning ?? "";
        }
        else
        {
            _profileEntries = [new("preview-pro.json", "GMMK PRO ISO", "pro-iso", new DateTime(2026, 10, 10, 12, 0, 0), 4, 2),
                new("preview-pad.json", "GMMK Numpad", "numpad", new DateTime(2026, 10, 10, 11, 0, 0), 4, 1)];
        }
        FilterProfiles();
    }
    private void FilterProfiles()
    {
        if (ProfileList is null) return;
        var selected = (ProfileList.SelectedItem as ProfileRow)?.Path;
        var query = ProfileSearch.Text.Trim();
        var rows = _profileEntries.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || DeviceCatalog.Get(p.DeviceId).Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(p => new ProfileRow(p, DeviceCatalog.Get(p.DeviceId).Name,
                L.F($"{p.Modified:g} · {p.Layers} Ebenen · {p.Macros} Makros"),
                ProfileLibrary.PathsEqual(p.Path, _projectPath) ? L.T("Geöffnet") : "")).ToList();
        ProfileList.ItemsSource = rows;
        ProfileList.SelectedItem = rows.FirstOrDefault(p => ProfileLibrary.PathsEqual(p.Path, selected ?? _projectPath));
        ProfileEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenSelectedProfileButton.IsEnabled = ProfileList.SelectedItem is not null && !_screenshotMode;
    }
    private void ProfileSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterProfiles();
    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (OpenSelectedProfileButton is not null) OpenSelectedProfileButton.IsEnabled = ProfileList.SelectedItem is not null && !_screenshotMode; }
    private void ProfileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (ProfileList.SelectedItem is ProfileRow && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ProfileList, source) is ListBoxItem) OpenSelectedProfile_Click(sender, e); }
    private void OpenSelectedProfile_Click(object sender, RoutedEventArgs e)
    { if (!_screenshotMode && ProfileList.SelectedItem is ProfileRow row) OpenProfilePath(row.Path); }
    private void RememberProfilePath(string path, bool activate = true)
    {
        if (_screenshotMode) return;
        try { _profileLibrary.Remember(path, ProjectService.Load(path).DeviceId, activate); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { ProfileLibraryHint.Text = L.T("Die Profilliste konnte nicht gespeichert werden: ") + ex.Message; }
    }
    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshotMode || _readingDevice || !ResolvePendingEdits()) return;
        CommitNames();
        var copy = ProjectService.Clone(_project);
        copy.Name = L.F($"{copy.Name} – Kopie");
        try
        {
            var path = _profileLibrary.SuggestPath(copy.Name);
            Directory.CreateDirectory(_profileLibrary.Folder);
            var dialog = new SaveFileDialog { Title = L.T("Profil duplizieren"), Filter = L.T("INGLORIOUS-Profil (*.json)|*.json"),
                InitialDirectory = Path.GetDirectoryName(path), FileName = Path.GetFileName(path), DefaultExt = ".json", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            ProjectService.Save(dialog.FileName, copy); RememberProfilePath(dialog.FileName, activate: false); RefreshProfiles(); Status(L.T("Profilkopie gespeichert"));
        }
        catch (Exception ex) { ShowError(L.T("Das Profil konnte nicht gespeichert werden."), ex); }
    }
    private void OpenProfilePath(string path)
    {
        if (_readingDevice || !ResolvePendingEdits()) return;
        CommitNames();
        try
        {
            if (!OpenProfilePathCore(path, ConfirmReplace)) return;
        }
        catch (Exception ex) { ShowError(L.T("Datei konnte nicht geöffnet werden. Aktuelles Profil unverändert."), ex); return; }
        RememberProfilePath(path);
        RefreshAll(); Status(L.F($"Profil „{_project.Name}“ geöffnet"));
    }
    // Confirmation may save a draft, while the selected file can disappear or change.
    // Keep both editor sessions until the reread has succeeded and its device still matches.
    private bool OpenProfilePathCore(string path, Func<bool> confirmReplace)
    {
        var originalDevice = _project.DeviceId;
        var adopted = false;
        try
        {
            var first = ProjectService.Load(path);
            SwitchDevice(first.DeviceId);
            if (_project.DeviceId != first.DeviceId || !confirmReplace()) return false;
            var project = ProjectService.Load(path);
            if (project.DeviceId != first.DeviceId)
                throw new InvalidDataException(L.T("Die Profildatei wurde während des Öffnens geändert. Bitte erneut öffnen."));
            ApplyOpening(new(project, path, "Gespeichertes Profil · nicht mit dem Gerät abgeglichen", null));
            adopted = true;
            return true;
        }
        finally
        {
            if (!adopted && _project.DeviceId != originalDevice) SwitchDevice(originalDevice);
        }
    }
}
