using System.Security.Cryptography;
using System.Text.Json;
using InGlorious.Core;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException) { checks++; return; }
    throw new Exception(message);
}
Task<FirmwareResult> ExecutePreparedAsync(FirmwareService service, FirmwarePlan plan, FakeRunner runner, IProgress<string>? log = null)
{ return service.ExecuteAsync(plan, true, log); }
FirmwareRuntimeVersion RuntimeVersion(string deviceId, string release)
{
    if (!FirmwareSemanticVersion.TryParse(release.Split('-')[0], out var version)) throw new InvalidDataException("Invalid test release.");
    return new(new(deviceId, version, 1), $@"USB\VID_FEED&PID_{(deviceId == "pro-iso" ? "1C01" : "1C02")}\RUNTIME-TEST", DateTimeOffset.UtcNow);
}
Task<FirmwarePlan> PrepareFixtureAsync(FirmwareService service, string deviceId, FirmwareOperation operation, IProgress<string>? log = null)
    => service.PrepareAsync(deviceId, operation, log, installedFirmware: operation == FirmwareOperation.Update
        ? RuntimeVersion(deviceId, deviceId == "pro-iso" ? "0.2-fn-macros" : "0.3-wb32-bulk") : null);

foreach (var deviceId in new[] { "pro-iso", "numpad" })
{
    var v07 = new FirmwareSemanticVersion(0, 7, 0);
    var v08 = new FirmwareSemanticVersion(0, 8, 0);
    var v09 = new FirmwareSemanticVersion(0, 9, 0);
    var v091 = new FirmwareSemanticVersion(0, 9, 1);
    var v092 = new FirmwareSemanticVersion(0, 9, 2);
    var current = FirmwareRelease.CurrentVersion;
    var notes = FirmwareChangelog.Between(deviceId, v07, v09);
    Check(notes.Select(n => n.Version).SequenceEqual(new[] { v09, v08 }), "Changelog includes skipped releases in descending firmware-version order");
    Check(notes[0].Changes.Count == 2 && notes[1].Changes.Count == 1, "Each firmware release has only its concise documented changes");
    Check(notes[0].Changes.SequenceEqual(new[] { "New lighting test after firmware updates.", "Optional screensaver." }), "Both supported devices show the exact short update notes");
    Check(notes[1].Changes.SequenceEqual(new[] { "Installed firmware version shown in the app." }), "Previous release retains its short customer-facing version note");
    Check(FirmwareChangelog.Between(deviceId, v08, v09).Select(n => n.Version).SequenceEqual(new[] { v09 }), "Installed version is an exclusive lower bound");
    Check(FirmwareChangelog.Between(deviceId, v07, v08).Select(n => n.Version).SequenceEqual(new[] { v08 }), "Target version is an inclusive upper bound");
    Check(FirmwareChangelog.Between(deviceId, v07, new(0, 8, 99)).Select(n => n.Version).SequenceEqual(new[] { v08 }), "A target below0.9 cannot include0.9 notes");
    var currentNotes = FirmwareChangelog.Between(deviceId, v07, current);
    var expectedVersions = deviceId == "numpad" ? new[] { v092, v091, v09, v08 } : new[] { v092, v09, v08 };
    Check(currentNotes.Select(n => n.Version).SequenceEqual(expectedVersions), "Both devices receive the color-output update; only Numpad receives the earlier slider fix");
    var patchNotes = FirmwareChangelog.Between(deviceId, v09, v091);
    var colorNotes = FirmwareChangelog.Between(deviceId, v091, v092);
    Check(colorNotes.Count == 1 && colorNotes[0].Version == v092 && colorNotes[0].Changes.SequenceEqual(new[] { "Improved color output for less washed-out colors." }),
        "Both devices receive exactly the short color-output note from0.9.1 to0.9.2");
    Check(deviceId == "numpad" ? patchNotes.Count == 1 && patchNotes[0].Version == v091
        && patchNotes[0].Changes.SequenceEqual(new[] { "Fixed the Numpad lighting test stopping too early.", "Reduced unintended wake-ups from standby." }) : patchNotes.Count == 0,
        "Numpad patch note is exact; a PRO patch update invents no device-specific fix");
    Check(FirmwareChangelog.Between(deviceId, v07, new(65535, 65535, 65535)).All(n => n.Version <= current)
        && FirmwareChangelog.Between(deviceId, v07, new(65535, 65535, 65535)).Select(n => n.Version).SequenceEqual(expectedVersions), "A future target cannot expose notes beyond the current shipped firmware");
    foreach (var installed in new FirmwareSemanticVersion?[] { null, default(FirmwareSemanticVersion), current, new(0, 9, 3), new(1, 0, 0) })
        Check(FirmwareChangelog.Between(deviceId, installed, current).Count == 0, "Missing/default/current/newer firmware has no proposed update changelog");
    Check(FirmwareChangelog.Between(deviceId, v08, v08).Count == 0 && FirmwareChangelog.Between(deviceId, v09, v08).Count == 0, "Equal or downgrade targets never show update notes");
    Check(FirmwareChangelog.Between(deviceId, new(0, 4, 0), v07).Count == 0, "No features are invented for uncatalogued releases");
    var englishNotes = currentNotes.SelectMany(n => n.Changes).ToArray();
    L.SetLanguage("ja");
    Check(FirmwareChangelog.Between(deviceId, v07, current).SelectMany(n => n.Changes).SequenceEqual(englishNotes)
        && englishNotes[0] == "Improved color output for less washed-out colors."
        && englishNotes[^1] == "Installed firmware version shown in the app.", "Changelog remains English independently of the app language");
    L.SetLanguage("de");
    Check(((ICollection<FirmwareReleaseNotes>)notes).IsReadOnly && ((ICollection<string>)notes[0].Changes).IsReadOnly, "Catalog results and their changes cannot mutate shared release notes");
}
foreach (var unknownDevice in new string?[] { null, "", "PRO-ISO", "pro", "GMMK PRO", "other", "numpad " })
    Check(FirmwareChangelog.Between(unknownDevice!, new(0, 7, 0), FirmwareRelease.CurrentVersion).Count == 0, "Unknown model cannot receive another device's changelog");
if (args.Contains("--changelog-only"))
{
    Console.WriteLine($"PASS {checks} firmware-changelog checks. Pure catalog only; no files, processes or devices.");
    return;
}

foreach (var name in new[] { "Glorious Core", "GLORIOUS CORE", "glorious core" })
    Check(FirmwareService.IsGloriousCoreProcessName(name), "Verified shared CORE Legacy 1.1.36 / CORE 2.1.21 process name matches independent of Windows case");
foreach (var name in new string?[] { null, "", "Glorious", "Core", "GloriousCore", "Glorious_Core", "Glorious Core.exe", "Glorious Core 2", "Glorious Core Helper", " Glorious Core", "Glorious Core ", "InGlorious Core", "INGLORIOUS Core", "INGLORIOUS Advanced", "Squirrel", "setup", "RtkAudUService64", "updateDock", "updateDongle", "updateMouse" })
    Check(!FirmwareService.IsGloriousCoreProcessName(name), "Process-name filter excludes INGLORIOUS, partial matches and unrelated installed helpers: " + name);

