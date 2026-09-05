// --------------------------------------------------------------------------------
// GVCPServer.cs
//
// the GigEVision Control Protocol server implementation.
// listens on UDP 3956.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace GigEVirtual;

internal record Ack(ushort Message, ushort Status, byte[] Buffer);

internal class GVCPServer
{
    // --------------------------------------------------------------- fields and properties

    private const int _port = 3956;
    private readonly IPAddress _bindAddress;
    private DeviceState _deviceState;
    private UdpClient? _udpClient;
    bool _shareToNetwork = false;

    // shared discovery
    private static readonly List<GVCPServer> _instances = [];
    private static readonly object _instancesLock = new();
    private static Task? _discoveryTask;

    // --------------------------------------------------------------- constructors

    public GVCPServer(IPAddress bindAddress, DeviceState deviceState, bool shareToNetwork)
    {
        _deviceState = deviceState;
        _bindAddress = bindAddress;
        _shareToNetwork = shareToNetwork;

        lock (_instancesLock)
            _instances.Add(this);
    }

    public Task Start()
    {
        // the first device to run Start() will initialize the discovery
        // for all instances of devices
        lock (_instancesLock)
            if (_discoveryTask is null)
                _discoveryTask = Task.Run(StartDiscovery);
        return StartOwnSocket();
    }

