using InGlorious.Core;

namespace InGlorious.Desktop;

public sealed partial class MainWindow
{
    // The portable editor stays pinned to the explicitly loaded profile. A
    // physical switch never changes a draft's upload destination implicitly.
    private readonly Dictionary<string, OnboardProfileBinding> _hardwareProfileBindings = [];

    private void RememberHardwareProfile(string deviceId, OnboardHardwareProfileSnapshot? snapshot)
    {
        if (snapshot is null) _hardwareProfileBindings.Remove(deviceId);
        else
        {
            if (snapshot.Status.DeviceId != deviceId) throw new InvalidDataException("Hardware profile model mismatch.");
            _hardwareProfileBindings[deviceId] = snapshot.Binding;
        }
    }

    private bool HardwareBindingChanged(string deviceId, OnboardHardwareProfileSnapshot? incoming) =>
        _hardwareProfileBindings.GetValueOrDefault(deviceId) != incoming?.Binding;
}
