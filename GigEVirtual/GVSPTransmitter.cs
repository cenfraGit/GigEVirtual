// --------------------------------------------------------------------------------
// GVSPTransmitter.cs
//
// handles the transmission of gvsp packets.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GigEVirtual;

internal class GVSPTransmitter
{
    // --------------------------------------------------------------- fields and properties

    private CancellationTokenSource? _cts = null;

    private UdpClient? _udpClient;
    private int _packetSize;
    private int _width;
    private int _height;
    private uint _pixelFormat;

    private ulong _block_id;
    private uint _packet_id;

    private const int gvspHeaderSize = 20;

    private byte[] _testImage;


    // --------------------------------------------------------------- methods

    public void StartAcquisition(DeviceState deviceState)
    {
        // if already streaming, return
        if (_cts != null) return;

        // read necessary registers. these should already be set in the device

        // SCDA0 (destination IP)
        deviceState.ReadRegister(0x0D18, out byte[]? scda0);
        IPAddress destinationIP = new IPAddress(scda0!);

        // SCP0 (destination port)
        deviceState.ReadRegister(0x0D00, out byte[]? scp0);
        // host_port is last two bytes
        int port = BinaryPrimitives.ReadUInt16BigEndian(scp0.AsSpan(2, 2));

        // SCPS0 (packet size)
        deviceState.ReadRegister(0x0D04, out byte[]? scps0);
        // packet_size is last two bytes
        _packetSize = BinaryPrimitives.ReadUInt16BigEndian(scps0.AsSpan(2,2));

        // width
        deviceState.ReadRegister(0xA000, out byte[]? widthOut);
        _width = BinaryPrimitives.ReadInt32BigEndian(widthOut);

        // height
        deviceState.ReadRegister(0xA004, out byte[]? heightOut);
        _height = BinaryPrimitives.ReadInt32BigEndian(heightOut);

        // pixel format
        deviceState.ReadRegister(0xA008, out byte[]? pixelFormatOut);
        _pixelFormat = BinaryPrimitives.ReadUInt32BigEndian(pixelFormatOut);

        // create temp image
        // 1 is bytes per pixel (temp, for mono8)
        _testImage = new byte[_width * _height * 1];
        for (int i = 0; i < _width * _height * 1; i++)
            _testImage[i] = (i % 2 == 0) ? (byte)255 : (byte)0;

        // udp connection
        IPEndPoint endpoint = new(destinationIP, port);
        _udpClient = new();
        _udpClient.Connect(endpoint);

        Console.WriteLine($"starting acq to {destinationIP}:{port}");

        _cts = new();
        _ = Stream(_cts.Token).ContinueWith(t =>
        {
            if (t.Exception is not null)
                Console.WriteLine(t.Exception.GetBaseException());
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void StopAcquisition()
    {
        _cts?.Cancel();
        _cts = null;
        _udpClient?.Dispose();
        _udpClient = null;
    }

    private async Task Stream(CancellationToken ct)
    {
        // reset blockId
        _block_id = 1;

        while (!ct.IsCancellationRequested)
        {
            // packet_id is reset at the start of each block
            _packet_id = 0;

            // build leader
            byte[] leader = BuildDataLeader();
            _udpClient?.Send(leader);

            // build data payload
            // spec says for data payload packets, the packet size
            // is IP header + UDP header + GVSP header
            int usablePayloadPerPacket = _packetSize - 20 - 8 - gvspHeaderSize;
            int image_byte_amount = _width * _height * 1;
            byte[] payload;

            for (int i = 0; i < image_byte_amount; i += usablePayloadPerPacket)
            {
                int chunkSize = Math.Min(usablePayloadPerPacket, image_byte_amount - i);
                payload = BuildDataPayload(i, chunkSize);
                _udpClient?.Send(payload);
            }

            // build trailer
            byte[] trailer = BuildDataTrailer();
            _udpClient?.Send(trailer);

            // increment for next block
            _block_id++;

            await Task.Delay(1000);
        }
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

        // we'll leave timestamp (optional), offsets (no roi), and paddings (not used)
        // set to 0 for now

        // timestamp (high) (4 bytes)
        offset += 4;

        // timestamp (low) (4 bytes)
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

    private byte[] BuildDataPayload(int imageOffset, int chunkSize)
    {
        byte[] payload = new byte[gvspHeaderSize + chunkSize];
        Array.Copy(_testImage, imageOffset, payload, gvspHeaderSize, chunkSize);
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