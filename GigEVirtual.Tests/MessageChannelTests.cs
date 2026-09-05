// --------------------------------------------------------------------------------
// MessageChannelTests.cs
//
// drives the message channel at a real socket on loopback, with this end
// standing in for the application: it opens the channel through the registers,
// reads what arrives and decides whether to acknowledge it.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GigEVirtual;
using Xunit;

namespace GigEVirtual.Tests;

[Collection(SocketTests.Name)]
public class MessageChannelTests : IDisposable
{
    private static readonly IPEndPoint Controller = new(IPAddress.Parse("127.0.0.1"), 50000);

    private const uint MCP = 0x0B00;
    private const uint MCDA = 0x0B10;
    private const uint MCTT = 0x0B14;
    private const uint MCRC = 0x0B18;
    private const uint MCSP = 0x0B1C;

    private readonly UdpClient _application;
    private readonly int _hostPort;

    private readonly DeviceState _state;
    private readonly MessageChannel _channel;

    public MessageChannelTests()
    {
        _application = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _hostPort = ((IPEndPoint)_application.Client.LocalEndPoint!).Port;

        _state = TestDevice.Camera();
        _channel = new MessageChannel(_state, IPAddress.Loopback);

        // the same wiring GigEDevice does, without standing a whole device up
        _state.OnEvent(_channel.Raise);
        _state.OnMessageChannel(destination =>
        {
            if (destination is null) _channel.Close();
            else _channel.Open(destination);
        });

        Write(0x0A00, 2); // take control
    }

    public void Dispose()
    {
        _channel.Close();
        _application.Dispose();
    }

    // --------------------------------------------------------------- helpers

