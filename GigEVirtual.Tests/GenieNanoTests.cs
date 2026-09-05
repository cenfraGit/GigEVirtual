// --------------------------------------------------------------------------------
// GenieNanoTests.cs
//
// the genie nano device. the real vendor description is not in the repo, so the
// fixture is a stand-in declaring the same port and one register: what is under
// test is the device's own wiring, not the vendor's file.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GigEVirtual;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace GigEVirtual.Tests;

[Collection(SocketTests.Name)]
public class GenieNanoTests : IDisposable
{
    private static readonly IPEndPoint Controller = new(IPAddress.Parse("127.0.0.1"), 50000);

    // addresses the device declares for itself, all little-endian
    private const uint Width = 0x20000070;
    private const uint Height = 0x20000090;
    private const uint PixelFormat = 0x20000060;
    private const uint FrameRate = 0x200000B0;
    private const uint ExposureTime = 0x20004BFC;
    private const uint Gain = 0x20001530;
    private const uint AcquisitionMode = 0x20000040;
    private const uint AcquisitionFrameCount = 0x20000050;
    private const uint TriggerMode = 0x20000F80;
    private const uint TriggerSource = 0x20001000;
    private const uint TriggerDelay = 0x200010C0;
    private const uint TriggerSoftware = 0x20001100;

    private readonly string _dir;
    private readonly string _xmlPath;
    private readonly GenieNano.Built _built;
    private readonly DeviceState _state;