using var fixture = new FirmwareFixture();
{
    L.SetLanguage("de");
    const string cli = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\"><MS><S N=\"Activity\">WinUSB timeout progress, must never become an error</S></MS></Obj><S S=\"Error\">Der zuvor identifizierte Bootloader ist nicht eindeutig am bekannten Anschluss vorhanden._x000D__x000A_At line:1 char:20_x000D__x000A_+ $items=Get-PnpDevice; throw 'WinUSB';_x000D__x000A_+ ~~~~~_x000D__x000A_CategoryInfo : OperationStopped_x000D__x000A_FullyQualifiedErrorId : SecretIdentifier</S></Objs>";
    var expected = FirmwareMessages.ForUser("Der erwartete Bootloader ist nicht bereit.");
    Check(FirmwareMessages.ForUser(cli) == expected && FirmwareMessages.ForUser("Der erwartete Bootloader ist nicht bereit. " + cli) == expected, "User CLIXML extracts only the real failure, not progress or echoed PowerShell code");
    var extracted = FirmwareMessages.ExtractDiagnosticText(cli);
    Check(extracted.Contains("Bootloader") && !extracted.Contains("WinUSB") && !extracted.Contains("CategoryInfo") && !extracted.Contains("_x000D_") && !extracted.Contains("<Objs"), "XML stream filtering and PowerShell escape decoding exclude invocation metadata");
    Check(FirmwareMessages.ForUser("#< CLIXML\n<Objs><S S=\"Warning\">Der geprüfte WinUSB-Treiber fehlt.</S></Objs>") == FirmwareMessages.ForUser("wrong-driver"), "Warning stream is recognized without exposing its text");
    Check(FirmwareMessages.ForUser("#< CLIXML\n<Objs><Obj S=\"Error\"><ToString>Windows-Gerätesuche dauerte zu lange.</ToString></Obj></Objs>") == FirmwareMessages.ForUser("timeout"), "Serialized error object supports its message without traversing arbitrary properties");
    var unknown = FirmwareMessages.ForUser(null);
    foreach (var raw in new string?[] { null, "", "  \r\n ", "<Objs><S S=\"Error\">Broken", "#< CLIXML\n<Objs><Obj S=\"progress\"><S>WinUSB</S></Obj></Objs>", "#< CLIXML\n<Objs><S S=\"Verbose\">timeout</S></Objs>", "#< CLIXML\n<Objs><!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/secret'>]><S S=\"Error\">&e;</S></Objs>", "{\n\"command\":\"PowerShell\",\n\"private\":\"D:\\\\private\"\n}", "[ {\"Bootloader\":false} ]", "Exception: D:\\private\\secret.txt USB\\VID_1234&PID_0000\\PRIVATE", "\0\u001b[31mPRIVATE\u001b[0m", new string('x', 4 * 1024 * 1024 + 1), "Success: firmware verified" })
        Check(FirmwareMessages.ForUser(raw) == unknown, "Unknown/progress/JSON/oversized diagnostics never leak or become a success");
    Check(FirmwareMessages.ForUser("stderr: Windows-Gerätesuche dauerte zu lange.\nIn Zeile:1 Zeichen:20\n+ Get-PnpDevice SECRET\nCategoryInfo: something") == FirmwareMessages.ForUser("timeout"), "Mixed PowerShell console errors retain only the relevant cause");
    Check(FirmwareMessages.ForUser("\u001b[31mDer geprüfte WinUSB-Treiber fehlt.\u001b[0m") == FirmwareMessages.ForUser("wrong-driver"), "ANSI decoration cannot leak into user messages");
    foreach (var language in L.SupportedLanguages)
    {
        L.SetLanguage(language.Code);
        foreach (var raw in new[] { cli, "wrong-driver", "timeout", "noch im Bootloader", "Original-Sicherungen fehlen", "PowerShell", "Pinned hash mismatch", "Ausgangsfirmware", "readback differs", "aktuellen Plan prüfen", "Access is denied", "unknown" })
        {
            var safe = FirmwareMessages.ForUser(raw);
            Check(FirmwareMessages.ForUser(safe) == safe, "Known translated failures remain idempotent in " + language.Code);
            var prefixed = L.T("Es wurde kein Flash-Vorgang gestartet. ") + safe;
            Check(FirmwareMessages.ForUser(prefixed) == prefixed, "No-write outcome survives repeated formatting in " + language.Code);
        }
        Check(FirmwareMessages.ForUser(FirmwareMessages.ForExecutionFailure()) == FirmwareMessages.ForExecutionFailure(), "Uncertain firmware outcome survives formatting in " + language.Code);
        Check(FirmwareMessages.ForUser(FirmwareMessages.ForUnsupportedSourceWithoutWrite()) == FirmwareMessages.ForUnsupportedSourceWithoutWrite(), "Proven unsupported-source/no-write outcome survives formatting in " + language.Code);
    }
    L.SetLanguage("de");
    var runner = new FakeRunner(fixture, "pro-iso", FirmwareOperation.Update);
    var service = fixture.Service(runner);
    var lightingCalls = runner.Calls.Count;
    service.RecordLightingTestFailure(new IOException("Synthetic early lighting-test interruption"));
    Check(runner.Calls.Count == lightingCalls && service.DiagnosticDirectory is { } lightingFolder && Directory.GetFiles(lightingFolder).Any(path =>
    {
        using var diagnostic = JsonDocument.Parse(File.ReadAllText(path));
        return diagnostic.RootElement.GetProperty("kind").GetString() == "lighting-test"
            && diagnostic.RootElement.GetProperty("error").GetString()!.Contains("Synthetic early lighting-test interruption", StringComparison.Ordinal);
    }), "Lighting-test failure is recorded locally without invoking any runner or device operation");
    var messages = new List<string>();
    var log = new ImmediateProgress(messages.Add);
    var plan = await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update, log);
    runner.IdentityError = cli;
    var failed = await service.CheckBootloaderAsync(plan);
    Check(!failed.Available && failed.Message == expected, "A CLIXML bootloader failure is a concise readiness error, never a success");
    Check(service.DiagnosticDirectory is { } folder && Directory.Exists(folder), "Pre-write errors have a real diagnostic directory");
    var count = Directory.GetFiles(service.DiagnosticDirectory!).Length;
    _ = await service.CheckBootloaderAsync(plan);
    Check(Directory.GetFiles(service.DiagnosticDirectory!).Length == count, "Repeated identical readiness failures do not create endless diagnostic copies");
    var rawPreserved = Directory.GetFiles(service.DiagnosticDirectory!).Any(path =>
    {
        using var data = JsonDocument.Parse(File.ReadAllText(path));
        return data.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.GetProperty("StandardError").GetString() == cli;
    });
    Check(rawPreserved, "The original CLIXML bytes decoded as UTF-8 remain in the separate diagnostic JSON");
    runner.IdentityError = null; runner.IdentityTimedOut = true;
    Check(!(await service.CheckBootloaderAsync(plan)).Available && (await service.CheckBootloaderAsync(plan)).Message == FirmwareMessages.ForUser("timeout"), "A bounded PnP timeout cannot permit a write");
    runner.IdentityTimedOut = false;
    var success = await ExecutePreparedAsync(service, plan, runner, log);
    Check(success.Verified && runner.LogSinks.All(value => !value), "Neither plan, PnP nor execution receives a raw UI sink");
    Check(messages.All(message => !message.Contains("Synthetic") && !message.Contains("CLIXML") && !message.Contains("Get-PnpDevice") && !message.Contains("D:\\") && !message.Contains("{")), "Visible log contains only fixed phase/outcome messages, no process JSON/code/paths");
    Check(messages.Any(message => message.Contains("Übertragung geprüft")), "Success phase is emitted only after complete evidence verification");
    foreach (var command in runner.Calls.Where(command => FirmwareProcessRunner.IsReadOnlyQuery(command.Kind)))
    {
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));
        Check(script.Contains("$ProgressPreference='SilentlyContinue'") || script.Contains("$ProgressPreference = 'SilentlyContinue'"), "PnP source suppresses progress");
        Check(script.Contains("UTF8Encoding") && script.Contains("$OutputEncoding") && script.Contains("[Console]::Error.WriteLine($_.Exception.Message)"), "PnP source emits UTF-8 and plain error text without automatic CLIXML error serialization");
        var start = FirmwareProcessRunner.BuildStartInfo(command);
        Check(start.StandardOutputEncoding?.CodePage == 65001 && start.StandardErrorEncoding?.CodePage == 65001 && !start.UseShellExecute && start.RedirectStandardError && start.RedirectStandardOutput, "Both redirected streams are explicitly decoded as UTF-8 without starting a process");
    }
    var normal = service.BuildNormalConnectionCommand(Environment.ProcessPath!);
    var normalScript = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(normal.Arguments[^1]));
    Check(normalScript.Contains("$ProgressPreference = 'SilentlyContinue'") && normalScript.Contains("UTF8Encoding") && normalScript.Contains("[Console]::Error.WriteLine($_.Exception.Message)"), "Reconnect query uses the same progress and encoding boundary");
    foreach (var failure in new[] { "execute-timeout", "after-execute-source-mismatch" })
    {
        runner = new FakeRunner(fixture, "pro-iso", FirmwareOperation.Update) { Failure = failure };
        service = fixture.Service(runner); messages.Clear();
        var result = await ExecutePreparedAsync(service, await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update, log), runner, log);
        Check(result.ExecutionStarted && !result.Verified && result.Message == FirmwareMessages.ForExecutionFailure(), "A partial-write timeout or post-write evidence mismatch never suggests retry or claims nothing was written");
        Check(!messages.Any(message => message.Contains("Übertragung geprüft")) && !result.Message.Contains("Bitte versuche") && !result.Message.Contains("Es wird nichts übertragen"), "No successful transfer phase or misleading pre-write instruction after failed verification");
    }
}
foreach (var id in new[] { "pro-iso", "numpad" })
foreach (var operation in new[] { FirmwareOperation.Update, FirmwareOperation.RestoreOriginal, FirmwareOperation.InstallFromOriginal })
{
    var runner = new FakeRunner(fixture, id, operation);
    var service = fixture.Service(runner);
    Check(service.CheckAvailability(id, operation).Available && runner.Calls.Count == 0, "Availability performs local file checks only");
    var plan = await PrepareFixtureAsync(service, id, operation);
    Check(runner.Calls.Count == 1 && runner.Calls[0].Kind == FirmwareCommandKind.Plan && !runner.Calls[0].Arguments.Contains("-Execute"), "Prepare only requests offline plan");
    Check(plan.DeviceId == id && plan.Operation == operation, "Plan retains exact device and operation");
    Check(!runner.Calls[0].Arguments.Contains("-Command") && !runner.Calls[0].Arguments.Contains("-EncodedCommand") && runner.Calls[0].Arguments.Contains("-File"), "Wrapper invocation is an argument-list file command");
    if (operation == FirmwareOperation.Update) Check(runner.Calls[0].Arguments.Contains(FirmwareRelease.ReleaseId) && runner.Calls[0].Arguments.TakeLast(2).SequenceEqual(new[] { "-UpdateFrom", "auto" }), "Update explicitly selects the current release and bounded automatic source detection");
    else Check(!runner.Calls[0].Arguments.Contains("-UpdateFrom"), "Non-update operations never select source detection");
    Check(runner.Calls[0].Arguments.Contains("-InstallRelease") == (operation == FirmwareOperation.InstallFromOriginal), "Only original-state installation selects an install release");
    if (operation == FirmwareOperation.InstallFromOriginal) Check(runner.Calls[0].Arguments.Contains("Install") && runner.Calls[0].Arguments.Contains(FirmwareRelease.ReleaseId), "Installation explicitly selects the current release");
    var withoutConsent = await service.ExecuteAsync(plan, explicitlyConfirmed: false);
    Check(!withoutConsent.ExecutionStarted && !withoutConsent.Verified && runner.Calls.Count == 1, "No explicit confirmation means no device or execute process");
    var result = await ExecutePreparedAsync(service, plan, runner);
    Check(result.Verified && result.ExecutionStarted && result.EvidenceDirectory is not null, "Complete evidence permits success");
    Check(result.EvidenceDirectory!.StartsWith(Path.Combine(fixture.Root, "reports", "firmware") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(result.EvidenceDirectory, "Archivmanifest.json")) && Directory.Exists(Path.Combine(result.EvidenceDirectory, "Originaldateien")), "Verified evidence is archived in the fixture's isolated report root");
    Check(result.ReconnectTarget is { } target && target.RunId == plan.Id && target.DeviceId == id && target.Operation == operation && target.LocationPaths.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(runner.LocationPaths.Order(StringComparer.OrdinalIgnoreCase)), "Verified evidence supplies the exact current operation and freshly captured connection");
    Check(runner.Calls.Select(command => command.Kind).SequenceEqual(new[] { FirmwareCommandKind.Plan, FirmwareCommandKind.IdentityCheck, FirmwareCommandKind.Execute }), "Live identity check precedes the only execute request");
    Check(runner.Calls[^1].Arguments.Count(argument => argument == "-Execute") == 1, "Exactly one execute switch");
    var replay = await service.ExecuteAsync(plan, explicitlyConfirmed: true);
    Check(!replay.ExecutionStarted && runner.Calls.Count == 3, "Consumed plan cannot retry an execution");
    Check(!service.CheckAvailability("other", operation).Available && !service.CheckAvailability(id, (FirmwareOperation)123).Available, "Unknown models and operations are blocked");

    foreach (var mutation in new[] { "after-byte", "after-tail", "before-pair", "before-padding", "before-prefix", "short-read", "missing-read", "prepared-image", "report-hash", "wrong-device", "unverified-report", "install-before", "install-tail", "install-release", "install-preserved" })
    {
        var evidence = fixture.MakeEvidence(id, operation);
        var pro = id == "pro-iso";
        string Capture(string phase, int number) => Path.Combine(evidence, pro ? $"{phase}-read-{number}.bin" : $"{phase}-{number}.bin");
        void Flip(string path, int index) { var data = File.ReadAllBytes(path); data[index] ^= 1; File.WriteAllBytes(path, data); }
        switch (mutation)
        {
            case "after-byte": Flip(Capture("after", 1), 0); break;
            case "after-tail": Flip(Capture("after", 2), FirmwareService.FlashBytes(id) - 1); break;
            case "before-pair": Flip(Capture("before", 2), 0); break;
            case "before-padding":
                if (operation != FirmwareOperation.Update) continue;
                Flip(Capture("before", 1), 200); Flip(Capture("before", 2), 200); break;
            case "before-prefix":
                if (operation != FirmwareOperation.Update) continue;
                Flip(Capture("before", 1), 0); Flip(Capture("before", 2), 0); break;
            case "short-read": File.WriteAllBytes(Capture("after", 1), new byte[20]); break;
            case "missing-read": File.Delete(Capture("after", 2)); break;
            case "prepared-image":
                if (operation != FirmwareOperation.Update) continue;
                Flip(Path.Combine(evidence, "update-image.bin"), 0); break;
            case "install-before":
                if (operation != FirmwareOperation.InstallFromOriginal) continue;
                Flip(Capture("before", 1), 0); Flip(Capture("before", 2), 0); break;
            case "install-tail":
                if (operation != FirmwareOperation.InstallFromOriginal) continue;
                // A duplicated wrong readback must not be accepted merely because both captures agree.
                Flip(Capture("after", 1), FirmwareService.FlashBytes(id) - 1); Flip(Capture("after", 2), FirmwareService.FlashBytes(id) - 1); break;
            default:
                if (mutation.StartsWith("install-") && operation != FirmwareOperation.InstallFromOriginal) continue;
                var reportPath = Path.Combine(evidence, pro ? "manifest.json" : "result.json");
                var report = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(reportPath))!;
                if (mutation == "report-hash") report[pro ? "payloadSha256" : "afterSha256"] = new string('0', 64);
                else if (mutation == "wrong-device") report[pro ? "device" : "targetInstance"] = "wrong";
                else if (mutation == "install-release") report["installRelease"] = "legacy";
                else if (mutation == "install-preserved") report["settingsPreserved"] = true;
                else if (pro) report["status"] = "unverified"; else report["readbackVerified"] = false;
                File.WriteAllText(reportPath, report.ToJsonString()); break;
        }
        await Reject(() => { service.VerifyEvidence(evidence, fixture.Binding(id), operation); return Task.CompletedTask; }, "Invalid evidence accepted: " + mutation);
    }
    await Reject(() => { service.VerifyEvidence(fixture.Root, fixture.Binding(id), operation); return Task.CompletedTask; }, "Evidence outside run directory rejected");
}
foreach (var id in new[] { "pro-iso", "numpad" })
foreach (var operation in new[] { FirmwareOperation.Update, FirmwareOperation.RestoreOriginal, FirmwareOperation.InstallFromOriginal })
{
    var runner = new FakeRunner(fixture, id, operation);
    var service = fixture.Service(runner);
    var plan = await PrepareFixtureAsync(service, id, operation);
    Check((await service.CheckBootloaderAsync(plan)).Available && (await service.CheckBootloaderAsync(plan)).Available, "A ready bootloader can be probed repeatedly without consuming the plan");
    Check(runner.Calls.Select(command => command.Kind).SequenceEqual(new[] { FirmwareCommandKind.Plan, FirmwareCommandKind.IdentityCheck, FirmwareCommandKind.IdentityCheck }), "Readiness probes only run the existing PnP identity query");
    var probeCommand = runner.Calls[^1];
    var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(probeCommand.Arguments[^1]));
    Check(script.Contains("Get-PnpDevice -PresentOnly", StringComparison.Ordinal) && script.Contains("$items.Count -ne 1", StringComparison.Ordinal) && script.Contains(".InstanceId -cne", StringComparison.Ordinal) && script.Contains(".Status -ne 'OK'", StringComparison.Ordinal) && script.Contains("DEVPKEY_Device_Service", StringComparison.Ordinal), "Probe checks a unique present device, its exact identity, status and driver");
    Check(!probeCommand.Arguments.Contains("-File") && !probeCommand.Arguments.Contains("-Execute") && !runner.LogSinks[^1], "No wrapper, execute switch or progress log is used by polling");

    foreach (var failure in new[] { "missing-identity", "ambiguous-identity", "wrong-identity", "case-identity", "wrong-driver", "identity-exit", "identity-json", "identity-fields", "identity-type" })
    {
        runner.Failure = failure;
        var unavailable = await service.CheckBootloaderAsync(plan);
        Check(!unavailable.Available && unavailable.Message.Length != 0, "Readiness rejects " + failure);
        Check(runner.Calls.All(command => command.Kind != FirmwareCommandKind.Execute), "A failed readiness probe never executes a firmware operation");
    }
    runner.Failure = "";
    Check((await service.CheckBootloaderAsync(plan)).Available, "Transient absence/errors do not consume the current prepared plan");
    runner.Failure = "missing-identity";
    var lostBeforeClick = await service.ExecuteAsync(plan, true);
    Check(!lostBeforeClick.Verified && !lostBeforeClick.ExecutionStarted && runner.Calls.All(command => command.Kind != FirmwareCommandKind.Execute), "A previously ready device must pass a fresh identity check at the explicit Execute boundary");
    var countAfterAttempt = runner.Calls.Count;
    Check(!(await service.CheckBootloaderAsync(plan)).Available && runner.Calls.Count == countAfterAttempt, "A consumed plan cannot keep polling");

    runner.Failure = "";
    var newPlan = await PrepareFixtureAsync(service, id, operation);
    Check((await service.CheckBootloaderAsync(newPlan)).Available, "A new plan can be prepared after a stopped preflight attempt");
    var executed = await ExecutePreparedAsync(service, newPlan, runner);
    Check(executed.Verified && runner.Calls.TakeLast(3).Select(command => command.Kind).SequenceEqual(new[] { FirmwareCommandKind.IdentityCheck, FirmwareCommandKind.IdentityCheck, FirmwareCommandKind.Execute }), "Polling does not authorize a write: Execute still independently checks identity and evidence");
}
{
    var runner = new FakeRunner(fixture, "pro-iso", FirmwareOperation.Update);
    var service = fixture.Service(runner);
    var first = await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update);
    var current = await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update);
    var foreign = fixture.Service(runner);
    var calls = runner.Calls.Count;
    Check(!(await service.CheckBootloaderAsync(first)).Available && !(await foreign.CheckBootloaderAsync(current)).Available && !(await service.CheckBootloaderAsync(null!)).Available && runner.Calls.Count == calls, "Stale, foreign-service and null plans are rejected before any PnP query");
    var scriptPath = Path.Combine(fixture.Root, "scripts", "Invoke-ProFirmware.ps1");
    var original = File.ReadAllBytes(scriptPath);
    try
    {
        File.AppendAllText(scriptPath, "tampered");
        Check(!(await service.CheckBootloaderAsync(current)).Available && runner.Calls.Count == calls, "Readiness rechecks pinned local files before every device query");
    }
    finally { File.WriteAllBytes(scriptPath, original); }
    Check((await service.CheckBootloaderAsync(current)).Available && runner.Calls.Count == calls + 1, "A failed pin check does not replace or consume the prepared plan");
}
foreach (var failure in new[] { "bad-plan", "plan-exit", "wrong-identity", "identity-exit", "execute-exit", "no-evidence" })
{
    var runner = new FakeRunner(fixture, "numpad", FirmwareOperation.Update) { Failure = failure };
    var service = fixture.Service(runner);
    if (failure is "bad-plan" or "plan-exit")
    {
        await Reject(() => PrepareFixtureAsync(service, "numpad", FirmwareOperation.Update), "Bad offline plan rejected");
        Check(runner.Calls.Count == 1 && runner.Calls[0].Kind == FirmwareCommandKind.Plan, "Failed plan never probes hardware");
    }
    else
    {
        var plan = await PrepareFixtureAsync(service, "numpad", FirmwareOperation.Update);
        var result = await ExecutePreparedAsync(service, plan, runner);
        Check(!result.Verified, "Exit zero or identity output alone cannot establish flash success");
        Check(result.ReconnectTarget is null, "Failed execution never issues a reconnect capability");
        Check(runner.Calls.Count(command => command.Kind == FirmwareCommandKind.Execute) == (failure.StartsWith("identity") || failure == "wrong-identity" ? 0 : 1), "Execution never precedes positive live identity");
    }
}
{
    var runner = new FakeRunner(fixture, "pro-iso", FirmwareOperation.Update);
    var service = fixture.Service(runner);
    var first = await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update);
    var second = await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update);
    Check(!(await service.ExecuteAsync(first, true)).ExecutionStarted && runner.Calls.Count == 2, "Stale plan cannot authorize a new operation");
    var foreignService = fixture.Service(runner);
    Check(!(await foreignService.ExecuteAsync(second, true)).ExecutionStarted && runner.Calls.Count == 2, "Plan cannot move to another service instance");
    var script = Path.Combine(fixture.Root, "scripts", "Invoke-ProFirmware.ps1");
    var original = File.ReadAllBytes(script); File.AppendAllText(script, "tampered");
    Check(!service.CheckAvailability("pro-iso", FirmwareOperation.Update).Available, "Changed wrapper hash blocks availability");
    Check(!(await service.ExecuteAsync(second, true)).ExecutionStarted && runner.Calls.Count == 2, "Post-plan wrapper changes block before any device process");
    File.WriteAllBytes(script, original);
    var bindings = Path.Combine(fixture.Root, "firmware", "local-firmware-bindings.json");
    var originalBindings = File.ReadAllBytes(bindings); File.Delete(bindings);
    Check(!service.CheckAvailability("pro-iso", FirmwareOperation.Update).Available && runner.Calls.Count == 2, "Missing private bindings leave public source build unavailable without process calls");
    File.WriteAllBytes(bindings, originalBindings);
    await Reject(() => PrepareFixtureAsync(service, "numpad;anything", FirmwareOperation.Update), "Device command injection rejected");
    await Reject(() => PrepareFixtureAsync(service, "pro-iso", (FirmwareOperation)99), "Unknown operation cannot become an argument");
}
foreach (var failure in new[] { "locations-missing", "locations-null", "locations-string", "locations-empty", "locations-null-item", "locations-number", "locations-blank", "locations-duplicate", "locations-malformed", "locations-child", "locations-too-many", "locations-too-long", "locations-acpi-child", "locations-acpi-root-only", "locations-acpi-wrong-root", "locations-acpi-mixed", "locations-acpi-empty", "locations-acpi-slash", "locations-acpi-long-name", "locations-acpi-too-deep", "locations-acpi-control", "locations-acpi-duplicate" })
{
    var runner = new FakeRunner(fixture, "numpad", FirmwareOperation.Update) { Failure = failure };
    var service = fixture.Service(runner);
    var plan = await PrepareFixtureAsync(service, "numpad", FirmwareOperation.Update);
    Check(!(await service.CheckBootloaderAsync(plan)).Available && runner.Calls.All(c => c.Kind != FirmwareCommandKind.Execute), "Ready requires the same complete valid connection data as Execute: " + failure);
    var result = await ExecutePreparedAsync(service, plan, runner);
    Check(!result.Verified && !result.ExecutionStarted && result.ReconnectTarget is null && runner.Calls.All(c => c.Kind != FirmwareCommandKind.Execute), "Missing/malformed connection fails before any write: " + failure);
}
foreach (var id in new[] { "pro-iso", "numpad" })
foreach (var operation in new[] { FirmwareOperation.Update, FirmwareOperation.RestoreOriginal, FirmwareOperation.InstallFromOriginal })
{
    var runner = new FakeRunner(fixture, id, operation);
    var service = fixture.Service(runner);
    var plan = await PrepareFixtureAsync(service, id, operation);
    Check((await service.CheckBootloaderAsync(plan)).Available, "Real Windows mixed PCI/USB and ACPI-only paths pass readiness for " + id);
    var result = await ExecutePreparedAsync(service, plan, runner);
    var target = result.ReconnectTarget!;
    Check(result.Verified && target.LocationPaths.SequenceEqual(runner.LocationPaths.Order(StringComparer.OrdinalIgnoreCase)), "Both complete Windows connection strings remain unchanged in the success context");
    var vid = operation == FirmwareOperation.RestoreOriginal ? "320F" : "FEED";
    var pid = (id, operation == FirmwareOperation.RestoreOriginal) switch { ("pro-iso", true) => "5046", ("numpad", true) => "5088", ("pro-iso", false) => "1C01", _ => "1C02" };
    Check(target.ExpectedVendorId == vid && target.ExpectedProductId == pid, "Normal identity depends on both operation and verified hardware model");
    var instance = $@"USB\VID_{vid}&PID_{pid}\NEWNORMAL";
    object Node(string? nodeId = null, string? status = "OK", object? problem = null, object? paths = null) => new { instanceId = nodeId ?? instance, status, problemCode = problem ?? 0, locationPaths = paths ?? runner.LocationPaths };
    void Snapshot(params object[] nodes) => runner.NormalResponseJson = JsonSerializer.Serialize(nodes);
    Snapshot(Node());
    var normal = await service.CheckNormalConnectionAsync(target);
    Check(normal.Available && normal.NormalInstanceId == instance, "Fresh normal-mode instance at captured port is recognized and returned exactly");
    var command = runner.Calls[^1];
    var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));
    Check(command.Kind == FirmwareCommandKind.NormalConnectionCheck && !command.Arguments.Contains("-File") && !command.Arguments.Contains("-Execute") && !runner.LogSinks[^1], "Normal polling invokes only its dedicated read-only query with no progress sink");
    Check(script.Contains("Get-PnpDevice -PresentOnly") && script.Contains("DEVPKEY_Device_LocationPaths") && script.Contains("DEVPKEY_Device_ProblemCode") && script.Contains("&MI_") && !script.Contains("CreateFile") && !script.Contains("dfu") && !script.Contains("Set-PnpDevice"), "Normal query reads only present physical devnodes, locations and problem status");
    Check(!script.Contains(instance) && !script.Contains(fixture.Binding(id).InstanceId), "Normal query is constant and never interpolates runtime device identifiers");
    Check(script.Split("Get-PnpDeviceProperty", StringSplitOptions.None).Length == 2 && script.Contains("-InstanceId $devices.InstanceId") && script.Contains("-ThrottleLimit 1"), "All root properties use one serialized provider call rather than a slow per-device query or concurrent result reassociation");
    Check(script.Contains("Where-Object InstanceId -eq $device.InstanceId") && script.Contains("$locations.Count -ne 1 -or $problems.Count -ne 1"), "Property records bind to their exact root ID and missing or duplicate fields fail closed");
    Check(script.Contains("if($devices.Count -gt 0)") && script.Contains("if($devices.Count -gt 4096)"), "Empty snapshots never turn into an unbounded all-device property query");
    foreach (var rootId in new[] { instance, instance.ToLowerInvariant(), fixture.Binding(id).InstanceId, @"USB\VID_046D&PID_C547\REALROOT" })
        Check(FirmwareService.IsNormalConnectionRootId(rootId), "Physical root identity remains admissible: " + rootId);
    foreach (var excluded in new string?[] { null, "", @"USB\VID_046D&PID_C547&LAMPARRAY\SLOT00", @"USB\VID_046D&PID_C547&MI_00\INTERFACE", @"USB\VID_320F&PID_5088&REV_0088\HARDWARE-ID-NOT-INSTANCE", @"HID\VID_320F&PID_5088\CHILD", @"USB\VID_320F&PID_5088\PARENT\CHILD", instance + "\n", instance + new string('X', 4096) })
        Check(!FirmwareService.IsNormalConnectionRootId(excluded), "Interface/software/hardware-ID extensions cannot enter the physical-port snapshot");
    Check(FirmwareProcessRunner.IsReadOnlyQuery(command.Kind) && FirmwareProcessRunner.IsReadOnlyQuery(FirmwareCommandKind.IdentityCheck) && !FirmwareProcessRunner.IsReadOnlyQuery(FirmwareCommandKind.Plan) && !FirmwareProcessRunner.IsReadOnlyQuery(FirmwareCommandKind.Execute), "Only the two PnP queries receive the host's bounded deadline");
    Snapshot(Node(paths: new[] { runner.LocationPaths[1].ToLowerInvariant() }));
    Check((await service.CheckNormalConnectionAsync(target)).Available, "A complete alternate captured path matches without case sensitivity");
    Snapshot(Node(), Node($@"USB\VID_{vid}&PID_{pid}\OTHERNORMAL", paths: new[] { "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(99)" }));
    Check((await service.CheckNormalConnectionAsync(target)).Available, "Another connected same-model device elsewhere does not confuse port correlation");
    foreach (var (name, nodes) in new (string, object[])[]
    {
        ("absent", []),
        ("different-port", [Node(paths: new[] { "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(99)" })]),
        ("prefix-only", [Node(paths: new[] { runner.LocationPaths[0] + "#USB(1)" })]),
        ("acpi-different-port", [Node(paths: new[] { "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS09)#ACPI(HSS9)" })]),
        ("acpi-prefix-only", [Node(paths: new[] { runner.LocationPaths[1] + "#ACPI(NEXT)" })]),
        ("acpi-interface-child", [Node(paths: new[] { runner.LocationPaths[1] + "#USBMI(0)" })]),
        ("wrong-model", [Node(@"USB\VID_FEED&PID_FFFF\WRONG")]),
        ("stock-after-update-or-custom-after-restore", [Node(operation == FirmwareOperation.RestoreOriginal ? @"USB\VID_FEED&PID_1C01\WRONG" : @"USB\VID_320F&PID_5046\WRONG")]),
        ("error-status", [Node(status: "Error")]),
        ("unknown-status", [Node(status: "Unknown")]),
        ("problem-code", [Node(problem: 28)]),
        ("problem-string", [Node(problem: "0")]),
        ("problem-fraction", [Node(problem: 0.5)]),
        ("problem-boolean", [Node(problem: false)]),
        ("missing-path", [Node(paths: Array.Empty<string>())]),
        ("two-devices-one-port", [Node(), Node($@"USB\VID_{vid}&PID_{pid}\OTHER")]),
        ("duplicate-row", [Node(), Node()]),
        ("composite-child", [Node($@"USB\VID_{vid}&PID_{pid}&MI_00\CHILD")]),
        ("hid-child", [Node($@"HID\VID_{vid}&PID_{pid}\CHILD")]),
        ("bootloader-still-present", [Node(), Node(fixture.Binding(id).InstanceId)]),
        ("bootloader-elsewhere", [Node(), Node(fixture.Binding(id).InstanceId, paths: new[] { "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(99)" })]),
        ("other-root-at-port", [Node(), Node(@"USB\VID_ABCD&PID_EF01\OTHER")]),
        ("virtual-lighting-node-at-port", [Node(@"USB\VID_046D&PID_C547&LAMPARRAY\SLOT00")]),
        ("virtual-lighting-node-after-valid-root", [Node(), Node(@"USB\VID_046D&PID_C547&LAMPARRAY\SLOT00", paths: new object?[] { null })]),
        ("invalid-location", [Node(paths: new[] { "USB(1)" })])
    })
    {
        Snapshot(nodes);
        var unavailable = await service.CheckNormalConnectionAsync(target);
        Check(!unavailable.Available && unavailable.NormalInstanceId is null, "Reconnect rejects " + name + " without returning a bindable normal instance");
    }
    foreach (var malformed in new[] { "not-json", "{}", "[null]", "[{\"instanceId\":null}]", "[{\"instanceId\":\"" + instance.Replace("\\", "\\\\") + "\",\"locationPaths\":null}]" })
    {
        runner.NormalResponseJson = malformed;
        Check(!(await service.CheckNormalConnectionAsync(target)).Available, "Malformed normal-mode snapshot is unknown");
    }
    Snapshot(Node()); runner.Failure = "normal-exit";
    Check(!(await service.CheckNormalConnectionAsync(target)).Available, "Failed PnP process cannot confirm normal mode");
    runner.Failure = "normal-timeout";
    Check(!(await service.CheckNormalConnectionAsync(target)).Available, "PnP timeout cannot confirm normal mode");
    runner.Failure = "";
    Check((await service.CheckNormalConnectionAsync(target)).Available, "Transient errors retain the target for a later fresh snapshot");
    Check(runner.Calls.Count(c => c.Kind == FirmwareCommandKind.Execute) == 1, "Normal-mode observation never adds a write or wrapper invocation");
    var count = runner.Calls.Count;
    var copiedPaths = runner.LocationPaths.ToArray();
    var forged = new FirmwareReconnectTarget(target.RunId, target.DeviceId, target.Operation, fixture.Binding(id).InstanceId, copiedPaths);
    copiedPaths[0] = "CHANGED";
    Check(forged.LocationPaths[0] == runner.LocationPaths[0] && forged.LocationPaths is not string[], "Target copies its paths and exposes no editable backing array");
    try { ((IList<string>)forged.LocationPaths)[0] = "CHANGED"; throw new Exception("Editable location list"); } catch (NotSupportedException) { checks++; }
    Check(!(await service.CheckNormalConnectionAsync(forged)).Available && !(await fixture.Service(runner).CheckNormalConnectionAsync(target)).Available && !(await service.CheckNormalConnectionAsync(null!)).Available && runner.Calls.Count == count, "Forged, foreign-service and null targets are rejected before a process starts");
    _ = await PrepareFixtureAsync(service, id, operation);
    count = runner.Calls.Count;
    Check(!(await service.CheckNormalConnectionAsync(target)).Available && runner.Calls.Count == count, "New preparation invalidates the earlier successful target");
}
{
    var runner = new FakeRunner(fixture, "pro-iso", FirmwareOperation.Update);
    var service = fixture.Service(runner);
    var plan = await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update);
    var result = await ExecutePreparedAsync(service, plan, runner);
    runner.NormalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    runner.NormalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = service.CheckNormalConnectionAsync(result.ReconnectTarget!);
    await runner.NormalStarted.Task;
    var prepare = PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update);
    runner.NormalGate.SetResult();
    Check(!(await pending).Available, "A late positive snapshot cannot outlive a new preparation request");
    _ = await prepare;
    var current = await ExecutePreparedAsync(service, await PrepareFixtureAsync(service, "pro-iso", FirmwareOperation.Update), runner);
    var count = runner.Calls.Count;
    Check(!(await service.ExecuteAsync(plan, false)).Verified, "A subsequent invalid execution is rejected");
    Check(!(await service.CheckNormalConnectionAsync(current.ReconnectTarget!)).Available && runner.Calls.Count == count, "Even an invalid new execution attempt revokes the previous result target");
    await Reject(() => PrepareFixtureAsync(service, "unknown", FirmwareOperation.Update), "Invalid preparation fails");
    Check(!(await service.CheckNormalConnectionAsync(current.ReconnectTarget!)).Available && runner.Calls.Count == count, "Failed preparation never restores an older reconnect target");
}
foreach (var failure in new[] { "", "execute-exit", "execute-throw" })
{
    var runner = new FakeRunner(fixture, "numpad", FirmwareOperation.Update) { Failure = failure };
    var service = fixture.Service(runner);
    var result = await ExecutePreparedAsync(service, await PrepareFixtureAsync(service, "numpad", FirmwareOperation.Update), runner);
    Check(result.EvidenceDirectory is not null && result.EvidenceDirectory.StartsWith(Path.Combine(fixture.Root, "reports", "firmware")), "Both successful and failed attempts archive available evidence");
    using var archive = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.EvidenceDirectory!, "Archivmanifest.json")));
    Check(archive.RootElement.GetProperty("firmwareVerified").GetBoolean() == (failure == "") && result.Verified == (failure == ""), "Archive records the actual verification outcome without promoting a failed flash");
    Check(result.ExecutionStarted && (result.ReconnectTarget is not null) == (failure == ""), "Only the successful archived result permits reconnect observation");
}
{
    var blocker = Path.Combine(fixture.Root, "archive-root-is-a-file"); File.WriteAllText(blocker, "Synthetic blocked archive directory");
    var runner = new FakeRunner(fixture, "numpad", FirmwareOperation.Update);
    var service = fixture.Service(runner, blocker);
    var messages = new List<string>();
    var result = await ExecutePreparedAsync(service, await PrepareFixtureAsync(service, "numpad", FirmwareOperation.Update), runner, new ImmediateProgress(messages.Add));
    Check(result.Verified && result.ReconnectTarget is not null && result.EvidenceDirectory!.StartsWith(Path.Combine(fixture.Root, "artifacts", "flash")), "Archive error preserves verified success and the original evidence directory");
    Check(File.Exists(Path.Combine(result.EvidenceDirectory!, "result.json")) && messages.Any(message => message.Contains("nicht archiviert")), "Archive fallback keeps the original files and reports the copy failure");
    Check(runner.Calls.Count(command => command.Kind == FirmwareCommandKind.Execute) == 1, "Report-copy errors never retry an execution");
    runner = new FakeRunner(fixture, "numpad", FirmwareOperation.Update) { Failure = "execute-exit" };
    service = fixture.Service(runner, blocker);
    result = await ExecutePreparedAsync(service, await PrepareFixtureAsync(service, "numpad", FirmwareOperation.Update), runner);
    Check(!result.Verified && result.ReconnectTarget is null && File.Exists(Path.Combine(result.EvidenceDirectory!, "result.json")), "Failed firmware plus failed archive retains the original uncertain result and evidence");
}
foreach (var id in new[] { "pro-iso", "numpad" })
{
    for (var index = 0; index < fixture.Binding(id).UpdateSources.Count; index++)
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update) { SourceIndex = index };
        var service = fixture.Service(runner);
        var result = await ExecutePreparedAsync(service, await service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: RuntimeVersion(id, fixture.Binding(id).UpdateSources[index].Release)), runner);
        Check(result.Verified && !result.WriteNotAttemptedVerified, "Each permitted migration source independently produces verified evidence: " + id + "/" + index);
    }
    foreach (var mutation in new[] { "source-missing", "source-duplicate", "source-path", "source-size", "source-hash", "source-release", "selected-prematurely", "selected-missing", "manual-source", "target-size", "target-hash" })
    {
        var plan = System.Text.Json.Nodes.JsonNode.Parse(fixture.PlanJson(id, FirmwareOperation.Update))!;
        var sources = plan["updateSources"]!.AsArray();
        switch (mutation)
        {
            case "source-missing": sources.RemoveAt(3); break;
            case "source-duplicate": sources[3] = sources[0]!.DeepClone(); break;
            case "source-path": sources[0]!["path"] = "../outside.bin"; break;
            case "source-size": sources[0]!["bytes"] = 1; break;
            case "source-hash": sources[0]!["sha256"] = new string('0', 64); break;
            case "source-release": sources[0]!["release"] = "other"; break;
            case "selected-prematurely": plan["updateFromRelease"] = fixture.Binding(id).UpdateSources[0].Release; break;
            case "selected-missing": plan.AsObject().Remove("updateFromCodeBytes"); break;
            case "manual-source": plan["updateFrom"] = "legacy"; break;
            case "target-size": plan[id == "pro-iso" ? "updateToCodeBytes" : "codeBytes"] = 1; break;
            case "target-hash": plan["updateToCodeSha256"] = new string('0', 64); break;
        }
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update) { PlanOverride = plan.ToJsonString() };
        await Reject(() => PrepareFixtureAsync(fixture.Service(runner), id, FirmwareOperation.Update), "Auto source plan mutation accepted: " + mutation);
        Check(runner.Calls.All(command => command.Kind == FirmwareCommandKind.Plan), "Bad metadata cannot reach even device identity query");
    }
    foreach (var field in new[] { "updateFromRelease", "updateFromCodeBytes", "updateFromCodeSha256", "updateSources" })
    {
        var run = fixture.MakeEvidence(id, FirmwareOperation.Update, 3);
        var path = Path.Combine(run, id == "pro-iso" ? "manifest.json" : "result.json");
        var report = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var metadata = id == "pro-iso" ? report["update"]! : report;
        metadata[field] = field == "updateFromCodeBytes" ? System.Text.Json.Nodes.JsonValue.Create(1) : field == "updateSources" ? new System.Text.Json.Nodes.JsonArray() : System.Text.Json.Nodes.JsonValue.Create("other");
        File.WriteAllText(path, report.ToJsonString());
        await Reject(() => { fixture.Service(new FakeRunner(fixture, id, FirmwareOperation.Update)).VerifyEvidence(run, fixture.Binding(id), FirmwareOperation.Update); return Task.CompletedTask; }, "Selected report source differs from independently identified capture: " + field);
    }
    // Two prefix lengths can otherwise describe the same FF-padded image. Exactly one must match.
    {
        var binding = fixture.Binding(id); var oldSource = binding.UpdateSources[1];
        var path = Path.Combine(fixture.Root, oldSource.Path); var oldBytes = File.ReadAllBytes(path);
        var pinIndex = binding.UpdateFiles.FindIndex(pin => pin.Path == oldSource.Path); var oldPin = binding.UpdateFiles[pinIndex];
        try
        {
            var ambiguous = Enumerable.Repeat((byte)255, (int)oldSource.Bytes).ToArray();
            File.ReadAllBytes(Path.Combine(fixture.Root, binding.UpdateSources[0].Path)).CopyTo(ambiguous, 0);
            var hash = Convert.ToHexString(SHA256.HashData(ambiguous)); File.WriteAllBytes(path, ambiguous);
            binding.UpdateSources[1] = oldSource with { Sha256 = hash }; binding.UpdateFiles[pinIndex] = oldPin with { Sha256 = hash };
            var run = fixture.MakeEvidence(id, FirmwareOperation.Update);
            await Reject(() => { fixture.Service(new FakeRunner(fixture, id, FirmwareOperation.Update)).VerifyEvidence(run, binding, FirmwareOperation.Update); return Task.CompletedTask; }, "Ambiguous source prefixes cannot verify a firmware result");
        }
        finally { File.WriteAllBytes(path, oldBytes); binding.UpdateSources[1] = oldSource; binding.UpdateFiles[pinIndex] = oldPin; }
    }
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update) { Failure = "prewrite-unsupported" };
        var service = fixture.Service(runner);
        var result = await ExecutePreparedAsync(service, await PrepareFixtureAsync(service, id, FirmwareOperation.Update), runner);
        Check(!result.Verified && result.ExecutionStarted && result.WriteNotAttemptedVerified && result.ReconnectTarget is null && result.Message == FirmwareMessages.ForUnsupportedSourceWithoutWrite(), "A complete saved read-only failure transcript establishes no write, without claiming success or enabling reconnect");
        Check(runner.Calls.Count(command => command.Kind == FirmwareCommandKind.Execute) == 1, "No-write classification never repeats a wrapper");
    }
    foreach (var mutation in new[] { "write-flag", "missing-flag", "missing-command", "extra-command", "download-argument", "foreign-tool", "wrong-output-path", "failed-read", "timed-out", "wrong-device", "read-pair", "short-read", "known-source", "write-artifact", "missing-report", "malformed-report" })
    {
        var run = fixture.MakePrewriteFailure(id); var pro = id == "pro-iso";
        var reportPath = Path.Combine(run, pro ? "FAILED.json" : "result.json");
        var report = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(reportPath))!; var commands = report["commands"]!.AsArray();
        switch (mutation)
        {
            case "write-flag": report[pro ? "writeAttempted" : "firmwareChanged"] = true; break;
            case "missing-flag": report.AsObject().Remove(pro ? "writeAttempted" : "firmwareChanged"); break;
            case "missing-command": commands.RemoveAt(2); break;
            case "extra-command": commands.Add(commands[2]!.DeepClone()); break;
            case "download-argument": commands[2]!["arguments"] = JsonSerializer.SerializeToNode(new[] { "-D", "anything.bin" }); break;
            case "foreign-tool": commands[0]!["executable"] = Path.Combine(fixture.Root, "other.exe"); break;
            case "wrong-output-path": commands[2]!["arguments"]![pro ? 15 : 1] = Path.Combine(run, "other.bin"); break;
            case "failed-read": commands[2]!["exitCode"] = 1; break;
            case "timed-out": commands[2]!["timedOut"] = true; break;
            case "wrong-device":
                if (pro) { var p = Path.Combine(run, "plan.json"); var plan = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p))!; plan["device"] = "wrong"; File.WriteAllText(p, plan.ToJsonString()); }
                else report["targetInstance"] = "wrong"; break;
            case "read-pair": { var p = Path.Combine(run, pro ? "before-read-2.bin" : "before-2.bin"); var bytes = File.ReadAllBytes(p); bytes[^1] ^= 1; File.WriteAllBytes(p, bytes); break; }
            case "short-read": File.WriteAllBytes(Path.Combine(run, pro ? "before-read-2.bin" : "before-2.bin"), [0]); break;
            case "known-source": foreach (var number in new[] { 1, 2 }) { var p = Path.Combine(run, pro ? $"before-read-{number}.bin" : $"before-{number}.bin"); var bytes = File.ReadAllBytes(p); bytes[0] ^= 128; File.WriteAllBytes(p, bytes); } break;
            case "write-artifact": File.WriteAllText(Path.Combine(run, "write.stdout.txt"), "unknown"); break;
        }
        File.WriteAllText(reportPath, report.ToJsonString());
        if (mutation == "missing-report") File.Delete(reportPath);
        if (mutation == "malformed-report") File.WriteAllText(reportPath, "not-json");
        Check(!fixture.Service(new FakeRunner(fixture, id, FirmwareOperation.Update)).VerifyNoWriteFailure(run, fixture.Binding(id), FirmwareOperation.Update), "Incomplete or contradictory no-write evidence remains uncertain: " + id + "/" + mutation);
    }
}
foreach (var id in new[] { "pro-iso", "numpad" })
{
    var sources = fixture.Binding(id).UpdateSources;
    Check(sources.Count == 8, "Runtime migration tests cover eight known source releases for " + id);
    for (var index = 0; index < sources.Count; index++)
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update) { SourceIndex = index };
        var service = fixture.Service(runner); var runtime = RuntimeVersion(id, sources[index].Release);
        var plan = await service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: runtime);
        Check(plan.DeviceId == id && plan.Operation == FirmwareOperation.Update && plan.ExpectedSourceRelease == sources[index].Release && plan.SourceVersion == runtime.Report.Version.ToString(), "Plan binds the reported semantic version to exactly one approved source");
        Check(runner.Calls.Count == 1 && runner.Calls[0].Kind == FirmwareCommandKind.Plan && !runner.Calls[0].Arguments.Contains("-ExpectedSourceRelease"), "Preparation only runs the offline plan, without device queries or an Execute-only parameter");
        var result = await service.ExecuteAsync(plan, true);
        Check(result.Verified && !result.WriteNotAttemptedVerified, "Every runtime-bound known source can verify its independent fresh full captures");
        Check(runner.Calls.Select(call => call.Kind).SequenceEqual(new[] { FirmwareCommandKind.Plan, FirmwareCommandKind.IdentityCheck, FirmwareCommandKind.Execute }), "Bound update still uses exactly Plan, fresh identity and one execution");
        var command = runner.Calls[^1]; var expectedIndex = command.Arguments.ToList().IndexOf("-ExpectedSourceRelease");
        Check(command.Arguments.Count(value => value == "-ExpectedSourceRelease") == 1 && expectedIndex >= 0 && command.Arguments[expectedIndex + 1] == sources[index].Release, "Execution carries exactly the source shown during preparation");
    }
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update);
        var service = fixture.Service(runner);
        await Reject(() => service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: null), "An update without a verified runtime version must be rejected");
        Check(runner.Calls.Count == 0, "Explicitly missing runtime version is rejected before any plan, identity or execution process");
        await Reject(() => service.PrepareAsync(id, FirmwareOperation.Update), "Omitting the optional API argument must not bypass the required update version");
        Check(runner.Calls.Count == 0, "The default null argument cannot start a migration or any process");
    }
    foreach (var invalid in new[] { "current", "newer-patch", "newer-major", "wrong-model", "wrong-usb-model", "stock-usb", "hid-child", "interface-usb", "blank-usb", "null-usb", "usb-control", "usb-too-long", "default-time", "future-time", "build-zero", "unknown-source", "null-report" })
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update); var service = fixture.Service(runner);
        var value = RuntimeVersion(id, sources[0].Release);
        value = invalid switch
        {
            "current" => value with { Report = value.Report with { Version = FirmwareRelease.CurrentVersion, BuildId = FirmwareRelease.CurrentBuildId } },
            "newer-patch" => value with { Report = value.Report with { Version = new(0, 9, 3) } },
            "newer-major" => value with { Report = value.Report with { Version = new(1, 0, 0) } },
            "wrong-model" => value with { Report = value.Report with { DeviceId = id == "pro-iso" ? "numpad" : "pro-iso" } },
            "wrong-usb-model" => value with { UsbInstanceId = $@"USB\VID_FEED&PID_{(id == "pro-iso" ? "1C02" : "1C01")}\RUNTIME-TEST" },
            "stock-usb" => value with { UsbInstanceId = @"USB\VID_320F&PID_5046\RUNTIME-TEST" },
            "hid-child" => value with { UsbInstanceId = value.UsbInstanceId.Replace("USB\\", "HID\\", StringComparison.Ordinal) },
            "interface-usb" => value with { UsbInstanceId = value.UsbInstanceId.Replace("\\RUNTIME-TEST", "&MI_02\\RUNTIME-TEST", StringComparison.Ordinal) },
            "blank-usb" => value with { UsbInstanceId = "" }, "null-usb" => value with { UsbInstanceId = null! },
            "usb-control" => value with { UsbInstanceId = value.UsbInstanceId + "\n" },
            "usb-too-long" => value with { UsbInstanceId = value.UsbInstanceId + new string('X', 257) },
            "default-time" => value with { ObservedAt = default }, "future-time" => value with { ObservedAt = DateTimeOffset.UtcNow.AddHours(1) },
            "build-zero" => value with { Report = value.Report with { BuildId = 0 } },
            "unknown-source" => value with { Report = value.Report with { Version = new(0, 1, 0) } },
            "null-report" => value with { Report = null! }, _ => throw new InvalidOperationException()
        };
        await Reject(() => service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: value), "Invalid runtime report accepted: " + id + "/" + invalid);
        Check(runner.Calls.Count == 0, "Invalid runtime version rejected before even a plan process: " + invalid);
    }
    foreach (var operation in new[] { FirmwareOperation.RestoreOriginal, FirmwareOperation.InstallFromOriginal })
    {
        var runner = new FakeRunner(fixture, id, operation);
        await Reject(() => fixture.Service(runner).PrepareAsync(id, operation, installedFirmware: RuntimeVersion(id, sources[0].Release)), "Runtime update version may not authorize installation or restore");
        Check(runner.Calls.Count == 0, "Runtime reports for the wrong operation never start a process");
    }
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update); var service = fixture.Service(runner);
        var first = await service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: RuntimeVersion(id, sources[0].Release));
        var second = await service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: RuntimeVersion(id, sources[1].Release));
        Check(second.ExpectedSourceRelease == sources[1].Release && second.Id != first.Id && first.ExpectedSourceRelease == sources[0].Release, "A new plan has its own immutable runtime-version binding");
        Check(!(await service.ExecuteAsync(first, true)).ExecutionStarted && runner.Calls.Count == 2, "An old runtime-bound plan cannot execute after a new preparation");
        runner.SourceIndex = 1;
        Check((await service.ExecuteAsync(second, true)).Verified && runner.Calls.Count == 4, "The current runtime-bound plan remains usable after a rejected old plan");
    }
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update); var service = fixture.Service(runner);
        var old = await service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: RuntimeVersion(id, sources[0].Release));
        await Reject(() => service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: RuntimeVersion(id, FirmwareRelease.CurrentVersion.ToString())), "Current firmware must invalidate a previously prepared old-version plan");
        Check(!(await service.ExecuteAsync(old, true)).ExecutionStarted && runner.Calls.Count == 1, "Failed replacement preparation cannot restore a stale expected-source token");
    }
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update) { Failure = "prewrite-source-changed" };
        var service = fixture.Service(runner); var plan = await service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: RuntimeVersion(id, sources[0].Release));
        var result = await service.ExecuteAsync(plan, true);
        Check(!result.Verified && result.WriteNotAttemptedVerified && result.ExecutionStarted && result.ReconnectTarget is null && result.Message == FirmwareMessages.ForSourceChangedWithoutWrite(), "A different known source in fresh full reads is a proven no-write mismatch, never an unsupported-source claim");
        Check(FirmwareMessages.ForUser(result.Message) == result.Message && runner.Calls.Count == 3, "Source-change failure is stable and never retries");
    }
    {
        var runner = new FakeRunner(fixture, id, FirmwareOperation.Update) { SourceIndex = 1 };
        var service = fixture.Service(runner); var plan = await service.PrepareAsync(id, FirmwareOperation.Update, installedFirmware: RuntimeVersion(id, sources[0].Release));
        var result = await service.ExecuteAsync(plan, true);
        Check(!result.Verified && result.ExecutionStarted && !result.WriteNotAttemptedVerified && result.ReconnectTarget is null && result.Message == FirmwareMessages.ForExecutionFailure(), "A purported successful write from a different source remains unverified even if readback matches its new image");
    }
    foreach (var mutation in new[] { "missing-expected", "wrong-expected", "read-failed", "after-artifact" })
    {
        var expected = sources[0].Release; var run = fixture.MakePrewriteFailure(id, expected, 1); var pro = id == "pro-iso";
        var proofPath = Path.Combine(run, pro ? "plan.json" : "result.json");
        var proof = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(proofPath))!;
        if (mutation == "missing-expected") proof.AsObject().Remove("expectedSourceRelease");
        if (mutation == "wrong-expected") proof["expectedSourceRelease"] = sources[2].Release;
        File.WriteAllText(proofPath, proof.ToJsonString());
        if (mutation == "read-failed")
        {
            var path = Path.Combine(run, pro ? "FAILED.json" : "result.json"); var report = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            report["commands"]![2]!["exitCode"] = 1; File.WriteAllText(path, report.ToJsonString());
        }
        if (mutation == "after-artifact") File.WriteAllText(Path.Combine(run, "after-extra.bin"), "unverified");
        Check(fixture.Service(new FakeRunner(fixture, id, FirmwareOperation.Update)).ProveNoWriteFailure(run, fixture.Binding(id), FirmwareOperation.Update, expected) == FirmwareNoWriteReason.None, "Incomplete runtime-source mismatch proof remains uncertain: " + mutation);
    }
}

