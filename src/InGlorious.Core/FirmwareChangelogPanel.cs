using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

/// <summary>Local release notes only; displaying this panel never queries a device.</summary>
public sealed class FirmwareChangelogPanel : Border
{
    private (string Device, FirmwareSemanticVersion? Installed, FirmwareSemanticVersion Target)? _rendered;
    internal IReadOnlyList<FirmwareReleaseNotes> Notes { get; private set; } = [];

    public FirmwareChangelogPanel()
    {
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(BorderBrushProperty, "Stroke");
        BorderThickness = new(1); CornerRadius = new(6); Padding = new(16); Margin = new(0, 0, 0, 16);
    }

    public void ShowChanges(string deviceId, FirmwareSemanticVersion? installed, FirmwareSemanticVersion target)
    {
        var key = (deviceId, installed, target);
        if (_rendered == key) return; // Status polling must not recreate the visible content.
        _rendered = key;
        Notes = FirmwareChangelog.Between(deviceId, installed, target);
        Visibility = Notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Notes.Count == 0) { Child = null; return; }
        var body = new StackPanel(); Child = body;
        var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "What’s new", FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(heading);
        foreach (var release in Notes)
        {
            var version = new TextBlock { Text = "INGLORIOUS " + release.Version, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            version.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            if (Notes.Count == 1) { version.Margin = new(12, 0, 0, 0); Grid.SetColumn(version, 1); heading.Children.Add(version); }
            else { version.Margin = new(0, 12, 0, 4); body.Children.Add(version); }
            foreach (var change in release.Changes)
            {
                var row = new Grid { Margin = new(0, 5, 0, 0) };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); row.ColumnDefinitions.Add(new());
                var bullet = new TextBlock { Text = "•" }; bullet.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); row.Children.Add(bullet);
                var text = new TextBlock { Text = change, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
                Grid.SetColumn(text, 1); row.Children.Add(text); body.Children.Add(row);
            }
        }
    }
}
