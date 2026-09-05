// --------------------------------------------------------------------------------
// GigECamera.cs
//
// the simple virtual camera. its feature registers live in one block starting at
// 0xA000, laid out to match GigEVirtual.xml.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;

namespace GigEVirtual;

public class GigECamera : GigEDevice
{
    // where this camera keeps its features. a device is free to choose these,
    // they just have to agree with its description file.
    private const uint Width = 0xA000;
    private const uint Height = 0xA004;
    private const uint PixelFormat = 0xA008;
    private const uint AcquisitionStart = 0xA00C;
    private const uint AcquisitionStop = 0xA010;
    private const uint AcquisitionMode = 0xA014;
    private const uint FrameRate = 0xA018;

    private const uint XmlAddress = 0xA200;
    private const string XmlFileName = "GigEVirtual.xml";

    private const uint MinWidth = 64;
    private const uint WidthIncrement = 4;
    private const float MinFrameRate = 0.1f;
    private const float MaxFrameRate = 1000.0f;

    public GigECamera(string ip,
                      string manufacturerName = "fromVirtual",
                      string modelName = "modelVirtual",
                      string deviceVersion = "1.0",
                      string manufacturerInfo = "C# cam",
                      string serialNumber = "S0001",
                      string deviceName = "virtualCam",
                      bool shareToNetwork = false,
                      string? imagePath = null)
        : this(BuildState(manufacturerName, modelName, deviceVersion,
                          manufacturerInfo, serialNumber, deviceName),
               ip, imagePath, shareToNetwork)
    {
    }

    private GigECamera(DeviceState state, string ip, string? imagePath, bool shareToNetwork)
        : base(ip, state, Settings(state), imagePath, shareToNetwork)
    {
        // these need the transmitter, so they cannot be wired until the base has
        // built one. everything else about the registers is already in place.
        state.OnWrite(AcquisitionStart, (_, _) => Transmitter.StartAcquisition());
        state.OnWrite(AcquisitionStop, (_, _) => Transmitter.StopAcquisition());
    }

    // --------------------------------------------------------------- registers

    internal static DeviceState BuildState(string manufacturerName = "fromVirtual",
                                           string modelName = "modelVirtual",
                                           string deviceVersion = "1.0",
                                           string manufacturerInfo = "C# cam",
                                           string serialNumber = "S0001",
                                           string deviceName = "virtualCam")
    {
        string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, XmlFileName));

        DeviceState state = new(xml, XmlFileName, XmlAddress,
                                manufacturerName, modelName, deviceVersion,
                                manufacturerInfo, serialNumber, deviceName);

        state.DefineUint(Width, RegAccess.ReadWrite, 640, onWrite: (_, v) =>
            SetGeometry(state, width: ReadU32(v)));

        state.DefineUint(Height, RegAccess.ReadWrite, 480, onWrite: (_, v) =>
            SetGeometry(state, height: ReadU32(v)));

        state.DefineUint(PixelFormat, RegAccess.ReadWrite, GVSPPixelFormats.Mono8, onWrite: (_, v) =>
            SetGeometry(state, pixelFormat: ReadU32(v)));

        state.DefineUint(AcquisitionStart, RegAccess.ReadWrite, 0, selfClearing: true);
        state.DefineUint(AcquisitionStop, RegAccess.ReadWrite, 0, selfClearing: true);
        state.DefineUint(AcquisitionMode, RegAccess.ReadWrite, 0); // 0 = continuous

        // frames per second. a float because that is how cameras expose it, and
        // the transmitter re-reads it every block so it can change mid-stream
        state.DefineFloat(FrameRate, RegAccess.ReadWrite, 10.0f, onWrite: (_, v) =>
        {
            float rate = BinaryPrimitives.ReadSingleBigEndian(v);

            return float.IsFinite(rate) && rate >= MinFrameRate && rate <= MaxFrameRate
                ? GVCPStatus.GEV_STATUS_SUCCESS
                : GVCPStatus.GEV_STATUS_INVALID_PARAMETER;
        });

        state.GeometrySource(() => (state.ReadUint(Width), state.ReadUint(Height),
                                    state.ReadUint(PixelFormat)));

        // seed the payload registers from the defaults above
        state.RecomputePayload(640, 480, GVSPPixelFormats.Mono8);

        return state;
    }

    // one geometry register is changing, the others keep their stored value.
    // whichever it is, the payload registers have to follow.
    private static ushort SetGeometry(DeviceState state, uint? width = null,
                                      uint? height = null, uint? pixelFormat = null)
    {
        uint w = width ?? state.ReadUint(Width);
        uint h = height ?? state.ReadUint(Height);

        if (w < MinWidth || w % WidthIncrement != 0 || h < 1)
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        return state.RecomputePayload(w, h, pixelFormat ?? state.ReadUint(PixelFormat));
    }

    internal static StreamSettings Settings(DeviceState state) => new(
        Width: () => (int)state.ReadUint(Width),
        Height: () => (int)state.ReadUint(Height),
        PixelFormat: () => state.ReadUint(PixelFormat),
        FrameRate: () => state.ReadFloat(FrameRate));

    private static uint ReadU32(byte[] value) => BinaryPrimitives.ReadUInt32BigEndian(value);
}