if (args.Contains("--local-files"))
{
    foreach (var id in new[] { "pro-iso", "numpad" }) foreach (var operation in new[] { FirmwareOperation.Update, FirmwareOperation.RestoreOriginal, FirmwareOperation.InstallFromOriginal })
    {
        var availability = new FirmwareService().CheckAvailability(id, operation);
        Check(availability.Available, "Actual local file pins rejected: " + availability.Message);
    }
    Console.WriteLine("PASS local artifact pins for all three operations on both devices; file reads only.");
}
Console.WriteLine($"PASS {checks} firmware-service checks. Synthetic files and fake process runner only; no PowerShell, PnP or firmware tool executed.");

sealed class FakeRunner(FirmwareFixture fixture, string id, FirmwareOperation operation) : IFirmwareProcessRunner
{
    public List<FirmwareCommand> Calls { get; } = [];
    public List<bool> LogSinks { get; } = [];
    public string Failure { get; set; } = "";
    public int SourceIndex { get; set; }
    public string? PlanOverride { get; set; }
    // Actual Windows path forms from both failed 0.6.13 preflights. Hardware IDs remain synthetic.
    public string[] LocationPaths { get; set; } = id == "pro-iso"
        ? ["PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(9)#USB(1)", "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS09)#ACPI(HSS1)"]
        : ["PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(9)#USB(4)", "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS09)#ACPI(HSS4)"];
    public string? NormalResponseJson { get; set; }
    public string? IdentityError { get; set; }
    public bool IdentityTimedOut { get; set; }
    public TaskCompletionSource? NormalStarted { get; set; }
    public TaskCompletionSource? NormalGate { get; set; }
    public Task<FirmwareProcessResult> RunAsync(FirmwareCommand command, IProgress<string>? log)
    {
        Calls.Add(command); LogSinks.Add(log is not null); log?.Report("Synthetic " + command.Kind);
        var device = fixture.Binding(id);
        if (command.Kind == FirmwareCommandKind.Plan) return Task.FromResult(new FirmwareProcessResult(Failure == "plan-exit" ? 1 : 0, Failure == "bad-plan" ? "{}" : PlanOverride ?? fixture.PlanJson(id, operation), ""));
        if (command.Kind == FirmwareCommandKind.IdentityCheck)
        {
            var output = Failure switch
            {
                "identity-json" => "not JSON",
                "identity-fields" => "{}",
                "identity-type" => "{\"present\":\"true\"}",
                _ => JsonSerializer.Serialize(new { present = Failure != "missing-identity", instanceId = Failure == "wrong-identity" ? "wrong" : Failure == "case-identity" ? device.InstanceId.ToLowerInvariant() : device.InstanceId, service = Failure == "wrong-driver" ? "HidUsb" : "WinUsb", locationPaths = LocationPaths })
            };
            if (Failure.StartsWith("locations-"))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(output)!;
                object? paths = Failure switch
                {
                    "locations-null" => null, "locations-string" => LocationPaths[0], "locations-empty" => Array.Empty<string>(),
                    "locations-null-item" => new object?[] { null }, "locations-number" => new object[] { 1 }, "locations-blank" => new[] { "" },
                    "locations-duplicate" => new[] { LocationPaths[0], LocationPaths[0].ToLowerInvariant() }, "locations-malformed" => new[] { "Port_#0001.Hub_#0002" },
                    "locations-child" => new[] { LocationPaths[0] + "#USBMI(0)" }, "locations-too-many" => Enumerable.Range(1, 17).Select(i => $"PCIROOT(0)#USB({i})").ToArray(),
                    "locations-too-long" => new[] { "PCIROOT(" + new string('A', 4096) + ")#USB(1)" },
                    "locations-acpi-child" => new[] { LocationPaths[1] + "#USBMI(0)" },
                    "locations-acpi-root-only" => new[] { "ACPI(_SB_)" },
                    "locations-acpi-wrong-root" => new[] { "ACPI(WRONG)#ACPI(PC00)#ACPI(HSS4)" },
                    "locations-acpi-mixed" => new[] { "ACPI(_SB_)#PCI(1400)#ACPI(HSS4)" },
                    "locations-acpi-empty" => new[] { "ACPI(_SB_)#ACPI()#ACPI(HSS4)" },
                    "locations-acpi-slash" => new[] { "ACPI(_SB_)#ACPI(PC00)#ACPI(HSS4/OTHER)" },
                    "locations-acpi-long-name" => new[] { "ACPI(_SB_)#ACPI(PC00)#ACPI(" + new string('A', 65) + ")" },
                    "locations-acpi-too-deep" => new[] { "ACPI(_SB_)" + string.Concat(Enumerable.Repeat("#ACPI(NEXT)", 16)) },
                    "locations-acpi-control" => new[] { LocationPaths[1] + "\n" },
                    "locations-acpi-duplicate" => new[] { LocationPaths[1], LocationPaths[1].ToLowerInvariant() },
                    _ => LocationPaths
                };
                node["locationPaths"] = JsonSerializer.SerializeToNode(paths);
                if (Failure == "locations-missing") node.AsObject().Remove("locationPaths");
                output = node.ToJsonString();
            }
            return Task.FromResult(new FirmwareProcessResult(Failure is "identity-exit" or "ambiguous-identity" || IdentityError is not null || IdentityTimedOut ? 1 : 0, output, IdentityError ?? (Failure == "ambiguous-identity" ? "Synthetic: multiple matching bootloaders" : ""), IdentityTimedOut));
        }
        if (command.Kind == FirmwareCommandKind.NormalConnectionCheck) return NormalResultAsync();
        var expectedIndex = command.Arguments.ToList().IndexOf("-ExpectedSourceRelease");
        var expectedSource = expectedIndex >= 0 ? command.Arguments[expectedIndex + 1] : null;
        if (Failure is "prewrite-unsupported" or "prewrite-source-changed")
        {
            fixture.MakePrewriteFailure(id, expectedSource, Failure == "prewrite-source-changed" ? 1 : null);
            return Task.FromResult(new FirmwareProcessResult(1, "", "Unknown source"));
        }
        if (Failure != "no-evidence")
        {
            var directory = fixture.MakeEvidence(id, operation, SourceIndex, expectedSource);
            if (Failure == "after-execute-source-mismatch")
                foreach (var number in new[] { 1, 2 })
                {
                    var path = Path.Combine(directory, id == "pro-iso" ? $"before-read-{number}.bin" : $"before-{number}.bin");
                    var bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes);
                }
        }
        if (Failure == "execute-throw") throw new IOException("Synthetic runner failure after producing evidence");
        if (Failure == "execute-timeout") return Task.FromResult(new FirmwareProcessResult(1, "", "Operation timed out. Device state may be partial; keep ROM mode."));
        return Task.FromResult(new FirmwareProcessResult(Failure == "execute-exit" ? 1 : 0, "Synthetic process completion", ""));
    }
    private async Task<FirmwareProcessResult> NormalResultAsync()
    {
        NormalStarted?.TrySetResult();
        if (NormalGate is not null) await NormalGate.Task;
        if (Failure == "normal-timeout") throw new IOException("Synthetic PnP timeout");
        var vid = operation == FirmwareOperation.RestoreOriginal ? "320F" : "FEED";
        var pid = (id, operation == FirmwareOperation.RestoreOriginal) switch { ("pro-iso", true) => "5046", ("numpad", true) => "5088", ("pro-iso", false) => "1C01", _ => "1C02" };
        return new(Failure == "normal-exit" ? 1 : 0, NormalResponseJson ?? JsonSerializer.Serialize(new[] { new { instanceId = $@"USB\VID_{vid}&PID_{pid}\NEWNORMAL", status = "OK", problemCode = 0, locationPaths = LocationPaths } }), "");
    }
}

