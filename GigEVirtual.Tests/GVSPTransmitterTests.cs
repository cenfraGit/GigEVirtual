// --------------------------------------------------------------------------------
// GVSPTransmitterTests.cs
//
// drives the transmitter at a real socket on loopback, so the things a register
// test cannot see (pacing, packet layout, block ids) are actually checked.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GigEVirtual;
using Xunit;

namespace GigEVirtual.Tests;

public class GVSPTransmitterTests : IDisposable
{
    private static readonly IPEndPoint Controller = new(IPAddress.Parse("127.0.0.1"), 50000);

    private readonly UdpClient _receiver;
    private readonly DeviceState _state;
    private readonly GVSPTransmitter _transmitter;

    public GVSPTransmitterTests()
    {
        _receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)_receiver.Client.LocalEndPoint!).Port;

        _state = new DeviceState();
        _transmitter = new GVSPTransmitter(_state, IPAddress.Loopback, new ImageSource());

        Write(0x0A00, 2);              // take control
        Write(0x0D18, 0x7F000001);     // SCDA0, 127.0.0.1
        Write(0x0D00, (uint)port);     // SCP0
        Write(0xA000, 64);             // a small frame keeps the tests quick
        Write(0xA004, 48);
    }

    public void Dispose()
    {
        _transmitter.StopAcquisition();
        _receiver.Dispose();
    }

    private void Write(uint address, uint value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _state.WriteRegister(Controller, address, buffer));
    }

    private void WriteFloat(uint address, float value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _state.WriteRegister(Controller, address, buffer));
    }

    // every packet that arrives within the window
    private List<byte[]> Collect(TimeSpan window)
    {
        var packets = new List<byte[]>();
        _receiver.Client.ReceiveTimeout = 200;

        DateTime until = DateTime.UtcNow + window;
        IPEndPoint from = new(IPAddress.Any, 0);

        while (DateTime.UtcNow < until)
        {
            try { packets.Add(_receiver.Receive(ref from)); }
            catch (SocketException) { } // nothing arrived before the timeout
        }

        return packets;
    }

    // byte 4 holds the extended id flag and the packet format in its low nibble:
    // 1 leader, 2 trailer, 3 payload. block_id64 starts at byte 8.
    private static int PacketFormat(byte[] packet) => packet[4] & 0x0F;

    private static ulong BlockId(byte[] packet) =>
        BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8, 8));

    // --------------------------------------------------------------- block structure

    [Fact]
    public void EachBlockIsALeaderThenPayloadsThenATrailer()
    {
        WriteFloat(0xA018, 20.0f);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _transmitter.StartAcquisition());

        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(400));
        _transmitter.StopAcquisition();

        Assert.NotEmpty(packets);

        // take the first complete block we saw
        int leader = packets.FindIndex(p => PacketFormat(p) == 1);
        int trailer = packets.FindIndex(leader + 1, p => PacketFormat(p) == 2);

        Assert.True(leader >= 0 && trailer > leader, "no complete block arrived");

        // everything between them is payload, and it is all one block
        ulong block = BlockId(packets[leader]);
        for (int i = leader + 1; i < trailer; i++)
        {
            Assert.Equal(3, PacketFormat(packets[i]));
            Assert.Equal(block, BlockId(packets[i]));
        }

        Assert.Equal(block, BlockId(packets[trailer]));
    }

    [Fact]
    public void PayloadPacketsAddUpToTheAdvertisedPayloadSize()
    {
        WriteFloat(0xA018, 20.0f);
        _transmitter.StartAcquisition();

        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(400));
        _transmitter.StopAcquisition();

        int leader = packets.FindIndex(p => PacketFormat(p) == 1);
        int trailer = packets.FindIndex(leader + 1, p => PacketFormat(p) == 2);
        Assert.True(leader >= 0 && trailer > leader, "no complete block arrived");

        // 20 bytes of gvsp header on each payload packet
        int bytes = packets.Skip(leader + 1).Take(trailer - leader - 1).Sum(p => p.Length - 20);

        Assert.Equal(64 * 48, bytes);
    }

    [Fact]
    public void BlockIdsIncrementByOne()
    {
        WriteFloat(0xA018, 50.0f);
        _transmitter.StartAcquisition();

        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(400));
        _transmitter.StopAcquisition();

        ulong[] leaders = [.. packets.Where(p => PacketFormat(p) == 1).Select(BlockId)];
        Assert.True(leaders.Length >= 2, $"only saw {leaders.Length} leaders");

        for (int i = 1; i < leaders.Length; i++)
            Assert.Equal(leaders[i - 1] + 1, leaders[i]);
    }

    [Fact]
    public void LeaderCarriesTheGeometryAndAMovingTimestamp()
    {
        WriteFloat(0xA018, 50.0f);
        _transmitter.StartAcquisition();

        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(400));
        _transmitter.StopAcquisition();

        byte[][] leaders = [.. packets.Where(p => PacketFormat(p) == 1)];
        Assert.True(leaders.Length >= 2, $"only saw {leaders.Length} leaders");

        // payload_type, then timestamp, pixel format, size_x, size_y
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(leaders[0].AsSpan(22, 2)));
        Assert.Equal(GVSPPixelFormats.Mono8, BinaryPrimitives.ReadUInt32BigEndian(leaders[0].AsSpan(32, 4)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32BigEndian(leaders[0].AsSpan(36, 4)));
        Assert.Equal(48u, BinaryPrimitives.ReadUInt32BigEndian(leaders[0].AsSpan(40, 4)));

        ulong first = BinaryPrimitives.ReadUInt64BigEndian(leaders[0].AsSpan(24, 8));
        ulong second = BinaryPrimitives.ReadUInt64BigEndian(leaders[1].AsSpan(24, 8));

        Assert.True(first > 0, "leader timestamp was zero");
        Assert.True(second > first, "leader timestamp did not advance");
    }

    // --------------------------------------------------------------- frame rate

    [Theory]
    [InlineData(10.0f)]
    [InlineData(40.0f)]
    public void BlocksArriveAtRoughlyTheRequestedRate(float rate)
    {
        WriteFloat(0xA018, rate);
        _transmitter.StartAcquisition();

        var window = TimeSpan.FromMilliseconds(1000);
        List<byte[]> packets = Collect(window);
        _transmitter.StopAcquisition();

        int blocks = packets.Count(p => PacketFormat(p) == 1);

        // generous either way, this is wall-clock on a machine running other tests
        Assert.InRange(blocks, rate * 0.4, rate * 1.6);
    }

    [Fact]
    public void ChangingTheRateWhileStreamingTakesEffect()
    {
        WriteFloat(0xA018, 5.0f);
        _transmitter.StartAcquisition();

        int slow = Collect(TimeSpan.FromMilliseconds(600)).Count(p => PacketFormat(p) == 1);

        WriteFloat(0xA018, 50.0f);
        int fast = Collect(TimeSpan.FromMilliseconds(600)).Count(p => PacketFormat(p) == 1);

        _transmitter.StopAcquisition();

        Assert.True(fast > slow * 2, $"saw {slow} blocks slow and {fast} fast");
    }

    // --------------------------------------------------------------- stream channel

    [Fact]
    public void StartingWithoutADestinationIsRefused()
    {
        Write(0x0D00, 0); // close the stream channel

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, _transmitter.StartAcquisition());
    }

    [Fact]
    public void StartingTwiceReportsBusy()
    {
        _transmitter.StartAcquisition();

        Assert.Equal(GVCPStatus.GEV_STATUS_BUSY, _transmitter.StartAcquisition());
    }

    [Fact]
    public void TestPacketIsTheRequestedSizeAndCarriesLfsrData()
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _transmitter.SendTestPacket(1500));

        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(300));
        Assert.Single(packets);

        // the requested size counts the ip and udp headers, which are not in
        // what we receive here
        Assert.Equal(1500 - 20 - 8, packets[0].Length);

        // block_id of 0 is what marks a test packet
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packets[0].AsSpan(2, 2)));

        byte[] expected = new byte[packets[0].Length - 8];
        GVSPTransmitter.FillLfsr(expected);
        Assert.Equal(expected, packets[0].Skip(8));
    }
}