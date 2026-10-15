using System.Buffers.Binary;
using InGlorious.Core;

namespace InGlorious.FirmwareAdapter;

internal sealed record EngineeringWriteAuthorization(int Schema, string Operation, string DeviceId, string SessionDirectory,
    Fingerprint Fingerprint, string FingerprintSha256, string OriginalSha256, Guid InstallationId,
    string PreparedImageSha256, int CandidateBytes, string CandidateSha256, string RetainedPrefixSha256);

internal static class EngineeringPolicy
{
    internal const int ApplicationOffset = 0x7000;
    internal const string ResidentLoaderSha256 = "079DC24559F9ED85368499AAC71CBC8A023C94524D5E4189C20C1D7E4CFCFCA4";
    internal sealed record Candidate(int Bytes, string Sha256, string ReleaseId);

    // Reviewed offset-linked engineering application; this does not authorize a hardware operation.
    // A request, authorization document or bundle can never populate this immutable slot.
    private static readonly Candidate? ReviewedCandidate = new(44452, "778DEDD35DEC168CDE6EB1A19799BD0C3ECD9EB57DA23D68A968A0FE5ED97059", "0.11.2");
    internal static bool HasReviewedCandidate => ReviewedCandidate != null;
    internal static Candidate RequireReviewedCandidate() => ReviewedCandidate ?? throw new InvalidDataException("ENGINEERING_CANDIDATE_NOT_REVIEWED");

    internal static void ValidateRequest(EngineeringWriteAuthorization authorization, AdapterRequest request, Model model)
        => ValidateScope(authorization, request, model, RequireReviewedCandidate());

    // Pure validation accepts an expected candidate for synthetic tests. It cannot issue a write grant.
    internal static void ValidateScope(EngineeringWriteAuthorization authorization, AdapterRequest request, Model model, Candidate candidate)
    {
        WbProtocol.ValidateModel(model);
        if (!model.EngineeringOnly || model.ProtectedPrefixBytes != ApplicationOffset || request.Operation is not ("install" or "restore") ||
            !request.ConfirmWrite || request.MigrateLegacy || request.ExpectedCurrentSha256 != null)
            throw new InvalidDataException("ENGINEERING_OPERATION_NOT_ALLOWED");
        if (authorization.Schema != 1 || authorization.Operation != request.Operation || authorization.DeviceId != model.DeviceId ||
            authorization.SessionDirectory != request.SessionDirectory || !Path.IsPathFullyQualified(authorization.SessionDirectory) ||
            request.DeviceId != model.DeviceId || authorization.Fingerprint is null || request.Selection is null ||
            authorization.FingerprintSha256 != request.ExpectedFingerprintSha256 || authorization.OriginalSha256 != request.ExpectedBackupSha256 ||
            authorization.InstallationId != request.ExpectedInstallationId || authorization.InstallationId == Guid.Empty ||
            authorization.CandidateBytes != candidate.Bytes || authorization.CandidateSha256 != candidate.Sha256 ||
            authorization.RetainedPrefixSha256 != ResidentLoaderSha256)
            throw new InvalidDataException("ENGINEERING_AUTHORIZATION_SCOPE_MISMATCH");
        foreach (string hash in new[] { authorization.FingerprintSha256, authorization.OriginalSha256,
            authorization.PreparedImageSha256, authorization.CandidateSha256, authorization.RetainedPrefixSha256 })
            if (hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || hash != hash.ToUpperInvariant())
                throw new InvalidDataException("ENGINEERING_AUTHORIZATION_HASH_INVALID");
        var f = authorization.Fingerprint;
        var s = request.Selection;
        if (Files.Hash(Json.Bytes(f)) != authorization.FingerprintSha256 ||
            f.Transport != model.Transport || f.Vid != 0x342D || f.Pid != 0xDFA0 || f.BootVersion != model.BootVersion ||
            f.ChipId is null || (f.ChipId.Value & 0x3FFF) != 0x2980 || f.FlashBase != Model.FlashBase ||
            f.FlashBytes != model.FlashBytes || f.SramBytes != model.SramBytes || f.Configuration != 1 || f.Interface != 0 || f.Alternate != 0 ||
            s.Vid != f.Vid || s.Pid != f.Pid || s.Bus != f.Bus || s.PortPath != f.PortPath || s.Transport != f.Transport ||
            s.Serial is null || s.Serial != f.Serial || !ValidPort(f.PortPath))
            throw new InvalidDataException("ENGINEERING_AUTHORIZATION_DEVICE_MISMATCH");
    }

