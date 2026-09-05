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

[Collection(SocketTests.Name)]
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

        _state = TestDevice.Camera();
        _transmitter = new GVSPTransmitter(_state, IPAddress.Loopback, new ImageSource(),
                                          GigECamera.Settings(_state));

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

    private static uint PacketId(byte[] packet) =>
        BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16, 4));

    private static ushort Status(byte[] packet) =>
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2));

    // bit 15 of the flag field, which the spec numbers from the msb
    private static bool IsResend(byte[] packet) => (packet[3] & 0x01) != 0;

    // streams slowly enough that one block goes out and the next is far away,
    // then hands back the packets of that block
    private List<byte[]> OneBlock()
    {
        WriteFloat(0xA018, 2.0f);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _transmitter.StartAcquisition());

        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(300));

        Assert.NotEmpty(packets);
        Assert.Equal(1u, (uint)BlockId(packets[0]));

        return packets;
    }

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
    public void StoppingEndsTheStream()
    {
        WriteFloat(0xA018, 50.0f);
        _transmitter.StartAcquisition();

        Assert.NotEmpty(Collect(TimeSpan.FromMilliseconds(300)));

        _transmitter.StopAcquisition();
        Collect(TimeSpan.FromMilliseconds(200)); // drain anything already in flight

        Assert.Empty(Collect(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void StartingAndStoppingRepeatedlyStaysHealthy()
    {
        // stopping disposes the socket while the streaming task may be mid-send,
        // so hammer that boundary
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _transmitter.StartAcquisition());
            Thread.Sleep(5);
            Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _transmitter.StopAcquisition());
        }

        // and it still streams afterwards
        WriteFloat(0xA018, 50.0f);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _transmitter.StartAcquisition());

        Assert.NotEmpty(Collect(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void IsStreamingTracksAcquisition()
    {
        Assert.False(_transmitter.IsStreaming);

        _transmitter.StartAcquisition();
        Assert.True(_transmitter.IsStreaming);

        _transmitter.StopAcquisition();
        Assert.False(_transmitter.IsStreaming);
    }

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

    // --------------------------------------------------------------- packet resend

    [Fact]
    public void AResentPacketComesBackMarkedAsOne()
    {
        List<byte[]> first = OneBlock();

        _transmitter.Resend(0, 1, 2, 3);
        List<byte[]> again = Collect(TimeSpan.FromMilliseconds(250));

        byte[][] resent = again.Where(IsResend).ToArray();

        Assert.Equal(2, resent.Length);
        Assert.Equal([2u, 3u], resent.Select(PacketId));
        Assert.All(resent, p => Assert.Equal(1ul, BlockId(p)));
        Assert.All(resent, p => Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, Status(p)));

        // and byte for byte what went out the first time, bar the flag
        byte[] original = first.Single(p => BlockId(p) == 1 && PacketId(p) == 2);
        Assert.Equal(original.Length, resent[0].Length);
        Assert.Equal(original.AsSpan(4).ToArray(), resent[0].AsSpan(4).ToArray());
    }

    // the reason resend exists, and the reason the device can be told to drop
    [Fact]
    public void DroppedPacketsNeverArriveAndCanBeAskedForAgain()
    {
        _transmitter.DropOneIn = 3;

        List<byte[]> packets = OneBlock();
        uint[] arrived = packets.Where(p => BlockId(p) == 1).Select(PacketId).ToArray();

        Assert.DoesNotContain(3u, arrived);
        Assert.DoesNotContain(6u, arrived);

        // the leader is never dropped, since a receiver cannot start without it
        Assert.Contains(0u, arrived);

        _transmitter.Resend(0, 1, 3, 3);
        byte[][] resent = Collect(TimeSpan.FromMilliseconds(250)).Where(IsResend).ToArray();

        Assert.Single(resent);
        Assert.Equal(3u, PacketId(resent[0]));
    }

    [Fact]
    public void AllOnesAsksForEverythingUpToTheTrailer()
    {
        List<byte[]> packets = OneBlock();
        int sent = packets.Count(p => BlockId(p) == 1);

        _transmitter.Resend(0, 1, 0, uint.MaxValue);
        byte[][] resent = Collect(TimeSpan.FromMilliseconds(400)).Where(IsResend).ToArray();

        Assert.Equal(sent, resent.Length);
        Assert.Equal(1, PacketFormat(resent[0]));            // leader
        Assert.Equal(2, PacketFormat(resent[^1]));           // trailer
    }

    // a packet the transmitter cannot produce still gets an answer: a header on
    // its own, saying why
    [Fact]
    public void ABlockThatHasNotHappenedYetIsRefused()
    {
        OneBlock();

        _transmitter.Resend(0, 4096, 0, 0);
        byte[][] refused = Collect(TimeSpan.FromMilliseconds(250)).Where(IsResend).ToArray();

        Assert.Single(refused);
        Assert.Equal(20, refused[0].Length);
        Assert.Equal(GVCPStatus.GEV_STATUS_PACKET_NOT_YET_AVAILABLE, Status(refused[0]));
    }

    [Fact]
    public void ABlockTheTransmitterHasThrownAwayIsRefused()
    {
        WriteFloat(0xA018, 60.0f);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _transmitter.StartAcquisition());

        // long enough that block 1 has been pushed out of what is kept
        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(400));
        Assert.True(packets.Max(BlockId) > 3, "not enough blocks went out");

        _transmitter.Resend(0, 1, 0, 0);
        byte[][] refused = Collect(TimeSpan.FromMilliseconds(250)).Where(IsResend).ToArray();

        Assert.NotEmpty(refused);
        Assert.Equal(GVCPStatus.GEV_STATUS_PACKET_AND_PREV_REMOVED_FROM_MEMORY, Status(refused[0]));
    }

    [Fact]
    public void APacketPastTheEndOfTheBlockIsRefused()
    {
        OneBlock();

        _transmitter.Resend(0, 1, 100_000, 100_000);
        byte[][] refused = Collect(TimeSpan.FromMilliseconds(250)).Where(IsResend).ToArray();

        Assert.Single(refused);
        Assert.Equal(GVCPStatus.GEV_STATUS_PACKET_UNAVAILABLE, Status(refused[0]));
    }

    // we only have one stream channel, so a request for another one is for a
    // block we never sent and gets nothing at all
    [Fact]
    public void ARequestForAnotherStreamChannelIsIgnored()
    {
        OneBlock();

        _transmitter.Resend(1, 1, 0, 0);

        Assert.DoesNotContain(Collect(TimeSpan.FromMilliseconds(250)), IsResend);
    }

    // --------------------------------------------------------------- the command itself

    [Fact]
    public void TheExtendedFormCarriesA64BitBlockId()
    {
        byte[] payload = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), 0);      // stream channel
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 7);      // first
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 9);      // last
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(12, 8), 0x1_0000_0002);

        var request = GVCPServer.ParsePacketResend(0x10, payload);

        Assert.Equal((ushort)0, request!.Value.Channel);
        Assert.Equal(0x1_0000_0002ul, request.Value.BlockId);
        Assert.Equal(7u, request.Value.First);
        Assert.Equal(9u, request.Value.Last);
    }

    // without the extended id flag the block id is the 16 bits in the header and
    // the packet ids are 24 bits, with all ones meaning up to the trailer
    [Fact]
    public void TheStandardFormCarriesA16BitBlockId()
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), 5);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 0x00FFFFFF);

        var request = GVCPServer.ParsePacketResend(0x00, payload);

        Assert.Equal(5ul, request!.Value.BlockId);
        Assert.Equal(1u, request.Value.First);
        Assert.Equal(uint.MaxValue, request.Value.Last);
    }

    [Fact]
    public void ATruncatedCommandIsNotARequest()
    {
        Assert.Null(GVCPServer.ParsePacketResend(0x00, new byte[11]));

        // the extended form needs the eight extra bytes of block id
        Assert.Null(GVCPServer.ParsePacketResend(0x10, new byte[12]));
    }
}