sealed class FirmwareFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "inglorious-firmware-checks-" + Guid.NewGuid().ToString("N"));
    private readonly FirmwareBindings _bindings = new() { SchemaVersion = 1 };
    private readonly string _bindingHash;
    public FirmwareFixture()
    {
        Directory.CreateDirectory(Root);
        _bindings.Common.Add(Write("scripts/FirmwareUpdate.Helpers.ps1", "# synthetic helper"u8.ToArray()));
        foreach (var id in new[] { "pro-iso", "numpad" })
        {
            var prefix = id == "pro-iso" ? "USB\\VID_1234&PID_5678\\" : "USB\\VID_1234&PID_5679\\";
            var stockPath = $"backups/{id}/stock.bin";
            var stock = new byte[FirmwareService.FlashBytes(id)]; for (var i = 0; i < stock.Length; i++) stock[i] = (byte)(i % 251);
            var oldRelease = id == "pro-iso" ? "0.2-fn-macros" : "0.3-wb32-bulk";
            var board = id == "pro-iso" ? "pro" : "numpad";
            var binding = new FirmwareDeviceBinding { DeviceId = id, InstanceId = prefix + "TESTONLY", BootloaderPrefix = prefix, Port = "1-1", StockPath = stockPath, StockHash = Hash(stock), NewCodePath = $"firmware/{id}-new.bin" };
            binding.BaseFiles.Add(Write(id == "pro-iso" ? "scripts/Invoke-ProFirmware.ps1" : "scripts/Invoke-NumpadFirmware.ps1", "# synthetic wrapper, never executed"u8.ToArray()));
            foreach (var tool in id == "pro-iso" ? new[] { "tools/InGlorious.Backup/vendor/dfu-util-0.11-binaries/win64/dfu-util-static.exe" } : new[] { "tools/InGlorious.Backup/bin/Release/net9.0/InGlorious.Backup.exe", "tools/InGlorious.Flash/wb32-no-unprotect.exe" })
                binding.BaseFiles.Add(Write(tool, "synthetic tool, never executed"u8.ToArray()));
            binding.BaseFiles.Add(Write(stockPath, stock));
            binding.UpdateFiles.Add(Write($"firmware/releases/{oldRelease}/{board}.bin", Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
            binding.UpdateFiles.Add(Write(binding.NewCodePath, Enumerable.Range(0, 128).Select(i => (byte)(255 - i)).ToArray()));
            binding.InstallFiles.Add(binding.UpdateFiles[1]);
            var oldPin = binding.UpdateFiles[0]; binding.UpdateSources.Add(new(oldRelease, oldPin.Path, oldPin.Bytes, oldPin.Sha256));
            foreach (var (release, index) in new[] { ("0.4-effects", 1), ("0.5-color-sequences", 2), ("0.6-standby", 3), ("0.7-lighting-test", 4), ("0.8-version-reporting", 5), ("0.9-lighting-test", 6), ("0.9.1-lighting-fix", 7) })
            {
                var pin = Write($"firmware/releases/{release}/{board}.bin", Enumerable.Range(0, 64 + index * 16).Select(i => (byte)(i + index)).ToArray());
                binding.UpdateFiles.Add(pin); binding.UpdateSources.Add(new(release, pin.Path, pin.Bytes, pin.Sha256));
            }
            var install = new byte[stock.Length]; Array.Fill(install, (byte)255);
            File.ReadAllBytes(Path.Combine(Root, binding.NewCodePath)).CopyTo(install, 0);
            binding.InstallPath = $"firmware/{id}-install.bin"; binding.InstallHash = Hash(install);
            binding.InstallFiles.Add(Write(binding.InstallPath, install));
            _bindings.Devices.Add(binding);
        }
        _bindingHash = Write("firmware/local-firmware-bindings.json", JsonSerializer.SerializeToUtf8Bytes(_bindings)).Sha256;
    }
    public FirmwareService Service(IFirmwareProcessRunner runner, string? evidenceRoot = null) => new(Root, runner, _bindingHash, Environment.ProcessPath!, evidenceRoot);
    public FirmwareDeviceBinding Binding(string id) => _bindings.Devices.Single(device => device.DeviceId == id);
    public string PlanJson(string id, FirmwareOperation operation)
    {
        var binding = Binding(id); var pro = id == "pro-iso";
        var plan = new Dictionary<string, object?> { ["mode"] = FirmwareService.ScriptMode(operation), [pro ? "executionRequested" : "execute"] = false, [pro ? "device" : "targetInstance"] = binding.InstanceId, [pro ? "bytes" : "internalFlashBytes"] = FirmwareService.FlashBytes(id), ["port"] = binding.Port, ["alternate"] = 0, ["interface"] = 0 };
        if (operation == FirmwareOperation.Update) foreach (var item in UpdateMetadata(binding, null)) plan[item.Key] = item.Value;
        else if (operation == FirmwareOperation.InstallFromOriginal) { plan["installRelease"] = FirmwareRelease.ReleaseId; plan["installCodeSha256"] = binding.InstallFiles[0].Sha256; plan["installCodeBytes"] = binding.InstallFiles[0].Bytes; plan["settingsPreserved"] = false; plan[pro ? "payload" : "image"] = Path.Combine(Root, binding.InstallPath); plan[pro ? "payloadSha256" : "sha256"] = binding.InstallHash; }
        else { plan[pro ? "payload" : "image"] = Path.Combine(Root, binding.StockPath); plan[pro ? "payloadSha256" : "sha256"] = binding.StockHash; }
        return JsonSerializer.Serialize(plan);
    }
    public Dictionary<string, object?> UpdateMetadata(FirmwareDeviceBinding binding, int? sourceIndex)
    {
        var source = sourceIndex is { } index ? binding.UpdateSources[index] : null;
        var next = binding.UpdateFiles.Single(file => file.Path == binding.NewCodePath);
        return new() { ["updateRelease"] = FirmwareRelease.ReleaseId, ["updateFrom"] = "auto", ["updateSources"] = binding.UpdateSources.Select(source => new { release = source.Release, path = source.Path, bytes = source.Bytes, sha256 = source.Sha256 }).ToArray(), ["updateFromRelease"] = source?.Release, ["updateFromCodeSha256"] = source?.Sha256, ["updateFromCodeBytes"] = source?.Bytes, ["updateToCodeSha256"] = next.Sha256, [binding.DeviceId == "pro-iso" ? "updateToCodeBytes" : "codeBytes"] = next.Bytes };
    }
    public string MakeEvidence(string id, FirmwareOperation operation, int sourceIndex = 0, string? expectedSource = null)
    {
        var device = Binding(id); var pro = id == "pro-iso"; var size = FirmwareService.FlashBytes(id);
        var runRoot = pro ? $"artifacts/install/{FirmwareRelease.ReleaseId}/pro-runs" : "artifacts/flash";
        var run = Path.Combine(Root, runRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        var before = new byte[size]; Array.Fill(before, (byte)255);
        File.ReadAllBytes(Path.Combine(Root, device.UpdateSources[sourceIndex].Path)).CopyTo(before, 0);
        for (var i = size - 32768; i < size; i++) before[i] = (byte)(i % 251);
        byte[] expected;
        if (operation == FirmwareOperation.Update)
        {
            expected = new byte[size]; Array.Fill(expected, (byte)255); File.ReadAllBytes(Path.Combine(Root, device.NewCodePath)).CopyTo(expected, 0); before.AsSpan(size - 32768).CopyTo(expected.AsSpan(size - 32768));
            File.WriteAllBytes(Path.Combine(run, "update-image.bin"), expected);
        }
        else if (operation == FirmwareOperation.InstallFromOriginal) { before = File.ReadAllBytes(Path.Combine(Root, device.StockPath)); expected = File.ReadAllBytes(Path.Combine(Root, device.InstallPath)); }
        else expected = File.ReadAllBytes(Path.Combine(Root, device.StockPath));
        foreach (var number in new[] { 1, 2 })
        {
            File.WriteAllBytes(Path.Combine(run, pro ? $"before-read-{number}.bin" : $"before-{number}.bin"), before);
            File.WriteAllBytes(Path.Combine(run, pro ? $"after-read-{number}.bin" : $"after-{number}.bin"), expected);
        }
        var report = new Dictionary<string, object?> { ["mode"] = FirmwareService.ScriptMode(operation), [pro ? "device" : "targetInstance"] = device.InstanceId, ["status"] = "two-readback-verified", ["writeAttempted"] = true, ["optionBytesWritten"] = false, ["protectionChanged"] = false, ["automaticResetOrLeave"] = false, ["readbackVerified"] = true, ["firmwareChanged"] = true, ["externalStorageWritten"] = false, ["resetSent"] = false, [pro ? "payloadSha256" : "afterSha256"] = Hash(expected) };
        if (operation == FirmwareOperation.Update)
        {
            var metadata = UpdateMetadata(device, sourceIndex);
            if (expectedSource is not null) metadata["expectedSourceRelease"] = expectedSource;
            if (pro) report["update"] = metadata; else foreach (var item in metadata) report[item.Key] = item.Value;
        }
        if (operation == FirmwareOperation.InstallFromOriginal) { report["installRelease"] = FirmwareRelease.ReleaseId; report["settingsPreserved"] = false; }
        File.WriteAllText(Path.Combine(run, pro ? "manifest.json" : "result.json"), JsonSerializer.Serialize(report));
        return run;
    }
    public string MakePrewriteFailure(string id, string? expectedSource = null, int? selectedSourceIndex = null)
    {
        var device = Binding(id); var pro = id == "pro-iso";
        var run = MakeEvidence(id, FirmwareOperation.Update);
        foreach (var path in Directory.GetFiles(run).Where(path => Path.GetFileName(path).StartsWith("after-") || Path.GetFileName(path) is "manifest.json" or "result.json" or "update-image.bin")) File.Delete(path);
        foreach (var number in new[] { 1, 2 })
        {
            var path = Path.Combine(run, pro ? $"before-read-{number}.bin" : $"before-{number}.bin");
            var bytes = File.ReadAllBytes(path);
            if (selectedSourceIndex is { } selectedIndex)
            {
                Array.Fill(bytes, (byte)255, 0, bytes.Length - 32768);
                File.ReadAllBytes(Path.Combine(Root, device.UpdateSources[selectedIndex].Path)).CopyTo(bytes, 0);
            }
            else bytes[0] ^= 128;
            File.WriteAllBytes(path, bytes);
        }
        var plan = System.Text.Json.Nodes.JsonNode.Parse(PlanJson(id, FirmwareOperation.Update))!.AsObject(); plan[pro ? "executionRequested" : "execute"] = true;
        if (expectedSource is not null) plan["expectedSourceRelease"] = expectedSource;
        if (!pro && selectedSourceIndex is { } selected)
        {
            var source = device.UpdateSources[selected]; plan["updateFromRelease"] = source.Release; plan["updateFromCodeBytes"] = source.Bytes; plan["updateFromCodeSha256"] = source.Sha256;
        }
        var commands = new List<object>();
        for (var index = 0; index < 3; index++)
        {
            var tool = pro ? "tools/InGlorious.Backup/vendor/dfu-util-0.11-binaries/win64/dfu-util-static.exe" : index == 0 ? "tools/InGlorious.Backup/bin/Release/net9.0/InGlorious.Backup.exe" : "tools/InGlorious.Flash/wb32-no-unprotect.exe";
            var capture = Path.Combine(run, pro ? $"before-read-{index}.bin" : $"before-{index}.bin");
            string[] prefix = ["-d", "1234:5678", "-S", "TESTONLY", "-p", "1-1", "-c", "1", "-i", "0", "-a", "0"];
            string[] arguments = pro ? index == 0 ? [.. prefix, "-l"] : [.. prefix, "-s", "0x08000000:262144", "-U", capture] : index == 0 ? ["info", "--read-only"] : ["-U", capture, "-Z", "131072"];
            commands.Add(new { name = index == 0 ? "before-descriptors" : $"before-read-{index}", executable = Path.Combine(Root, tool), arguments, write = false, exitCode = 0, timedOut = false, error = (string?)null });
        }
        if (pro)
        {
            File.WriteAllText(Path.Combine(run, "plan.json"), plan.ToJsonString());
            File.WriteAllText(Path.Combine(run, "FAILED.json"), JsonSerializer.Serialize(new { mode = "Update", status = "failed-review-required", writeAttempted = false, error = "Unknown source", commands }));
        }
        else
        {
            plan["firmwareChanged"] = false; plan["readbackVerified"] = false; plan["externalStorageWritten"] = false; plan["protectionChanged"] = false; plan["resetSent"] = false;
            plan["error"] = "Unknown source"; plan["commands"] = JsonSerializer.SerializeToNode(commands);
            File.WriteAllText(Path.Combine(run, "result.json"), plan.ToJsonString());
        }
        return run;
    }
    private FirmwareFilePin Write(string relative, byte[] bytes) { var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return new(relative, bytes.Length, Hash(bytes)); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Dispose()
    {
        var full = Path.GetFullPath(Root); var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("inglorious-firmware-checks-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe temporary cleanup path.");
        Directory.Delete(full, recursive: true);
    }
}

sealed class ImmediateProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
