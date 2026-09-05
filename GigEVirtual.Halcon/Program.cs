// --------------------------------------------------------------------------------
// Program.cs
//
// drives halcon against one of our emulated devices, in the same process, so a
// failure shows both halves at once: the gvcp traffic the device logs and the
// halcon error that came of it. hdevelop only ever shows the second half.
// --------------------------------------------------------------------------------

using System.Runtime.InteropServices;
using HalconDotNet;

namespace GigEVirtual.Halcon;

internal class Program
{
    private const string Ip = "192.168.100.137";
    private const string XmlPath = @"C:\Users\MiguelCeniceros\Documents\Repos\GigEVirtual\genie_nano.xml";
    private const string ImagePath = @"C:\Users\MiguelCeniceros\Pictures\EmulationBedford\CD_Diffuse+Photometric.jpg";

    // what halcon has to get through before it will hand back an image. reading
    // them one at a time says which one the device is failing on, which a snap
    // on its own does not.
    private static readonly string[] _interesting =
    [
        "DeviceVendorName", "DeviceModelName",
        "Width", "Height", "PixelFormat", "PayloadSize",
        "AcquisitionMode", "AcquisitionFrameRate", "ExposureTime", "Gain",
        "TriggerSelector", "TriggerMode", "TriggerSource", "TriggerDelay",
        "TLParamsLocked",
    ];

    private static void Main()
    {
        GenieNano nano = new(Ip, XmlPath, imagePath: ImagePath);
        _ = nano.Start();

        // the control server binds on a background task, so let it get there
        // before halcon starts looking for it
        Thread.Sleep(500);

        HTuple? device = Discover();
        if (device is null) return;

        HTuple handle;

        // every one of these but the device is what info_framegrabber reports as
        // this interface's default, and the interface rejects anything else:
        // 'field' and 'line_in' in particular have no other legal value.
        try
        {
            HOperatorSet.OpenFramegrabber("GigEVision2", 0, 0, 0, 0, 0, 0, "progressive",
                                          -1, "default", -1, "false", "default",
                                          device, 0, 0, out handle);
        }
        catch (HOperatorException e)
        {
            Report("open_framegrabber", e);
            return;
        }

        Console.WriteLine("--- opened");

        Dump(handle);
        Writes(handle);
        Snap(handle);
        Lossy(handle, nano);
        Triggered(handle, nano);
        Events(handle);

        HOperatorSet.CloseFramegrabber(handle);
    }

    private static HTuple? Discover()
    {
        HOperatorSet.InfoFramegrabber("GigEVision2", "device", out HTuple _, out HTuple devices);

        Console.WriteLine($"--- {devices.Length} device(s) found");
        for (int i = 0; i < devices.Length; i++)
            Console.WriteLine($"  [{i}] {devices[i].S}");

        if (devices.Length == 0) Console.WriteLine("nothing to open");

        return devices.Length > 0 ? devices[0] : null;
    }

    // reads every feature halcon says the device has. the ones that fail are
    // the diagnostic: a feature halcon cannot read is one it may refuse to grab
    // over, and the gvcp log beside it says which register let it down.
    private static void Dump(HTuple handle)
    {
        HOperatorSet.GetFramegrabberParam(handle, "available_param_names", out HTuple names);

        int failed = 0;

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i].S;

