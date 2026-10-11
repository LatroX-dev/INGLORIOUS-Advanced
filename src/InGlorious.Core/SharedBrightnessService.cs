namespace InGlorious.Core;

/// <summary>Copies a volatile dimmer value. Configuration and flash commands are never used.</summary>
public sealed class SharedBrightnessService(Func<IReadOnlyList<OnboardDevice>>? discover = null, Func<OnboardDevice, OnboardClient>? open = null)
{
    public const string Notice = "Gemeinsames Dimmen von GMMK Numpad und Tastatur benötigt INGLORIOUS Advanced im Hintergrund. Nur das GMMK Numpad dimmt auch ohne laufende App. Farben, Effekte und Belegungen bleiben auf den Geräten gespeichert.";
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private string _status = "";
    public event Action<string>? StatusChanged;
    public bool Running => _stop is not null;
    public void Start()
    {
        if (_stop is not null) return;
        var stop = new CancellationTokenSource(); _stop = stop; _worker = Task.Run(() => Run(stop.Token));
    }
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var stop = _stop; if (stop is null) return;
            stop.Cancel();
            try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
            stop.Dispose(); _stop = null; SetStatus("");
        }
        finally { _lifecycle.Release(); }
    }
    private void SetStatus(string value)
    { if (_status == value) return; _status = value; StatusChanged?.Invoke(value); }
    private async Task Run(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var devices = (discover ?? OnboardClient.FindDevices)();
                var pads = devices.Where(d => d.DeviceId == "numpad").ToArray();
                var pros = devices.Where(d => d.DeviceId == "pro-iso").ToArray();
                if (pads.Length != 1 || pros.Length != 1)
                    SetStatus(L.T("Zum gemeinsamen Dimmen genau ein GMMK Numpad und eine GMMK PRO ISO verbinden."));
                else
                {
                    await using var pad = (open ?? OnboardClient.Open)(pads[0]);
                    await using var pro = (open ?? OnboardClient.Open)(pros[0]);
                    var padInfo = await pad.GetInfoAsync(cancellation).ConfigureAwait(false);
                    var proInfo = await pro.GetInfoAsync(cancellation).ConfigureAwait(false);
                    if (!padInfo.SupportsRuntimeBrightness || !proInfo.SupportsRuntimeBrightness)
                        SetStatus(L.T("Gemeinsames Dimmen benötigt Firmware 0.5 oder neuer auf beiden Geräten."));
                    else
                    {
                        byte? last = null;
                        while (!cancellation.IsCancellationRequested)
                        {
                            var source = await pad.GetRuntimeBrightnessAsync(cancellation).ConfigureAwait(false);
                            if (source.SliderMode != 3)
                            { last = null; SetStatus(L.T("Am GMMK Numpad „Beleuchtung dimmen“ wählen und auf die Tastatur übertragen.")); }
                            else
                            {
                                if (last != source.Brightness)
                                { await pro.SetRuntimeBrightnessAsync(source.Brightness, cancellation).ConfigureAwait(false); last = source.Brightness; }
                                SetStatus(L.T("GMMK Numpad und GMMK PRO ISO werden gemeinsam gedimmt."));
                            }
                            await Task.Delay(100, cancellation).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception ex) { SetStatus(L.T("Gemeinsames Dimmen wartet auf die Geräte: ") + ex.Message); }
            await Task.Delay(3000, cancellation).ConfigureAwait(false);
        }
    }
}
