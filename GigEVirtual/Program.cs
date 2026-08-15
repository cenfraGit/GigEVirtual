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

        // start gvcp
        await GVCPServer.Start(deviceState);
    }
}