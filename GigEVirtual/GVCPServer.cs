// --------------------------------------------------------------------------------
// GVCPServer.cs
//
// the GigEVision Control Protocol server implementation.
// listens on UDP 3956.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

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

            //PrintConsole($"RAW recv from {result.RemoteEndPoint}: {data.Length} bytes, first byte = 0x{(data.Length > 0 ? data[0] : 0):X2}");

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

            // ------------------------------------------ header values

            // capture the rest of the header values:
            byte firstByte = data[0];
            byte flag = data[1];
            ushort command = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
            ushort req_id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));

            // payload
            ReadOnlySpan<byte> payload = data.AsSpan(8);

            // gigevision says discard if first byte is not 0x42:
            if (firstByte != 0x42)
                continue;

            // we might as well check if payload matches length
            if (payload.Length != length)
            {
                PrintConsole($"Wrong payload length: header says: {length}, actual is {payload.Length}");
                continue;
            }

            PrintConsole($"CMD from {result.RemoteEndPoint,-21}: " +
                $"{GVCPMessages.GetName(command)} (0x{command:X4}) " +
                $"length={length} " +
                $"req_id={req_id}");

            // helper method to print what ack was sent
            void PrintAck(ushort ackCode, ushort statusCode, int length) =>
                PrintConsole($"SENT to  {result.RemoteEndPoint,-21}: " +
                $"{GVCPMessages.GetName(ackCode)} (0x{ackCode:X4}) " +
                $"{GVCPStatus.GetName(statusCode)} (0x{statusCode:X4}) " +
                $"length={length}");

            // ------------------------------------------ CMD check

            // now we'll build the ack depending on the cmd
            byte[] ack;

            switch (command)
            {
                case GVCPMessages.DISCOVERY_CMD:

                    // resolve routing for the ack reply
                    using (var probeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        probeSocket.Connect(result.RemoteEndPoint.Address, 3956);
                        ack = BuildDiscoveryAck(req_id, ((IPEndPoint)probeSocket.LocalEndPoint!).Address);
                    }
                    // send ack to whoever asked
                    await client.SendAsync(ack, ack.Length, result.RemoteEndPoint);
                    PrintAck(GVCPMessages.DISCOVERY_ACK, GVCPStatus.GEV_STATUS_SUCCESS, ack.Length);
                    break;
                case GVCPMessages.READREG_CMD:
                    // READREG_CMD payload consists of one or more register addresses.
                    // we'll use the length in the header (which is payload length)
                    // to determine how many addresses to read (4 bytes per address)
                    int numAddresses = length / 4;

                    // now use numAddresses to get list of addresses to read:
                    uint[] addresses = new uint[numAddresses];
                    int offset = 0;
                    for (int i = 0; i < numAddresses; i++)
                    {
                        addresses[i] = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
                        offset += 4;
                    }

                    ack = BuildReadRegAck(req_id, addresses, deviceState);
                    await client.SendAsync(ack, ack.Length, result.RemoteEndPoint);
                    PrintAck(GVCPMessages.READREG_ACK, GVCPStatus.GEV_STATUS_SUCCESS, ack.Length);
                    break;
                default:
                    // must return GEV_STATUS_NOT_IMPLEMENTED via ack?
                    ack = null;
                    break;
            }
        }
    }

    // --------------------------------------------------------------- methods

    // this prints to the console. prepends "GVCP: " to message
    private static void PrintConsole(string message) =>
        Console.WriteLine($"GVCP: {message}");

    // --------------------------------------------------------------- ack builder methods

    private static byte[] BuildDiscoveryAck(ushort req_id, IPAddress localIp)
    {
        // first we'll build the payload, then the header

        byte[] ack = new byte[8 + 248];
        int offset = 8; // skip header initially

        // ------------------------------------------ payload (248 bytes total)

        // spec_version_major (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), 0x0002);
        offset += 2;

        // spec_version_minor (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), 0x0002);
        offset += 2;

        // device_mode (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(offset, 4), 0x00000001);
        offset += 4;

        // reserved (2 bytes)
        offset += 2;

        // device_MAC_address (high) (2 bytes)
        // device_MAC_address (low) (4 bytes)
        byte[] mac = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        Array.Copy(sourceArray: mac,
                   sourceIndex: 0,
                   destinationArray: ack,
                   destinationIndex: offset,
                   length: 6);
        offset += 6;

        // IP_config_options (4 bytes)
        // from network interface configuration registers (bootstrap):
        // bit 29, 30, 31: LLA, DHCP, Persistent_IP, respectively.
        // I guess all three should be supported, so 111
        BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(offset, 4), 0x00000007);
        offset += 4;

        // IP_config_current (4 bytes)
        // set it to persistent? (bit 31)
        BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(offset, 4), 0x00000001);
        offset += 4;

        // reserved (4 + 4 + 4 = 12 bytes)
        offset += 12;

        // current_IP (4 bytes)
        byte[] current_IP = localIp.GetAddressBytes();
        Array.Copy(current_IP, 0, ack, offset, current_IP.Length);
        offset += 4;

        // reserved (4 + 4 + 4 = 12 bytes)
        offset += 12;

        // current_subnet_mask (4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(offset, 4), 0xFFFFFF00); // 255.255.255.0
        offset += 4;

        // reserved (4 + 4 + 4 = 12 bytes)
        offset += 12;

        // default_gateway (4 bytes)
        byte[] default_gateway = IPAddress.Parse("192.168.1.1").GetAddressBytes();
        Array.Copy(default_gateway, 0, ack, offset, default_gateway.Length);
        offset += 4;

        // manufacturer_name (32 bytes)
        byte[] manufacturer_name = Encoding.ASCII.GetBytes("VIRTUAL");
        Array.Copy(manufacturer_name, 0, ack, offset, manufacturer_name.Length);
        offset += 32;

        // model_name (32 bytes)
        byte[] model_name = Encoding.ASCII.GetBytes("MODEL");
        Array.Copy(model_name, 0, ack, offset, model_name.Length);
        offset += 32;

        // device_version (32 bytes)
        byte[] device_version = Encoding.ASCII.GetBytes("1.0");
        Array.Copy(device_version, 0, ack, offset, device_version.Length);
        offset += 32;

        // manufacturer_specific_information (48 bytes)
        byte[] manufacturer_specific_information = Encoding.ASCII.GetBytes("C# GigEVision Cam");
        Array.Copy(manufacturer_specific_information, 0, ack, offset, manufacturer_specific_information.Length);
        offset += 48;

        // serial_number (16 bytes)
        byte[] serial_number = Encoding.ASCII.GetBytes("S0001");
        Array.Copy(serial_number, 0, ack, offset, serial_number.Length);
        offset += 16;

        // user_defined_name (16 bytes)
        byte[] user_defined_name = Encoding.ASCII.GetBytes("virtualDev");
        Array.Copy(user_defined_name, 0, ack, offset, user_defined_name.Length);
        //offset += 16;

        // ------------------------------------------ header (8 bytes)

        // reset offset
        offset = 0;

        // status (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPStatus.GEV_STATUS_SUCCESS);
        offset += 2;

        // answer (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPMessages.DISCOVERY_ACK);
        offset += 2;

        // length (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), (ushort)248);
        offset += 2;

        // ack_id (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), req_id);
        //offset += 2;

        return ack;
    }

    private static byte[] BuildReadRegAck(ushort req_id, uint[] addresses, DeviceState deviceState)
    {
        // header (8 bytes) + (4 bytes per address)
        byte[] ack = new byte[8 + (4 * addresses.Length)];

        // ------------------------------------------ payload

        int offset = 8; // skip header first

        // value read from register should be 4 bytes
        byte[] value = new byte[4];

        // read each value from address and copy to array
        foreach (var address in addresses)
        {
            if (!deviceState.TryRegisterRead(address, out value))
            {
                continue; // dont write anything
            }

            Array.Copy(value, 0, ack, offset, 4);

            offset += 4;
        }

        // ------------------------------------------ header

        // reset to write header
        offset = 0;

        // status (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPStatus.GEV_STATUS_SUCCESS);
        offset += 2;

        // acknowledge (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPMessages.READREG_ACK);
        offset += 2;

        // length
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), (ushort)(4 * addresses.Length));
        offset += 2;

        // ack_id
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), req_id);
        //offset += 2;

        return ack;
    }
}