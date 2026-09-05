// --------------------------------------------------------------------------------
// GVSPTransmitter.cs
//
// handles the transmission of gvsp packets.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GigEVirtual;

internal class GVSPTransmitter
{
    // --------------------------------------------------------------- fields and properties

    private CancellationTokenSource? _cts = null;

    private DeviceState _deviceState;
    private IPAddress _bindAddress;
    private UdpClient? _udpClient;
    private int _packetSize;

    // SCPD0 converted into Stopwatch ticks
    private long _packetDelayTicks;

    private int _width;
    private int _height;
    private uint _pixelFormat;

    private ulong _block_id;
    private uint _packet_id;

    private const int gvspHeaderSize = 20;

    private ImageSource _imageSource;

    // --------------------------------------------------------------- constructors

    public GVSPTransmitter(DeviceState deviceState, IPAddress bindAddress, ImageSource imageSource)
    {
        _deviceState = deviceState;
        _bindAddress = bindAddress;
        _imageSource = imageSource;
    }

    // --------------------------------------------------------------- methods

    public ushort StartAcquisition()
    {
        // if already streaming
        if (_cts != null) return GVCPStatus.GEV_STATUS_BUSY;

        // read necessary registers. these should already be set in the device

        // SCDA0 (destination IP)
        _deviceState.ReadRegister(0x0D18, out byte[]? scda0);
        IPAddress destinationIP = new IPAddress(scda0!);

        // SCP0 (destination port)
        _deviceState.ReadRegister(0x0D00, out byte[]? scp0);
        // host_port is last two bytes
        int port = BinaryPrimitives.ReadUInt16BigEndian(scp0.AsSpan(2, 2));

        // the client has to configure the stream channel before starting
        if (port == 0 || destinationIP.Equals(IPAddress.Any))
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        // SCPS0 (packet size). bit 1 in spec numbering asks us to set the ip
        // don't fragment flag on every stream packet
        _deviceState.ReadRegister(0x0D04, out byte[]? scps0);
        uint scps = BinaryPrimitives.ReadUInt32BigEndian(scps0);
        _packetSize = (int)(scps & 0xFFFF);
        bool doNotFragment = (scps & 0x40000000) != 0;

        // SCPD0 (inter-packet delay). the spec measures it in timestamp ticks,
        // and ours are nanoseconds
        _deviceState.ReadRegister(0x0D08, out byte[]? scpd0);
        long delayNanoseconds = BinaryPrimitives.ReadUInt32BigEndian(scpd0);
        _packetDelayTicks = delayNanoseconds * Stopwatch.Frequency / 1_000_000_000;

        // width
        _deviceState.ReadRegister(0xA000, out byte[]? widthOut);
        _width = BinaryPrimitives.ReadInt32BigEndian(widthOut);

        // height
        _deviceState.ReadRegister(0xA004, out byte[]? heightOut);
        _height = BinaryPrimitives.ReadInt32BigEndian(heightOut);

        // pixel format
        _deviceState.ReadRegister(0xA008, out byte[]? pixelFormatOut);
        _pixelFormat = BinaryPrimitives.ReadUInt32BigEndian(pixelFormatOut);

        // udp connection
        _udpClient = new(new IPEndPoint(_bindAddress, 0));
        IPEndPoint endpoint = new(destinationIP, port);
        _udpClient.Connect(endpoint);
        _udpClient.Client.DontFragment = doNotFragment;
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0], null);
        }

        Console.WriteLine($"[GVSP] StartAcquisition: dest={destinationIP}:{port}, " +
            $"packetSize={_packetSize}, packetDelay={delayNanoseconds}ns");

        _cts = new();

        // Task.Run, not a bare call: an async method runs inline until its first
        // await, and Stream only awaits at the end of a block. calling it
        // directly would send a whole frame on the GVCP thread before we get to
        // acknowledge the write that started acquisition
        CancellationToken token = _cts.Token;
        _ = Task.Run(() => Stream(token)).ContinueWith(t =>
        {
            if (t.Exception is not null)
                Console.WriteLine(t.Exception.GetBaseException());
        }, TaskContinuationOptions.OnlyOnFaulted);

        return GVCPStatus.GEV_STATUS_SUCCESS;
    }

    // fires one test packet so the application can work out the MTU. it does not
    // use the usual gvsp layout: an 8 byte header whose block_id is 0 is what
    // marks it as a test packet, and everything else in that header is ignored.
    public ushort SendTestPacket(int packetSize)
    {
        _deviceState.ReadRegister(0x0D18, out byte[]? scda0);
        _deviceState.ReadRegister(0x0D00, out byte[]? scp0);

        IPAddress destination = new(scda0!);
        int port = BinaryPrimitives.ReadUInt16BigEndian(scp0.AsSpan(2, 2));

        // nowhere to send it yet
        if (port == 0 || destination.Equals(IPAddress.Any))
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        // the requested size covers IP header, UDP header, the 8 byte test
        // packet header and the filler after it
        int payloadSize = packetSize - 20 - 8 - 8;
        if (payloadSize < 0) return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        byte[] packet = new byte[8 + payloadSize];

        // block_id 0 is the marker. real blocks start at 1, so it cannot collide
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0);

        FillLfsr(packet.AsSpan(8));

        using var client = new UdpClient(new IPEndPoint(_bindAddress, 0));

        // spec requires the don't fragment bit, otherwise the probe would
        // succeed at sizes the link cannot actually carry in one piece
        client.Client.DontFragment = true;
        client.Send(packet, packet.Length, new IPEndPoint(destination, port));

        Console.WriteLine($"[GVSP] test packet of {packetSize} bytes to {destination}:{port}");

        return GVCPStatus.GEV_STATUS_SUCCESS;
    }

    // the 16-bit right-shifting LFSR from the spec: initial value 0xFFFF,
    // polynomial 0x8016, clocked once per output byte. the first byte comes out
    // as 0xFF, which is what an application checks the payload against.
    internal static void FillLfsr(Span<byte> data)
    {
        ushort lfsr = 0xFFFF;

        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(lfsr & 0xFF);
            lfsr = (ushort)((lfsr >> 1) ^ (-(lfsr & 1) & 0x8016));
        }
    }

    public ushort StopAcquisition()
    {
        _cts?.Cancel();
        _cts = null;
        _udpClient?.Dispose();
        _udpClient = null;

        // stopping while not streaming is not an error
        return GVCPStatus.GEV_STATUS_SUCCESS;
    }

    private async Task Stream(CancellationToken ct)
    {
        // reset blockId
        _block_id = 1;

        while (!ct.IsCancellationRequested)
        {
            // packet_id is reset at the start of each block
            _packet_id = 0;

            // one frame per block
            byte[] frame = _imageSource.NextFrame(_width, _height, _pixelFormat);

            // build leader
            byte[] leader = BuildDataLeader();
            _udpClient?.Send(leader);
            Pace();

            // build data payload
            // spec says for data payload packets, the packet size
            // is IP header + UDP header + GVSP header
            int usablePayloadPerPacket = _packetSize - 20 - 8 - gvspHeaderSize;
            byte[] payload;

            for (int i = 0; i < frame.Length; i += usablePayloadPerPacket)
            {
                int chunkSize = Math.Min(usablePayloadPerPacket, frame.Length - i);
                payload = BuildDataPayload(frame, i, chunkSize);
                _udpClient?.Send(payload);
                Pace();
            }

            // build trailer
            byte[] trailer = BuildDataTrailer();
            _udpClient?.Send(trailer);

            // increment for next block
            _block_id++;

            await Task.Delay(800);
        }
    }

    // holds off the next packet by the delay the application asked for in SCPD0.
    // Task.Delay cannot do sub-millisecond, and inter-packet delays are typically
    // a few microseconds, so this spins
    private void Pace()
    {
        if (_packetDelayTicks <= 0) return;

        long until = Stopwatch.GetTimestamp() + _packetDelayTicks;
        while (Stopwatch.GetTimestamp() < until)
            Thread.SpinWait(1);
    }

    // all gvsp packets share the same header.
    // packet_format says:
    // DATA_LEADER_FORMAT = 1
    // DATA_TRAILER_FORMAT = 2
    // DATA_PAYLOAD_FORMAT = 3,5,6,7,8
    // note: auto-increments packet_id
    private byte[] BuildGVSPHeader(byte packet_format)
    {
        byte[] header = new byte[gvspHeaderSize];
        int offset = 0;

        // status
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset, 2), GVCPStatus.GEV_STATUS_SUCCESS);
        offset += 2;

        // flag
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset, 2), 0);
        offset += 2;

        // extended ID flag and packet format
        byte EI = 0b1000_0000;

        header[offset] = (byte)(EI | packet_format);
        offset += 1;

        // packet_id / reserved
        offset += 3;

        // block_id64 (8 bytes, high then low)
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(offset, 8), _block_id);
        offset += 8;

        // packet_id32 (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset, 4), _packet_id++);
        offset += 4;

        return header;
    }

    private byte[] BuildDataLeader()
    {
        byte[] leader = new byte[gvspHeaderSize + 4 * 9];

        // we'll copy the header at the end
        int offset = gvspHeaderSize;

        // field_id (4 bits)
        // leave 0 for progressive data

        // field_count (4 bits)
        // leave 0 for progressive data

        offset += 1;

        // reserved (1 byte)
        offset += 1;

        // payload_type (2 bytes)
        // 0x0001 is image payload type
        BinaryPrimitives.WriteUInt16BigEndian(leader.AsSpan(offset, 2), 0x0001);
        offset += 2;

        // offsets (no roi) and paddings (not used) stay 0

        ulong timestamp = _deviceState.Timestamp();

        // timestamp (high) (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(offset, 4), (uint)(timestamp >> 32));
        offset += 4;

        // timestamp (low) (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(offset, 4), (uint)timestamp);
        offset += 4;

        // pixel_format (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(offset, 4), _pixelFormat);
        offset += 4;

        // size_x (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(offset, 4), (uint)_width);
        offset += 4;

        // size_y (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(offset, 4), (uint)_height);
        offset += 4;

        // offset_x (4 bytes)
        offset += 4;

        // offset_y (4 bytes)
        offset += 4;

        // padding_x (2 bytes)
        offset += 2;

        // padding_y (2 bytes)
        offset += 2;

        // copy header
        byte[] header = BuildGVSPHeader(1);
        Array.Copy(header, 0, leader, 0, gvspHeaderSize);

        return leader;
    }

    private byte[] BuildDataPayload(byte[] frame, int imageOffset, int chunkSize)
    {
        byte[] payload = new byte[gvspHeaderSize + chunkSize];
        Array.Copy(frame, imageOffset, payload, gvspHeaderSize, chunkSize);
        // copy header
        byte[] header = BuildGVSPHeader(3);
        Array.Copy(header, 0, payload, 0, gvspHeaderSize);

        return payload;
    }

    private byte[] BuildDataTrailer()
    {
        byte[] trailer = new byte[gvspHeaderSize + 8];

        // we'll copy the header at the end
        int offset = gvspHeaderSize;

        // reserved (2 bytes)
        // set 0 on transmission
        offset += 2;

        // payload_type (2 bytes)
        // 0x0001 is image payload type
        BinaryPrimitives.WriteUInt16BigEndian(trailer.AsSpan(offset, 2), 0x0001);
        offset += 2;

        // size_y (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(trailer.AsSpan(offset, 4), (uint)_height);
        offset += 4;

        // copy header
        byte[] header = BuildGVSPHeader(2);
        Array.Copy(header, 0, trailer, 0, gvspHeaderSize);

        return trailer;
    }
}