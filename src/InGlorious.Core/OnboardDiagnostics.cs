using System.IO;

namespace InGlorious.Core;

public sealed record OnboardDeviceTestStatus(bool Active, byte[] SeenRows, ushort Clockwise, ushort Counterclockwise, ushort RemainingMs)
{
    public bool WasPressed(string keyId)
    {
        var parts = keyId.Split('c');
        return parts.Length == 2 && parts[0].StartsWith('r') && int.TryParse(parts[0].AsSpan(1), out var row) &&
            int.TryParse(parts[1], out var col) && row >= 0 && row < SeenRows.Length && col is >= 0 and < 8 && (SeenRows[row] & (1 << col)) != 0;
    }
}

public sealed partial class OnboardClient
{
    public async Task<OnboardDeviceTestStatus> DeviceTestAsync(byte action, CancellationToken token = default)
    {
        if (action > 3) throw new ArgumentOutOfRangeException(nameof(action));
        await _exclusive.WaitAsync(token);
        try
        {
            if (action is 1 or 3)
            {
                var info = await InfoCore(token);
                if (!info.Persistent || (info.Flags & 4) != 0 || info.StagingBytes != 0)
                    throw new IOException(L.T("Eine Übertragung läuft. Starte den Gerätetest danach erneut."));
                if (!(await ExtensionsCore(token)).SupportsDeviceTest)
                    throw new NotSupportedException(L.T("Für den vollständigen Gerätetest ist ein Firmware-Update erforderlich."));
            }
            var data = await Send(OnboardProtocol.DeviceTest, [action], token);
            return ParseDeviceTest(data, expectedDeviceId, action);
        }
        finally { _exclusive.Release(); }
    }

    internal static OnboardDeviceTestStatus ParseDeviceTest(byte[] data, string deviceId, byte action)
    {
        var shape = BoardDescriptorRegistry.Get(deviceId);
        if (data.Length != 24 || data[0] != 1 || data[1] > 1 || data[2] != shape.Rows || data[3] != shape.Cols || shape.Rows > 14 || shape.Cols > 8 ||
            data.AsSpan(4 + shape.Rows, 14 - shape.Rows).IndexOfAnyExcept((byte)0) >= 0 ||
            Get16(data, 22) > 10000 || (data[1] == 1 ? Get16(data, 22) == 0 : Get16(data, 22) != 0) ||
            (action is 1 or 3 && data[1] != 1) || (action == 2 && data[1] != 0))
            throw new InvalidDataException(L.T("Der Gerätetest hat eine ungültige Antwort erhalten."));
        var rows = data.AsSpan(4, shape.Rows).ToArray();
        if (rows.Any(row => (row & ~((1 << shape.Cols) - 1)) != 0)) throw new InvalidDataException("Invalid diagnostic matrix.");
        return new(data[1] == 1, rows, Get16(data, 18), Get16(data, 20), Get16(data, 22));
    }

    public async Task SetDeviceTestLedAsync(byte mode, byte index = 0, byte red = 0, byte green = 0, byte blue = 0, CancellationToken token = default)
    {
        if (mode > 2 || (mode != 2 && index != 0) || (mode == 2 && index >= BoardDescriptorRegistry.Get(expectedDeviceId).LedCount) ||
            (mode == 0 && (red != 0 || green != 0 || blue != 0))) throw new ArgumentOutOfRangeException(nameof(mode));
        await _exclusive.WaitAsync(token);
        try
        {
            byte[] payload = [mode, index, red, green, blue];
            var data = await Send(OnboardProtocol.DeviceTestLed, payload, token);
            if (data.Length != 8 || !data.AsSpan(0, 5).SequenceEqual(payload) || data[5] != 0 || Get16(data, 6) is 0 or > 10000)
                throw new InvalidDataException(L.T("Der Gerätetest hat eine ungültige Antwort erhalten."));
        }
        finally { _exclusive.Release(); }
    }
}
