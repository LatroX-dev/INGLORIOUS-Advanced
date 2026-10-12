using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace InGlorious.Core;

public partial class MainWindow
{
    private sealed record PreviewVisual(SolidColorBrush Light, SolidColorBrush? Cap);
    private readonly Dictionary<int, PreviewVisual> _previewVisuals = [];
    private readonly Stopwatch _previewClock = new();
    private readonly PreviewHit[] _previewHits = new PreviewHit[LightingPreviewEngine.MaxHits];
    private int _previewHitCount;
    private string _previewDeviceId = "";
    private LightingPreviewScene? _previewScene;
    private PreviewRgb[] _previewFrame = [];
    private DispatcherTimer? _previewTimer;
    private long _previewTickCount;
    private uint _previewLastTickTime;
    private bool _previewStarted, _previewSceneDirty = true;
    private bool PreviewAllowed => !_screenshotMode || Environment.GetCommandLineArgs().Contains("--animate-preview");

    private void BeginLightingPreviewRender()
    {
        _previewVisuals.Clear(); _previewSceneDirty = true;
        if (_previewDeviceId == Device.Id) return;
        _previewDeviceId = Device.Id; _previewHitCount = 0; _previewScene = null;
    }
    private void RegisterPreviewLed(int led, Border light, Button? keycap = null)
    {
        if (led < 0 || led >= Device.LedCount) return;
        // Keep dedicated mutable brushes. Selection borders and legends are untouched.
        var lightBrush = new SolidColorBrush((light.Background as SolidColorBrush)?.Color ?? Colors.Black);
        light.Background = lightBrush;
        SolidColorBrush? capBrush = null;
        if (keycap is not null)
        {
            capBrush = new SolidColorBrush((keycap.Background as SolidColorBrush)?.Color ?? Color.FromRgb(34, 37, 41));
            keycap.Background = capBrush;
        }
        _previewVisuals[led] = new(lightBrush, capBrush);
    }
    private void EndLightingPreviewRender()
    {
        _previewSceneDirty = true;
        // Offline captures render a deterministic frame too; only the running timer is disabled.
        RenderLightingPreviewFrame(unchecked((uint)_previewClock.ElapsedMilliseconds));
        UpdateLightingPreviewActivity();
    }
    private void RefreshLightingPreview()
    {
        _previewSceneDirty = true;
        if (!_refreshing && !_refreshingEffects)
        {
            RenderLightingPreviewFrame(unchecked((uint)_previewClock.ElapsedMilliseconds));
            UpdateLightingPreviewActivity();
        }
    }
    private void StartLightingPreview()
    {
        if (_previewStarted || !PreviewAllowed) return;
        _previewStarted = true; _previewClock.Start();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1d / 30) };
        _previewTimer.Tick += PreviewTick;
        ColorPanel.IsVisibleChanged += PreviewVisibilityChanged;
        IsVisibleChanged += PreviewVisibilityChanged; StateChanged += PreviewWindowStateChanged;
        EffectChoice.SelectionChanged += PreviewInputChanged; EffectTarget.SelectionChanged += PreviewInputChanged;
        EffectSpeed.ValueChanged += PreviewInputChanged; EffectBrightness.ValueChanged += PreviewInputChanged;
        EffectReverse.Checked += PreviewInputChanged; EffectReverse.Unchecked += PreviewInputChanged;
        Closed += PreviewWindowClosed;
        _previewSceneDirty = true; UpdateLightingPreviewActivity();
    }
    private void StopLightingPreview()
    {
        if (!_previewStarted) return;
        _previewStarted = false;
        _previewTimer?.Stop(); if (_previewTimer is not null) _previewTimer.Tick -= PreviewTick;
        _previewTimer = null; _previewClock.Stop();
        ColorPanel.IsVisibleChanged -= PreviewVisibilityChanged;
        IsVisibleChanged -= PreviewVisibilityChanged; StateChanged -= PreviewWindowStateChanged;
        EffectChoice.SelectionChanged -= PreviewInputChanged; EffectTarget.SelectionChanged -= PreviewInputChanged;
        EffectSpeed.ValueChanged -= PreviewInputChanged; EffectBrightness.ValueChanged -= PreviewInputChanged;
        EffectReverse.Checked -= PreviewInputChanged; EffectReverse.Unchecked -= PreviewInputChanged;
        Closed -= PreviewWindowClosed;
        _previewVisuals.Clear(); _previewScene = null;
    }
    private void PreviewWindowClosed(object? sender, EventArgs e) => StopLightingPreview();
    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateLightingPreviewActivity();
    private void PreviewWindowStateChanged(object? sender, EventArgs e) => UpdateLightingPreviewActivity();
    private void PreviewInputChanged(object sender, RoutedEventArgs e)
    {
        _previewSceneDirty = true;
        UpdateLightingPreviewActivity();
    }
    private void UpdateLightingPreviewActivity()
    {
        if (!_previewStarted || _previewTimer is null) return;
        var visible = IsVisible && WindowState != WindowState.Minimized && _activeTab == 0 && ColorPanel.IsVisible;
        if (!visible) { _previewTimer.Stop(); return; }
        if (_refreshing || _refreshingEffects) { _previewTimer.Start(); return; }
        EnsureLightingPreviewScene();
        RenderLightingPreviewFrame(unchecked((uint)_previewClock.ElapsedMilliseconds));
        if (_previewScene?.Animated == true) _previewTimer.Start(); else _previewTimer.Stop();
    }
    private void PreviewTick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _activeTab != 0 || !ColorPanel.IsVisible) { _previewTimer?.Stop(); return; }
        if (_refreshing || _refreshingEffects) return;
        _previewLastTickTime = unchecked((uint)_previewClock.ElapsedMilliseconds);
        RenderLightingPreviewFrame(_previewLastTickTime);
        ++_previewTickCount;
        if (_previewScene?.Animated != true) _previewTimer?.Stop();
    }
    private void EnsureLightingPreviewScene()
    {
        if (!_previewSceneDirty && _previewScene is not null) return;
        PreviewEffect? draft = null; IReadOnlySet<int>? selected = null;
        // Unapplied effect controls may be previewed without changing the project or its undo stack.
        if (_showLightingEffects && _pendingEditsReady && !_refreshingEffects && HasPendingEffect && EffectChoice.SelectedItem is LightingEffectOption option &&
            (_effectForSelection || EffectTarget.SelectedIndex is >= 0 and <= 2))
        {
            var effect = EffectiveEffectInput();
            draft = new((byte)effect.Mode, (byte)effect.Speed, (byte)effect.Brightness,
                (byte)(_effectForSelection ? 3 : effect.Target), (byte)((effect.Reverse ? 1 : 0) | (Layer.SynchronizeSides ? 2 : 0)), effect.Colors.Select(PreviewRgb.FromHex).ToArray());
            if (_effectForSelection) selected = SelectedEffectLeds().ToHashSet();
        }
        _previewScene = LightingPreviewEngine.CreateScene(Device, Layer, draft, selected);
        if (_previewFrame.Length != _previewScene.LedCount) _previewFrame = new PreviewRgb[_previewScene.LedCount];
        _previewSceneDirty = false;
        if (FindName("PreviewAnimationHint") is TextBlock hint)
        {
            hint.Text = L.T("Zum Testen auf Tasten klicken.");
            hint.Visibility = _previewScene.Reactive ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    // Deterministic explicit frame entrypoint for offline UI checks; it never starts a timer.
    private void RenderLightingPreviewFrame(uint time)
    {
        if (_refreshingEffects) return;
        EnsureLightingPreviewScene();
        _previewScene!.Render(time, _previewHits.AsSpan(0, _previewHitCount), _previewFrame);
        foreach (var (index, visual) in _previewVisuals)
        {
            if (index >= _previewFrame.Length) continue;
            var rgb = _previewFrame[index]; var color = Color.FromRgb(rgb.R, rgb.G, rgb.B);
            if (visual.Light.Color != color) visual.Light.Color = color;
            if (visual.Cap is not null)
            {
                var tint = Color.FromRgb((byte)(rgb.R * .055 + 34), (byte)(rgb.G * .055 + 37), (byte)(rgb.B * .055 + 41));
                if (visual.Cap.Color != tint) visual.Cap.Color = tint;
            }
        }
    }
    private void PreviewKeyHit(string keyId)
    {
        if (!PreviewAllowed || _activeTab != 0) return;
        RecordLightingPreviewHit(keyId, unchecked((uint)_previewClock.ElapsedMilliseconds));
        UpdateLightingPreviewActivity();
    }
    private void RecordLightingPreviewHit(string keyId, uint time)
    {
        var key = Device.Keys.FirstOrDefault(k => k.Id == keyId);
        if (key?.LedIndex is not int led) return;
        var point = LightingPreviewEngine.Layout(Device)[led];
        _previewHitCount = Math.Min(_previewHitCount + 1, _previewHits.Length);
        for (var i = _previewHitCount - 1; i > 0; --i) _previewHits[i] = _previewHits[i - 1];
        _previewHits[0] = new(time, point.X, point.Y, point.Index);
    }
}