            try
            {
                HOperatorSet.GetFramegrabberParam(handle, name, out HTuple _);
            }
            catch (HOperatorException e)
            {
                // halcon hides some genicam features behind its own names, so
                // this one is not the device's fault
                if (e.GetErrorCode() == 5321) continue;

                failed++;
                Console.WriteLine($"  {name} FAILED {e.GetErrorCode()}: {e.GetErrorMessage()}");
            }
        }

        Console.WriteLine($"--- {failed} of {names.Length} features unreadable");

        foreach (string name in _interesting)
        {
            try
            {
                HOperatorSet.GetFramegrabberParam(handle, name, out HTuple value);
                Console.WriteLine($"  {name} = {Show(value)}");
            }
            catch (HOperatorException e)
            {
                Console.WriteLine($"  {name} FAILED {e.GetErrorCode()}: {e.GetErrorMessage()}");
            }
        }
    }

    // reading a feature only proves the device answers. an application that
    // cannot write one is just as stuck, and the usual reason is a min or max
    // register the description points at that we left reading zero.
    private static void Writes(HTuple handle)
    {
        (string Name, HTuple Value)[] writes =
        [
            ("ExposureTime", 20000),
            ("Gain", 2.0),
            ("AcquisitionFrameRate", 15.0),
            ("Width", 640),
            ("Height", 480),
            ("PixelFormat", "Mono8"),

            // the description locks the frame count away unless the device is
            // in multi frame mode, so the order here is not incidental
            ("AcquisitionMode", "MultiFrame"),
            ("AcquisitionFrameCount", 3),
            ("AcquisitionMode", "Continuous"),
        ];

        Console.WriteLine("--- writing");

        foreach ((string name, HTuple value) in writes)
        {
            try
            {
                HOperatorSet.SetFramegrabberParam(handle, name, value);
                HOperatorSet.GetFramegrabberParam(handle, name, out HTuple back);
                Console.WriteLine($"  {name} <- {Show(value)}, reads back {Show(back)}");
            }
            catch (HOperatorException e)
            {
                Console.WriteLine($"  {name} <- {Show(value)} FAILED {e.GetErrorCode()}: {e.GetErrorMessage()}");
            }
        }
    }

    private static void Snap(HTuple handle)
    {
        Console.WriteLine("--- grabbing");

        try
        {
            HOperatorSet.SetFramegrabberParam(handle, "grab_timeout", 5000);
            HOperatorSet.GrabImage(out HObject image, handle);
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);

            Console.WriteLine($"  grabbed {width[0].I} x {height[0].I}");
            image.Dispose();
        }
        catch (HOperatorException e)
        {
            Report("grab_image", e);
        }
    }

    // the other thing only an emulator can do: lose packets on purpose and watch
    // the application ask for them back
    private static void Lossy(HTuple handle, GigEDevice device)
    {
        Console.WriteLine("--- dropping one packet in fifty");

        device.DropOneIn = 50;

        try
        {
            HOperatorSet.SetFramegrabberParam(handle, "grab_timeout", 5000);
            HOperatorSet.GrabImage(out HObject image, handle);
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);

            HOperatorSet.GetFramegrabberParam(handle, "buffer_is_incomplete", out HTuple incomplete);
            Console.WriteLine($"  grabbed {width[0].I} x {height[0].I}, incomplete={Show(incomplete)}");

            image.Dispose();
        }
        catch (HOperatorException e)
        {
            Report("lossy grab", e);
        }
        finally
        {
            device.DropOneIn = 0;
        }
    }

    // the whole point of the emulator: a trigger line with nothing wired to it,
    // pulsed from code, and halcon on the other end none the wiser
    private static void Triggered(HTuple handle, GenieNano nano)
    {
        Console.WriteLine("--- hardware trigger on line 2");

        try
        {
            HOperatorSet.SetFramegrabberParam(handle, "TriggerSelector", "FrameStart");
            HOperatorSet.SetFramegrabberParam(handle, "TriggerMode", "On");
            HOperatorSet.SetFramegrabberParam(handle, "TriggerSource", "Line2");

            HOperatorSet.GetFramegrabberParam(handle, "TriggerSource", out HTuple source);
            Console.WriteLine($"  TriggerSource = {Show(source)}");
        }
        catch (HOperatorException e)
        {
            Report("setting up the trigger", e);
            return;
        }

        // a quiet line should produce nothing at all
        try
        {
            HOperatorSet.SetFramegrabberParam(handle, "grab_timeout", 1000);
            HOperatorSet.GrabImage(out HObject none, handle);
            none.Dispose();
            Console.WriteLine("  !!! a frame arrived without a trigger");
        }
        catch (HOperatorException e)
        {
            Console.WriteLine($"  quiet line timed out, as it should ({e.GetErrorCode()})");
        }

        // and one pulse should produce exactly one frame
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Console.WriteLine($"  pulsing line 2: {nano.PulseLine(2)}");
        });

        try
        {
            HOperatorSet.SetFramegrabberParam(handle, "grab_timeout", 5000);
            HOperatorSet.GrabImage(out HObject image, handle);
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);

            Console.WriteLine($"  the pulse produced {width[0].I} x {height[0].I}");
            image.Dispose();
        }
        catch (HOperatorException e)
        {
            Report("triggered grab", e);
        }
    }

    // halcon calls this from its own thread when an event turns up on the
    // message channel. cdecl with three pointers is the shape its acquisition
    // callbacks have, and the delegate has to outlive the framegrabber or the
    // collector will pull the function out from under it.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CallbackFunction(IntPtr handle, IntPtr context, IntPtr userContext);

    private static readonly List<CallbackFunction> _callbacks = [];
    private static readonly Dictionary<string, int> _fired = [];

    // events reach an application over a channel of its own, which it opens
    // rather than us. the device log says what went down that channel; the
    // counts here say whether halcon made anything of it.
    private static void Events(HTuple handle)
    {
        Console.WriteLine("--- events");

        // halcon delivers nothing unless it is handling device events, which it
        // decides for itself: this reads 1 as soon as the device says it has a
        // message channel
        HOperatorSet.GetFramegrabberParam(handle, "device_event_handling", out HTuple handling);
        Console.WriteLine($"  device_event_handling reads {Show(handling)}");

        // the description switches events on one at a time: pick one with the
        // selector, then say yes to it
        string[] wanted = ["FrameStart", "ExposureStart", "ExposureEnd", "ValidFrameTrigger"];

        foreach (string name in wanted)
        {
            try
            {
                HOperatorSet.SetFramegrabberParam(handle, "EventSelector", name);
                HOperatorSet.SetFramegrabberParam(handle, "EventNotification", "On");
            }
            catch (HOperatorException e)
            {
                Console.WriteLine($"  {name} FAILED {e.GetErrorCode()}: {e.GetErrorMessage()}");
            }
        }

        // halcon names the callback after the event the description declares
        foreach (string name in wanted.Select(w => "Event" + w))
        {
            _fired[name] = 0;

            CallbackFunction callback = (_, _, _) =>
            {
                lock (_fired) _fired[name]++;
                return 0;
            };

            // the delegate has to outlive the framegrabber, or the collector
            // pulls the function out from under halcon
            _callbacks.Add(callback);

            try
            {
                HOperatorSet.SetFramegrabberCallback(handle, name,
                    Marshal.GetFunctionPointerForDelegate(callback).ToInt64(), 0);
            }
            catch (HOperatorException e)
            {
                Console.WriteLine($"  cannot listen for {name}: {e.GetErrorCode()}");
            }
        }

        // free running again, so frames come without anyone pulsing a line
        try
        {
            HOperatorSet.SetFramegrabberParam(handle, "TriggerMode", "Off");
            HOperatorSet.SetFramegrabberParam(handle, "grab_timeout", 5000);

            for (int i = 0; i < 5; i++)
            {
                HOperatorSet.GrabImage(out HObject image, handle);
                image.Dispose();
            }
        }
        catch (HOperatorException e)
        {
            Report("grabbing for events", e);
        }

        // and one triggered round, which is the only way the trigger events can
        // happen at all
        try
        {
            HOperatorSet.SetFramegrabberParam(handle, "TriggerSelector", "FrameStart");
            HOperatorSet.SetFramegrabberParam(handle, "TriggerMode", "On");
            HOperatorSet.SetFramegrabberParam(handle, "TriggerSource", "Software");
            HOperatorSet.SetFramegrabberParam(handle, "TriggerSoftware", 1);

            HOperatorSet.GrabImage(out HObject image, handle);
            image.Dispose();
        }
        catch (HOperatorException e)
        {
            Report("software triggered grab", e);
        }

        // the message channel runs alongside the stream rather than in step with
        // it, so the last few events are still in flight when the grab returns
        Thread.Sleep(500);

        lock (_fired)
            foreach ((string name, int count) in _fired)
                Console.WriteLine($"  {name} fired {count} time(s)");

    }

    private static void Report(string what, HOperatorException e) =>
        Console.WriteLine($"!!! {what} FAILED {e.GetErrorCode()}: {e.GetErrorMessage()}");

    private static string Show(HTuple value)
    {
        string[] parts = new string[value.Length];

        for (int i = 0; i < value.Length; i++)
            parts[i] = value[i].Type switch
            {
                HTupleType.STRING => value[i].S,
                HTupleType.DOUBLE => value[i].D.ToString(),

                // some parameters hand back a halcon object rather than a
                // value, and asking one for its number throws
                HTupleType.HANDLE => "<handle>",
                _ => value[i].I.ToString(),
            };

        return string.Join(" | ", parts);
    }
}
