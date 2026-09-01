// --------------------------------------------------------------------------------
// DeviceState.cs
//
// accessed by GVCP and GVSP. holds the device memory for the bootstrap and
// manufacturer-specific registers, plus helper methods to read from these.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace GigEVirtual;

internal class DeviceState
{
    // --------------------------------------------------------------- fields and properties

    // registers: GigE-compliant devices have:
    // - bootstrap registers
    // - manufacturer-specific registers
    //
    // these registers, as far as gigevision knows, are what the actual device
    // represents.
    //
    // bootstrap registers must be present on all gigevision compliant devices,
    // (we must implement) and consist of version, device mode, etc. registers)
    //
    // non-bootstrap registers start at 0xA000 (so memory should at least be
    // 40960 bytes)
    //
    // addresses are 32-bit. but spec doesn't say about a strict memory ceiling.
    // maybe 1MB is fine?

    private byte[] _memory = new byte[0x100000];

    // just in case it's accessed at the same time either from GVCP or GVSP
    // implementations.
    private object _registersLock = new();

    private IPEndPoint? _primaryController;

    // --------------------------------------------------------------- constructors

    public DeviceState()
    {
        // version
        WriteMemoryUint(0x0000, 0x00020002); // version 2.2

        // device mode
        uint deviceMode =
            Pack(value: 1, specBitStart: 0, width: 1) | // endianness
            Pack(value: 0, specBitStart: 1, width: 3) | // device_class (transmitter)
            Pack(value: 0, specBitStart: 6, width: 2) | // current_link_configuration (single link config for now)
            Pack(value: 2, specBitStart: 24, width: 8); // character_set_index
        WriteMemoryUint(0x0004, deviceMode);

        // device mac address (high)
        WriteMemoryUint(0x0008, 0x0000AABB);

        // device mac address (low)
        WriteMemoryUint(0x000C, 0xCCDDEEFF);

        // network interface capability
        uint networkInterfaceCapability =
            Pack(0, specBitStart: 0, width: 1) | // PAUSE_reception
            Pack(0, specBitStart: 1, width: 1) | // PAUSE_reneration
            Pack(1, specBitStart: 29, width: 1) | // LLA
            Pack(1, specBitStart: 30, width: 1) | // DHCP
            Pack(1, specBitStart: 31, width: 1); // Persistent_IP
        WriteMemoryUint(0x0010, networkInterfaceCapability);

        // network interface configuration
        uint networkInterfaceConfiguration =
            Pack(0, specBitStart: 0, width: 1) | // PAUSE_reception
            Pack(0, specBitStart: 1, width: 1) | // PAUSE_reneration
            Pack(1, specBitStart: 29, width: 1) | // LLA
            Pack(1, specBitStart: 30, width: 1) | // DHCP
            Pack(1, specBitStart: 31, width: 1); // Persistent_IP
        WriteMemoryUint(0x0014, networkInterfaceConfiguration);

        // current IP address
        //WriteMemory(0x0024, null);

        // current subnet mask
        WriteMemoryUint(0x0034, 0xFFFFFF00);

        // current default gateway
        byte[] default_gateway = IPAddress.Parse("192.168.1.1").GetAddressBytes();
        WriteMemory(0x0044, default_gateway);

        // manufacturer name
        WriteMemoryString(0x0048, "VIRTUAL", 32);

        // model name
        WriteMemoryString(0x0068, "MODEL", 32);

        // device version
        WriteMemoryString(0x0088, "1.0", 32);

        // manufacturer info
        WriteMemoryString(0x00A8, "C# GigEVision Cam", 48);

        // serial number register
        WriteMemoryString(0x00D8, "S0001", 16);

        // user-defined name
        WriteMemoryString(0x00E8, "virtualDev", 16);

        // number of network interfaces
        WriteMemoryUint(0x0600, 1);

        // gvsp capability
        uint gvspCapability =
            Pack(1, specBitStart: 0, width: 1) | // SCSPx is supported
            Pack(0, specBitStart: 1, width: 1) | // legacy_16bit_block_id_supported
            Pack(1, specBitStart: 2, width: 1) | // SCMBSx_supported
            Pack(1, specBitStart: 3, width: 1);  // SCEBAx_supported
        WriteMemoryUint(0x092C, gvspCapability);

        // gvcp capability
        uint gvcpCapability =
            Pack(1, specBitStart: 0, width: 1) | // user_defined_name supported
            Pack(1, specBitStart: 1, width: 1);  // serial_number supported
        WriteMemoryUint(0x0934, gvcpCapability);

        // heartbeat timeout
        WriteMemoryUint(0x0938, 0x0BB8); // factory default

        // control channel privilege
        //WriteMemory(0x0A00, null);

        // first url and xml
        string xmlContent = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GigEVirtual.xml"));
        int xmlLength = Encoding.ASCII.GetBytes(xmlContent).Length;
        uint xmlAddress = 0xA200;
        string firstUrl = $"Local:GigEVirtual.xml;{xmlAddress:x};{xmlLength:x}";

        WriteMemoryString(xmlAddress, xmlContent, xmlLength);
        WriteMemoryString(0x0200, firstUrl, 512);

        // manufacturer-values
        WriteMemoryUint(0xA000, 640); // width
        WriteMemoryUint(0xA004, 480); // height
        WriteMemoryUint(0xA008, GVSPPixelFormats.Mono8); // pixelFormat
        WriteMemoryUint(0xA014, 0); // acquisition mode (0 = continuous)

        // gvsp registers

        // stream channel port 0 (scp0)
        WriteMemoryUint(0x0D00, 0);

        // stream channel packet size 0 (scps0)
        uint packetSize = 1500;
        WriteMemoryUint(0x0D04, packetSize);

        // stream channel packet delay 0 (scpd0)
        WriteMemoryUint(0x0D08, 0);

        // stream channel destination address 0 (scda0)
        WriteMemoryUint(0x0D18, 0);

        // stream channel max block size 0 (scmbs0)
        // read width/height again to computer values
        ReadRegister(0xA000, out byte[]? w);
        ReadRegister(0xA004, out byte[]? h);
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(w);
        uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h);
        int bytesPerPixel = 1; // Mono8
        ulong payloadSize = (ulong)width * height * (ulong)bytesPerPixel;

