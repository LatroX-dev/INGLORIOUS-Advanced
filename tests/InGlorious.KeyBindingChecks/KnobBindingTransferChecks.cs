using System.Text.Json;
using System.Text.Json.Nodes;
using InGlorious.Core;

internal static class KnobBindingTransferChecks
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards.Where(board => board.HasEncoder))
        {
            var source = ProjectService.Create(board.DeviceId);
            var press = DeviceCatalog.Get(board.DeviceId).Keys.Single(key => key.Label == "Knob").Id;
            source.Layers[1].KnobCycleEnabled = true; source.Layers[1].KnobCycleMask = 128;
            source.Layers[3].KnobCycleEnabled = true; source.Layers[3].KnobCycleMacZoom = true;
            source.Layers[3].KnobCycleMask = 255; source.Layers[3].KnobStep = 5; source.Layers[3].KnobUndoLayout = 2;
            source.Layers[1].KnobCycleOrder = [128, 64, 32, 16, 8, 4, 2, 1];
            source.Layers[3].KnobCycleOrder = [2, 1, 8, 4, 32, 16, 128, 64];
            KnobPresets.Apply(source.Layers[3], "undo-mac");
            var capture = KeyBindingTransfer.Capture(source, [1, 3], includeSlider: false);
            var capturedOrder = capture.Layers.Single(layer => layer.Index == 3).KnobCycleOrder.ToArray();
            source.Layers[3].KnobCycleOrder.Reverse();
            check(capture.Layers.Single(layer => layer.Index == 3).KnobCycleOrder.SequenceEqual(capturedOrder),
                board.DeviceId + ": a captured order cannot change when the source profile is edited");
            source.Layers[3].KnobCycleOrder.Reverse();
            var file = Path.Combine(root, board.DeviceId + ".knob.bindings.json");
            KeyBindingTransfer.Export(file, capture);
            var loaded = KeyBindingTransfer.Load(file);
            check(Json(loaded) == Json(capture), board.DeviceId + ": selected knob layers export and load with every knob option");
            var target = ProjectService.Create(board.DeviceId);
            target.Layers[0].KnobStep = 3;
            target.Layers[0].KnobCycleOrder = [2, 1, 4, 8, 16, 32, 64, 128];
            target.Layers[1].KnobCycleMask = 32;
            target.Layers[2].KnobUndoLayout = 1;
            target.Layers[3].Keys[press].Color = "#FF9999";
            target.Layers[3].LightingEffect.Mode = 3;
            var before = Json(target);
            var merged = KeyBindingTransfer.Apply(target, loaded, [3], includeSlider: false);
            check(Json(target) == before && Enumerable.Range(0, 3).All(index => Json(merged.Layers[index]) == Json(target.Layers[index])),
                board.DeviceId + ": a selected-layer import preserves the input and all unselected knob settings");
            check(merged.Layers[3] is { KnobCycleEnabled: true, KnobCycleMacZoom: true, KnobCycleMask: 255, KnobStep: 5, KnobUndoLayout: 2 } &&
                KnobPresets.Find(merged.Layers[3])?.Id == "undo-mac" && merged.Layers[3].Keys[press].Color == "#FF9999" && merged.Layers[3].LightingEffect.Mode == 3,
                board.DeviceId + ": imported knob settings retain color and lighting settings on their target layer");
            check(merged.Layers[3].KnobCycleOrder.SequenceEqual(source.Layers[3].KnobCycleOrder),
                board.DeviceId + ": selected-layer import retains the full active and inactive function order");
            merged.Layers[3].KnobCycleOrder.Reverse();
            check(loaded.Layers.Single(layer => layer.Index == 3).KnobCycleOrder.SequenceEqual(capturedOrder) && Json(target) == before,
                board.DeviceId + ": editing an imported order cannot mutate its source or original target");
            merged.Layers[3].KnobCycleOrder.Reverse();
            var profileFile = Path.Combine(root, board.DeviceId + ".knob.inglorious.json");
            ProjectService.Save(profileFile, merged);
            check(Json(ProjectService.Load(profileFile)) == Json(merged), board.DeviceId + ": complete profiles preserve all knob settings when reopened");

            var oldFile = JsonNode.Parse(File.ReadAllText(file))!;
            foreach (var layer in oldFile["layers"]!.AsArray())
                foreach (var property in new[] { "knobCycleEnabled", "knobCycleMacZoom", "knobCycleMask", "knobStep", "knobUndoLayout", "knobCycleOrder" })
                    layer!.AsObject().Remove(property);
            var legacyFile = Path.Combine(root, board.DeviceId + ".legacy.bindings.json");
            File.WriteAllText(legacyFile, oldFile.ToJsonString());
            var legacy = KeyBindingTransfer.Load(legacyFile);
            check(legacy.Layers.All(layer => !layer.KnobCycleEnabled && !layer.KnobCycleMacZoom && layer.KnobCycleMask == 31 && layer.KnobStep == 1 && layer.KnobUndoLayout == 0 &&
                KnobPresets.IsDefaultOrder(layer.KnobCycleOrder)),
                board.DeviceId + ": older binding files without knob options use safe defaults");
            var previousVersion = JsonNode.Parse(File.ReadAllText(file))!;
            foreach (var layer in previousVersion["layers"]!.AsArray()) layer!.AsObject().Remove("knobCycleOrder");
            File.WriteAllText(legacyFile, previousVersion.ToJsonString());
            var previous = KeyBindingTransfer.Load(legacyFile);
            check(previous.Layers.All(layer => KnobPresets.IsDefaultOrder(layer.KnobCycleOrder)) &&
                previous.Layers.Single(layer => layer.Index == 3) is { KnobCycleEnabled: true, KnobCycleMask: 255, KnobStep: 5, KnobUndoLayout: 2 },
                board.DeviceId + ": files predating ordering retain their cycle settings and use the established order");
            foreach (var invalid in new[] { "null", "3", "\"1,2,4,8,16,32,64,128\"", "[]",
                "[1,2,4,8,16,32,64]", "[1,2,4,8,16,32,64,64]", "[1,2,4,8,16,32,64,128,1]",
                "[1,2,4,8,16,32,64,256]", "[1,2,4,8,16,32,64,128.5]" })
            {
                var corrupt = JsonNode.Parse(File.ReadAllText(file))!;
                corrupt["layers"]![0]!["knobCycleOrder"] = JsonNode.Parse(invalid);
                var invalidFile = Path.Combine(root, board.DeviceId + ".invalid-order.json");
                File.WriteAllText(invalidFile, corrupt.ToJsonString());
                reject(() => KeyBindingTransfer.Load(invalidFile), board.DeviceId + ": malformed imported order is rejected: " + invalid);
            }

            foreach (var property in new[] { "knobCycleMask", "knobStep", "knobUndoLayout" })
            {
                var corrupt = JsonNode.Parse(File.ReadAllText(file))!;
                corrupt["layers"]![0]![property] = 999;
                var badFile = Path.Combine(root, board.DeviceId + ".bad-" + property + ".json");
                File.WriteAllText(badFile, corrupt.ToJsonString());
                reject(() => KeyBindingTransfer.Load(badFile), board.DeviceId + ": corrupt imported " + property + " is rejected");
            }
            var unsafeTarget = ProjectService.Create(board.DeviceId);
            unsafeTarget.Layers[0].Keys[press].Binding = "MO(1)";
            var unsafeBefore = Json(unsafeTarget);
            reject(() => KeyBindingTransfer.Apply(unsafeTarget, loaded, [3], includeSlider: false),
                board.DeviceId + ": importing a transparent cycle press cannot inherit an unsafe action from an untouched layer");
            check(Json(unsafeTarget) == unsafeBefore, board.DeviceId + ": rejected import cannot change the original profile");
            RunPressBindings(root, board.DeviceId, check, reject);
        }
    }

    private static void RunPressBindings(string root, string deviceId, Action<bool, string> check, Action<Action, string> reject)
    {
        static MacroDefinition Tap(string id, string key) => new()
        { Id = id, Name = id, Steps = [new() { Kind = "down", Key = key }, new() { Kind = "up", Key = key }] };
        var source = ProjectService.Create(deviceId);
        source.Macros = [Tap("press-macro", "KC_A"), Tap("inactive-macro", "KC_B"), Tap("unused-macro", "KC_C")];
        source.Layers[1].KnobPressBindings[1] = "MACRO:inactive-macro";
        source.Layers[2].KnobPressByFunction = true;
        source.Layers[2].KnobPressBindings = new() { [2] = "MACRO:press-macro", [128] = "KC_MPLY" };
        var captured = KeyBindingTransfer.Capture(source, [1, 2]);
        check(captured.Macros.Select(macro => macro.Id).ToHashSet().SetEquals(["press-macro", "inactive-macro"]),
            deviceId + ": function-specific short presses export only their referenced macros, including retained inactive assignments");
        source.Layers[2].KnobPressBindings[128] = "KC_NO";
        check(captured.Layers.Single(layer => layer.Index == 2).KnobPressBindings[128] == "KC_MPLY",
            deviceId + ": captured per-function press assignments are independent of later source edits");
        var file = Path.Combine(root, deviceId + ".knob-press.bindings.json");
        KeyBindingTransfer.Export(file, captured);
        var loaded = KeyBindingTransfer.Load(file);
        check(Json(loaded) == Json(captured), deviceId + ": per-function short presses and macros survive strict file export and import");
        var target = ProjectService.Create(deviceId);
        target.Macros = [Tap("press-macro", "KC_Z")];
        target.Layers[0].KnobPressBindings[1] = "KC_CALC";
        var before = Json(target);
        var applied = KeyBindingTransfer.Apply(target, loaded, [2]);
        var assigned = applied.Layers[2].KnobPressBindings[2][6..];
        check(assigned != "press-macro" && applied.Macros.Single(macro => macro.Id == assigned).Steps[0].Key == "KC_A" &&
            applied.Macros.Single(macro => macro.Id == "press-macro").Steps[0].Key == "KC_Z",
            deviceId + ": a conflicting macro ID is remapped in each per-function assignment without replacing the existing macro");
        check(applied.Macros.Count == 2 && !applied.Macros.Any(macro => macro.Id == "inactive-macro") &&
            applied.Layers[2].KnobPressByFunction && applied.Layers[2].KnobPressBindings[128] == "KC_MPLY" &&
            Enumerable.Range(0, 4).Where(index => index != 2).All(index => Json(applied.Layers[index]) == Json(target.Layers[index])) && Json(target) == before,
            deviceId + ": layer selection imports exactly its short presses and macros while preserving unselected layers and input");
        var repeated = KeyBindingTransfer.Apply(applied, loaded, [2]);
        check(Json(repeated) == Json(applied), deviceId + ": reimporting a remapped short-press macro reuses it without duplicates");
        applied.Layers[2].KnobPressBindings[128] = "KC_NO";
        check(loaded.Layers.Single(layer => layer.Index == 2).KnobPressBindings[128] == "KC_MPLY",
            deviceId + ": editing imported short presses cannot change the binding document");
        foreach (var malformed in new[] { "null", "[]", "{\"3\":\"KC_MPLY\"}", "{\"1\":null}", "{\"1\":\"MACRO:missing\"}", "{\"01\":\"KC_MPLY\"}", "{\"1\":\"KC_TRNS\"}", "{\"1\":\"MO(1)\"}" })
        {
            var damaged = JsonNode.Parse(File.ReadAllText(file))!;
            damaged["layers"]![1]!["knobPressBindings"] = JsonNode.Parse(malformed);
            var badFile = Path.Combine(root, deviceId + ".bad-press.bindings.json");
            File.WriteAllText(badFile, damaged.ToJsonString());
            reject(() => KeyBindingTransfer.Load(badFile), deviceId + ": invalid short-press assignment rejected: " + malformed);
        }
        var original = KeyBindingTransfer.Capture(ProjectService.Create(deviceId), [0]);
        var older = JsonNode.Parse(Json(original))!;
        older["layers"]![0]!.AsObject().Remove("knobPressByFunction");
        older["layers"]![0]!.AsObject().Remove("knobPressBindings");
        var olderFile = Path.Combine(root, deviceId + ".before-short-press.bindings.json");
        File.WriteAllText(olderFile, older.ToJsonString());
        var compatible = KeyBindingTransfer.Load(olderFile);
        check(!compatible.Layers[0].KnobPressByFunction && compatible.Layers[0].KnobPressBindings.Count == 0,
            deviceId + ": previous binding documents keep a single shared short press by default");
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectService.JsonOptions);
}
