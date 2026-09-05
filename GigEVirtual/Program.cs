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
        GigECamera cam1 = new("192.168.100.137", shareToNetwork:false, imagePath: @"C:\Users\MiguelCeniceros\Pictures\EmulationBedford\CD_Diffuse+Photometric.jpg");
        //GigECamera cam2 = new("192.168.100.138", serialNumber:"S0002", deviceName: "secondDevice");
        //await Task.WhenAll(cam1.Start(), cam2.Start());
        await cam1.Start();

        // the genie nano needs the path to its own description, which is not in
        // the repo. everything else about it comes out of that file.
        //GenieNano nano = new("192.168.100.137",
        //                     xmlPath: @"C:\Users\MiguelCeniceros\Documents\Repos\GigEVirtual\genie_nano.xml",
        //                     imagePath: @"C:\Users\MiguelCeniceros\Pictures\EmulationBedford\CD_Diffuse+Photometric.jpg");
        //await nano.Start();
    }
}