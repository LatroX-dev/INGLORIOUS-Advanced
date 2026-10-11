using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InGlorious.Core;

public partial class MainWindow
{
    // Explicit screenshot fixture: exercises the real dispatcher timer without opening a device.
    private async Task CaptureAnimationProofAsync(string folder)
    {
        if (!_screenshotMode || !PreviewAllowed || !Environment.GetCommandLineArgs().Contains("--animation-proof"))
            throw new InvalidOperationException("Animation proof requires explicit screenshot, animate-preview and animation-proof flags.");
        folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
        var paths = new[] { Path.Combine(folder,"frame-1.png"), Path.Combine(folder,"frame-2.png"), Path.Combine(folder,"proof.txt") };
        if (paths.Any(File.Exists)) throw new IOException("Animation proof output already exists; choose a new directory.");
        void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        Dictionary<int, Color> Pixels() => _previewVisuals.ToDictionary(p => p.Key, p => p.Value.Light.Color);
        var deviceId = Device.Id;
        ApplyOpening(OpenDefaultProject(deviceId, allowDefault:false)); _layerIndex = 0; _activeTab = 0;
        _lightingTarget = LightingTarget.Keys; _selection.Clear(); _selectedSideLeds.Clear(); _effectForSelection = false;
        Layer.LightingEffect = new() { Mode = 3, Speed = 255, Brightness = 255, Target = 3 }; Layer.LedEffects.Clear();
        RefreshAll(); SetLightingEditor(false); StartLightingPreview(); UpdateLightingPreviewActivity();
        var unchanged = JsonSerializer.Serialize(_project); var startTicks = _previewTickCount;
        try
        {
            await Task.Delay(150);
            Assert(_previewTimer?.IsEnabled == true && _previewTickCount > startTicks, "No real DispatcherTimer frame occurred before capture.");
            var firstTicks = _previewTickCount; var firstMs = _previewLastTickTime; var first = Pixels();
            SaveVisualScreenshot((FrameworkElement)Content, paths[0]);
            await Task.Delay(180);
            var secondTicks = _previewTickCount; var secondMs = _previewLastTickTime; var second = Pixels();
            Assert(secondTicks > firstTicks, "Animation was not advanced by real dispatcher timer ticks.");
            var changedKeys = Device.Keys.Count(k => k.LedIndex is int index && first[index] != second[index]);
            var changedSides = Device.NonKeyLedIndices.Count(index => first[index] != second[index]);
            Assert(changedKeys > 0 && changedSides > 0, "Timed captures did not change both keys and side LEDs.");
            SaveVisualScreenshot((FrameworkElement)Content, paths[1]);
            Assert(JsonSerializer.Serialize(_project) == unchanged, "Timer rendering mutated the project.");

            SetTab(5); var stoppedTicks = _previewTickCount;
            Assert(_previewTimer?.IsEnabled != true, "Timer remained enabled outside lighting view.");
            await Task.Delay(120);
            Assert(_previewTickCount == stoppedTicks, "Timer still rendered hidden lighting frames.");
            SetTab(0); await Task.Delay(120);
            Assert(_previewTimer?.IsEnabled == true && _previewTickCount > stoppedTicks, "Returning to lighting did not resume the timer.");
            Assert(JsonSerializer.Serialize(_project) == unchanged, "Navigation or animation changed project data.");
            File.WriteAllText(paths[2], $"PASS actual WPF DispatcherTimer animation; no device calls.\nDevice: {deviceId}\nEffect: 3, speed255, brightness255, target3\nFrame1: timerTick={firstTicks}, milliseconds={firstMs}\nFrame2: timerTick={secondTicks}, milliseconds={secondMs}\nChanged key LEDs: {changedKeys}\nChanged side LEDs: {changedSides}\nProject unchanged: true\nProfiles tab: timer stopped, zero further frames during120ms\nLighting return: timer resumed, tick={_previewTickCount}\nTwo PNGs captured from actual timed WPF visuals. No manual frame advance was used between captures.\n");
        }
        finally { StopLightingPreview(); }
    }

