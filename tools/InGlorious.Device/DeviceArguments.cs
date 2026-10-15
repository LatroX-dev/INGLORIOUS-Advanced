using InGlorious.Core;

namespace InGlorious.DeviceDiagnostics;

internal sealed record DeviceArguments(string Operation, string? DeviceId = null, string? OutputDirectory = null,
    string? ProjectPath = null, bool Engineering = false)
{
    public const string Usage = "InGlorious.Device list | read DEVICE --out NEW_DIRECTORY | verify-save DEVICE --out NEW_DIRECTORY | apply DEVICE --project ABSOLUTE_JSON_PATH --out NEW_DIRECTORY\nLocal engineering: read --engineering --device gmmk3-pro-100-iso --out NEW_DIRECTORY | verify-save --engineering --device gmmk3-pro-100-iso --out NEW_DIRECTORY --confirm-write | apply --engineering --device gmmk3-pro-100-iso --project ABSOLUTE_JSON_PATH --out NEW_DIRECTORY --confirm-write\nread captures active configuration. verify-save persists the exact same configuration. apply backs up the current configuration, then writes a validated project. All writes require complete byte readback. Own runtime firmware only; no flash commands.";

    public static DeviceArguments Parse(string[] args)
    {
        DeviceArguments result = args switch
        {
            [] or ["list"] => new("list"),
            ["read", "--engineering", "--device", var id, "--out", var output] => new("read", id, output, Engineering: true),
            ["verify-save", "--engineering", "--device", var id, "--out", var output, "--confirm-write"] => new("verify-save", id, output, Engineering: true),
            ["apply", "--engineering", "--device", var id, "--project", var project, "--out", var output, "--confirm-write"] => new("apply", id, output, project, true),
            ["read" or "verify-save", var id, "--out", var output] => new(args[0], id, output),
            ["apply", var id, "--project", var project, "--out", var output] => new("apply", id, output, project),
            _ => throw new ArgumentException("Invalid command or missing explicit engineering write confirmation.\n" + Usage)
        };
        if (result.Operation == "list") return result;
        if (result.DeviceId is null || !BoardDescriptorRegistry.TryGet(result.DeviceId, out var board) ||
            (result.Engineering && !EngineeringConfigurationPolicy.IsEnabled(result.DeviceId, true)) ||
            (board.IsEngineeringOnly && !result.Engineering) || string.IsNullOrWhiteSpace(result.OutputDirectory) ||
            (result.ProjectPath is not null && !Path.IsPathFullyQualified(result.ProjectPath)))
            throw new ArgumentException("The exact model, local engineering opt-in, absolute project path or output directory is invalid.");
        return result;
    }

    public void ValidateRuntime(OnboardFirmwareVersion? report)
    {
        if (Engineering && (report is null || report.DeviceId != DeviceId ||
            !EngineeringConfigurationPolicy.MatchesRuntime(report.Version.ToString(), report.BuildId)))
            throw new InvalidDataException("The runtime version/build is not the reviewed local engineering candidate. No configuration was read or staged.");
    }
}