    private static bool ValidPort(string path) => path is not null && path.Split('.') is var parts && parts.Length is >= 1 and <= 8 &&
        parts.All(part => byte.TryParse(part, out byte value) && value > 0 && part == value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static string ValidateCandidateImage(byte[] image, Model model)
        => ValidateCandidateImage(image, model, RequireReviewedCandidate());

    internal static string ValidateCandidateImage(byte[] image, Model model, Candidate candidate)
    {
        WbProtocol.ValidateModel(model);
        if (!model.EngineeringOnly || image.Length != model.FlashBytes || candidate.Bytes < 8 ||
            candidate.Bytes > InstallationIdentity.Offset(model.FlashBytes) - ApplicationOffset ||
            candidate.Sha256.Length != 64 || candidate.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("ENGINEERING_CANDIDATE_SHAPE");
        var identity = InstallationIdentity.Read(image);
        if (identity is null || identity.BoardId != model.BoardId) throw new InvalidDataException("ENGINEERING_CANDIDATE_IDENTITY_REQUIRED");
        int end = ApplicationOffset + candidate.Bytes;
        int marker = InstallationIdentity.Offset(model.FlashBytes);
        if (Files.Hash(image.AsSpan(ApplicationOffset, candidate.Bytes).ToArray()) != candidate.Sha256 ||
            image.AsSpan(end, marker - end).ContainsAnyExcept((byte)0xFF) ||
            image.AsSpan(marker + InstallationIdentity.Size, 256 - InstallationIdentity.Size).ContainsAnyExcept((byte)0xFF))
            throw new InvalidDataException("ENGINEERING_CANDIDATE_NOT_APPROVED");
        uint stack = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ApplicationOffset));
        uint reset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ApplicationOffset + 4));
        if (stack <= 0x20000000 || stack > 0x20000000UL + (uint)model.SramBytes || stack % 4 != 0 ||
            (reset & 1) == 0 || (reset & ~1U) < Model.FlashBase + ApplicationOffset || (reset & ~1U) >= Model.FlashBase + end)
            throw new InvalidDataException("ENGINEERING_APPLICATION_VECTORS");
        return candidate.ReleaseId;
    }

    internal static void ValidatePrefixEquality(Model model, byte[] original, byte[] current, byte[] image)
    {
        WbProtocol.ValidateModel(model);
        if (!model.EngineeringOnly || original.Length != model.FlashBytes || current.Length != model.FlashBytes || image.Length != model.FlashBytes ||
            !original.AsSpan(0, ApplicationOffset).SequenceEqual(current.AsSpan(0, ApplicationOffset)) ||
            !original.AsSpan(0, ApplicationOffset).SequenceEqual(image.AsSpan(0, ApplicationOffset)))
            throw new InvalidDataException("ENGINEERING_RETAINED_PREFIX_CHANGED");
    }

    internal static WriteGrant Authorize(EngineeringWriteAuthorization authorization, AdapterRequest request, Model model,
        Fingerprint fingerprint, byte[] original, byte[] current, byte[] image)
        => WriteGrant.Create(authorization, request, model, fingerprint, original, current, image);

    private static void ValidateAuthorizationInputs(EngineeringWriteAuthorization authorization, AdapterRequest request, Model model,
        Fingerprint fingerprint, byte[] original, byte[] current, byte[] image)
    {
        ValidateRequest(authorization, request, model);
        ValidatePrefixEquality(model, original, current, image);
        if (fingerprint != authorization.Fingerprint || Files.Hash(original) != authorization.OriginalSha256 ||
            Files.Hash(image) != authorization.PreparedImageSha256 || Files.Hash(original.AsSpan(0, ApplicationOffset).ToArray()) != ResidentLoaderSha256)
            throw new InvalidDataException("ENGINEERING_AUTHORIZATION_IMAGE_MISMATCH");
        if (request.Operation == "install") _ = ValidateCandidateImage(image, model);
        else if (!image.AsSpan().SequenceEqual(original)) throw new InvalidDataException("ENGINEERING_RESTORE_NOT_EXACT_ORIGINAL");
        // Both operations retain the existing marker check in AdapterEngine. No marker-loss override exists.
    }

    internal sealed class WriteGrant : IRetainedLoaderWriteGrant
    {
        private readonly EngineeringWriteAuthorization authorization;
        private readonly Model model;
        private bool consumed;
        private WriteGrant(EngineeringWriteAuthorization authorization, Model model) { this.authorization = authorization; this.model = model; }
        // Only the policy may create a grant; this nested helper repeats immutable approval and scope checks.
        internal static WriteGrant Create(EngineeringWriteAuthorization authorization, AdapterRequest request, Model model,
            Fingerprint fingerprint, byte[] original, byte[] current, byte[] image)
        {
            ValidateAuthorizationInputs(authorization, request, model, fingerprint, original, current, image);
            return new(authorization, model);
        }
        public void ValidateTransport(Model selectedModel, Fingerprint fingerprint, byte[] image)
        {
            var candidate = RequireReviewedCandidate();
            if (consumed || selectedModel != model || !model.EngineeringOnly || fingerprint != authorization.Fingerprint ||
                authorization.CandidateBytes != candidate.Bytes || authorization.CandidateSha256 != candidate.Sha256 ||
                Files.Hash(image) != authorization.PreparedImageSha256 ||
                Files.Hash(image.AsSpan(0, ApplicationOffset).ToArray()) != ResidentLoaderSha256)
                throw new InvalidDataException("ENGINEERING_TRANSPORT_AUTHORIZATION_MISMATCH");
        }
        public void Consume(Model selectedModel, Fingerprint fingerprint, byte[] image, byte[] livePrefix)
        {
            ValidateTransport(selectedModel, fingerprint, image);
            if (livePrefix.Length != ApplicationOffset || Files.Hash(livePrefix) != ResidentLoaderSha256 ||
                !image.AsSpan(0, ApplicationOffset).SequenceEqual(livePrefix)) throw new InvalidDataException("ENGINEERING_LIVE_PREFIX_CHANGED");
            consumed = true;
        }
    }
}