    // this method will continuously run in the background as 0.0.0.0
    // so that all devices can listen to DISCOVERY_CMD. once a CMD is
    // found, this method will make all devices reply
    private static async Task StartDiscovery()
    {
        var client = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0], null);
        }

        Console.WriteLine($"GVCP: shared discovery listener started on 0.0.0.0:{_port}");

        // will be used if "shareToNetwork" is disabled
        var localAddresses = GetLocalAddresses();

        while (true)
        {
            var result = await client.ReceiveAsync();
            byte[] data = result.Buffer;

            if (data.Length < 8)
                continue;

            if (data[0] != 0x42)
                continue;

            // capture the rest of the header values:
            byte flag = data[1];
            ushort command = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
            ushort req_id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));

            if (command != GVCPMessages.DISCOVERY_CMD)
                continue;

            List<GVCPServer> snapshot;
            lock (_instancesLock)
                snapshot = [.. _instances];

            foreach (var server in snapshot)
            {
                if (server._udpClient is null)
                    continue;

                // if network sharing is disabled, and endpoint is not local address, ignore
                if (!server._shareToNetwork && !localAddresses.Contains(result.RemoteEndPoint.Address))
                    continue;

                server._deviceState.SetIP(server._bindAddress);
                var ack = server.BuildDiscoveryAck(req_id, server._bindAddress);
                await server._udpClient.SendAsync(ack.Buffer, ack.Buffer.Length, result.RemoteEndPoint);
                PrintConsole($"[{server._bindAddress}] SENT DISCOVERY_ACK to {result.RemoteEndPoint}");
            }
        }
    }

    private async Task StartOwnSocket()
    {
        _udpClient = new UdpClient(new IPEndPoint(_bindAddress, _port));
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0], null);
        }

        // if shareToNetwork is false, we'll ignore addresses not on this machine
        var localAddresses = GetLocalAddresses();

        PrintConsole($"Listening on UDP {_port}");

        while (true)
        {
            var result = await _udpClient.ReceiveAsync();
            byte[] data = result.Buffer;

            // if network sharing is disabled, and endpoint is not local address, ignore
            if (!_shareToNetwork && !localAddresses.Contains(result.RemoteEndPoint.Address))
                continue;

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
            void PrintAck(Ack ack) =>
                PrintConsole($"SENT to  {result.RemoteEndPoint,-21}: " +
                $"{GVCPMessages.GetName(ack.Message)} (0x{ack.Message:X4}) " +
                $"{GVCPStatus.GetName(ack.Status)} (0x{ack.Status:X4}) " +
                $"length={ack.Buffer.Length - 8}"); // buffer contains header already

            // ------------------------------------------ CMD check

            // now we'll build the ack depending on the cmd
            Ack ack;
            int offset; // reused

            switch (command)
            {
                case GVCPMessages.READREG_CMD:
                    // READREG_CMD payload consists of one or more register addresses.
                    // we'll use the length in the header (which is payload length)
                    // to determine how many addresses to read (4 bytes per address)
                    int numAddresses = length / 4;

                    // now use numAddresses to get list of addresses to read:
                    uint[] addresses = new uint[numAddresses];
                    offset = 0;
                    for (int i = 0; i < numAddresses; i++)
                    {
                        addresses[i] = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
                        offset += 4;
                    }

                    Console.WriteLine($"List of addresses to read: {string.Join(", ", addresses.Select(a => $"0x{a:X8}"))}");

                    ack = BuildReadRegAck(req_id, addresses);
                    await _udpClient.SendAsync(ack.Buffer, ack.Buffer.Length, result.RemoteEndPoint);
                    break;
                case GVCPMessages.WRITEREG_CMD:
                    // WRITEREG_CMD payload consists of pairs of address + data,
                    // so layout looks like:
                    // register_address
                    // register_data
                    // register_address
                    // register_data

                    var sb = new StringBuilder();
                    sb.AppendLine($"[WRITEREG_CMD] Summary ({payload.Length / 8} registers):");

                    for (int i = 0; i < payload.Length; i += 8)
                    {
                        uint addressin = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(i, 4));
                        uint value = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(i + 4, 4));

                        sb.AppendLine($"  Address: 0x{addressin:X8} | Data: 0x{value:X8}");
                    }

                    Console.Write(sb.ToString());

                    // we'll do the logic in the write reg method
                    ack = BuildWriteRegAck(req_id, payload, result.RemoteEndPoint);
                    await _udpClient.SendAsync(ack.Buffer, ack.Buffer.Length, result.RemoteEndPoint);
                    break;
                case GVCPMessages.READMEM_CMD:
                    // from payload: "address" (4 bytes) is the starting address
                    offset = 0;

                    uint address = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
                    offset += 4;

                    // reserved (2 bytes)
                    offset += 2;

                    // count (2 bytes)
                    ushort count = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));

                    Console.WriteLine($"[READMEM] address is {address:X8}");

                    ack = BuildReadMemAck(req_id, address, count);
                    await _udpClient.SendAsync(ack.Buffer, ack.Buffer.Length, result.RemoteEndPoint);
                    break;
                case GVCPMessages.WRITEMEM_CMD:

                    // from payload: "address" (4 bytes) is the starting address
                    offset = 0;

                    uint addressToStartWritingTo = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
                    offset += 4;


                    // length includes address (4 bytes) + 1 byte per data. so just iterate
                    // over data
                    byte[] dataToWrite = new byte[length - 4];
                    for (int i = 0; i < (length - 4); i++)
                    {
                        dataToWrite[i] = payload[offset++];
                    }

                    Console.WriteLine($"[WRITEMEM] addrss to start writing to: {addressToStartWritingTo:X8}");

                    ack = BuildWriteMemAck(req_id, addressToStartWritingTo, dataToWrite, result.RemoteEndPoint);
                    await _udpClient.SendAsync(ack.Buffer, ack.Buffer.Length, result.RemoteEndPoint);
                    break;
                default:
                    // ack ids are the command id with the low bit set (DISCOVERY_CMD
                    // 0x0002 -> DISCOVERY_ACK 0x0003, and so on)
                    ack = BuildErrorAck(req_id, (ushort)(command | 1), GVCPStatus.GEV_STATUS_NOT_IMPLEMENTED);
                    await _udpClient.SendAsync(ack.Buffer, ack.Buffer.Length, result.RemoteEndPoint);
                    break;
            }

            PrintAck(ack);
        }
    }

    // --------------------------------------------------------------- methods

    // this prints to the console. prepends "GVCP: " to message
    private static void PrintConsole(string message) =>
        Console.WriteLine($"GVCP: {message}");

    // --------------------------------------------------------------- ack builder methods

    private Ack BuildDiscoveryAck(ushort req_id, IPAddress localIp)
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
        _deviceState.ReadRegister(0x0008, out byte[]? macHigh);
        _deviceState.ReadRegister(0x000C, out byte[]? macLow);
        byte[] mac = new byte[6];
        mac[0] = macHigh![2];
        mac[1] = macHigh[3];
        Array.Copy(macLow!, 0, mac, 2, 4);
        Array.Copy(mac, 0, ack, offset, 6);
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
        _deviceState.ReadMemory(0x0048, 32, out byte[]? manufacturer_name);
        Array.Copy(manufacturer_name!, 0, ack, offset, manufacturer_name!.Length);
        offset += 32;

        // model_name (32 bytes)
        _deviceState.ReadMemory(0x0068, 32, out byte[]? model_name);
        Array.Copy(model_name!, 0, ack, offset, model_name!.Length);
        offset += 32;

        // device_version (32 bytes)
        _deviceState.ReadMemory(0x0088, 32, out byte[]? device_version);
        Array.Copy(device_version!, 0, ack, offset, device_version!.Length);
        offset += 32;

        // manufacturer_specific_information (48 bytes)
        _deviceState.ReadMemory(0x00A8, 48, out byte[]? manufacturer_specific_information);
        Array.Copy(manufacturer_specific_information!, 0, ack, offset, manufacturer_specific_information!.Length);
        offset += 48;

        // serial_number (16 bytes)
        _deviceState.ReadMemory(0x00D8, 16, out byte[]? serial_number);
        Array.Copy(serial_number!, 0, ack, offset, serial_number!.Length);
        offset += 16;

        // user_defined_name (16 bytes)
        _deviceState.ReadMemory(0x00E8, 16, out byte[]? user_defined_name);
        Array.Copy(user_defined_name!, 0, ack, offset, user_defined_name!.Length);
        //offset += 16;

        // ------------------------------------------ header (8 bytes)

        // reset offset
        offset = 0;

        // status (2 bytes)
        ushort status = GVCPStatus.GEV_STATUS_SUCCESS;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), status);
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

        return new Ack(GVCPMessages.DISCOVERY_ACK, status, ack);
    }

    private Ack BuildReadRegAck(ushort req_id, uint[] addresses)
    {
        // header (8 bytes) + (4 bytes per address)
        byte[] ack = new byte[8 + (4 * addresses.Length)];

        // ------------------------------------------ payload

        int offset = 8; // skip header first

        // the results of each register read from are stored in here inside the loop.
        // we'll use this value to set the status in the header value
        ushort readRegisterResult = GVCPStatus.GEV_STATUS_SUCCESS;

        // value read from register should be 4 bytes
        byte[]? value = new byte[4];

        // read each value from address and copy to array
        foreach (var address in addresses)
        {
            // save status and if not successful, exit early. status
            // will be written as overall operation status
            readRegisterResult = _deviceState.ReadRegister(address, out value);
            if (readRegisterResult != GVCPStatus.GEV_STATUS_SUCCESS || value is null)
            {
                break;
            }

            Array.Copy(value, 0, ack, offset, 4);

            offset += 4;
        }

        // ------------------------------------------ header

        // reset to write header
        offset = 0;

        // status (2 bytes)
        // note: its all or nothing, so if any operation fails, whole operation fails
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), readRegisterResult);
        offset += 2;

        // acknowledge (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPMessages.READREG_ACK);
        offset += 2;

        // length (2 bytes) (only payload)
        // if not success, length will be 0
        ushort length = (readRegisterResult == GVCPStatus.GEV_STATUS_SUCCESS) ? (ushort)(4 * addresses.Length) : (ushort)0;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), length);
        offset += 2;

        // ack_id (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), req_id);
        //offset += 2;

        return new Ack(GVCPMessages.READREG_ACK, readRegisterResult, ack);
    }

    private Ack BuildWriteRegAck(ushort req_id, ReadOnlySpan<byte> payload, IPEndPoint sender)
    {
        // header (8 bytes) + 4 bytes per item in payload
        byte[] ack = new byte[8 + 4];
        int offset = 8;

        // ------------------------------------------ payload

        ushort status = GVCPStatus.GEV_STATUS_SUCCESS;

        // each pair is a register_address, then register_data
        uint register_address, register_data;

        // payload / 4 = actual uints. / 2 because pairs
        int numberOfRegisters = payload.Length / 4 / 2;
        // different offset for reading from payload
        int offsetForReading = 0;

        // how far we got. on success that's every register, on failure it's the
        // index of the one that failed
        int index = 0;

        for (index = 0; index < numberOfRegisters; index++)
        {
            register_address = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offsetForReading, 4));
            offsetForReading += 4;
            register_data = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offsetForReading, 4));
            offsetForReading += 4;

            byte[] buffer = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, register_data);

            // the register map decides whether this address exists, whether it's
            // writable, and what the write does
            status = _deviceState.WriteRegister(sender, register_address, buffer);

            if (status != GVCPStatus.GEV_STATUS_SUCCESS)
            {
                break; // exit early, will be reported
            }
        }

        // reserved (2 bytes)
        // spec says set 0 on transmission, ignore on reception.
        // so we'll set to 0?
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), 0x0000);
        offset += 2;

        // index (2 bytes)
        // spec says on success, index indicates how many written successfully,
        // on failure, indicates the index of the register in the list
        // where the error occurred
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), (ushort)index);

        // ------------------------------------------ header

        // reset to write header
        offset = 0;

        // status (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), status);
        offset += 2;

        // acknowledge (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPMessages.WRITEREG_ACK);
        offset += 2;

        // length (2 bytes) (only payload): (reserved + index = 4 bytes)
        ushort length = (status == GVCPStatus.GEV_STATUS_SUCCESS) ? (ushort)4 : (ushort)0;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), length);
        offset += 2;

        // ack_id (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), req_id);
        //offset += 2;

        return new Ack(GVCPMessages.WRITEREG_ACK, status, ack);
    }

    private Ack BuildReadMemAck(ushort req_id, uint address, ushort count)
    {
        // header (8 bytes) + address (4 bytes) + (1 byte per data)
        byte[] ack = new byte[8 + 4 + count];
        int offset = 8;

        // ------------------------------------------ payload

        // first value is the address
        BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(offset, 4), address);
        offset += 4;

        // then just read memory and copy to ack payload section
        ushort status = _deviceState.ReadMemory(address, count, out byte[]? value);
        if (status == GVCPStatus.GEV_STATUS_SUCCESS)
            Array.Copy(value!, 0, ack, offset, value!.Length);

        // ------------------------------------------ header

        // reset to write header
        offset = 0;

        // status (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), status);
        offset += 2;

        // acknowledge (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPMessages.READMEM_ACK);
        offset += 2;

        // length (2 bytes) (only payload): address + 1 byte per data
        ushort length = (status == GVCPStatus.GEV_STATUS_SUCCESS) ? (ushort)(4 + count) : (ushort)0;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), length);
        offset += 2;

        // ack_id (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), req_id);
        //offset += 2;

        return new Ack(GVCPMessages.READMEM_ACK, status, ack);
    }

    private Ack BuildWriteMemAck(ushort req_id, uint address, byte[] data, IPEndPoint sender)
    {
        // header (8 bytes) + address (4 bytes) + (1 byte per data)
        byte[] ack = new byte[8 + 4];
        int offset = 8;

        // ------------------------------------------ payload

        // first we'll actually write the data to device
        ushort status = _deviceState.WriteMemory(sender, address, data);

        // reserved (2 bytes)
        // spec says set 0 on transmission, ignore on reception.
        // so we'll set to 0?
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), 0x0000);
        offset += 2;

        // index (2 bytes)
        // number of bytes written successfully. the write is all-or-nothing, so
        // it's either everything or nothing at all
        ushort written = (status == GVCPStatus.GEV_STATUS_SUCCESS) ? (ushort)data.Length : (ushort)0;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), written);

        // ------------------------------------------ header

        // reset to write header
        offset = 0;

        // status (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), status);
        offset += 2;

        // acknowledge (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), GVCPMessages.WRITEMEM_ACK);
        offset += 2;

        // length (2 bytes) (only payload): (reserved + index = 4 bytes)
        ushort length = (status == GVCPStatus.GEV_STATUS_SUCCESS) ? (ushort)4 : (ushort)0;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), length);
        offset += 2;

        // ack_id (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(offset, 2), req_id);
        //offset += 2;

        return new Ack(GVCPMessages.WRITEMEM_ACK, status, ack);
    }

    // header-only ack, for when there is nothing to report but a status
    private static Ack BuildErrorAck(ushort req_id, ushort message, ushort status)
    {
        byte[] ack = new byte[8];

        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(0, 2), status);
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2, 2), message);
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(4, 2), 0); // length
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(6, 2), req_id);

        return new Ack(message, status, ack);
    }

    // ------------------------------------------------------------ methods

    private static HashSet<IPAddress> GetLocalAddresses()
    {
        var localAddresses = new HashSet<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var properties = nic.GetIPProperties();

            // attempt to look for adapters with default gateway (hopefully wifi adapter),
            // ignoring virtual adapters like wsl's
            if (properties.GatewayAddresses.Count == 0) continue;

            foreach (var address in properties.UnicastAddresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    localAddresses.Add(address.Address);
        }

        return localAddresses;
    }
}