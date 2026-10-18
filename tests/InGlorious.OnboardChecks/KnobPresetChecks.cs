using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using InGlorious.Core;

internal static class KnobPresetChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckCycleOrder(check);
        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var model = DeviceCatalog.Get(board.DeviceId);
            var original = ProjectService.Create(board.DeviceId);
            check(original.Layers.All(layer => !layer.KnobCycleEnabled), board.DeviceId + ": long-press switching starts disabled");
            if (!board.HasEncoder)
            {
                check(!KnobPresets.CanCycle(original, 0) && !KnobPresets.IsSelection(model, [model.Keys[0].Id]),
                    board.DeviceId + ": a board without a knob does not offer knob actions");
                original.Layers[0].KnobCycleEnabled = true;
                Reject(() => OnboardSerializer.Prepare(original), check, board.DeviceId + ": an imported cycle setting cannot create a nonexistent knob");
                original.Layers[0].KnobCycleEnabled = false;
                original.Layers[1].KnobStep = 2;
                Reject(() => OnboardSerializer.Prepare(original), check, board.DeviceId + ": step settings require a physical knob");
                original.Layers[1].KnobStep = 1;
                original.Layers[1].KnobCycleMask = 255;
                Reject(() => OnboardSerializer.Prepare(original), check, board.DeviceId + ": function selection requires a physical knob");
                original.Layers[1].KnobCycleMask = 31;
                foreach (var code in new[] { "IG_ZOOM_IN", "IG_ZOOM_OUT", "IG_ZOOM_IN_MAC", "IG_ZOOM_OUT_MAC",
                    "IG_TAB_PREV", "IG_TAB_NEXT", "IG_UNDO", "IG_REDO", "IG_UNDO_MAC", "IG_REDO_MAC" })
                {
                    var unsupported = ProjectService.Clone(original);
                    unsupported.Layers[2].Keys[model.Keys[0].Id].Binding = code;
                    Reject(() => OnboardSerializer.Prepare(unsupported), check,
                        board.DeviceId + ": encoder-only shortcut " + code + " cannot be assigned to a board without a knob");
                }
                foreach (var code in new[] { "MS_WHLU", "MS_WHLD", "MS_WHLL", "MS_WHLR" })
                {
                    var wheel = ProjectService.Clone(original);
                    wheel.Layers[2].Keys[model.Keys[0].Id].Binding = code;
                    check(OnboardSerializer.Decode(OnboardSerializer.Prepare(wheel).Payload).Layers[2].Keys[model.Keys[0].Id].Binding == code,
                        board.DeviceId + ": ordinary mouse-wheel actions remain available without a knob");
                }
                original.Standby.Enabled = true;
                var forged = OnboardSerializer.Prepare(original).Payload;
                BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(4), 5); forged[17] = 1;
                Reject(() => OnboardSerializer.Decode(forged), check, board.DeviceId + ": a schema-5 cycle mask on a board without an encoder is rejected");
                continue;
            }

            var press = model.Keys.Single(key => key.Label == "Knob").Id;
            var ordinary = model.Keys.First(key => !key.IsEncoder && key.Id != press).Id;
            original.Layers[2].Keys[ordinary].Color = "#123456";
            original.Layers[1].LightingEffect = new() { Mode = 2, Speed = 79, Brightness = 141, Colors = ["#FF0000", "#010203"] };
            original.Layers[3].Keys[press].Binding = "KC_MPLY";
            original.DeviceSettings.DebounceMs = 7;
            if (model.HasSlider) original.Slider.Mode = 3;
            check(KnobPresets.IsSelection(model, [press]) && KnobPresets.IsSelection(model, ["encoder:cw"]) &&
                KnobPresets.IsSelection(model, ["encoder:ccw", "encoder:cw"]), board.DeviceId + ": both knob press and rotation controls offer presets");
            check(!KnobPresets.IsSelection(model, []) && !KnobPresets.IsSelection(model, [ordinary]) &&
                !KnobPresets.IsSelection(model, [ordinary, "encoder:cw"]), board.DeviceId + ": empty or mixed key selections cannot reassign the knob");

            foreach (var layerIndex in Enumerable.Range(0, 4))
            foreach (var preset in KnobPresets.All)
            {
                var configured = ProjectService.Clone(original);
                var before = Json(configured);
                var layer = configured.Layers[layerIndex];
                var oldLeft = layer.Keys["encoder:ccw"].Binding;
                var oldRight = layer.Keys["encoder:cw"].Binding;
                var oldUndoLayout = layer.KnobUndoLayout;
                KnobPresets.Apply(layer, preset.Id);
                check(KnobPresets.Find(layer)?.Id == preset.Id, board.DeviceId + ": applied " + preset.Id + " is selected on layer " + layerIndex);
                var image = OnboardSerializer.Prepare(configured);
                var decoded = OnboardSerializer.Decode(image.Payload);
                check(KnobPresets.Find(decoded.Layers[layerIndex])?.Id == preset.Id &&
                    decoded.Layers[layerIndex].KnobUndoLayout == layer.KnobUndoLayout && !decoded.Layers[layerIndex].KnobCycleEnabled &&
                    (layer.KnobUndoLayout == 0 ? image.SchemaVersion != 5 : image.SchemaVersion == 5),
                    board.DeviceId + ": ordinary presets preserve their undo-layout choice without enabling long press");
                layer.Keys["encoder:ccw"].Binding = oldLeft;
                layer.Keys["encoder:cw"].Binding = oldRight;
                layer.KnobUndoLayout = oldUndoLayout;
                check(Json(configured) == before, board.DeviceId + ": a preset preserves the press, colors, other layers, slider and all other settings");
            }

            original.Layers[0].Keys["encoder:ccw"].Binding = "KC_LEFT";
            original.Layers[0].Keys["encoder:cw"].Binding = "KC_RGHT";
            check(KnobPresets.Find(original.Layers[0]) is null, board.DeviceId + ": separately assigned directions remain custom");
            var savedCulture = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "de-DE", "fr-FR" })
                foreach (var undoLayout in Enumerable.Range(0, 3))
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    var configured = ProjectService.Clone(original);
                    configured.Layers[2].KnobUndoLayout = undoLayout;
                    foreach (var preset in new[] { "undo", "volume", "undo-mac", "brightness", "undo" })
                    {
                        KnobPresets.Apply(configured.Layers[2], preset);
                        check(configured.Layers[2].KnobUndoLayout == undoLayout,
                            board.DeviceId + ": changing presets retains the selected undo layout under " + culture);
                    }
                }
            }
            finally { CultureInfo.CurrentCulture = savedCulture; }
            foreach (var presetId in new[] { "zoom", "zoom-mac", "undo", "undo-mac" })
            {
                var configured = ProjectService.Clone(original);
                var layer = configured.Layers[2];
                layer.KnobCycleEnabled = true;
                layer.KnobCycleMask = 1 | 16 | 128;
                KnobPresets.Apply(layer, presetId);
                var preset = KnobPresets.Find(layer)!;
                var expectedCycle = string.Join(" → ", KnobPresets.CycleOptions
                    .Where(option => (layer.KnobCycleMask & option.Bit) != 0).Select(option => option.Label));
                check(KnobPresets.CycleSummary(layer) == expectedCycle,
                    board.DeviceId + ": matching shortcut variants do not add an extra cycle entry");
                layer.KnobCycleMacZoom = !layer.KnobCycleMacZoom;
                var before = Json(configured);
                check(KnobPresets.CycleSummary(layer) == preset.Label + " → " + expectedCycle,
                    board.DeviceId + ": a saved shortcut using the other OS appears explicitly in the cycle summary");
                check(Json(configured) == before && KnobPresets.Find(layer)?.Id == presetId,
                    board.DeviceId + ": describing a different OS variant does not overwrite saved knob directions");
            }
            foreach (var mask in Enumerable.Range(1, 15))
            foreach (var macMask in new[] { 0, mask, mask & 5 }.Distinct())
            {
                var configured = ProjectService.Clone(original);
                for (var layer = 0; layer < 4; layer++)
                {
                    configured.Layers[layer].KnobCycleEnabled = (mask & (1 << layer)) != 0;
                    configured.Layers[layer].KnobCycleMacZoom = (macMask & (1 << layer)) != 0;
                }
                var image = OnboardSerializer.Prepare(configured);
                var decoded = OnboardSerializer.Decode(image.Payload);
                check(image.SchemaVersion == 5 && image.RequiresKnobCycle && image.Payload[17] == mask &&
                    decoded.Layers.Select(layer => layer.KnobCycleEnabled).SequenceEqual(configured.Layers.Select(layer => layer.KnobCycleEnabled)) &&
                    image.Payload[31] == macMask && decoded.Layers.Select(layer => layer.KnobCycleMacZoom).SequenceEqual(configured.Layers.Select(layer => layer.KnobCycleMacZoom)),
                    board.DeviceId + ": cycle selection for mask " + mask + " survives device decoding");
                check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(image.Payload),
                    board.DeviceId + ": complete schema-5 configuration retains a byte-exact roundtrip");
                foreach (var layer in decoded.Layers) { layer.KnobCycleEnabled = false; layer.KnobCycleMacZoom = false; }
                check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(OnboardSerializer.Prepare(original).Payload),
                    board.DeviceId + ": turning cycling off restores the previous payload and retains custom bindings");
            }

            var active = ProjectService.Clone(original); active.Layers[0].KnobCycleEnabled = true;
            var valid = OnboardSerializer.Prepare(active).Payload;
            foreach (var selectedFunctions in new[] { 1, 2, 4, 8, 16, 32, 64, 128, 255 })
            foreach (var step in Enumerable.Range(1, 5))
            foreach (var layout in Enumerable.Range(0, 3))
            {
                var configured = ProjectService.Clone(original);
                configured.Layers[2].KnobCycleMask = selectedFunctions;
                configured.Layers[2].KnobStep = step;
                configured.Layers[2].KnobUndoLayout = layout;
                var image = OnboardSerializer.Prepare(configured);
                var decoded = OnboardSerializer.Decode(image.Payload);
                var effectOffset = OnboardSerializer.Shape(board.DeviceId).Size + 2 * 8;
                check(image.SchemaVersion == 5 && image.Payload[17] == 0 && image.Payload[effectOffset + 6] == selectedFunctions &&
                    image.Payload[effectOffset + 7] == (step | (layout << 3)) &&
                    decoded.Layers[2].KnobCycleMask == selectedFunctions && decoded.Layers[2].KnobStep == step && decoded.Layers[2].KnobUndoLayout == layout,
                    board.DeviceId + ": selected functions, step and undo layout persist even with long press disabled");
                check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(image.Payload), board.DeviceId + ": independent knob settings round-trip without losing an OS-layout choice");
                decoded.Layers[2].KnobCycleMask = 31; decoded.Layers[2].KnobStep = 1; decoded.Layers[2].KnobUndoLayout = 0;
                check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(OnboardSerializer.Prepare(original).Payload),
                    board.DeviceId + ": resetting knob options restores the complete previous configuration");
            }
            foreach (var invalid in new[] { -1, 0, 256 })
            {
                var project = ProjectService.Clone(original); project.Layers[2].KnobCycleMask = invalid;
                Reject(() => OnboardSerializer.Prepare(project), check, board.DeviceId + ": invalid selected-function mask is rejected");
            }
            foreach (var invalid in new[] { -1, 0, 6, 256 })
            {
                var project = ProjectService.Clone(original); project.Layers[2].KnobStep = invalid;
                Reject(() => OnboardSerializer.Prepare(project), check, board.DeviceId + ": invalid knob step is rejected");
            }
            foreach (var invalid in new[] { -1, 3, 255 })
            {
                var project = ProjectService.Clone(original); project.Layers[2].KnobUndoLayout = invalid;
                Reject(() => OnboardSerializer.Prepare(project), check, board.DeviceId + ": unsupported undo keyboard layout is rejected");
            }
            var baseEffectOffset = OnboardSerializer.Shape(board.DeviceId).Size;
            var emptyFunctions = valid.ToArray(); emptyFunctions[baseEffectOffset + 6] = 0;
            Reject(() => OnboardSerializer.Decode(emptyFunctions), check, board.DeviceId + ": wire data must select at least one function");
            foreach (var invalidPackedStep in new byte[] { 0, 6, 7, 24, 25, 32, 255 })
            {
                var corrupt = valid.ToArray(); corrupt[baseEffectOffset + 7] = invalidPackedStep;
                Reject(() => OnboardSerializer.Decode(corrupt), check, board.DeviceId + ": invalid packed step or undo layout is rejected");
            }
            foreach (var invalidMask in new byte[] { 0, 16, 128, 255 })
            {
                var corrupt = valid.ToArray(); corrupt[17] = invalidMask;
                Reject(() => OnboardSerializer.Decode(corrupt), check, board.DeviceId + ": invalid cycle mask " + invalidMask + " is rejected");
            }
            foreach (var invalidMacMask in new byte[] { 2, 16, 255 })
            {
                var reserved = valid.ToArray(); reserved[31] = invalidMacMask;
                Reject(() => OnboardSerializer.Decode(reserved), check, board.DeviceId + ": macOS zoom cannot be enabled outside cycling layers");
            }
            var macWithoutCycle = ProjectService.Clone(original); macWithoutCycle.Layers[0].KnobCycleMacZoom = true;
            Reject(() => OnboardSerializer.Prepare(macWithoutCycle), check, board.DeviceId + ": orphan macOS cycle setting is rejected before encoding");
            KnobPresets.Apply(active.Layers[0], "zoom-mac");
            check(active.Layers[0].KnobCycleMacZoom, board.DeviceId + ": macOS zoom preset selects macOS zoom for an enabled cycle");
            KnobPresets.Apply(active.Layers[0], "zoom");
            check(!active.Layers[0].KnobCycleMacZoom, board.DeviceId + ": Windows/Linux zoom preset selects Control zoom for an enabled cycle");
            var legacy = valid.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(4), 4);
            Reject(() => OnboardSerializer.Decode(legacy), check, board.DeviceId + ": older schema cannot disguise a cycle mask");

            foreach (var unsafePress in new[] { "MO(1)", "TG(2)", "TO(3)", "KC_LCTL", "KC_RCTL", "KC_LSFT", "KC_RSFT", "KC_LALT", "KC_RALT", "KC_LGUI", "KC_RGUI" })
            {
                var direct = ProjectService.Clone(original);
                direct.Layers[0].KnobCycleEnabled = true; direct.Layers[0].Keys[press].Binding = unsafePress;
                check(!KnobPresets.CanCycle(direct, 0), board.DeviceId + ": " + unsafePress + " cannot become a delayed short press");
                Reject(() => OnboardSerializer.Prepare(direct), check, board.DeviceId + ": invalid short-press configuration is rejected before encoding");
                var inherited = ProjectService.Clone(original);
                inherited.Layers[1].Keys[press].Binding = unsafePress;
                inherited.Layers[3].Keys[press].Binding = "KC_TRNS";
                inherited.Layers[3].KnobCycleEnabled = true;
                check(!KnobPresets.CanCycle(inherited, 3), board.DeviceId + ": a transparent press cannot inherit an unsafe lower-layer action");
                Reject(() => OnboardSerializer.Prepare(inherited), check, board.DeviceId + ": inherited unsafe cycle configuration is rejected");
            }
            var transparent = ProjectService.Clone(original);
            transparent.Layers[0].Keys[press].Binding = "KC_TRNS";
            transparent.Layers[2].KnobCycleEnabled = true;
            check(!KnobPresets.CanCycle(transparent, 2), board.DeviceId + ": transparent press needs a defined safe base action");

            var coordinates = press[1..].Split('c').Select(int.Parse).ToArray();
            var pressOffset = 32 + 2 * (coordinates[0] * board.Cols + coordinates[1]);
            var forgedPress = valid.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(forgedPress.AsSpan(pressOffset), 0x5221);
            Reject(() => OnboardSerializer.Decode(forgedPress), check, board.DeviceId + ": device read cannot accept a cycle flag with an unsafe press action");
        }
    }

    private static void CheckCycleOrder(Action<bool, string> check)
    {
        int[] identity = [1, 2, 4, 8, 16, 32, 64, 128];
        check(KnobPresets.DecodeOrder(0).SequenceEqual(identity) &&
            KnobPresets.DecodeOrder(40319).SequenceEqual(identity.Reverse()) &&
            KnobPresets.EncodeOrder([1, 2, 4, 8, 16, 32, 128, 64]) == 1 &&
            KnobPresets.EncodeOrder([2, 1, 4, 8, 16, 32, 64, 128]) == 5040,
            "Knob order uses the agreed first, adjacent, factorial and final permutation ranks");
        var permutations = new HashSet<string>(StringComparer.Ordinal);
        for (var rank = 0; rank < 40320; rank++)
        {
            var order = KnobPresets.DecodeOrder(rank);
            check(order.Order().SequenceEqual(identity) && KnobPresets.EncodeOrder(order) == rank &&
                permutations.Add(string.Join(",", order)),
                "Knob order rank " + rank + " has one unique, valid and reversible permutation");
        }
        foreach (var rank in new[] { -1, 40320, 65535, int.MaxValue })
            Reject(() => KnobPresets.DecodeOrder(rank), check, "An out-of-range knob order rank is rejected");

        foreach (var board in BoardDescriptorRegistry.ProtocolBoards)
        {
            var original = ProjectService.Create(board.DeviceId);
            check(original.Layers.All(layer => layer.KnobCycleOrder.SequenceEqual(identity)),
                board.DeviceId + ": every layer starts with the established order");
            var initial = OnboardSerializer.Prepare(original);
            if (!board.HasEncoder)
            {
                original.Layers[2].KnobCycleOrder = identity.Reverse().ToList();
                Reject(() => OnboardSerializer.Prepare(original), check, board.DeviceId + ": custom knob order needs a physical encoder");
                continue;
            }
            foreach (var changedLayer in Enumerable.Range(0, 4))
            foreach (var rank in new[] { 1, 5040, 19873, 40319 })
            {
                var project = ProjectService.Clone(original);
                var expected = KnobPresets.DecodeOrder(rank);
                project.Layers[changedLayer].KnobCycleOrder = expected.ToList();
                var image = OnboardSerializer.Prepare(project);
                var offset = OnboardSerializer.Shape(board.DeviceId, 5).Size;
                var decoded = OnboardSerializer.Decode(image.Payload);
                check(image.SchemaVersion == 6 && image.RequiresKnobCycle && image.RequiresKnobOrder &&
                    image.Payload.Length == offset + 8 && image.Payload[17] == 0 &&
                    OnboardSerializer.MacroCapacity(board.DeviceId, 6) == OnboardSerializer.MacroCapacity(board.DeviceId, 5),
                    board.DeviceId + ": order alone uses the extended format without enabling cycling or reducing macro capacity");
                check(Enumerable.Range(0, 4).All(layer =>
                    BinaryPrimitives.ReadUInt16LittleEndian(image.Payload.AsSpan(offset + layer * 2)) == (layer == changedLayer ? rank : 0) &&
                    decoded.Layers[layer].KnobCycleOrder.SequenceEqual(layer == changedLayer ? expected : identity)),
                    board.DeviceId + ": all four persistent order ranks retain the correct layer");
                check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(image.Payload),
                    board.DeviceId + ": reordered functions retain a byte-exact device roundtrip");
                decoded.Layers[changedLayer].KnobCycleOrder = identity.ToList();
                check(OnboardSerializer.Prepare(decoded).Payload.SequenceEqual(initial.Payload),
                    board.DeviceId + ": resetting the order restores every previous configuration byte");
            }
            foreach (var invalid in new List<int>?[] { null, [], [1, 2, 4, 8, 16, 32, 64],
                [1, 2, 4, 8, 16, 32, 64, 128, 1], [1, 2, 4, 8, 16, 32, 64, 64],
                [0, 2, 4, 8, 16, 32, 64, 128], [1, 2, 4, 8, 16, 32, 64, 256] })
            {
                var project = ProjectService.Clone(original);
                project.Layers[1].KnobCycleOrder = invalid!;
                Reject(() => OnboardSerializer.Prepare(project), check, board.DeviceId + ": invalid full-order lists cannot be encoded");
            }
            var ordered = ProjectService.Clone(original);
            ordered.Layers[1].KnobCycleEnabled = true;
            ordered.Layers[1].KnobCycleMask = 1 | 8 | 128;
            ordered.Layers[1].KnobCycleOrder = [128, 16, 8, 2, 64, 1, 32, 4];
            KnobPresets.Apply(ordered.Layers[1], "volume");
            var visible = KnobPresets.OrderedCycleOptions(ordered.Layers[1])
                .Where(option => (ordered.Layers[1].KnobCycleMask & option.Bit) != 0).ToArray();
            check(visible.Select(option => option.Bit).SequenceEqual([128, 8, 1]) &&
                KnobPresets.CycleSummary(ordered.Layers[1]) == string.Join(" → ", visible.Select(option => option.Label)),
                board.DeviceId + ": active functions follow user order and inactive entries do not appear");
            var valid = OnboardSerializer.Prepare(ordered).Payload;
            var orderOffset = valid.Length - 8;
            foreach (var rank in new ushort[] { 40320, 65535 })
            {
                var invalid = valid.ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(orderOffset + 6), rank);
                Reject(() => OnboardSerializer.Decode(invalid), check, board.DeviceId + ": even disabled layers reject invalid stored order ranks");
            }
            var redundant = valid.ToArray(); redundant.AsSpan(orderOffset, 8).Clear();
            Reject(() => OnboardSerializer.Decode(redundant), check, board.DeviceId + ": schema 6 cannot disguise an unchanged function order");
            Reject(() => OnboardSerializer.Decode(valid[..^1]), check, board.DeviceId + ": a truncated order trailer cannot be accepted");
            Reject(() => OnboardSerializer.Decode(valid.Concat(new byte[] { 0 }).ToArray()), check, board.DeviceId + ": extra order bytes cannot be accepted");
        }
    }

    private static string Json(AppProject project) => JsonSerializer.Serialize(project, ProjectService.JsonOptions);
    private static void Reject(Action action, Action<bool, string> check, string message)
    {
        try { action(); throw new Exception("Unexpected acceptance: " + message); }
        catch (InvalidDataException) { check(true, message); }
    }
}