        WriteMemoryUint(0x0D34, (uint)(payloadSize >> 32));
        WriteMemoryUint(0x0D38, (uint)(payloadSize & 0xFFFFFFFF));

        // stream channel max packet count 0 (scmpc0)
        uint overhead = 8;
        uint maxPacketCount = (uint)Math.Ceiling((double)payloadSize / (packetSize - overhead)) + 2; // leader/trailer
        WriteMemoryUint(0x0D30, maxPacketCount);

        // stream channel extended bootstrap address 0 (sceba0)
        WriteMemoryUint(0x0D3C, 0);

        // number of message channels
        WriteMemoryUint(0x0900, 0);

        // number of stream channels
        WriteMemoryUint(0x0904, 1);
    }

    // --------------------------------------------------------------- methods

    private static uint Pack(uint value, int specBitStart, int width)
    {
        int shift = 32 - specBitStart - width;
        uint mask = (1u << width) - 1;
        return (value & mask) << shift;
    }

    private ushort WriteMemoryString(uint address, string value, int registerLength)
    {
        int paddedLength = ((registerLength + 3) / 4) * 4;
        byte[] buffer = new byte[paddedLength];
        byte[] stringBytes = Encoding.ASCII.GetBytes(value);

        // need to leave at least 1 byte for NULL terminator,
        // so, maybe..

        int copyLength = Math.Min(registerLength, stringBytes.Length);

        if (copyLength >= registerLength && stringBytes.Length >= registerLength)
            copyLength = registerLength;
        else
            copyLength = Math.Min(copyLength, registerLength - 1); // leave for null

        Array.Copy(stringBytes, buffer, copyLength);
        return WriteMemory(address, buffer);
    }

    public ushort ReadMemory(uint address, ushort count, out byte[]? value)
    {
        // we'll lock the whole method so alignment/bounds don't change,
        // for safer reading
        lock (_registersLock)
        {
            // spec (READMEM section) says number of addresses read must
            // be a multiple of 4, otherwise return bad alignment status
            if (address % 4 != 0 || count % 4 != 0)
            {
                value = null;
                return GVCPStatus.GEV_STATUS_BAD_ALIGNMENT;
            }

            // now we check for invalid address input
            //
            // {0} -> length 1
            //
            // if input is 0: valid
            // if input is 1: invalid
            // if input is 2: invalid
            //
            // {0, 1, 2} -> length 3
            //
            // if input 0: valid
            // if input 1: valid
            // if input 2: valid
            // if input 3: invalid
            //
            // so input address must be less than length.
            //
            // but address is uint and count is ushort, if address is close to max
            // and we add count, wrap around?

            if (address > _memory.Length || count > _memory.Length - address)
            {
                value = null;
                return GVCPStatus.GEV_STATUS_INVALID_ADDRESS;
            }

            value = _memory.AsSpan<byte>((int)address, (int)count).ToArray();
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }
    }

    public ushort WriteMemory(uint address, byte[] value)
    {
        lock (_registersLock)
        {
            if (address % 4 != 0 || value.Length % 4 != 0)
            {
                return GVCPStatus.GEV_STATUS_BAD_ALIGNMENT;
            }

            if (address > _memory.Length || value.Length > _memory.Length - address)
            {
                return GVCPStatus.GEV_STATUS_INVALID_ADDRESS;
            }

            Array.Copy(value, 0, _memory, (int)address, value.Length);
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }
    }

    public ushort WriteMemoryUint(uint address, uint value)
    {
        byte[] array = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(array, value);
        return WriteMemory(address, array);
    }

    public ushort ReadRegister(uint address, out byte[]? value)
    {
        return ReadMemory(address, 4, out value);
    }

    public ushort WriteRegister(uint address, byte[] value)
    {
        return WriteMemory(address, value);
    }

    // resolved from gvcp server, so server must set to device...
    public void SetIP(IPAddress ipLocal)
    {
        WriteMemory(0x0024, ipLocal.GetAddressBytes());
    }

    public ushort HandleCCPWrite(IPEndPoint sender, byte[] value)
    {
        uint requested = BinaryPrimitives.ReadUInt32BigEndian(value);

        if (requested == 0)
        {
            // closing control channel
            if (Equals(_primaryController, sender)) _primaryController = null;
            WriteMemoryUint(0x0A00, 0);
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }

        if (_primaryController == null || Equals(_primaryController, sender))
        {
            // same app re-requesting is allowed
            _primaryController = sender;
            WriteMemoryUint(0x0A00, requested);
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }

        // someone else already has it
        return GVCPStatus.GEV_STATUS_ACCESS_DENIED;
    }
}