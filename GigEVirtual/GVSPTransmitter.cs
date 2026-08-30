// --------------------------------------------------------------------------------
// GVSPTransmitter.cs
//
// handles the transmission of gvsp packets.
// --------------------------------------------------------------------------------

namespace GigEVirtual;

internal class GVSPTransmitter
{
    // --------------------------------------------------------------- fields and properties

    private CancellationTokenSource? _cts = null;

    // --------------------------------------------------------------- methods

    public void StartAcquisition(DeviceState deviceState)
    {
        // if already streaming, return
        if (_cts != null) return;
        _cts = new();
        _ = Stream(_cts.Token);
    }

    public void StopAcquisition()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task Stream(CancellationToken ct)
    {
        await Task.CompletedTask;
    }
}