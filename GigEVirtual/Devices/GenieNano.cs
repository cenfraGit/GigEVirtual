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
    private const uint FrameRateMin = 0x200000B4;
    private const uint FrameRateMax = 0x200000B8;

    // the description hides the frame rate entirely unless this is on, so a
    // device that free-runs at a set rate comes up with it already on
    private const uint FrameRateEnable = 0x20001FD0;
    private const uint TriggerMode = 0x20000F80;
    private const uint TriggerSource = 0x20001000;
    private const uint TriggerDelay = 0x200010C0; // microseconds
    private const uint TriggerSoftware = 0x20001100;
    private const uint ExposureTime = 0x20004BFC; // microseconds
    private const uint Gain = 0x20001530;         // 200 * log10(factor)

    // the description points every feature's min, max and increment at a register
    // of its own, and an application refuses to write a feature whose bounds read
    // zero. these are those registers, all found by resolving the pMin and pMax
    // chains in the description back to addresses.
    private const uint WidthMin = 0x20000074;
    private const uint WidthMax = 0x20000078;
    private const uint WidthInc = 0x2000007C;
    private const uint HeightMin = 0x20000094;
    private const uint HeightMax = 0x20000098;
    private const uint HeightInc = 0x2000009C;
    private const uint ExposureMin = 0x20004BF4;
    private const uint ExposureMax = 0x20004BF8;
    private const uint ExposureMaxLive = 0x20001FE0; // the limit while streaming
    private const uint GainMin = 0x20001534;
    private const uint GainMax = 0x20001538;
    private const uint FrameCountMin = 0x20000054;
    private const uint FrameCountMax = 0x20000058;
    private const uint BinningMaxX = 0x20003BFC;
    private const uint BinningMaxY = 0x20002A40;

    // what the sensor can actually do. the description reads these to work out
    // which features exist at all, and every register it generates itself starts
    // at zero, which reads as a camera that can do nothing.
    private const uint PixelFormatCaps = 0x20001120;   // one bit per format
    private const uint EffectiveBinningX = 0x20002A50;
    private const uint EffectiveBinningY = 0x20002A60;

    // mono8, mono10 and mono12, which are bits 0, 1 and 2
    private const uint MonoFormats = 0b111;

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
    private const uint Line1Trigger = 6;
    private const uint Line2Trigger = 7;

    private const uint MinFrameRate = 100;        // 0.1 Hz
    private const uint MaxFrameRate = 1000 * MilliHertz;
    private const uint DefaultFrameRate = 30 * MilliHertz;
    private const uint MinExposure = 1;
    private const uint MaxExposure = 2_000_000;   // 2 s, matching the trigger delay range

    // what the image on disk is taken to already represent. exposing longer than
    // this brightens the frame, shorter darkens it.
    private const float ReferenceExposure = 10_000f;

    // the description converts a gain factor to the register with 200*log10, so
    // going back is a power of ten. 0 means unity gain.
    private const uint MaxGain = 800;             // 200 * log10(10000)
    private const uint MaxTriggerDelay = 2_000_000; // 2 s, the range the description gives
    private const uint MaxFrameCount = 65535;

    // --------------------------------------------------------------- construction

    private readonly Built _built;

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
        _built = built;

        built.State.OnWrite(AcquisitionStart, (_, _) => Transmitter.StartAcquisition());
        built.State.OnWrite(AcquisitionStop, (_, _) => Transmitter.StopAcquisition());
    }

    // --------------------------------------------------------------- the wiring

    // brings the device up already waiting on one of its input lines, which is
    // what a real nano does when it boots with a user set that says so. line 1
    // or line 2.
    public void UseHardwareTrigger(int line)
    {
        State.WriteUint(TriggerSource, LineTrigger(line));
        State.WriteUint(TriggerMode, TriggerOn);
    }

    // stands in for a signal arriving on the connector. there is no connector,
    // so whatever calls this is the cable. false means the device is listening
    // to a different source and the pulse went nowhere, exactly as it would.
    public bool PulseLine(int line) => Pulse(_built, line);

    internal static bool Pulse(Built built, int line)
    {
        if (built.State.ReadUint(TriggerSource) != LineTrigger(line)) return false;

        built.Gate.Fire();
        return true;
    }

    private static uint LineTrigger(int line) => line switch
    {
        1 => Line1Trigger,
        2 => Line2Trigger,
        _ => throw new ArgumentOutOfRangeException(nameof(line), "the nano has line 1 and line 2"),
    };

    // --------------------------------------------------------------- registers

    internal static Built BuildState(string xmlPath,
                                     string serialNumber = "S1234567",
                                     string deviceName = "Nano-M1920")
    {
        if (!File.Exists(xmlPath))
            throw new FileNotFoundException($"genie nano device description not found: {xmlPath}");

        byte[] xml = File.ReadAllBytes(xmlPath);

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
        state.DefineFromXml(System.Text.Encoding.UTF8.GetString(xml));

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

        Define(state, FrameRate, DefaultFrameRate, (_, v) =>
        {
            if (InRange(Read(v), MinFrameRate, MaxFrameRate) != GVCPStatus.GEV_STATUS_SUCCESS)
                return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

            // a frame cannot be exposed for longer than it lasts
            state.WriteUint(ExposureMaxLive, FramePeriod(Read(v)));

            return GVCPStatus.GEV_STATUS_SUCCESS;
        });

        Define(state, ExposureTime, (uint)ReferenceExposure, (_, v) =>
        {
            if (InRange(Read(v), MinExposure, MaxExposure) != GVCPStatus.GEV_STATUS_SUCCESS)
                return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

            // a sensor cannot produce frames faster than it takes to expose one,
            // and this register is how an application finds that limit out
            state.WriteUint(FrameRateMax, Math.Min(MaxFrameRate, 1_000_000_000 / Read(v)));

            return GVCPStatus.GEV_STATUS_SUCCESS;
        });

        Define(state, Gain, 0, (_, v) => InRange(Read(v), 0, MaxGain));

        // commands, which the device clears again once they have run
        Define(state, AcquisitionStart, 0, selfClearing: true);
        Define(state, AcquisitionStop, 0, selfClearing: true);
        // the description is explicit that this one fires "no matter what the
        // TriggerSource feature is set to", so it does not consult the source
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
            InRange(Read(v), 1, MaxFrameCount));

        Define(state, TriggerMode, 0);
        Define(state, FrameRateEnable, 1);

        DefineReadOnly(state, PixelFormatCaps, MonoFormats);
        DefineReadOnly(state, FrameRateMin, MinFrameRate);
        DefineReadOnly(state, FrameRateMax, MaxFrameRate);
        DefineReadOnly(state, EffectiveBinningX, 1);
        DefineReadOnly(state, EffectiveBinningY, 1);

        // binning is not modelled, so one is the only factor on offer. the
        // description divides by these, so zero is not a harmless default.
        DefineReadOnly(state, BinningMaxX, 1);
        DefineReadOnly(state, BinningMaxY, 1);

        DefineReadOnly(state, WidthMin, SizeIncrement);
        DefineReadOnly(state, WidthMax, SensorWidth);
        DefineReadOnly(state, WidthInc, SizeIncrement);
        DefineReadOnly(state, HeightMin, 1);
        DefineReadOnly(state, HeightMax, SensorHeight);
        DefineReadOnly(state, HeightInc, 1);
        DefineReadOnly(state, ExposureMin, MinExposure);
        DefineReadOnly(state, ExposureMax, MaxExposure);
        DefineReadOnly(state, ExposureMaxLive, FramePeriod(DefaultFrameRate));
        DefineReadOnly(state, GainMin, 0);
        DefineReadOnly(state, GainMax, MaxGain);
        DefineReadOnly(state, FrameCountMin, 1);
        DefineReadOnly(state, FrameCountMax, MaxFrameCount);
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

    // how long one frame lasts at a given rate, in microseconds
    private static uint FramePeriod(uint frameRate) =>
        Math.Min(MaxExposure, 1_000_000_000 / frameRate);

    private static void DefineReadOnly(DeviceState state, uint address, uint value) =>
        state.DefineUint(address, RegAccess.ReadOnly, value, endianness: Endianness.Little);

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

            // whoever fired the gate already checked they were the selected
            // source, so by here a waiting trigger is a real one
            TriggerEnabled = () => state.ReadUint(TriggerMode) == TriggerOn,
            TakeTrigger = () => built.Gate.Take(),
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
