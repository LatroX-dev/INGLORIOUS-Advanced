using InGlorious.Core;

if (args is ["--validate-project", var projectPath])
{
    var project = ProjectService.Load(projectPath);
    Console.WriteLine($"PASS: offline project validation: {project.DeviceId}, {project.Layers.Count} layers, {project.Macros.Count} macros. No hardware access.");
    return;
}

int checks = 0;
void Check(bool passed, string message) { checks++; if (!passed) throw new Exception(message); }
void Reject(Action action, string message) { checks++; try { action(); } catch (InvalidDataException) { return; } catch (ArgumentException) { return; } catch (NotSupportedException) { return; } throw new Exception("Accepted unsafe input: " + message); }
Dictionary<string, StockBinding> Defaults(StockDevice device) => StockProtocol.Keys(device).ToDictionary(k => k.MatrixId, k => new StockBinding(k.CanRemap ? k.DefaultBinding : StockProtocol.PreserveFixedControl));

foreach (var (device, id) in new[] { (StockDevice.ProIso, "pro-iso"), (StockDevice.Numpad, "numpad") })
{
    var physical = DeviceCatalog.Get(id).Keys.Where(k => !k.IsEncoder).ToArray();
    var native = StockProtocol.Keys(device);
    Check(physical.Select(k => k.Id).Order().SequenceEqual(native.Select(k => k.MatrixId).Order()), id + " physical matrix coverage");
    Check(native.Select(k => k.BindingIndex).Distinct().Count() == native.Count, id + " native indices unique");
    Check(native.Where(k => k.LedIndex.HasValue).Select(k => k.MatrixId).Order().SequenceEqual(physical.Where(k => k.LedIndex.HasValue).Select(k => k.Id).Order()), id + " LED-less knob excluded");
    foreach (var key in native.Where(k => k.CanRemap && !(device == StockDevice.Numpad && k.MatrixId == "r0c0")))
        Check(physical.Single(k => k.Id == key.MatrixId).DefaultBinding == key.DefaultBinding, id + " identity/physical matrix " + key.MatrixId);
    var bindings = Defaults(device);
    var keys = StockProtocol.EncodeKeyBindings(device, 2, 1, bindings).ToArray();
    Check(keys.Length == 264 && keys[0] == 7 && keys[1] == 3 && keys[2] == 3 && keys[3] == 2, id + " key report header");
    bindings.Remove(native[0].MatrixId);
    Reject(() => StockProtocol.EncodeKeyBindings(device, 0, 0, bindings), "partial replacement");
    bindings = Defaults(device); bindings[native[0].MatrixId] = new("KC_TRNS");
    Reject(() => StockProtocol.EncodeKeyBindings(device, 0, 0, bindings), "QMK inheritance");
    bindings[native[0].MatrixId] = new("MO(1)");
    Reject(() => StockProtocol.EncodeKeyBindings(device, 0, 0, bindings), "QMK momentary layer");
    var colors = native.Where(k => k.LedIndex.HasValue).ToDictionary(k => k.MatrixId, _ => new StockColor(0x12, 0x34, 0x56));
    var sides = StockProtocol.SideLedIndices(device).ToDictionary(i => i, _ => new StockColor(0, 0, 0));
    var packets = StockProtocol.EncodeLighting(device, 1, 2, colors, sides);
    Check(packets.Count == 4 && packets.Take(3).All(p => p.Length == 264 && p.DelayAfterMs == 100) && packets[3].Length == 64 && packets[3].DelayAfterMs == 550, id + " page sequence and explicit short control report");
    var data = packets.Take(3).SelectMany(p => p.ToArray().Skip(8).Take(200)).ToArray();
    foreach (var key in native.Where(k => k.LedIndex.HasValue)) { var at = key.LedIndex!.Value * 5; Check(data[at] == 255 && data[at + 1] == key.LedIndex && data[at + 2] == 0x12 && data[at + 4] == 0x56, id + " LED index " + key.MatrixId); }
    var copy = packets[0].ToArray(); copy[1] = 99; Check(packets[0].ToArray()[1] == 6, "packet immutable");
    sides.Remove(sides.Keys.First()); Reject(() => StockProtocol.EncodeLighting(device, 0, 0, colors, sides), "missing side baseline");
    Check(StockProtocol.ValidateDraft(ProjectService.Create(id)).Any(e => e.Contains("four-layer")), "four-layer drafts rejected");
}

