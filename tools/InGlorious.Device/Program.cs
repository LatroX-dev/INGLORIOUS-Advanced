using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InGlorious.Core;
using InGlorious.DeviceDiagnostics;

string? auditDirectory = null;
OnboardInfo? capturedInfo = null;
string? capturedSha256 = null;
string? targetSha256 = null;
string? requestedProjectPath = null;
string? devicePath = null;
bool writeAttempted = false;
string phase = "capture";
TimedTransport? timedTransport = null;
DeviceArguments? command = null;
OnboardFirmwareVersion? runtimeVersion = null;

try
{
    try { command = DeviceArguments.Parse(args); }
    catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
    if (command.Operation == "list")
    {
        Console.WriteLine(JsonSerializer.Serialize(OnboardClient.FindDevices(), ProjectService.JsonOptions));
        return 0;
    }
    bool isApply = command.Operation == "apply";
    var id = command.DeviceId!; _ = DeviceCatalog.Get(id);
    AppProject? requestedProject = null;
    OnboardImage? requestedImage = null;
    if (isApply)
    {
        phase = "validate-project";
        requestedProjectPath = Path.GetFullPath(command.ProjectPath!);
        requestedProject = ProjectService.Load(requestedProjectPath);
        if (requestedProject.DeviceId != id) throw new ArgumentException("The project belongs to a different device.");
        requestedImage = OnboardSerializer.Prepare(requestedProject, command.Engineering);
        if (requestedImage.DeviceId != id) throw new ArgumentException("The prepared configuration belongs to a different device.");
        targetSha256 = Convert.ToHexString(SHA256.HashData(requestedImage.Payload));
    }
    var directory = Path.GetFullPath(command.OutputDirectory!);
    if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Output already exists.");
    phase = "capture";
    var matches = OnboardHidTransport.FindDevices(command.Engineering).Where(d => d.DeviceId == id).ToList();
    if (matches.Count != 1) throw new IOException($"Expected one {id} with INGLORIOUS firmware, found {matches.Count}.");
    devicePath = matches[0].Path;
    timedTransport = new TimedTransport(OnboardHidTransport.Open(matches[0], command.Engineering));
    await using var client = new OnboardClient(timedTransport, matches[0].DeviceId, command.Engineering);
    if (command.Engineering)
    {
        phase = "verify-engineering-runtime";
        runtimeVersion = await client.GetFirmwareVersionAsync();
        command.ValidateRuntime(runtimeVersion);
    }
    var before = await client.GetInfoAsync();
    var blob = await client.ReadAsync();
    var after = await client.GetInfoAsync();
    if (before.Generation != after.Generation || before.ActiveCrc != after.ActiveCrc || OnboardProtocol.Crc32(blob) != after.ActiveCrc) throw new IOException("Configuration changed during capture.");
    capturedInfo = after;
    capturedSha256 = Convert.ToHexString(SHA256.HashData(blob));
    var project = OnboardSerializer.Decode(blob, command.Engineering);
    Directory.CreateDirectory(directory);
    auditDirectory = directory;
    var raw = Path.Combine(directory, "configuration.bin");
    WriteNewVerifiedBytes(raw, blob);
    ProjectService.Save(Path.Combine(directory, "project.json"), project);
    WriteNewJson(Path.Combine(directory, "capture-report.json"), new
    {
        operation = command.Operation, engineering = command.Engineering, runtimeVersion, device = matches[0], info = capturedInfo, bytes = blob.Length,
        sha256 = capturedSha256, rawBackup = "configuration.bin", rawBackupReadbackVerified = true,
        writeAttempted = false, scope = "stable active configuration captured before any persistence attempt", utc = DateTimeOffset.UtcNow
    });
    var targetPayload = requestedImage?.Payload ?? blob;
    targetSha256 ??= capturedSha256;
    if (isApply)
    {
        // Freeze the reviewed target beside the lossless old-state backup before any write.
        WriteNewVerifiedBytes(Path.Combine(directory, "target-configuration.bin"), targetPayload);
        ProjectService.Save(Path.Combine(directory, "target-project.json"), requestedProject!);
    }
    OnboardTransferResult? saved = null;
    if (command.Operation == "verify-save" || isApply)
    {
        var device = DeviceCatalog.Get(id);
        var image = requestedImage ?? new OnboardImage(id, blob, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(14)), device.Keys.Count, device.LedCount, []);
        phase = "persistence";
        WriteNewJson(Path.Combine(directory, "write-intent.json"), new
        {
            operation = command.Operation, engineering = command.Engineering, runtimeVersion, devicePath, expectedGeneration = after.Generation,
            expectedCrc32 = after.ActiveCrc, beforeSha256 = capturedSha256,
            targetSha256, targetCrc32 = OnboardProtocol.Crc32(targetPayload), targetBytes = targetPayload.Length,
            requestedProjectPath, targetBackup = isApply ? "target-configuration.bin" : "configuration.bin",
            scope = "intent to persist the exact prepared target bytes; not proof of commit", utc = DateTimeOffset.UtcNow
        });
        // Any error after entering Apply is treated conservatively as an uncertain outcome.
        writeAttempted = true;
        saved = await client.ApplyAsync(image, expectedGeneration: after.Generation);
        if (!saved.ReadbackVerified) throw new IOException("The persistence operation did not confirm its readback.");
        phase = "post-write-readback";
        var persisted = await client.ReadAsync();
        if (!persisted.AsSpan().SequenceEqual(targetPayload)) throw new IOException("Persistence verification differs from the intended configuration.");
        WriteNewVerifiedBytes(Path.Combine(directory, "readback.bin"), persisted);
        after = await client.GetInfoAsync();
        if (after.Generation != saved.Generation || after.ActiveCrc != saved.Crc32 ||
            after.ActiveCrc != OnboardProtocol.Crc32(targetPayload) || !after.Persistent)
            throw new IOException("Configuration generation, checksum or storage status changed after persistence readback. Re-read before any retry.");
    }
    phase = "success-report";
    var report = new
    {
        operation = command.Operation, engineering = command.Engineering, runtimeVersion, device = matches[0], before = capturedInfo, beforeSha256 = capturedSha256,
        info = after, saved, writeAttempted, bytes = targetPayload.Length, sha256 = targetSha256,
        target = new { deviceId = id, requestedProjectPath, bytes = targetPayload.Length, sha256 = targetSha256, crc32 = OnboardProtocol.Crc32(targetPayload), warnings = requestedImage?.Warnings },
        commandTimingAggregate = timedTransport.CommandTimingAggregate, commitDurationMs = timedTransport.CommitDurationMs,
        scope = "active configuration readback; not a firmware dump or power-cycle test", utc = DateTimeOffset.UtcNow
    };
    var text = WriteNewJson(Path.Combine(directory, "read-report.json"), report);
    Console.WriteLine(text);
    return 0;
}
catch (Exception error)
{
    if (auditDirectory is not null)
    {
        try
        {
            WriteNewJson(Path.Combine(auditDirectory, "FAILED.json"), new
            {
                status = "failed-review-required", operation = args.FirstOrDefault(), phase,
                error = error.Message, devicePath, before = capturedInfo, beforeSha256 = capturedSha256,
                targetSha256, requestedProjectPath,
                commandTimingAggregate = timedTransport?.CommandTimingAggregate, commitDurationMs = timedTransport?.CommitDurationMs,
                writeAttempted, outcome = writeAttempted
                    ? "Persistence outcome is uncertain. Read the active configuration before considering another write; no automatic retry was attempted."
                    : "No persistence attempt was started.", utc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception logError) { Console.Error.WriteLine("Could not save failure metadata: " + logError.Message); }
    }
    Console.Error.WriteLine(error.Message);
    if (writeAttempted) Console.Error.WriteLine("Persistence outcome is uncertain. Re-read the device before considering any retry.");
    return 1;
}
finally
{
    if (timedTransport is not null)
    {
        var aggregate = timedTransport.CommandTimingAggregate;
        var commitDurationMs = timedTransport.CommitDurationMs;
        // stderr preserves the existing single-JSON stdout contract, also on failed operations.
        Console.Error.WriteLine("Transport timing (diagnostic): " + string.Join("; ", aggregate.Select(a =>
            FormattableString.Invariant($"{a.Command} count={a.Count} total={a.TotalMs:F3}ms max={a.MaximumMs:F3}ms failed={a.Failed}"))));
        if (commitDurationMs.HasValue) Console.Error.WriteLine(FormattableString.Invariant($"COMMIT duration: {commitDurationMs.Value:F3} ms"));
        if (auditDirectory is not null)
        {
            try
            {
                WriteNewJson(Path.Combine(auditDirectory, "transport-timings.json"), new
                {
                    operation = args.FirstOrDefault(), phase, commandTimingAggregate = aggregate, commitDurationMs,
                    records = timedTransport.Records,
                    scope = "Host exchange durations; success means transport completed, not semantic acceptance or proven persistence. No report payloads or keystrokes recorded.",
                    utc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception logError) { Console.Error.WriteLine("Could not save transport timing metadata: " + logError.Message); }
        }
    }
}

static void WriteNewVerifiedBytes(string path, byte[] data)
{
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
    {
        stream.Write(data);
        stream.Flush(true);
    }
    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) throw new IOException("Saved bytes differ: " + path);
}

static string WriteNewJson(string path, object value)
{
    string text = JsonSerializer.Serialize(value, ProjectService.JsonOptions);
    byte[] data = new UTF8Encoding(false).GetBytes(text);
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    stream.Write(data);
    stream.Flush(true);
    return text;
}
