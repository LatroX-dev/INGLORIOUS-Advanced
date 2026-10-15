using System.Text.Json;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
async Task RejectAsync(Func<Task> action, string message) { try { await action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
Check(BoardDescriptorRegistry.All.Count == 3, "Exactly three hardware-tested protocol boards");
Check(BoardDescriptorRegistry.All.Select(b => b.BoardId).Distinct().Count() == 3 && BoardDescriptorRegistry.All.Select(b => b.DeviceId).Distinct().Count() == 3, "Unique board and model identities");
Check(BoardDescriptorRegistry.All is not BoardDescriptor[], "Registry does not expose its mutable array");
Check(BoardDescriptorRegistry.Experimental.Count == 13 && BoardDescriptorRegistry.ProtocolBoards.Count == 16, "Thirteen untested variants retain their experimental status");
Check(BoardDescriptorRegistry.ProtocolBoards.Select(b => b.BoardId).SequenceEqual(Enumerable.Range(1, 16).Select(id => (byte)id)), "Support status changes do not reorder protocol identities");
Check(BoardDescriptorRegistry.ProtocolBoards.Select(b => b.BoardId).Distinct().Count() == 16 && BoardDescriptorRegistry.ProtocolBoards.Select(b => b.RuntimeProductId).Distinct().Count() == 16, "Every candidate has a unique board and runtime identity");
Check(BoardDescriptorRegistry.ProtocolBoards.Where(board => board.BoardId != 16).All(board => board.DeviceId is "pro-iso" or "pro-ansi"
    ? board.FlashBytes == 262144 && board.SramBytes == 40960 : board.FlashBytes == 131072 && board.SramBytes == 28672), "All fifteen existing memory geometries remain unchanged");
Check(BoardDescriptorRegistry.EngineeringOnly.Count == 0 && BoardDescriptorRegistry.KnownBoards.Count == 16 &&
    BoardDescriptorRegistry.KnownBoards.Select(b => b.BoardId).Distinct().Count() == 16 && BoardDescriptorRegistry.KnownBoards.Select(b => b.RuntimeProductId).Distinct().Count() == 16,
    "The sixteenth normal protocol identity is unique without a hidden engineering-only board");
var engineering = BoardDescriptorRegistry.Get("gmmk3-pro-100-iso");
var editor = DeviceCatalog.Get(engineering.DeviceId);
Check(engineering.BoardId == 16 && engineering.RuntimeProductId == 0x1C10 && engineering.Rows == 14 && engineering.Cols == 8 &&
    engineering.LedCount == 126 && engineering.LegacySize == 5656 && engineering.PayloadCapacity == 7648 &&
    engineering.FlashBytes == 262144 && engineering.SramBytes == 36864 && engineering.StockProductId is null,
    "The separate PRO identity has exact geometry without borrowing legacy stock IDs");
Check(!engineering.IsEngineeringOnly && !engineering.IsExperimental && engineering.HasApprovedFirmwareManagement && BoardDescriptorRegistry.All.Contains(engineering) &&
    !editor.IsOfflineOnly && editor.SupportsOnboardConfiguration && !editor.IsExperimentalFirmware && editor.HasApprovedFirmwareManagement && editor.Name == "GMMK 3 PRO 100% ISO",
    "Only the exact hardware-tested GMMK 3 PRO 100% ISO is promoted");
Check(OnboardIdentity.MatchesNames(engineering.DeviceId, "INGLORIOUS", engineering.RuntimeName) &&
    !OnboardIdentity.MatchesNames(engineering.DeviceId, "InGlorious", "InGlorious" + engineering.ProductSuffix),
    "Removing the experimental badge never enables an unsupported legacy USB product spelling");
Check(!BoardDescriptorRegistry.TryGetStockUsb(0x320F, 0xE3CE, out _) &&
    !BoardDescriptorRegistry.TryGetStockUsb(0x342D, 0xE3CE, out _),
    "Promoted PRO stock discovery still uses the exact separate manufacturer catalog");
Check(StockDeviceCatalog.Match(0x342D, 0xE3CE)!.CandidateDeviceIds.SequenceEqual([engineering.DeviceId]), "Exact stock PRO PID resolves only board16");
Check(BoardDescriptorRegistry.TryGetRuntimeUsb(0xFEED, 0x1C10, out var engineeringRuntime) && engineeringRuntime == engineering &&
    PlatformDeviceService.ConnectedModelFromUsbMetadata(0xFEED, 0x1C10) == engineering.DeviceId,
    "Reserved runtime metadata is identified without opening its transport");
var engineeringScan = DeviceScanner.ParseDevices("""[{"vendorId":"FEED","productId":"1C10","release":"0001","instanceId":"USB\\VID_FEED&PID_1C10\\TEST"}]""").Single();
Check(engineeringScan.DeviceId == engineering.DeviceId && engineeringScan.IsSupported, "Metadata scanner names the distinct engineering model");
for (var schema = 1; schema <= 4; schema++) Check(OnboardSerializer.Shape(engineering.DeviceId, schema).Size == (schema == 1 ? 5656 : 7200), "Engineering shape is reserved separately");
var engineeringProject = ProjectService.Create(engineering.DeviceId);
OnboardSerializer.ValidateEditorProject(engineeringProject);
Check(engineeringProject.DeviceId != "gmmk3-100-iso" && ProjectService.Validate(engineeringProject).Count == 0, "Engineering editor preserves its own local profile identity");
var engineeringPayload = OnboardSerializer.Prepare(engineeringProject).Payload;
Check(engineeringPayload[6] == 16 && OnboardSerializer.Decode(engineeringPayload).DeviceId == engineering.DeviceId &&
    OnboardSerializer.Prepare(OnboardSerializer.Decode(engineeringPayload)).Payload.SequenceEqual(engineeringPayload), "Normal board16 payload is byte-exact and has a distinct model identity");
Check(OnboardHidTransport.IsAllowedPath(@"\\?\hid#vid_feed&pid_1c10&mi_01#offline"), "Exact board16 path passes the pure identity filter; this test never opens it");
Reject(() => OnboardHidTransport.Open(new(engineering.DeviceId, engineering.RuntimeName, @"\\?\hid#vid_feed&pid_1c0f&mi_01#offline")), "PRO model cannot open generic100ISO path, before native access");
var genericEditor = DeviceCatalog.Get("gmmk3-100-iso");
Check(JsonSerializer.Serialize(editor.Keys) == JsonSerializer.Serialize(genericEditor.Keys) &&
    editor.NonKeyLedIndices.SequenceEqual(genericEditor.NonKeyLedIndices) && editor.BadgeLedIndices.SequenceEqual(genericEditor.BadgeLedIndices) && editor.SideLedIndices.SequenceEqual(genericEditor.SideLedIndices),
    "Shared editor layout and default bindings match independently of model identity");
foreach (var (id, boardId, rows, cols, leds, legacySize, extendedSize, stockPid, runtimePid) in new[] {
    ("pro-iso", 1, 11, 8, 99, 5140, 6360, 0x5046, 0x1C01), ("numpad", 2, 5, 4, 31, 3780, 4184, 0x5088, 0x1C02) })
{
    var board = BoardDescriptorRegistry.Get(id);
    Check(board.BoardId == boardId && board.Rows == rows && board.Cols == cols && board.LedCount == leds && board.LegacySize == legacySize, "Reviewed wire dimensions unchanged");
    Check(BoardDescriptorRegistry.GetByBoardId((byte)boardId) == board, "Exact board reverse mapping");
    Check(BoardDescriptorRegistry.TryGetStockUsb(0x320F, stockPid, out var stock) && stock == board, "Exact stock identity");
    Check(BoardDescriptorRegistry.TryGetRuntimeUsb(0xFEED, runtimePid, out var runtime) && runtime == board, "Exact runtime identity");
    Check(!BoardDescriptorRegistry.TryGetRuntimeUsb(0x320F, runtimePid, out _) && !BoardDescriptorRegistry.TryGetStockUsb(0xFEED, stockPid, out _), "Vendor/product combinations cannot cross-match");
    for (var schema = 1; schema <= 4; schema++) Check(OnboardSerializer.Shape(id, schema).Size == (schema == 1 ? legacySize : extendedSize), "All schema sizes remain exact");
    OnboardSerializer.ValidateEditorProject(ProjectService.Create(id));
    Check(OnboardIdentity.MatchesNames(id, "INGLORIOUS", board.RuntimeName), "Exact runtime product name preserved");
    Check(OnboardIdentity.MatchesNames(id, "InGlorious", "InGlorious" + board.ProductSuffix), "Original product spelling preserved");
    foreach (var pair in new[] { (0x320F, stockPid), (0xFEED, runtimePid) })
    {
        var json = JsonSerializer.Serialize(new[] { new { vendorId = pair.Item1.ToString("X4"), productId = pair.Item2.ToString("X4"), release = "0001", instanceId = $"USB\\VID_{pair.Item1:X4}&PID_{pair.Item2:X4}\\TEST", name = "not trusted" } });
        var found = DeviceScanner.ParseDevices(json);
        Check(found.Count == 1 && found[0].DeviceId == id && found[0].IsSupported, "Scanner maps only the exact USB pair");
    }
}
foreach (var unknown in new[] { "", "unknown", "pro-rev3-iso", "gmmk3", "unverified-usb-320f-5093" })
{
    Check(!BoardDescriptorRegistry.TryGet(unknown, out _) && !OnboardIdentity.MatchesNames(unknown, "INGLORIOUS", "INGLORIOUS PRO ISO"), "Candidate has no wire identity");
    Reject(() => OnboardSerializer.Shape(unknown), "Candidate cannot acquire a wire shape");
    var device = new OnboardDevice(unknown, "INGLORIOUS PRO ISO", @"\\?\hid#vid_feed&pid_1c01&mi_01#offline");
    Reject(() => OnboardHidTransport.Open(device), "Unknown model is rejected before any native open");
    await RejectAsync(() => PlatformDeviceService.GetInfoAsync(device), "Unknown model INFO rejected without transport");
    await RejectAsync(() => PlatformDeviceService.ReadAsync(device), "Unknown model READ rejected without transport");
    var image = new OnboardImage(unknown, [], 0, 0, 0, []);
    var writes = 0;
    await RejectAsync(() => PlatformDeviceService.ApplyAsync(device, image, null, () => writes++), "Unknown model upload rejected without backup or transport");
    Check(writes == 0 && PlatformDeviceService.UnsupportedReason(new(unknown, 255, 0, 0, 0, 0), image) is not null, "No unknown image can be approved by capability flags");
}
foreach (var board in new byte[] { 0, 17, 255 }) Reject(() => BoardDescriptorRegistry.GetByBoardId(board), "Unknown board cannot decode as Numpad");
Check(!BoardDescriptorRegistry.TryGetStockUsb(0x320F, 0x5093, out _), "5093 is not the reviewed 5046 revision");
var candidate = DeviceScanner.ParseDevices("""[{"vendorId":"320F","productId":"5093","release":"0001","instanceId":"USB\\VID_320F&PID_5093\\TEST"}]""").Single();
Check(candidate.DeviceId == "unverified-usb-320f-5093" && !candidate.IsSupported && candidate.DeviceId != "pro-iso", "5093 remains visible but quarantined");
Check(DeviceScanner.ParseDevices("""[{"vendorId":"9999","productId":"5046","release":"0001","instanceId":"USB\\VID_9999&PID_5046\\TEST"}]""").Count == 0, "Foreign vendor cannot masquerade as PRO");
Reject(() => DeviceScanner.ParseDevices("""[{"vendorId":"320F","productId":"5046","release":"0001","instanceId":"USB\\VID_320F&PID_5093\\TEST"}]"""), "Metadata does not override physical USB identity");
foreach (var path in new[] { @"\\?\hid#foreign#vid_feed&pid_1c01&mi_01", @"\\?\hid#vid_320f&pid_5046&mi_01", @"\\?\hid#vid_feed&pid_1c11&mi_01", @"C:\vid_feed&pid_1c01&" })
    Check(!OnboardHidTransport.IsAllowedPath(path), "Unreviewed or embedded HID identity is rejected");
Check(OnboardHidTransport.IsAllowedPath(@"\\?\hid#vid_feed&pid_1c01&mi_01#offline"), "Actual reviewed path remains eligible for descriptor check");
Reject(() => OnboardHidTransport.Open(new("numpad", "INGLORIOUS Numpad", @"\\?\hid#vid_feed&pid_1c01&mi_01#offline")), "Cross-board path rejected before native access");

// Public, synthetic vectors only. Schema-specific offsets and historical schema
// decoding are covered by InGlorious.OnboardChecks; no local release tree is needed.
foreach (var model in DeviceCatalog.All.Where(d => d.SupportsOnboardConfiguration))
{
    var before = OnboardSerializer.Prepare(ProjectService.Create(model.Id)).Payload;
    var after = OnboardSerializer.Prepare(OnboardSerializer.Decode(before)).Payload;
    Check(after.SequenceEqual(before), "Registry codec preserves neutral public defaults: " + model.Id);
    var invalid = (byte[])before.Clone(); invalid[6] = 255;
    Reject(() => OnboardSerializer.Decode(invalid), "Unknown wire board never defaults to an existing board");
}
var candidates = DeviceCatalog.All.Where(d => d.IsExperimentalFirmware).ToArray();
Check(candidates.Length == 13 && candidates.All(d => d.SupportsOnboardConfiguration && !d.IsOfflineOnly && !d.HasApprovedFirmwareManagement), "Thirteen untested candidate codecs retain their individual support status");
foreach (var model in candidates)
{
    var board = BoardDescriptorRegistry.Get(model.Id);
    Check(board.IsExperimental && !board.HasApprovedFirmwareManagement && board.StockProductId is null && !BoardDescriptorRegistry.All.Contains(board), "No candidate invents a reviewed stock identity or flash permission");
    Check(BoardDescriptorRegistry.TryGetRuntimeUsb(0xFEED, board.RuntimeProductId, out var runtime) && runtime == board, "Exact experimental runtime identity");
    Check(OnboardIdentity.MatchesNames(model.Id, "INGLORIOUS", board.RuntimeName) && !OnboardIdentity.MatchesNames(model.Id, "InGlorious", "InGlorious" + board.ProductSuffix), "New firmware accepts only its exact current product-name pair");
    Check((model.ProductId.Length == 0 || !BoardDescriptorRegistry.TryGetStockUsb(0x320F, Convert.ToInt32(model.ProductId, 16), out _)), "Ambiguous or unapproved stock IDs cannot identify a candidate");
    Check(OnboardHidTransport.IsAllowedPath($@"\\?\hid#vid_feed&pid_{board.RuntimeProductId:x4}&mi_01#offline"), "Experimental runtime path remains subject to separate descriptor and INFO checks");
    Reject(() => OnboardHidTransport.Open(new(model.Id, board.RuntimeName, @"\\?\hid#vid_feed&pid_1c01&mi_01#offline")), "Candidate cannot open a stable board's path");
    var detected = DeviceScanner.ParseDevices(JsonSerializer.Serialize(new[] { new { vendorId="FEED", productId=board.RuntimeProductId.ToString("X4"), release="0001", instanceId=$"USB\\VID_FEED&PID_{board.RuntimeProductId:X4}\\TEST" } })).Single();
    Check(detected.DeviceId == model.Id && detected.IsSupported, "PnP maps an exact own candidate runtime identity only");
    var project = ProjectService.Create(model.Id);
    OnboardSerializer.ValidateEditorProject(project);
    Check(ProjectService.Validate(project).Count == 0, "Known candidate layout remains editable: " + model.Id);
    var encoded = OnboardSerializer.Prepare(project);
    Check(encoded.Payload[6] == board.BoardId && OnboardSerializer.Prepare(OnboardSerializer.Decode(encoded.Payload)).Payload.SequenceEqual(encoded.Payload), "Candidate has its own byte-exact default codec");
    var encoder = model.Keys.FirstOrDefault(k => k.IsEncoder);
    if (encoder is not null) { project.Layers[0].Keys[encoder.Id].Binding = "MO(1)"; Reject(() => OnboardSerializer.ValidateEditorProject(project), "Offline encoder cannot hold a layer"); project.Layers[0].Keys[encoder.Id].Binding = encoder.DefaultBinding; }
    var first = model.Keys[0];
    project.Layers[0].Keys[first.Id].Binding = "KC_NOT_A_KEY";
    Reject(() => OnboardSerializer.ValidateEditorProject(project), "Offline editor rejects unsupported binding codes");
    project.Layers[0].Keys[first.Id].Binding = "KC_TRNS";
    Reject(() => OnboardSerializer.ValidateEditorProject(project), "Offline base layer cannot be transparent");
    project.Layers[0].Keys[first.Id].Binding = first.DefaultBinding;
    project.Macros.Add(new() { Id = "bad", Name = "Media macro", Steps = [new() { Kind = "down", Key = "KC_MUTE" }, new() { Kind = "up", Key = "KC_MUTE" }] });
    Reject(() => OnboardSerializer.ValidateEditorProject(project), "Offline macro keeps keyboard-code limits");
    project.Macros.Clear();
    for (var macro = 0; macro < 7; macro++) project.Macros.Add(new() { Id = "large-" + macro, Name = "Local macro " + macro,
        Steps = Enumerable.Range(0, 128).Select(i => new MacroStep { Kind = i % 2 == 0 ? "down" : "up", Key = "KC_A" }).ToList() });
    Reject(() => OnboardSerializer.ValidateEditorProject(project), "Candidate enforces its real wire macro budget without truncation");
}
Console.WriteLine($"PASS {checks} board registry / scanner / offline-editor / synthetic-codec checks. No hardware discovery, native open or device write.");
