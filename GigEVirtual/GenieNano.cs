// --------------------------------------------------------------------------------
// GenieNano.cs
//
// emulates a Teledyne DALSA Genie Nano from the device description the real
// camera serves. most of its registers come straight out of that file: the ones
// declared here are the handful the description places at an address only
// resolvable at runtime, plus the ones that have to actually do something.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;

namespace GigEVirtual;

public class GenieNano : GigEDevice
{
    // a trigger that has arrived but not yet produced a block. the register that
    // fires it and the transmitter that consumes it are on different threads.
    internal sealed class TriggerGate
    {
        private int _pending;

        public void Fire() => Interlocked.Exchange(ref _pending, 1);
        public bool Take() => Interlocked.Exchange(ref _pending, 0) == 1;
    }

    // what BuildState hands back: the registers, plus the trigger state that
    // lives beside them rather than in them
    internal record Built(DeviceState State, TriggerGate Gate);

    // --------------------------------------------------------------- the model

    // Nano-M1920: 1920x1200 monochrome. change these four and the device
    // becomes a different Nano.
    private const uint SensorWidth = 1920;
    private const uint SensorHeight = 1200;
    private const uint SizeIncrement = 4;

    private static readonly uint[] _pixelFormats =
        [GVSPPixelFormats.Mono8, GVSPPixelFormats.Mono10, GVSPPixelFormats.Mono12];

    // --------------------------------------------------------------- registers

    // every one of these is little-endian, which is how the description lays out
    // the whole manufacturer space. addresses were read out of that file.
    private const uint AcquisitionStart = 0x20000000;
    private const uint AcquisitionStop = 0x20000010;
    private const uint AcquisitionMode = 0x20000040;
    private const uint AcquisitionFrameCount = 0x20000050;
    private const uint PixelFormat = 0x20000060;
    private const uint Width = 0x20000070;
    private const uint Height = 0x20000090;
    private const uint FrameRate = 0x200000B0;   // milli-hertz
    private const uint TriggerMode = 0x20000F80;
    private const uint TriggerSource = 0x20001000;
    private const uint TriggerDelay = 0x200010C0; // microseconds
    private const uint TriggerSoftware = 0x20001100;
    private const uint ExposureTime = 0x20004BFC; // microseconds
    private const uint Gain = 0x20001530;         // 200 * log10(factor)

    // the description reaches 0xB0000000, so the blob sits clear of all of it
    private const uint XmlAddress = 0xF0000000;

    // the description converts Hz to the register with FROM * 1000
    private const uint MilliHertz = 1000;

    // values the description gives these enumerations
    private const uint ContinuousMode = 0;
    private const uint SingleFrameMode = 1;
    private const uint MultiFrameMode = 2;
    private const uint TriggerOn = 1;
    private const uint SoftwareTrigger = 0;

    private const uint MinFrameRate = 100;        // 0.1 Hz
    private const uint MaxFrameRate = 1000 * MilliHertz;
    private const uint MinExposure = 1;
    private const uint MaxExposure = 2_000_000;   // 2 s, matching the trigger delay range

    // what the image on disk is taken to already represent. exposing longer than
    // this brightens the frame, shorter darkens it.
    private const float ReferenceExposure = 10_000f;

    // the description converts a gain factor to the register with 200*log10, so
    // going back is a power of ten. 0 means unity gain.
    private const uint MaxGain = 800;             // 200 * log10(10000)
    private const uint MaxTriggerDelay = 2_000_000; // 2 s, the range the description gives

    // --------------------------------------------------------------- construction

    public GenieNano(string ip,
                     string xmlPath,
                     string serialNumber = "S1234567",
                     string deviceName = "Nano-M1920",
                     bool shareToNetwork = false,
                     string? imagePath = null)
        : this(BuildState(xmlPath, serialNumber, deviceName), ip, imagePath, shareToNetwork)
    {
    }

    private GenieNano(Built built, string ip, string? imagePath, bool shareToNetwork)
        : base(ip, built.State, Settings(built), imagePath, shareToNetwork)
    {
        built.State.OnWrite(AcquisitionStart, (_, _) => Transmitter.StartAcquisition());
        built.State.OnWrite(AcquisitionStop, (_, _) => Transmitter.StopAcquisition());
    }

    // --------------------------------------------------------------- registers

    internal static Built BuildState(string xmlPath,
                                     string serialNumber = "S1234567",
                                     string deviceName = "Nano-M1920")
    {
        if (!File.Exists(xmlPath))
            throw new FileNotFoundException($"genie nano device description not found: {xmlPath}");

        string xml = File.ReadAllText(xmlPath);

        DeviceState state = new(xml, Path.GetFileName(xmlPath), XmlAddress,
                                manufacturerName: "Teledyne DALSA",
                                modelName: "Nano-M1920",
                                deviceVersion: "1.0",
                                manufacturerInfo: "Genie Nano",
                                serialNumber: serialNumber,
                                deviceName: deviceName);

        TriggerGate gate = new();
        DefineFeatures(state, gate);

        // everything else the description declares. this runs second so the
        // registers above keep the hooks and defaults set for them.
        state.DefineFromXml(xml);

        state.GeometrySource(() => (state.ReadUint(Width), state.ReadUint(Height),
                                    state.ReadUint(PixelFormat)));

        state.RecomputePayload(SensorWidth, SensorHeight, GVSPPixelFormats.Mono8);

        return new Built(state, gate);
    }

