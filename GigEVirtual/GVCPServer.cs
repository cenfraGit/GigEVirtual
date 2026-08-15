// --------------------------------------------------------------------------------
// GVCPServer.cs
//
// the GigEVision Control Protocol server implementation.
// listens on UDP 3956.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net.Sockets;

namespace GigEVirtual;

internal class GVCPServer
{
    // --------------------------------------------------------------- fields and properties

    private const int _port = 3956;

    // --------------------------------------------------------------- constructors

    public static async Task Start(DeviceState deviceState)
    {
        var client = new UdpClient(_port);
        PrintConsole($"Listening on UDP {_port}");

        while (true)
        {
            var result = await client.ReceiveAsync();
            byte[] data = result.Buffer;

            // note: use short/ushort for 2 bytes, uint/int for 4 bytes

            // now we'll validate gvcp command header. the gigevision spec provides
            // a specific structure for the command header (big endian):
            // - 0x42 (1 byte)
            // - flag (1 byte)
            // - command (2 bytes)
            // - length (2 bytes)
            // - req_id (2 byets)
            //
            // header is 8 bytes, so skip if less

            if (data.Length < 8)
                continue;

            // capture the rest of the header values:
            byte firstByte = data[0];
            byte flag = data[1];
            ushort command = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
            ushort req_id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));

            // gigevision says discard if first byte is not 0x42:
            if (firstByte != 0x42)
                continue;

            PrintConsole($"CMD from {result.RemoteEndPoint,-21}: " +
                $"{GVCPMessages.GetName(command)} (0x{command:X4}) " +
                $"length={length} " +
                $"req_id={req_id}");

            // gigespec says if recipient does not support command, return GEV_STATUS_NOT_IMPLEMENTED
            // via ack

            // helper method to print what ack was sent
            void PrintAck(ushort ackCode, ushort statusCode, int length) =>
                PrintConsole($"SENT to  {result.RemoteEndPoint,-21}: " +
                $"{GVCPMessages.GetName(ackCode)} (0x{ackCode:X4}) " +
                $"{GVCPStatus.GetName(statusCode)} (0x{statusCode:X4}) " +
                $"length={length}");
        }
    }

    // --------------------------------------------------------------- methods

    // this prints to the console. prepends "GVCP: " to message
    private static void PrintConsole(string message) =>
        Console.WriteLine($"GVCP: {message}");
}