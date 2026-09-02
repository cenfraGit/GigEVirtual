// --------------------------------------------------------------------------------
// Program.cs
//
// Starting point.
// --------------------------------------------------------------------------------

namespace GigEVirtual;

internal class Program
{
    internal static async Task Main()
    {
        // create device
        DeviceState deviceState = new();

        await GVCPServer.Start(deviceState, new GVSPTransmitter(), false);
    }
}