    // the ones the description cannot pin down, and the ones that have to act.
    // width and height sit behind a formula that picks between a single-roi and a
    // multi-roi address, so they are declared at the single-roi one.
    private static void DefineFeatures(DeviceState state, TriggerGate gate)
    {
        Define(state, Width, SensorWidth, (_, v) => SetGeometry(state, width: Read(v)));
        Define(state, Height, SensorHeight, (_, v) => SetGeometry(state, height: Read(v)));

        Define(state, PixelFormat, GVSPPixelFormats.Mono8, (_, v) =>
            _pixelFormats.Contains(Read(v))
                ? SetGeometry(state, pixelFormat: Read(v))
                : GVCPStatus.GEV_STATUS_INVALID_PARAMETER);

        Define(state, FrameRate, 30 * MilliHertz, (_, v) =>
            InRange(Read(v), MinFrameRate, MaxFrameRate));

        Define(state, ExposureTime, (uint)ReferenceExposure, (_, v) =>
            InRange(Read(v), MinExposure, MaxExposure));

        Define(state, Gain, 0, (_, v) => InRange(Read(v), 0, MaxGain));

        // commands, which the device clears again once they have run
        Define(state, AcquisitionStart, 0, selfClearing: true);
        Define(state, AcquisitionStop, 0, selfClearing: true);
        Define(state, TriggerSoftware, 0, selfClearing: true, onWrite: (_, _) =>
        {
            gate.Fire();
            return GVCPStatus.GEV_STATUS_SUCCESS;
        });

        Define(state, AcquisitionMode, ContinuousMode, (_, v) =>
            Read(v) <= MultiFrameMode
                ? GVCPStatus.GEV_STATUS_SUCCESS
                : GVCPStatus.GEV_STATUS_INVALID_PARAMETER);

        Define(state, AcquisitionFrameCount, 1, (_, v) =>
            InRange(Read(v), 1, 65535));

        Define(state, TriggerMode, 0);
        Define(state, TriggerSource, SoftwareTrigger);
        Define(state, TriggerDelay, 0, (_, v) => InRange(Read(v), 0, MaxTriggerDelay));
    }

    // every register the nano owns is little-endian, and the byte order has to be
    // settled before the default value goes in
    private static void Define(DeviceState state, uint address, uint value,
                               Func<System.Net.IPEndPoint, byte[], ushort>? onWrite = null,
                               bool selfClearing = false) =>
        state.DefineUint(address, RegAccess.ReadWrite, value,
                         selfClearing: selfClearing, onWrite: onWrite,
                         endianness: Endianness.Little);

    private static ushort InRange(uint value, uint min, uint max) =>
        value >= min && value <= max
            ? GVCPStatus.GEV_STATUS_SUCCESS
            : GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

    private static ushort SetGeometry(DeviceState state, uint? width = null,
                                      uint? height = null, uint? pixelFormat = null)
    {
        uint w = width ?? state.ReadUint(Width);
        uint h = height ?? state.ReadUint(Height);

        if (w < SizeIncrement || w > SensorWidth || w % SizeIncrement != 0)
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        if (h < 1 || h > SensorHeight)
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        return state.RecomputePayload(w, h, pixelFormat ?? state.ReadUint(PixelFormat));
    }

    internal static StreamSettings Settings(Built built)
    {
        DeviceState state = built.State;

        return new StreamSettings(
            Width: () => (int)state.ReadUint(Width),
            Height: () => (int)state.ReadUint(Height),
            PixelFormat: () => state.ReadUint(PixelFormat),
            FrameRate: () => FrameRateCeiling(state))
        {
            Brightness = () => Brightness(state),

            // a source other than software means a cable we do not have, so the
            // device waits for a trigger that never comes. that is what a real
            // camera does with nothing plugged in.
            TriggerEnabled = () => state.ReadUint(TriggerMode) == TriggerOn,
            TakeTrigger = () => state.ReadUint(TriggerSource) == SoftwareTrigger && built.Gate.Take(),
            TriggerDelay = () => TimeSpan.FromMicroseconds(state.ReadUint(TriggerDelay)),
            BlockLimit = () => BlockLimit(state),
        };
    }

    // a sensor cannot produce frames faster than it takes to expose one, so a
    // long exposure holds the rate down however fast the register asks for
    internal static float FrameRateCeiling(DeviceState state)
    {
        float requested = state.ReadUint(FrameRate) / (float)MilliHertz;
        float ceiling = 1_000_000f / state.ReadUint(ExposureTime);

        return MathF.Min(requested, ceiling);
    }

    // how many blocks the current acquisition mode asks for. continuous streams
    // until stopped, so it asks for none in particular.
    internal static int BlockLimit(DeviceState state) => state.ReadUint(AcquisitionMode) switch
    {
        SingleFrameMode => 1,
        MultiFrameMode => (int)state.ReadUint(AcquisitionFrameCount),
        _ => 0,
    };

    // a sensor collects light in proportion to how long it is exposed, and gain
    // amplifies whatever it collected
    internal static float Brightness(DeviceState state)
    {
        float exposure = state.ReadUint(ExposureTime) / ReferenceExposure;
        float gain = MathF.Pow(10f, state.ReadUint(Gain) / 200f);

        return exposure * gain;
    }

    private static uint Read(byte[] value) => BinaryPrimitives.ReadUInt32LittleEndian(value);
}
