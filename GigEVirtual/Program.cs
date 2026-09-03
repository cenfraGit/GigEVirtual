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
        GigECamera cam1 = new("192.168.100.137");
        GigECamera cam2 = new("192.168.100.138", serialNumber:"S0002", deviceName: "secondDevice");
        await Task.WhenAll(cam1.Start(), cam2.Start());
    }
}