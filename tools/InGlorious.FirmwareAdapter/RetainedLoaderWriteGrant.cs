using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

/// <summary>One operation on a known loader and reviewed application, bound to the
/// current device and its own original backup. The CLI cannot construct this grant.</summary>
internal sealed class RetainedLoaderWriteGrant : IRetainedLoaderWriteGrant
{
    private readonly Model model;
    private readonly Fingerprint fingerprint;
    private readonly string imageSha256;
    private bool consumed;
    private RetainedLoaderWriteGrant(Model model, Fingerprint fingerprint, byte[] image)
    { this.model = model; this.fingerprint = fingerprint; imageSha256 = Files.Hash(image); }

    internal static void ValidateBackupDevice(Fingerprint backup, Fingerprint current)
    {
        // A later USB port move is permitted. The immutable hardware fields and
        // the current installation's GUID/original digest still have to agree.
        if (backup with { Bus = current.Bus, PortPath = current.PortPath } != current)
            throw new InvalidDataException("RETAINED_BACKUP_HARDWARE_MISMATCH");
    }

    internal static RetainedLoaderWriteGrant Create(AdapterRequest request, Model model, Fingerprint fingerprint,
        byte[] original, byte[] current, byte[] image)
    {
        WbProtocol.ValidateModel(model);
        if (model.EngineeringOnly || model.ProtectedPrefixBytes != RetainedLoaderLayout.PrefixBytes ||
            request.DeviceId != model.DeviceId || request.Operation is not ("install" or "update" or "restore") || !request.ConfirmWrite || request.MigrateLegacy ||
            Files.Hash(Json.Bytes(fingerprint)) != request.ExpectedFingerprintSha256 || Files.Hash(original) != request.ExpectedBackupSha256)
            throw new InvalidDataException("RETAINED_WRITE_SCOPE_MISMATCH");
        ValidateFingerprint(fingerprint, model);
        if (original.Length != model.FlashBytes || current.Length != model.FlashBytes || image.Length != model.FlashBytes ||
            InstallationIdentity.Read(original) != null) throw new InvalidDataException("RETAINED_ORIGINAL_INVALID");
        RetainedLoaderLayout.ValidatePrefix(original);
        StockFirmwareInspector.Validate(original, 0x342D, 0xE3CE, model.DeviceId);
        if (!original.AsSpan(0, model.ProtectedPrefixBytes).SequenceEqual(current.AsSpan(0, model.ProtectedPrefixBytes)) ||
            !original.AsSpan(0, model.ProtectedPrefixBytes).SequenceEqual(image.AsSpan(0, model.ProtectedPrefixBytes)))
            throw new InvalidDataException("RETAINED_LOADER_CHANGED");
        var identity = InstallationIdentity.Read(request.Operation == "install" ? image : current);
        if (identity is null || identity.BoardId != model.BoardId || identity.InstallationId != request.ExpectedInstallationId ||
            identity.OriginalSha256 != request.ExpectedBackupSha256 || !identity.MatchesOriginal(original))
            throw new InvalidDataException("RETAINED_RECOVERY_ID_MISMATCH");
        if (request.Operation == "restore")
        {
            if (!image.AsSpan().SequenceEqual(original)) throw new InvalidDataException("RETAINED_RESTORE_NOT_ORIGINAL");
        }
        else
        {
            string targetVersion = KnownFirmwareImages.Validate(image, model.DeviceId, (byte)model.BoardId);
            if (InstallationIdentity.Read(image) != identity) throw new InvalidDataException("UPDATE_RECOVERY_ID_CHANGED");
            if (request.Operation == "install")
            {
                if (!current.AsSpan().SequenceEqual(original) || image.AsSpan(InstallationIdentity.Offset(model.FlashBytes) + InstallationIdentity.Size).ContainsAnyExcept((byte)0xFF))
                    throw new InvalidDataException("RETAINED_INSTALL_CHANGED");
            }
            else
            {
                if (Files.Hash(current) != request.ExpectedCurrentSha256) throw new InvalidDataException("CURRENT_CHANGED_SINCE_CAPTURE");
                string currentVersion = KnownFirmwareImages.Validate(current, model.DeviceId, (byte)model.BoardId);
                if (Version.Parse(currentVersion) > Version.Parse(targetVersion)) throw new InvalidDataException("FIRMWARE_DOWNGRADE_NOT_ALLOWED");
                if (!current.AsSpan(model.FlashBytes - InstallationIdentity.StorageBytes).SequenceEqual(image.AsSpan(model.FlashBytes - InstallationIdentity.StorageBytes)))
                    throw new InvalidDataException("UPDATE_CONFIGURATION_CHANGED");
            }
        }
        return new(model, fingerprint, image);
    }

    private static void ValidateFingerprint(Fingerprint value, Model model)
    {
        if (value.Transport != model.Transport || value.Vid != 0x342D || value.Pid != 0xDFA0 || value.BootVersion != 4 ||
            value.ChipId is null || (value.ChipId.Value & 0x3FFF) != 0x2980 || value.FlashBase != Model.FlashBase ||
            value.FlashBytes != model.FlashBytes || value.SramBytes != model.SramBytes || value.Configuration != 1 || value.Interface != 0 || value.Alternate != 0)
            throw new InvalidDataException("RETAINED_DEVICE_MISMATCH");
    }

    public void ValidateTransport(Model selectedModel, Fingerprint currentFingerprint, byte[] image)
    {
        if (consumed || selectedModel != model || currentFingerprint != fingerprint || Files.Hash(image) != imageSha256)
            throw new InvalidDataException("RETAINED_TRANSPORT_GRANT_MISMATCH");
        ValidateFingerprint(currentFingerprint, model);
        RetainedLoaderLayout.ValidatePrefix(image);
    }

    public void Consume(Model selectedModel, Fingerprint currentFingerprint, byte[] image, byte[] livePrefix)
    {
        ValidateTransport(selectedModel, currentFingerprint, image);
        RetainedLoaderLayout.ValidatePrefix(livePrefix);
        if (livePrefix.Length != model.ProtectedPrefixBytes || !image.AsSpan(0, livePrefix.Length).SequenceEqual(livePrefix))
            throw new InvalidDataException("RETAINED_LIVE_PREFIX_CHANGED");
        consumed = true;
    }
}
