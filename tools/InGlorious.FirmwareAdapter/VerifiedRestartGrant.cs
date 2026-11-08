namespace InGlorious.FirmwareAdapter;

// This is an in-process, one-use continuation of a verified update. It is never
// deserialized and cannot be supplied as a separate adapter operation.
internal sealed class VerifiedRestartGrant
{
    private readonly IDevice device;
    private readonly Model model;
    private readonly Fingerprint fingerprint;
    private readonly string imageHash;
    private bool consumed;

    private VerifiedRestartGrant(IDevice device, Model model, Fingerprint fingerprint, string imageHash)
    { this.device = device; this.model = model; this.fingerprint = fingerprint; this.imageHash = imageHash; }

    internal static void ValidateModel(Model model)
    {
        // Explicit source-backed model entries only; a matching USB family is not
        // authorization for arbitrary WB32/STM32 devices or engineering models.
        if (model.EngineeringOnly || model != Model.Get(model.DeviceId) ||
            model.Transport is not ("wb32-dfu" or "stm32-dfuse"))
            throw new InvalidDataException("RESTART_MODEL_NOT_SUPPORTED");
    }

    internal static VerifiedRestartGrant Create(AdapterRequest request, Model model, IDevice device,
        Fingerprint fingerprint, bool writeAttempted, byte[] image, byte[] firstReadback, byte[] secondReadback)
    {
        ValidateModel(model);
        if (request.Operation != "update" || request.DeviceId != model.DeviceId || !request.ConfirmWrite ||
            !request.RestartAfterVerify || !writeAttempted || request.MigrateLegacy ||
            request.ExpectedFingerprintSha256 != Files.Hash(Json.Bytes(fingerprint)))
            throw new InvalidDataException("VERIFIED_RESTART_REQUIRED");
        if (image.Length != model.FlashBytes || !firstReadback.AsSpan().SequenceEqual(image) ||
            !secondReadback.AsSpan().SequenceEqual(image))
            throw new InvalidDataException("VERIFIED_RESTART_READBACK_MISMATCH");
        if (device.Probe(model) != fingerprint) throw new InvalidDataException("VERIFIED_RESTART_IDENTITY_CHANGED");
        return new(device, model, fingerprint, Files.Hash(image));
    }

    internal void Consume(IDevice selectedDevice, Model selectedModel, Fingerprint current, string? completedWriteHash)
    {
        if (consumed) throw new InvalidDataException("VERIFIED_RESTART_ALREADY_USED");
        consumed = true; // A refusal must not leave an authorization available for retry.
        if (!ReferenceEquals(device, selectedDevice) || selectedModel != model || current != fingerprint || completedWriteHash != imageHash)
            throw new InvalidDataException("VERIFIED_RESTART_SESSION_CHANGED");
    }
}