    public GenieNanoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nano-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);

        _xmlPath = Path.Combine(_dir, "genie_nano.xml");
        File.WriteAllText(_xmlPath, Fixture);

        _built = GenieNano.BuildState(_xmlPath);
        _state = _built.State;
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _state.WriteRegister(Controller, 0x0A00, U32BE(2)));
    }

    public void Dispose()
    {
        _transmitter?.StopAcquisition();
        _receiver?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // stands in for the vendor file: same port, plus one register the device does
    // not declare itself, so the generated path is exercised as well
    private const string Fixture =
        """<?xml version="1.0" encoding="UTF-8"?>""" +
        """<RegisterDescription xmlns="http://www.genicam.org/GenApi/Version_1_1">""" +
        """<IntReg Name="DeviceTemperature"><Address>0x200001B0</Address><Length>4</Length>""" +
        """<AccessMode>RO</AccessMode><pPort>Device</pPort>""" +
        """<Endianess>LittleEndian</Endianess></IntReg>""" +
        """<Port Name="Device" /></RegisterDescription>""";

    private static byte[] U32BE(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    private static byte[] U32LE(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private ushort Write(uint address, uint value) =>
        _state.WriteRegister(Controller, address, U32LE(value));

    private ulong PayloadSize() =>
        ((ulong)_state.ReadUint(0x0D34) << 32) | _state.ReadUint(0x0D38);

    // --------------------------------------------------------------- the model

    [Fact]
    public void DefaultsToTheSensorSize()
    {
        Assert.Equal(1920u, _state.ReadUint(Width));
        Assert.Equal(1200u, _state.ReadUint(Height));
        Assert.Equal(GVSPPixelFormats.Mono8, _state.ReadUint(PixelFormat));
        Assert.Equal(1920u * 1200u, PayloadSize());
    }

    [Fact]
    public void FeatureRegistersAreLittleEndian()
    {
        // an application writes the low byte first, the way the description says
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            _state.WriteRegister(Controller, Width, [0x80, 0x02, 0x00, 0x00]));

        Assert.Equal(640u, _state.ReadUint(Width));
    }

    [Fact]
    public void GeometryChangesFollowThroughToThePayloadRegisters()
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(Width, 640));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(Height, 480));

        Assert.Equal(640u * 480u, PayloadSize());

        // twelve bits ride in a sixteen bit container
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(PixelFormat, GVSPPixelFormats.Mono12));
        Assert.Equal(640u * 480u * 2, PayloadSize());
    }

    [Fact]
    public void GeometryBeyondTheSensorIsRefused()
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, Write(Width, 4096));
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, Write(Height, 4096));

        Assert.Equal(1920u, _state.ReadUint(Width));
        Assert.Equal(1200u, _state.ReadUint(Height));
    }

    [Fact]
    public void AMonoModelRefusesColourFormats()
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER,
            Write(PixelFormat, GVSPPixelFormats.BayerRG8));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(PixelFormat, GVSPPixelFormats.Mono12));
    }

    [Fact]
    public void FrameRateIsStoredInMilliHertz()
    {
        Assert.Equal(30_000u, _state.ReadUint(FrameRate)); // 30 Hz

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(FrameRate, 12_500));
        Assert.Equal(12.5f, GenieNano.Settings(_built).FrameRate());
    }

    [Fact]
    public void ImplausibleFrameRatesAndExposuresAreRefused()
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, Write(FrameRate, 0));
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, Write(ExposureTime, 0));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(ExposureTime, 5_000));
        Assert.Equal(5_000u, _state.ReadUint(ExposureTime));
    }

    // --------------------------------------------------------------- exposure and gain

    [Fact]
    public void BrightnessIsUnchangedAtTheDefaults()
    {
        Assert.Equal(1.0f, GenieNano.Brightness(_state), 3);
    }

    [Fact]
    public void ExposingLongerBrightensAndShorterDarkens()
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(ExposureTime, 20_000));
        Assert.Equal(2.0f, GenieNano.Brightness(_state), 3);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(ExposureTime, 5_000));
        Assert.Equal(0.5f, GenieNano.Brightness(_state), 3);
    }

    [Fact]
    public void GainAmplifiesOnTopOfExposure()
    {
        // the description stores gain as 200 * log10(factor), so 200 is ten times
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(Gain, 200));
        Assert.Equal(10.0f, GenieNano.Brightness(_state), 2);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(ExposureTime, 5_000));
        Assert.Equal(5.0f, GenieNano.Brightness(_state), 2);
    }

    [Fact]
    public void BrightnessReachesTheFrame()
    {
        // a flat grey image, so every pixel answers the same question. the
        // generated pattern would not do: it moves between frames.
        string path = Path.Combine(_dir, "grey.png");
        using (var image = new Image<L8>(16, 16, new L8(100)))
            image.SaveAsPng(path);

        var source = new ImageSource(path);

        Assert.Equal(100, source.NextFrame(16, 16, GVSPPixelFormats.Mono8, 1.0f)[0]);
        Assert.Equal(50, source.NextFrame(16, 16, GVSPPixelFormats.Mono8, 0.5f)[0]);
        Assert.Equal(200, source.NextFrame(16, 16, GVSPPixelFormats.Mono8, 2.0f)[0]);

        // a real sensor saturates rather than wrapping round
        Assert.Equal(255, source.NextFrame(16, 16, GVSPPixelFormats.Mono8, 8.0f)[0]);
    }

    // --------------------------------------------------------------- the description

    [Fact]
    public void RegistersTheDescriptionDeclaresAreReachableToo()
    {
        // this one comes from the fixture rather than the device's own list
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _state.ReadRegister(0x200001B0, out _));
    }

    [Fact]
    public void TheDescriptionIsServedFromItsOwnAddress()
    {
        _state.ReadMemory(0x0200, 512, out byte[]? url);
        string firstUrl = Encoding.ASCII.GetString(url!).TrimEnd((char)0);

        Assert.StartsWith("Local:genie_nano.xml;f0000000;", firstUrl);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _state.ReadMemory(0xF0000000, 64, out _));
    }

    [Fact]
    public void AMissingDescriptionFailsAtConstruction()
    {
        Assert.Throws<FileNotFoundException>(
            () => GenieNano.BuildState(Path.Combine(_dir, "nope.xml")));
    }

    // --------------------------------------------------------------- timing

    [Fact]
    public void ALongExposureHoldsTheFrameRateDown()
    {
        // 100 ms of exposure cannot produce more than ten frames a second, no
        // matter what the frame rate register asks for
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(ExposureTime, 100_000));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(FrameRate, 60_000));

        Assert.Equal(10.0f, GenieNano.FrameRateCeiling(_state), 2);

        // a short exposure leaves the register in charge again
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(ExposureTime, 1_000));
        Assert.Equal(60.0f, GenieNano.FrameRateCeiling(_state), 2);
    }

    [Fact]
    public void AcquisitionModeDecidesHowManyBlocksARunSends()
    {
        Assert.Equal(0, GenieNano.BlockLimit(_state)); // continuous, no limit

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(AcquisitionMode, 1));
        Assert.Equal(1, GenieNano.BlockLimit(_state));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(AcquisitionMode, 2));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(AcquisitionFrameCount, 7));
        Assert.Equal(7, GenieNano.BlockLimit(_state));
    }

    [Fact]
    public void AnUnknownAcquisitionModeIsRefused()
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, Write(AcquisitionMode, 9));
    }

    // --------------------------------------------------------------- streaming

    private UdpClient? _receiver;
    private GVSPTransmitter? _transmitter;

    // a transmitter pointed at a loopback receiver, with a small frame so the
    // tests stay quick
    private GVSPTransmitter Streaming()
    {
        _receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)_receiver.Client.LocalEndPoint!).Port;

        Write(Width, 64);
        Write(Height, 48);

        _state.WriteRegister(Controller, 0x0D18, U32BE(0x7F000001));
        _state.WriteRegister(Controller, 0x0D00, U32BE((uint)port));

        _transmitter = new GVSPTransmitter(_state, IPAddress.Loopback, new ImageSource(),
                                           GenieNano.Settings(_built));
        return _transmitter;
    }

    // leaders seen in the window. one leader is one block.
    private int Blocks(TimeSpan window)
    {
        int count = 0;
        _receiver!.Client.ReceiveTimeout = 100;
        IPEndPoint from = new(IPAddress.Any, 0);
        DateTime until = DateTime.UtcNow + window;

        while (DateTime.UtcNow < until)
        {
            try
            {
                byte[] packet = _receiver.Receive(ref from);
                if ((packet[4] & 0x0F) == 1) count++;
            }
            catch (SocketException) { }
        }

        return count;
    }

    [Fact]
    public void ItStreamsItsOwnGeometry()
    {
        GVSPTransmitter transmitter = Streaming();
        Write(FrameRate, 50_000);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, transmitter.StartAcquisition());

        var packets = new List<byte[]>();
        _receiver!.Client.ReceiveTimeout = 200;
        IPEndPoint from = new(IPAddress.Any, 0);
        DateTime until = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);

        while (DateTime.UtcNow < until)
        {
            try { packets.Add(_receiver.Receive(ref from)); }
            catch (SocketException) { }
        }

        transmitter.StopAcquisition();

        byte[][] leaders = [.. packets.Where(p => (p[4] & 0x0F) == 1)];
        Assert.NotEmpty(leaders);

        // the leader carries what the nano registers say
        Assert.Equal(GVSPPixelFormats.Mono8, BinaryPrimitives.ReadUInt32BigEndian(leaders[0].AsSpan(32, 4)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32BigEndian(leaders[0].AsSpan(36, 4)));
        Assert.Equal(48u, BinaryPrimitives.ReadUInt32BigEndian(leaders[0].AsSpan(40, 4)));
    }

    // --------------------------------------------------------------- trigger

    [Fact]
    public void WithTheTriggerOnNothingArrivesUntilItFires()
    {
        GVSPTransmitter transmitter = Streaming();
        Write(FrameRate, 50_000);
        Write(TriggerMode, 1);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, transmitter.StartAcquisition());

        // free-running it would have sent about twenty blocks by now
        Assert.Equal(0, Blocks(TimeSpan.FromMilliseconds(400)));

        transmitter.StopAcquisition();
    }

    [Fact]
    public void EachSoftwareTriggerProducesExactlyOneBlock()
    {
        GVSPTransmitter transmitter = Streaming();
        Write(TriggerMode, 1);
        transmitter.StartAcquisition();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(TriggerSoftware, 1));
        Assert.Equal(1, Blocks(TimeSpan.FromMilliseconds(300)));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Write(TriggerSoftware, 1));
        Assert.Equal(1, Blocks(TimeSpan.FromMilliseconds(300)));

        // and nothing more without another one
        Assert.Equal(0, Blocks(TimeSpan.FromMilliseconds(300)));

        transmitter.StopAcquisition();
    }

    [Fact]
    public void ATriggerSourceWeHaveNoCableForNeverFires()
    {
        GVSPTransmitter transmitter = Streaming();
        Write(TriggerMode, 1);
        Write(TriggerSource, 6); // Line1

        transmitter.StartAcquisition();
        Write(TriggerSoftware, 1);

        Assert.Equal(0, Blocks(TimeSpan.FromMilliseconds(300)));

        transmitter.StopAcquisition();
    }

    [Fact]
    public void TheTriggerDelayHoldsTheBlockBack()
    {
        GVSPTransmitter transmitter = Streaming();
        Write(TriggerMode, 1);
        Write(TriggerDelay, 400_000); // 400 ms

        transmitter.StartAcquisition();
        Write(TriggerSoftware, 1);

        Assert.Equal(0, Blocks(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, Blocks(TimeSpan.FromMilliseconds(500)));

        transmitter.StopAcquisition();
    }

    // --------------------------------------------------------------- acquisition mode

    [Fact]
    public void SingleFrameSendsOneBlockAndStops()
    {
        GVSPTransmitter transmitter = Streaming();
        Write(FrameRate, 50_000);
        Write(AcquisitionMode, 1);

        transmitter.StartAcquisition();

        Assert.Equal(1, Blocks(TimeSpan.FromMilliseconds(400)));

        // the run ended by itself, so the device is idle again
        Assert.False(transmitter.IsStreaming);
    }

    [Fact]
    public void MultiFrameSendsTheCountItWasGiven()
    {
        GVSPTransmitter transmitter = Streaming();
        Write(FrameRate, 50_000);
        Write(AcquisitionMode, 2);
        Write(AcquisitionFrameCount, 5);

        transmitter.StartAcquisition();

        Assert.Equal(5, Blocks(TimeSpan.FromMilliseconds(700)));
        Assert.False(transmitter.IsStreaming);
    }
}