    private void RunAnimationChecks(Action<bool, string> check)
    {
        if (!_screenshotMode) throw new InvalidOperationException("Animation checks require offline UI-smoke mode.");
        try
        {
            foreach (var deviceId in new[] { "pro-iso", "numpad" })
            {
                ApplyOpening(OpenDefaultProject(deviceId, allowDefault: false)); _layerIndex = 0; _activeTab = 0;
                _lightingTarget = LightingTarget.Keys; _selection.Clear(); _selectedSideLeds.Clear(); _showLightingEffects = true; _effectForSelection = false;
                Layer.LightingEffect = new() { Mode = 3, Speed = 255 }; RefreshAll();
                check(_previewVisuals.Count == Device.LedCount, deviceId + ": all key and strip LED visuals are registered");
                check(_previewTimer?.IsEnabled != true, deviceId + ": offline smoke starts no animation timer");
                var first = Device.Keys.First(k => k.LedIndex.HasValue); var button = _keyButtons[first.Id];
                var firstLed = first.LedIndex!.Value;
                var labels = string.Join("|", ((Grid)button.Content).Children.OfType<TextBlock>().Select(t => t.Text));
                var selectedBorder = button.BorderBrush; var borderThickness = button.BorderThickness;
                var original = JsonSerializer.Serialize(_project);
                RenderLightingPreviewFrame(0); var before = _previewVisuals[firstLed].Light.Color;
                RenderLightingPreviewFrame(100); var after = _previewVisuals[firstLed].Light.Color;
                check(before != after, deviceId + ": explicit timestamps produce visibly different animated frames");
                check(ReferenceEquals(button.BorderBrush, selectedBorder) && button.BorderThickness == borderThickness && string.Join("|", ((Grid)button.Content).Children.OfType<TextBlock>().Select(t => t.Text)) == labels, deviceId + ": animation preserves key legends and selection outlines");
                var reference = LightingPreviewEngine.CreateScene(Device, Layer); var expected = new PreviewRgb[Device.LedCount]; reference.Render(100, [], expected);
                check(_previewVisuals.All(pair => pair.Value.Light.Color == Color.FromRgb(expected[pair.Key].R, expected[pair.Key].G, expected[pair.Key].B)), deviceId + ": actual WPF brushes match every engine pixel, including strips");
                check(JsonSerializer.Serialize(_project) == original, deviceId + ": rendering never edits or saves a project");

                _showLightingEffects = false; RefreshInspector(); RenderLightingPreviewFrame(300);
                var staticColor = ParseColor(Assignment(first.Id).Color);
                reference.Render(300, [], expected);
                check(_previewScene?.Animated == true && _previewVisuals.All(pair => pair.Value.Light.Color == Color.FromRgb(expected[pair.Key].R, expected[pair.Key].G, expected[pair.Key].B)),
                    deviceId + ": color editor keeps the committed effects on every key and side LED");
                reference.Render(100, [], expected);
                _showLightingEffects = true; _effectForSelection = true; _selection.Add(first.Id); RefreshInspector();
                var beforeDraft = JsonSerializer.Serialize(_project);
                EffectChoice.SelectedValue = 2; EffectSpeed.Value = 255; EffectBrightness.Value = 123;
                RefreshLightingPreview(); RenderLightingPreviewFrame(100);
                var point = LightingPreviewEngine.Layout(Device)[firstLed];
                var draft = LightingPreviewEngine.Pixel(new(2,255,123,3,0), default, point,100,[]);
                check(_previewVisuals[firstLed].Light.Color == Color.FromRgb(draft.R,draft.G,draft.B) && JsonSerializer.Serialize(_project) == beforeDraft && HasPendingEffect, deviceId + ": pending effect controls preview without implicit apply");
                DiscardPendingEdits(); RefreshLightingPreview(); RenderLightingPreviewFrame(100);
                check(_previewVisuals[firstLed].Light.Color == Color.FromRgb(expected[firstLed].R,expected[firstLed].G,expected[firstLed].B), deviceId + ": discarded inputs restore the committed animation");

                _effectForSelection = false; Layer.LightingEffect = new() { Mode = 9, Speed = 255 }; _previewHitCount = 0; RefreshAll();
                RenderLightingPreviewFrame(1000);
                check(_previewVisuals[firstLed].Light.Color == Colors.Black, deviceId + ": reactive effects do not synthesize idle key presses");
                RecordLightingPreviewHit(first.Id, 1000); RenderLightingPreviewFrame(1000);
                check(_previewVisuals[firstLed].Light.Color == staticColor, deviceId + ": explicit local key click triggers its physical LED");
                for (uint time = 1001; time <= 1010; ++time) RecordLightingPreviewHit(first.Id,time);
                check(_previewHitCount == 8 && _previewHits[0].Time == 1010 && _previewHits[7].Time == 1003, deviceId + ": hit queue retains exactly the eight latest presses");
                RecordLightingPreviewHit("encoder:cw", 2000);
                check(_previewHitCount == 8 && _previewHits[0].Time == 1010, deviceId + ": encoder rotation invents no physical LED hit");
                RenderLightingPreviewFrame(1410);
                check(_previewVisuals[firstLed].Light.Color == Colors.Black && PreviewAnimationHint.Visibility == Visibility.Visible, deviceId + ": reactive hits expire and expose the click-to-test hint");
                RunColorEditorEffectPreviewChecks(check);
            }
        }
        finally
        {
            _previewHitCount = 0; _sessions.Clear(); ApplyOpening(OpenDefaultProject("pro-iso", allowDefault:false));
            _layerIndex = 0; _activeTab = 0; _showLightingEffects = false; _effectForSelection = false;
            _lightingTarget = LightingTarget.Keys; _selection.Clear(); _selectedSideLeds.Clear(); RefreshAll();
        }
    }

