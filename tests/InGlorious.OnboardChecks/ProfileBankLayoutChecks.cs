using InGlorious.Core;

static class ProfileBankLayoutChecks
{
    public static void Run(Action<bool, string> check, string? fixtures = null)
    {
        void Reject(Action action, string name)
        { try { action(); } catch (InvalidDataException) { check(true, name); return; } throw new Exception(name); }
        var before = Enumerable.Repeat((byte)255, 0x40000).ToArray();
        for (int i = 0x38000; i < before.Length; i++) before[i] = (byte)i;
        var target = Enumerable.Repeat((byte)255, before.Length).ToArray();
        ProfileBankLayout.ValidateGeometry("gmmk3-pro-100-iso", 16, before.Length, 0x7000, 0x23000, ProfileBankLayout.Name);
        check(ProfileBankLayout.GapIsValid(before, 0x28000, ProfileBankLayout.Name), "empty explicit bank is allowed with erased surrounding gaps");
        ProfileBankLayout.Preserve(before, target, null, ProfileBankLayout.Name);
        check(target.AsSpan(0x38000).SequenceEqual(before.AsSpan(0x38000)), "first bank firmware retains legacy tail exactly");
        Reject(() => ProfileBankLayout.ValidateGeometry("numpad", 2, 0x20000, 0, 4096, ProfileBankLayout.Name), "bank geometry never inferred on another board");
        Reject(() => ProfileBankLayout.ValidateGeometry("gmmk3-pro-100-iso", 16, 0x40000, 0x7000, 0x23001, ProfileBankLayout.Name), "code cannot overlap profile region");
        Reject(() => ProfileBankLayout.Preserve(before, target, ProfileBankLayout.Name, null), "bank downgrade is blocked even if currently empty");
        Reject(() => ProfileBankLayout.Preserve(before, target, "future", ProfileBankLayout.Name), "unknown source layout is blocked");
        ProfileBankLayout.RequireInitialState(target, ProfileBankLayout.Name);
        var seeded = target.ToArray(); seeded[ProfileBankLayout.Start] = 0;
        Reject(() => ProfileBankLayout.RequireInitialState(seeded, ProfileBankLayout.Name), "first installation cannot carry preseeded hardware profiles");
        foreach (int offset in new[] { 0x29000, 0x36000, 0x37EFF })
        {
            var broken = before.ToArray(); broken[offset] = 1;
            check(!ProfileBankLayout.GapIsValid(broken, 0x28000, ProfileBankLayout.Name), "unrelated gap remains erased at " + offset);
        }
        if (fixtures is null) return;
        foreach (string file in new[] { "empty", "first-save-torn", "valid", "torn-inactive" })
        {
            var bank = File.ReadAllBytes(Path.Combine(fixtures, file + ".bin")); var scan = ProfileBankLayout.Inspect(bank);
            check(scan.Profiles.Count == (file is "valid" or "torn-inactive" ? 3 : 0), "native fixture selected count: " + file);
            foreach (var profile in scan.Profiles)
                check(profile.Generation == 2 && OnboardSerializer.Decode(profile.Payload).DeviceId == "gmmk3-pro-100-iso", "native fixture real schema payload decoded: " + file);
            bank.CopyTo(before, ProfileBankLayout.Start);
            check(!ProfileBankLayout.GapIsValid(before, 0x28000, null) == (file != "empty"), "old releases do not gain a bank exception: " + file);
            ProfileBankLayout.Preserve(before, target, ProfileBankLayout.Name, ProfileBankLayout.Name);
            check(target.AsSpan(ProfileBankLayout.Start, ProfileBankLayout.Length).SequenceEqual(bank), "all bank bytes survive update including torn record: " + file);
            ProfileBankLayout.RequirePreserved(before, target, ProfileBankLayout.Name, ProfileBankLayout.Name);
            target[ProfileBankLayout.Start + 99] ^= 1;
            Reject(() => ProfileBankLayout.RequirePreserved(before, target, ProfileBankLayout.Name, ProfileBankLayout.Name), "one altered bank byte blocks adapter write: " + file);
        }
        foreach (string file in new[] { "future-header", "foreign-header", "equal-generation", "wrong-pair" })
            Reject(() => ProfileBankLayout.Inspect(File.ReadAllBytes(Path.Combine(fixtures, file + ".bin"))), "native unsafe fixture rejected: " + file);
    }
}