    private void Write(uint address, uint value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, _state.WriteRegister(Controller, address, buffer));
    }

    // opens the channel the way an application does: destination first, port last
    private void OpenChannel()
    {
        Write(MCDA, 0x7F000001);
        Write(MCP, (uint)_hostPort);
    }

    private List<byte[]> Collect(TimeSpan window)
    {
        var packets = new List<byte[]>();
        _application.Client.ReceiveTimeout = 50;

        DateTime until = DateTime.UtcNow + window;
        IPEndPoint from = new(IPAddress.Any, 0);

        while (DateTime.UtcNow < until)
        {
            try { packets.Add(_application.Receive(ref from)); }
            catch (SocketException) { } // nothing arrived before the timeout
        }

        return packets;
    }

    private static ushort ReqId(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
    private static ushort EventId(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2));
    private static ushort Channel(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12, 2));
    private static ushort BlockId(byte[] packet) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14, 2));
    private static ulong Timestamp(byte[] packet) => BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(16, 8));

    // acknowledge_required is bit 7 in spec numbering, so the low bit
    private static bool AckRequested(byte[] packet) => (packet[1] & 0x01) != 0;

    private void Acknowledge(ushort reqId)
    {
        byte[] ack = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(0, 2), GVCPStatus.GEV_STATUS_SUCCESS);
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2, 2), GVCPMessages.EVENT_ACK);
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(6, 2), reqId);

        _application.Send(ack, ack.Length,
            new IPEndPoint(IPAddress.Loopback, (int)_state.ReadUint(MCSP)));
    }

    // --------------------------------------------------------------- opening and closing

    [Fact]
    public void OpeningTheChannelReportsTheSourcePortItWillSendFrom()
    {
        Assert.Equal(0u, _state.ReadUint(MCSP));

        OpenChannel();

        uint sourcePort = _state.ReadUint(MCSP);
        Assert.NotEqual(0u, sourcePort);
        Assert.InRange(sourcePort, 1u, 65535u);
    }

    [Fact]
    public void ClosingTheChannelClearsTheSourcePort()
    {
        OpenChannel();
        Write(MCP, 0);

        Assert.Equal(0u, _state.ReadUint(MCSP));
    }

    // the port is written last precisely so the destination is already there
    [Fact]
    public void OpeningWithNowhereToSendIsRefused()
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)_hostPort);

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER,
            _state.WriteRegister(Controller, MCP, buffer));

        Assert.Equal(0u, _state.ReadUint(MCSP));
    }

    // an application that goes away without closing anything leaves the device
    // sending events into a socket nobody is reading
    [Fact]
    public void ReleasingControlClosesTheChannel()
    {
        OpenChannel();
        Write(0x0A00, 0);

        Assert.Equal(0u, _state.ReadUint(MCP));
        Assert.Equal(0u, _state.ReadUint(MCSP));
    }

    [Fact]
    public void AnEventRaisedWithNoChannelOpenGoesNowhere()
    {
        _state.RaiseEvent(0x9C41, 7);

        Assert.Empty(Collect(TimeSpan.FromMilliseconds(150)));
    }

    // --------------------------------------------------------------- the message itself

    [Fact]
    public void AnEventArrivesAsAnEventCommand()
    {
        OpenChannel();
        _state.RaiseEvent(0x9C41, 7);

        byte[] packet = Assert.Single(Collect(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(24, packet.Length);
        Assert.Equal(0x42, packet[0]);
        Assert.Equal(GVCPMessages.EVENT_CMD, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2)));

        // length counts everything after the 8 byte header, and one event is 16
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2))); // event_size

        Assert.Equal(0x9C41, EventId(packet));
        Assert.Equal(0, Channel(packet));
        Assert.Equal(7, BlockId(packet));
        Assert.NotEqual(0ul, Timestamp(packet));
    }

    // 0xFFFF says the event had nothing to do with a transfer, which is not the
    // same as saying it belonged to block 0
    [Fact]
    public void AnEventWithNoBlockSaysSoInTheStreamChannelIndex()
    {
        OpenChannel();
        _state.RaiseEvent(0x9C42, 0);

        byte[] packet = Assert.Single(Collect(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(0xFFFF, Channel(packet));
        Assert.Equal(0, BlockId(packet));
    }

    // this is how an application spots a message that never arrived, so it has
    // to move even when no acknowledge is asked for
    [Fact]
    public void ReqIdCountsUpFromOneMessageToTheNext()
    {
        OpenChannel();

        _state.RaiseEvent(0x9C41, 1);
        _state.RaiseEvent(0x9C41, 2);
        _state.RaiseEvent(0x9C41, 3);

        ushort[] ids = [.. Collect(TimeSpan.FromMilliseconds(300)).Select(ReqId)];

        Assert.Equal(3, ids.Length);
        Assert.Equal([ids[0], (ushort)(ids[0] + 1), (ushort)(ids[0] + 2)], ids);
    }

    // --------------------------------------------------------------- acknowledges

    [Fact]
    public void WithTheTimeoutOffNoAcknowledgeIsAskedFor()
    {
        Write(MCTT, 0);
        OpenChannel();

        _state.RaiseEvent(0x9C41, 1);

        byte[] packet = Assert.Single(Collect(TimeSpan.FromMilliseconds(250)));
        Assert.False(AckRequested(packet));
    }

    [Fact]
    public void WithATimeoutSetTheAcknowledgeFlagGoesUp()
    {
        Write(MCTT, 60);
        Write(MCRC, 0);
        OpenChannel();

        _state.RaiseEvent(0x9C41, 1);

        byte[] packet = Collect(TimeSpan.FromMilliseconds(250))[0];
        Assert.True(AckRequested(packet));
    }

    // spec: a retransmission repeats the req_id rather than taking a new one,
    // which is what lets an application tell a repeat from a fresh message
    [Fact]
    public void AnUnacknowledgedMessageIsRetriedTheRetryCountTimes()
    {
        Write(MCTT, 60);
        Write(MCRC, 2);
        OpenChannel();

        _state.RaiseEvent(0x9C41, 5);

        List<byte[]> packets = Collect(TimeSpan.FromMilliseconds(500));

        // the first send plus two retries
        Assert.Equal(3, packets.Count);
        Assert.All(packets, p => Assert.Equal(ReqId(packets[0]), ReqId(p)));
        Assert.All(packets, p => Assert.Equal(5, BlockId(p)));
    }

    [Fact]
    public void AcknowledgingStopsTheRetries()
    {
        Write(MCTT, 200);
        Write(MCRC, 3);
        OpenChannel();

        _state.RaiseEvent(0x9C41, 5);

        _application.Client.ReceiveTimeout = 500;
        IPEndPoint from = new(IPAddress.Any, 0);
        byte[] first = _application.Receive(ref from);

        Acknowledge(ReqId(first));

        Assert.Empty(Collect(TimeSpan.FromMilliseconds(500)));
    }

    // spec has applications send throwaway packets to this port to punch a hole
    // in their own firewall, so anything that is not an acknowledge is ignored
    [Fact]
    public void TrafficThatIsNotAnAcknowledgeDoesNotCountAsOne()
    {
        Write(MCTT, 150);
        Write(MCRC, 1);
        OpenChannel();

        _state.RaiseEvent(0x9C41, 5);

        _application.Client.ReceiveTimeout = 500;
        IPEndPoint from = new(IPAddress.Any, 0);
        byte[] first = _application.Receive(ref from);

        _application.Send([1, 2, 3, 4], 4,
            new IPEndPoint(IPAddress.Loopback, (int)_state.ReadUint(MCSP)));

        byte[] retry = Assert.Single(Collect(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(ReqId(first), ReqId(retry));
    }

    [Fact]
    public void AnAcknowledgeForAnotherMessageIsNotThisMessagesAcknowledge()
    {
        Write(MCTT, 150);
        Write(MCRC, 1);
        OpenChannel();

        _state.RaiseEvent(0x9C41, 5);

        _application.Client.ReceiveTimeout = 500;
        IPEndPoint from = new(IPAddress.Any, 0);
        byte[] first = _application.Receive(ref from);

        Acknowledge((ushort)(ReqId(first) + 1));

        byte[] retry = Assert.Single(Collect(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(ReqId(first), ReqId(retry));
    }
}