    private void RunColorEditorEffectPreviewChecks(Action<bool, string> check)
    {
        // Reproduce the report: red base colors, but blue/yellow sequences on both side strips.
        Layer.LightingEffect = new(); Layer.SideLedColor = "#FF0000";
        Layer.ExtraLedColors.Clear(); Layer.LedEffects.Clear(); Layer.SynchronizeSides = true;
        foreach (var led in Device.NonKeyLedIndices)
            Layer.LedEffects[led] = new() { Mode = 2, Speed = 200, Colors = ["#0000FF", "#FFFF00"] };
        _previewHitCount = 0; _showLightingEffects = false; _showLightingStandby = false;
        _effectForSelection = false; _lightingTarget = LightingTarget.Left; _wholeSideSelection = true;
        _selection.Clear(); RefreshAll(); RenderLightingPreviewFrame(0);
        var original = JsonSerializer.Serialize(_project); var history = _undo.Count;
        check(_previewScene?.Animated == true && Device.NonKeyLedIndices.All(led => _previewVisuals[led].Light.Color == Colors.Blue),
            Device.Id + ": color editor shows blue sequence output on both strips instead of their red base");
        check(ColorHex.Text == "#FF0000" && SelectedColorLabel.Text == "#FF0000" && ((SolidColorBrush)SelectedColorChip.Background).Color == Colors.Red,
            Device.Id + ": base-color controls stay red while the strip preview follows its effect");
        ColorHex.Text = "#123456"; RenderLightingPreviewFrame(500);
        var colorFrame = _previewFrame.ToArray();
        check(Device.NonKeyLedIndices.Any(led => colorFrame[led] != new PreviewRgb(0, 0, 255)) && ColorHex.Text == "#123456" && JsonSerializer.Serialize(_project) == original && _undo.Count == history,
            Device.Id + ": side animation advances without applying pending hex input or changing history");
        SetLightingEditor(true); RenderLightingPreviewFrame(500);
        check(colorFrame.SequenceEqual(_previewFrame) && JsonSerializer.Serialize(_project) == original && _undo.Count == history,
            Device.Id + ": switching color and effect editors preserves the same rendered LED result");
        _showLightingEffects = false; Layer.LedEffects.Clear(); RefreshAll(); RenderLightingPreviewFrame(500);
        check(_previewScene?.Animated == false && Device.NonKeyLedIndices.All(led => _previewVisuals[led].Light.Color == Colors.Red),
            Device.Id + ": a genuinely static side configuration still renders its base color");
    }
}
