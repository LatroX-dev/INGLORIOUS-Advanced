using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

internal sealed record SoftwareUpdateInstallation(SoftwareUpdateInfo Update, string Path);

internal sealed class SoftwareUpdateWindow : Window
{
    private readonly SoftwareUpdateService? _service;
    private readonly bool _offlinePreview;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 5, Visibility = Visibility.Collapsed };
    private readonly Button _action = new() { MinWidth = 140 };
    private SoftwareUpdateInfo? _update;
    private string? _download;
    private bool _busy, _closed;
    internal SoftwareUpdateInstallation? Installation { get; private set; }

    public SoftwareUpdateWindow(SoftwareUpdateInfo? available = null, bool offlinePreview = false)
    {
        _offlinePreview = offlinePreview;
        if (!offlinePreview) _service = new(Paths.UserDataRoot);
        Title = L.T("Software-Updates") + " · INGLORIOUS Advanced";
        Width = 580; Height = 510; MinWidth = 500; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("WindowBackground");
        Foreground = (Brush)Application.Current.FindResource("Text");
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = L.T("Software-Updates"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "INGLORIOUS Advanced · " + SoftwareUpdateService.CurrentVersion,
            Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 7, 0, 20) });
        grid.Children.Add(heading);
        var body = new StackPanel();
        _status.Margin = new Thickness(0, 0, 0, 16); body.Children.Add(_status);
        _notes.Margin = new Thickness(0, 0, 0, 18); body.Children.Add(_notes);
        body.Children.Add(_progress);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var close = new Button { Content = L.T("Schließen"), MinWidth = 100, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        close.Click += (_, _) => Close(); footer.Children.Add(close);
        _action.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _action.Click += async (_, _) => await ActAsync(); footer.Children.Add(_action);
        Grid.SetRow(footer, 2); grid.Children.Add(footer); Content = grid;
        Loaded += async (_, _) =>
        {
            if (available is not null) ShowAvailable(available);
            else if (!offlinePreview) await CheckAsync();
        };
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); if (!_busy) _service?.Dispose(); };
    }

    private async Task CheckAsync()
    {
        if (_busy || _closed || _offlinePreview) return;
        _busy = true; _action.IsEnabled = false;
        _action.Content = L.T("Nach Updates suchen"); _status.Text = L.T("Suche nach Updates …");
        try
        {
            if (SoftwareUpdateService.CurrentPlatform is not { } platform)
            { _status.Text = L.T("Für dieses System ist noch kein Update-Paket verfügbar."); return; }
            var result = await _service!.CheckAsync(SoftwareUpdateService.CurrentVersion, platform, _cancellation.Token);
            if (_closed) return;
            if (result.Update is { } update) ShowAvailable(update);
            else _status.Text = L.T(result.Status == SoftwareUpdateStatus.Current
                ? "Deine App ist aktuell." : "Für dieses System ist noch kein Update-Paket verfügbar.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException)
        { if (!_closed) _status.Text = L.T("Updates konnten nicht abgerufen werden. Prüfe deine Internetverbindung und versuche es erneut."); }
        finally { _busy = false; if (_closed) _service?.Dispose(); else _action.IsEnabled = true; }
    }

    private void ShowAvailable(SoftwareUpdateInfo update)
    {
        _update = update; _status.Text = L.T("Neue Version verfügbar: ") + update.Version;
        _notes.Text = update.Notes; _action.Content = L.T("Update herunterladen"); _action.IsEnabled = true;
    }

    private async Task ActAsync()
    {
        if (_busy || _closed || _offlinePreview) return;
        if (_update is null) { await CheckAsync(); return; }
        if (_download is not null)
        { Installation = new(_update, _download); DialogResult = true; return; }
        _busy = true; _action.IsEnabled = false; _progress.Visibility = Visibility.Visible;
        _status.Text = L.T("Update wird heruntergeladen …");
        try
        {
            var progress = new Progress<double>(value => { if (!_closed) _progress.Value = value * 100; });
            _download = await _service!.DownloadAsync(_update, progress, _cancellation.Token);
            if (_closed) return;
            _progress.Visibility = Visibility.Collapsed;
            _status.Text = L.T("Das Update ist bereit. Zur Installation wird die App geschlossen. Profile und Sicherungen bleiben erhalten.");
            _action.Content = L.T("Jetzt installieren");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        {
            if (!_closed) { _status.Text = L.T("Das Update konnte nicht heruntergeladen werden. Bitte versuche es erneut."); _progress.Visibility = Visibility.Collapsed; }
        }
        finally { _busy = false; if (_closed) _service?.Dispose(); else _action.IsEnabled = true; }
    }
}