var pro = StockProtocol.EncodeKeyBindings(StockDevice.ProIso, 0, 0, Defaults(StockDevice.ProIso)).ToArray();
Check(pro[8 + 53] == 1 && pro[8 + 120 + 53] == 49 && pro[8 + 19] == 255 && pro[8 + 120 + 19] == 255, "PRO reserved ISO position and unused holes preserved");
Check(pro[8 + 14] == 3 && pro[8 + 120 + 14] == 2 && pro[8 + 109] == 1 && pro[8 + 120 + 109] == 160, "PRO fixed mute and Fn bytes");
var num = StockProtocol.EncodeKeyBindings(StockDevice.Numpad, 0, 0, Defaults(StockDevice.Numpad)).ToArray();
Check(num[8 + 20] == 83 && num[8 + 20 + 11] == 2 && num[8 + 11] == 3 && num[8 + 17] == 0, "Numpad stock NumLock/mute/default gap");
var remap = Defaults(StockDevice.Numpad); remap["r4c3"] = new("KC_A", 3);
var combo = StockProtocol.EncodeKeyBindings(StockDevice.Numpad, 0, 0, remap).ToArray();
Check(combo[8 + 19] == 227 && combo[8 + 20 + 19] == 4, "Numpad function index differs from LED index39");
remap["r4c3"] = new("MACRO", 0, 255);
Check(StockProtocol.EncodeKeyBindings(StockDevice.Numpad, 0, 0, remap).ToArray()[8 + 20 + 19] == 255, "explicit byte macro slot");
remap["r4c3"] = new("MACRO", 0, 256); Reject(() => StockProtocol.EncodeKeyBindings(StockDevice.Numpad, 0, 0, remap), "slot overflow");
remap["r2c3"] = new("KC_CALC"); Reject(() => StockProtocol.EncodeKeyBindings(StockDevice.Numpad, 0, 0, remap), "unproven encoder remap");
var modes = StockProtocol.EncodeMacroModes(StockDevice.Numpad, 0, 0, new Dictionary<string, StockMacroRepeat> { ["r4c3"] = StockMacroRepeat.WhileHeld, ["r4c0"] = StockMacroRepeat.Once }).ToArray();
Check(modes[8 + 39 * 2] == 255 && modes[9 + 39 * 2] == 255 && modes[9 + 38 * 2] == 1 && modes[8 + 19 * 2] == 0, "unsigned BE modes at observed native LED index");
var macro = StockProtocol.EncodeMacro(1, new[] { new StockMacroEvent("KC_A", true, 300), new StockMacroEvent("KC_A", false) }).ToArray();
Check(macro[0] == 7 && macro[1] == 5 && macro[2] == 1 && macro[8] == 129 && macro[9] == 44 && macro[10] == 4 && macro[11] == 0 && macro[12] == 1 && macro[13] == 4, "macro golden packet high-bit press and 15-bit delay");
var maxEvents = Enumerable.Range(0, 82).Select(i => new StockMacroEvent("KC_A", i % 2 == 0)).ToArray();
Check(StockProtocol.EncodeMacro(255, maxEvents).ToArray()[8 + 81 * 3 + 2] == 4, "82 complete events fit");
Reject(() => StockProtocol.EncodeMacro(1, maxEvents.Concat(maxEvents.Take(2)).ToArray()), "84 events never truncated");
Reject(() => StockProtocol.EncodeMacro(1, [new("KC_A", true, 32768), new("KC_A", false)]), "macro delay overflow");
Reject(() => StockProtocol.EncodeMacro(1, [new("KC_A", true), new("KC_A", false, 2)]), "unrepresentable trailing delay");
Reject(() => StockProtocol.EncodeMacro(1, [new("KC_A", false), new("KC_A", true)]), "unbalanced keyup");
Reject(() => StockProtocol.EncodeMacro(1, [new("KC_MUTE", true), new("KC_MUTE", false)]), "consumer usage not keyboard macro");
Reject(() => StockProtocol.SelectProfile(3, 0), "profile4"); Reject(() => StockProtocol.SelectProfile(0, 3), "layer4");
var longDraft = ProjectService.Create("numpad");
longDraft.Macros.Add(new MacroDefinition { Name = "Too long", Steps = Enumerable.Range(0, 128).Select(i => new MacroStep { Kind = i % 2 == 0 ? "down" : "up", Key = "KC_A" }).ToList() });
Check(StockProtocol.ValidateDraft(longDraft).Any(e => e.Contains("128 events exceed")), "128 draft events explicitly rejected");
var descriptor = new StockHidDescriptor(0x320f, 0x5088, 0xff01, 1, 264, 0, 0);
StockHidContract.ValidateDescriptor(StockDevice.Numpad, descriptor); Check(!StockHidContract.LiveTransportImplemented, "no live sender");
var profilePacket = StockProtocol.SelectProfile(1, 2);
var normalized = StockHidContract.NormalizeFeatureReport(StockDevice.Numpad, descriptor, profilePacket);
Check(normalized.Length == 264 && normalized[0] == 7 && normalized[1] == 1 && normalized[2] == 2 && normalized[3] == 3 && normalized.Skip(4).All(b => b == 0), "native wrapper zero-pads short profile report");
Check(profilePacket.Length == 64, "normalization preserves original packet");
Check(StockHidContract.NormalizeFeatureReport(StockDevice.Numpad, descriptor, StockProtocol.EncodeMacro(1, [new("KC_A", true), new("KC_A", false)])).Length == 264, "full report remains264");
Reject(() => StockHidContract.ValidateDescriptor(StockDevice.ProIso, descriptor), "wrong product");
Reject(() => StockHidContract.ValidateDescriptor(StockDevice.Numpad, descriptor with { UsagePage = 0xff00 }), "wrong collection");
Reject(() => StockHidContract.ValidateDescriptor(StockDevice.Numpad, descriptor with { FeatureReportByteLength = 65 }), "wrong feature size");
Reject(() => StockHidContract.Transmit(StockDevice.Numpad, descriptor, [StockProtocol.SelectProfile(0, 0)]), "live execution gated");
Console.WriteLine($"PASS: {checks} offline stock-protocol checks. No hardware accessed; no persistence/restore claim.");
