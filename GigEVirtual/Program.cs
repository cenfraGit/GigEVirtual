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
        //GigECamera cam1 = new("192.168.100.137", shareToNetwork: false, imagePath: @"C:\Users\MiguelCeniceros\Pictures\EmulationBedford\CD_Diffuse+Photometric.jpg");
        //await cam1.Start();

        // the genie nano needs the path to its own description, which is not in
        // the repo. everything else about it comes out of that file.
        GenieNano nano = new("192.168.100.137",
                             xmlPath: @"C:\Users\MiguelCeniceros\Documents\Repos\GigEVirtual\genie_nano.xml",
                             imagePath: @"C:\Users\MiguelCeniceros\Pictures\EmulationBedford\CD_Diffuse+Photometric.jpg");

        // comes up already waiting on line 2, so an application does not have to
        // set the trigger up itself before it can grab
        nano.UseHardwareTrigger(2);

        Console.WriteLine("[LINE2] press any key to pulse line 2, esc to stop pulsing");
        _ = Task.Run(() => PulseOnKeypress(nano, line: 2));

        await nano.Start();
    }

    // there is no connector to put a signal on, so the keyboard is the cable.
    // every keypress is one edge on the line.
    private static void PulseOnKeypress(GenieNano nano, int line)
    {
        if (Console.IsInputRedirected) return;

        while (Console.ReadKey(intercept: true).Key != ConsoleKey.Escape)
            Console.WriteLine(nano.PulseLine(line)
                ? $"[LINE{line}] pulse"
                : $"[LINE{line}] pulse ignored, the device is listening to another source");
    }
}
