using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InGlorious.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static string Fingerprint(object value) => JsonSerializer.Serialize(value);
    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
    private static JsonObject Color(string key, int r = 10, int g = 20, int b = 30, int alpha = 100, bool breathing = false) => new() { ["keyIdentifier"] = key, ["color"] = new JsonArray(r, g, b, alpha), ["breathing"] = breathing };
    private static JsonObject Binding(string key, string target, string type = "Keystroke", string modifier = "None") => new() { ["componentIdentifier"] = key, ["target"] = new JsonObject { ["bindingType"] = type, ["keyIdentifier"] = target, ["modifier"] = modifier } };
    private static (JsonObject Devices, JsonObject Profiles) Fixture(string product = "GMMK3PRO100WiredISO")
    {
        var device = new JsonObject { ["productId"] = product, ["instanceId"] = "synthetic-device", ["currentDeviceProfileId"] = "synthetic-profile", ["iterableProfileIds"] = new JsonArray("synthetic-profile") };
        var layer = new JsonObject { ["order"] = 2, ["keybindingData"] = new JsonObject { ["boundKeys"] = new JsonArray(Binding("KeyA", "KeyB"), Binding("KeyC", "KeyD", "Macro"), Binding("KeyE", "KeyF", modifier: "Ctrl")) } };
        var profile = new JsonObject { ["id"] = "synthetic-profile", ["activeLayerIndex"] = 2, ["layers"] = new JsonObject { ["2"] = layer }, ["presetLightingRecord"] = new JsonObject { ["brightness"] = 40, ["effectIdentifier"] = "Wave1" }, ["perKeyLightingRecord"] = new JsonObject { ["brightness"] = 93, ["keyLightingRecords"] = new JsonArray(Color("KeyA", 255, 1, 33, 50), Color("Hashtag"), Color("Backslash"), Color("KeyC", breathing: true), Color("Sidelight"), Color("Unknown")) } };
        return (new() { ["deviceInstances"] = new JsonArray(device) }, new() { ["profiles"] = new JsonArray(profile) });
    }
    private static CoreImportPreview Parse((JsonObject Devices, JsonObject Profiles) data, string id = "gmmk3-pro-100-iso") => CoreSettingsImport.Parse(Bytes(data.Devices), Bytes(data.Profiles), id);
    private static JsonObject Device((JsonObject Devices, JsonObject Profiles) data) => (JsonObject)data.Devices["deviceInstances"]![0]!;
    private static JsonObject Profile((JsonObject Devices, JsonObject Profiles) data) => (JsonObject)data.Profiles["profiles"]![0]!;
    private static void Reject(Action<JsonObject, JsonObject> mutate, string message)
    {
        var data = Fixture(); mutate(data.Devices, data.Profiles);
        try { Parse(data); throw new Exception("Accepted: " + message); } catch (InvalidDataException) { _checks++; }
    }
    private static T Find<T>(DependencyObject parent, string name) where T : FrameworkElement => Descendants(parent).OfType<T>().Single(x => x.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) { yield return parent; foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) foreach (var nested in Descendants(child)) yield return nested; }

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new App(); app.InitializeComponent(); L.SetLanguage("en");
        var data = Fixture(); var beforeDevices = Fingerprint(data.Devices); var beforeProfiles = Fingerprint(data.Profiles);
        var preview = Parse(data); var current = ProjectService.Create(preview.DeviceId); current.Name = "Synthetic current";
        var model = DeviceCatalog.Get(current.DeviceId); string Key(string code) => model.Keys.Single(k => k.DefaultBinding == code).Id;
        current.Layers[0].SideLedColor = "#123456"; current.Layers[1].Keys[Key("KC_A")].Color = "#ABCDEF";
        current.Layers[0].Keys[Key("KC_C")].Color = "#987654"; current.Layers[0].Keys[Key("KC_E")].Binding = "KC_F8";
        current.Layers[0].LightingEffect.Mode = 2; current.DeviceSettings.DebounceMs = 9; current.Standby.Enabled = true;
        var before = Fingerprint(current); var copy = preview.ApplyToCopy(current);
        Check(preview.Colors.Count == 3 && preview.Bindings.Count == 1 && preview.OmittedColors == 3 && preview.OmittedBindings == 2, "only static mapped colors and simple unmodified bindings accepted");
        Check(preview.ActiveCoreLayer == 2 && preview.BrightnessPercent == 40, "active layer and actually transmitted global brightness, not unused per-key brightness");
        Check(copy.Layers[0].Keys[Key("KC_A")].Color == "#800111", "CORE percentage alpha and positive half rounding");
        Check(copy.Layers[0].Keys[Key("KC_NUHS")].Color == "#0A141E" && copy.Layers[0].Keys[Key("KC_NUBS")].Color == "#0A141E", "ISO Hashtag and Backslash are distinct physical keys");
        Check(copy.Layers[0].Keys[Key("KC_A")].Binding == "KC_B" && copy.Layers[0].Keys[Key("KC_E")].Binding == "KC_F8", "simple remap imports while modified keystroke stays unchanged");
        Check(copy.Layers[0].Keys[Key("KC_C")].Color == "#987654" && copy.Layers[0].SideLedColor == "#123456" && copy.Layers[1].Keys[Key("KC_A")].Color == "#ABCDEF", "omitted effects, sides and other layers preserve current colors");
        var led = model.Keys.Single(k => k.Id == Key("KC_A")).LedIndex!.Value;
        Check(copy.Layers[0].LedEffects[led].Mode == 0 && copy.Layers[0].LedEffects[led].Brightness == 102 && copy.Layers[0].LightingEffect.Mode == 2, "static override and brightness affect imported LEDs only");
        Check(copy.Name != current.Name && copy.DeviceSettings.DebounceMs == 9 && copy.Standby.Enabled && Fingerprint(current) == before && Fingerprint(data.Devices) == beforeDevices && Fingerprint(data.Profiles) == beforeProfiles, "separate copy preserves source profile and cache");
        Check(preview.ApplyToCopy(current, false, true).Layers[0].Keys[Key("KC_A")].Color == current.Layers[0].Keys[Key("KC_A")].Color, "unselected lighting untouched");
        Check(preview.ApplyToCopy(current, true, false).Layers[0].Keys[Key("KC_A")].Binding == current.Layers[0].Keys[Key("KC_A")].Binding, "unselected bindings untouched");
        var selectedBindings = new CoreImportSelection(preview, false, true).ApplyToCopy(current);
        Check(selectedBindings.Layers[0].Keys[Key("KC_C")].Color == "#987654" && selectedBindings.Layers[0].SideLedColor == "#123456" && selectedBindings.Layers[1].Keys[Key("KC_A")].Color == "#ABCDEF", "setup selection reapplies only chosen values to destination profile, never fresh red defaults");
        try { ((IDictionary<string, string>)preview.Colors).Add("forged", "#FFFFFF"); throw new Exception("mutable preview"); } catch (NotSupportedException) { _checks++; }
        Check(preview.DescribeChanges(true, true).Contains("40%") && preview.DescribeChanges(true, true).Contains("#800111") && !Fingerprint(preview).Contains("synthetic-profile") && !Fingerprint(preview).Contains("synthetic-device"), "preview shows values but does not retain CORE identifiers");
        foreach (var device in DeviceCatalog.All)
        {
            var products = CoreSettingsImport.CoreProductIds(device.Id); Check(products.Count > 0, "exact CORE product mapping " + device.Id);
            foreach (var product in products)
            {
                var sample = Fixture(product);
                Profile(sample)["perKeyLightingRecord"]!["keyLightingRecords"] = new JsonArray(Color(device.Id == "numpad" ? "Numpad1" : "KeyA"));
                Profile(sample)["layers"]!["2"]!["keybindingData"]!["boundKeys"] = new JsonArray();
                var result = Parse(sample, device.Id); Check(result.Colors.Count == 1 && result.DeviceId == device.Id, "exact model supports verified key color " + product);
            }
            var allNames = Enumerable.Range('A', 26).Select(c => "Key" + (char)c)
                .Concat(Enumerable.Range(0, 10).Select(n => "Digit" + n)).Concat(Enumerable.Range(0, 10).Select(n => "Numpad" + n))
                .Concat(Enumerable.Range(1, 24).Select(n => "F" + n))
                .Concat("Escape PrintScreen ScrollLock Pause Backquote Minus Equal Backspace Tab CapsLock BracketLeft BracketRight Semicolon Quote Enter Hashtag Backslash Comma Period Slash Space Menu ControlLeft ControlRight ShiftLeft ShiftRight AltLeft AltRight MetaLeft MetaRight Insert Delete Home End PageUp PageDown ArrowUp ArrowDown ArrowLeft ArrowRight NumLock NumpadDivide NumpadMultiply NumpadSubtract NumpadAdd NumpadEnter NumpadDecimal RotaryEncoder".Split(' '))
                .Concat(device.Id == "numpad" ? System.Array.Empty<string>() : new[] { "Fn" }).ToArray();
            var full = Fixture(products.First());
            Profile(full)["perKeyLightingRecord"]!["keyLightingRecords"] = new JsonArray(allNames.Select(name => (JsonNode)Color(name)).ToArray());
            Profile(full)["layers"]!["2"]!["keybindingData"]!["boundKeys"] = new JsonArray();
            var complete = Parse(full, device.Id);
            Check(complete.Colors.Count == device.Keys.Count(key => key.LedIndex is not null && !key.IsEncoder), "all physical RGB keys have an explicit named mapping " + device.Id);
        }
        Reject((d, p) => d["deviceInstances"]![0]!["productId"] = "GMMK3PRO100WiredANSI", "ANSI mismatch");
        Reject((d, p) => d["deviceInstances"]![0]!["productId"] = "GMMK3PROHE100WiredISO", "HE mismatch");
        Reject((d, p) => d["deviceInstances"]![0]!["productId"] = "GMMK3100WiredISO", "generic 3 is not PRO");
        Reject((d, p) => ((JsonArray)d["deviceInstances"]!).Add(d["deviceInstances"]![0]!.DeepClone()), "duplicate matching instances");
        Reject((d, p) => { var other = d["deviceInstances"]![0]!.DeepClone(); other["productId"] = "GMMKNumpad"; other["instanceId"] = "other-synthetic-device"; ((JsonArray)d["deviceInstances"]!).Add(other); }, "shared profile assignment across device instances");
        Reject((d, p) => d["deviceInstances"]![0]!["currentDeviceProfileId"] = "unassigned", "current profile not assigned");
        Reject((d, p) => ((JsonArray)d["deviceInstances"]![0]!["iterableProfileIds"]!).Add("synthetic-profile"), "duplicate profile assignment");
        Reject((d, p) => ((JsonArray)p["profiles"]!).Add(p["profiles"]![0]!.DeepClone()), "duplicate profile record");
        Reject((d, p) => ((JsonArray)p["profiles"]!).Clear(), "missing selected profile");
        Reject((d, p) => p["profiles"]![0]!["activeLayerIndex"] = 3, "active layer out of range");
        Reject((d, p) => p["profiles"]![0]!["layers"]!["2"]!["order"] = 1, "active layer order mismatch");
        Reject((d, p) => p["profiles"]![0]!["presetLightingRecord"]!["brightness"] = 101, "brightness out of range");
        Reject((d, p) => p["profiles"]![0]!["perKeyLightingRecord"]!["keyLightingRecords"]![0]!["color"] = new JsonArray(255, 0, 0, 255), "alpha is percent, not byte");
        Reject((d, p) => p["profiles"]![0]!["perKeyLightingRecord"]!["keyLightingRecords"]![0]!["color"] = new JsonArray(-1, 0, 0, 100), "negative RGB");
        Reject((d, p) => ((JsonArray)p["profiles"]![0]!["perKeyLightingRecord"]!["keyLightingRecords"]!).Add(Color("KeyA")), "duplicate color name");
        Reject((d, p) => ((JsonArray)p["profiles"]![0]!["perKeyLightingRecord"]!["keyLightingRecords"]!).Add(Color("BackslashLeft")), "two aliases cannot target one key");
        Reject((d, p) => ((JsonArray)p["profiles"]![0]!["layers"]!["2"]!["keybindingData"]!["boundKeys"]!).Add(Binding("KeyA", "KeyZ")), "duplicate bindings");
        try { CoreSettingsImport.Parse(Encoding.UTF8.GetBytes("{\"deviceInstances\":[],\"deviceInstances\":[]}"), Bytes(data.Profiles), current.DeviceId); throw new Exception("duplicate JSON accepted"); } catch (InvalidDataException) { _checks++; }
        try { preview.ApplyToCopy(ProjectService.Create("gmmk3-100-iso")); throw new Exception("wrong model accepted"); } catch (InvalidDataException) { _checks++; }
        try { preview.ApplyToCopy(current, false, false); throw new Exception("empty selection accepted"); } catch (InvalidDataException) { _checks++; }
        try { CoreSettingsImport.Parse(new byte[CoreSettingsImport.MaximumFileBytes + 1], Bytes(data.Profiles), current.DeviceId); throw new Exception("oversized source accepted"); } catch (InvalidDataException) { _checks++; }
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        Check(CoreSettingsImport.Parse(bom.Concat(Bytes(data.Devices)).ToArray(), bom.Concat(Bytes(data.Profiles)).ToArray(), current.DeviceId).Colors.Count == 3, "UTF-8 BOM supported without altering records");
        using (var reference = JsonDocument.Parse(File.ReadAllBytes("reference/glorious-stock-devices.json")))
        {
            var records = reference.RootElement.GetProperty("devices").EnumerateArray().Concat(reference.RootElement.GetProperty("identifiedWithoutFirmware").EnumerateArray());
            foreach (var row in records)
            {
                var ids = row.TryGetProperty("candidateDeviceIds", out var candidates) ? candidates.EnumerateArray().Select(c => c.GetString()!).ToArray() : System.Array.Empty<string>();
                if (row.GetProperty("coreProductId").GetString() == "GMMK3PRO100WiredISO") ids = new[] { "gmmk3-pro-100-iso" };
                foreach (var modelId in ids) Check(CoreSettingsImport.CoreProductIds(modelId).Contains(row.GetProperty("coreProductId").GetString()!), "mapping agrees with reviewed manufacturer reference " + modelId);
            }
        }
        var fixtureFolder = Path.GetFullPath(Path.Combine("workspace", "checks", "core-import-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixtureFolder);
        var devicesPath = Path.Combine(fixtureFolder, "Devices.json"); var profilesPath = Path.Combine(fixtureFolder, "DeviceProfiles.json");
        try
        {
            File.WriteAllBytes(devicesPath, Bytes(data.Devices)); File.WriteAllBytes(profilesPath, Bytes(data.Profiles));
            Check(CoreSettingsImport.ReadFolder(fixtureFolder, current.DeviceId).Colors.Count == 3, "folder reader loads the exact paired snapshot");
            Check(File.ReadAllBytes(devicesPath).SequenceEqual(Bytes(data.Devices)) && File.ReadAllBytes(profilesPath).SequenceEqual(Bytes(data.Profiles)), "source cache files remain byte-identical");
        }
        finally { File.Delete(devicesPath); File.Delete(profilesPath); Directory.Delete(fixtureFolder, recursive: false); }
        foreach (var entry in L.Catalog.Where(x => x.Key.Contains("CORE", StringComparison.Ordinal)))
            foreach (var language in L.SupportedLanguages.Where(l => l.Code != "de")) Check(entry.Value.TryGetValue(language.Code, out var value) && value.Length > 0, "CORE translation " + language.Code);
        var window = new CoreImportWindow(current, preview);
        Check(Find<TextBox>(window, "CoreImportPreview").Text.Contains("#800111") && Find<Button>(window, "ApplyCoreImport").IsEnabled, "UI displays exact selected values before accepting");
        Find<CheckBox>(window, "CoreImportColors").IsChecked = false;
        Check(!Find<TextBox>(window, "CoreImportPreview").Text.Contains("#800111"), "unchecked colors leave preview");
        Find<CheckBox>(window, "CoreImportBindings").IsChecked = false;
        Check(!Find<Button>(window, "ApplyCoreImport").IsEnabled && window.ImportedProject is null, "no selection or cancellation cannot import");
        Find<CheckBox>(window, "CoreImportColors").IsChecked = true; Find<CheckBox>(window, "CoreImportBindings").IsChecked = true;
        if (args.Length == 2 && args[0] == "--preview")
        {
            var visual = (FrameworkElement)window.Content; visual.Measure(new Size(650, 680)); visual.Arrange(new Rect(0, 0, 650, 680)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(650, 680, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(args[1]); png.Save(output);
        }
        window.Close();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, checks = _checks, hardwareAccess = false, personalCacheRead = false, profileWrites = false }));
    }
}
