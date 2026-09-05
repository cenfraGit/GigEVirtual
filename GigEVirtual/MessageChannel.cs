// --------------------------------------------------------------------------------
// MessageChannel.cs
//
// the channel a device uses to tell an application that something happened. it
// runs the opposite way round to the control channel: here the device sends the
// command and the application acknowledges it.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace GigEVirtual;

internal class MessageChannel
{
    // --------------------------------------------------------------- fields and properties

    // an event that happened, waiting its turn to go out. the timestamp is taken
    // when it happened rather than when it is sent, which is the whole point of
    // the field: an application uses it to line events up against frames.
    private readonly record struct Event(ushort EventId, ulong BlockId, ulong Timestamp);

    private readonly DeviceState _state;
    private readonly IPAddress _bindAddress;

    private UdpClient? _client;
    private Channel<Event>? _queue;
    private CancellationTokenSource? _cts;

    private readonly object _lock = new();

    // spec: req_id counts up from one message to the next, but a retransmission
    // repeats it. that is how an application tells a lost message from a
    // repeated one. 0 is never a valid req_id.
    private ushort _reqId = 1;

    // how many events can be waiting before the oldest starts being dropped. a
    // real camera has a queue too, and the nano even has an event for running
    // out of it.
    private const int QueueDepth = 64;

    // message channel registers
    private const uint MCTT = 0x0B14;
    private const uint MCRC = 0x0B18;
    private const uint MCSP = 0x0B1C;

    // --------------------------------------------------------------- constructors

    public MessageChannel(DeviceState state, IPAddress bindAddress)
    {
        _state = state;
        _bindAddress = bindAddress;
    }

    // --------------------------------------------------------------- methods

    // opens the channel to where MCDA and MCP point. the socket binds to a port
    // of the system's choosing and MCSP reports it, so an application can let
    // our traffic back in through its own firewall.
    public void Open(IPEndPoint destination)
    {
        Close();

        UdpClient client = new(new IPEndPoint(_bindAddress, 0));
        client.Connect(destination);

        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0], null);
        }

        int sourcePort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        Channel<Event> queue = Channel.CreateBounded<Event>(
            new BoundedChannelOptions(QueueDepth)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        CancellationTokenSource cts = new();

        lock (_lock)
        {
            _client = client;
            _queue = queue;
            _cts = cts;
        }

        _state.WriteUint(MCSP, (uint)sourcePort);

        Console.WriteLine($"[MSG] channel open to {destination}, our source port is {sourcePort}");

        _ = Task.Run(() => Pump(client, queue.Reader, cts.Token));
    }

    // spec says a message already going out has to finish, which a single udp
    // send does by itself. what is still queued behind it is dropped: the
    // application asked for the channel to be shut.
    public void Close()
    {
        UdpClient? client;
        CancellationTokenSource? cts;

        lock (_lock)
        {
            client = _client;
            cts = _cts;

            _client = null;
            _queue = null;
            _cts = null;
        }

        if (client is null) return;

        cts?.Cancel();
        client.Dispose();

        _state.WriteUint(MCSP, 0);

        Console.WriteLine("[MSG] channel closed");
    }

    // an event goes on the queue rather than straight out the socket, since
    // sending one can wait out a timeout and a retry budget and whatever raised
    // it is usually the thread streaming. with no channel open it goes nowhere,
    // which is what the spec asks for.
    public void Raise(ushort eventId, ulong blockId)
    {
        ChannelWriter<Event>? writer;
        lock (_lock) writer = _queue?.Writer;

        writer?.TryWrite(new Event(eventId, blockId, _state.Timestamp()));
    }

    private async Task Pump(UdpClient client, ChannelReader<Event> reader, CancellationToken ct)
    {
        try
        {
            await foreach (Event raised in reader.ReadAllAsync(ct))
            {
                // read per message: an application can change either of these
                // while the channel is open. a timeout of 0 turns acknowledges
                // off, and then the flag has to be clear as well.
                uint timeout = _state.ReadUint(MCTT);
                uint retries = _state.ReadUint(MCRC);

                byte[] packet = BuildEvent(raised, _reqId, ackRequested: timeout > 0);

                Console.WriteLine($"[MSG] event 0x{raised.EventId:X4} block {raised.BlockId} " +
                    $"req_id {_reqId}");

                for (uint attempt = 0; !ct.IsCancellationRequested; attempt++)
                {
                    await client.SendAsync(packet.AsMemory(), ct);

                    if (timeout == 0) break;
                    if (await Acknowledged(client, _reqId, (int)timeout, ct)) break;

                    if (attempt >= retries)
                    {
                        Console.WriteLine($"[MSG] event 0x{raised.EventId:X4} went unacknowledged");
                        break;
                    }
                }

                // 0 is not a valid req_id, so the wrap goes to 1
                _reqId = _reqId == ushort.MaxValue ? (ushort)1 : (ushort)(_reqId + 1);
            }
        }
        catch (OperationCanceledException) { } // the channel closed
        catch (ObjectDisposedException) { }    // and took the socket with it
        catch (SocketException) { }            // nowhere left to send to
    }

    // waits out MCTT for this message's acknowledge. anything else that turns up
    // is ignored rather than counted: the spec has applications send throwaway
    // packets to this port to open a hole in their own firewall.
    private static async Task<bool> Acknowledged(UdpClient client, ushort reqId,
                                                 int timeout, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            while (true)
            {
                UdpReceiveResult result = await client.ReceiveAsync(deadline.Token);
                byte[] data = result.Buffer;

                if (data.Length < 8) continue;

                if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)) != GVCPMessages.EVENT_ACK)
                    continue;

                if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2)) == reqId) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // one event per message. several may be concatenated, but they would have to
    // be waiting at the same instant for that to be worth doing, and events on a
    // camera are far enough apart that they never are.
    //
    // the 16-bit block_id form rather than the extended one: a description maps
    // an event as a buffer and reads its timestamp at a fixed offset, and that
    // offset only lines up with the 16 byte entry.
    internal static byte[] BuildEvent(ushort eventId, ulong blockId, ulong timestamp,
                                      ushort reqId, bool ackRequested) =>
        BuildEvent(new Event(eventId, blockId, timestamp), reqId, ackRequested);

    private static byte[] BuildEvent(Event raised, ushort reqId, bool ackRequested)
    {
        byte[] packet = new byte[8 + 16];

        // the message channel carries the same header as the control channel,
        // acknowledge_required being bit 7 in spec numbering and so the low bit
        packet[0] = 0x42;
        packet[1] = (byte)(ackRequested ? 0x01 : 0x00);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), GVCPMessages.EVENT_CMD);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), reqId);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), 16); // event_size
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), raised.EventId);

        // we have the one stream channel, and 0xFFFF says no block was involved
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12, 2),
            raised.BlockId == 0 ? (ushort)0xFFFF : (ushort)0);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14, 2), (ushort)raised.BlockId);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(16, 8), raised.Timestamp);

        return packet;
    }
}
