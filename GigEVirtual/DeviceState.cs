// --------------------------------------------------------------------------------
// DeviceState.cs
//
// accessed by GVCP and GVSP. holds the device memory for the bootstrap and
// manufacturer-specific registers, plus the register map that decides which
// addresses exist and who is allowed to write to them.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace GigEVirtual;

internal enum RegAccess { ReadOnly, ReadWrite }

// the gige bootstrap registers are big-endian by definition. a device is free
// to lay its own registers out the other way round, and most do.
internal enum Endianness { Big, Little }

internal class Register
{
    public uint Address;
    public int Length;
    public RegAccess Access;

    // this register's own bytes. storage follows the registers rather than the
    // address space, which is what lets a device scatter registers across the
    // full 32-bit range without allocating the gaps between them.
    public byte[] Data = [];

    // only the primary application may write. false for CCP itself, which has
    // to be writable before anyone holds control.
    public bool NeedsControl = true;

    // genicam command register. set to 0 again once the write goes through
    public bool SelfClearing;

    // how a multi-byte value sits in this register's bytes. only matters where we
    // read or write the value ourselves: an application reads the endianness out
    // of the device description and we hand it the bytes either way.
    public Endianness Endianness = Endianness.Big;

    // the stream channel registers describe a transfer in progress, so changing
    // them mid-stream would leave the application decoding against the wrong
    // shape. spec has these answer BUSY instead.
    public bool LockedWhileStreaming;

    // runs before the value is stored. the bytes it is handed are the ones that
    // get written, so a hook can clamp them in place. returning anything but
    // SUCCESS aborts the write.
    public Func<IPEndPoint, byte[], ushort>? OnWrite;
}

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

    // every register this device answers for, and the bytes behind each one.
    // an address that is not covered here does not exist as far as an
    // application is concerned.
    private readonly SortedList<uint, Register> _registers = [];

    // just in case it's accessed at the same time either from GVCP or GVSP
    // implementations.
    private object _registersLock = new();

    private IPEndPoint? _primaryController;

    // one-shot, re-armed by every command from the primary application. if it
    // ever fires, the application went away without closing its channel
    private System.Threading.Timer? _heartbeatTimer;

    // set by the device, so closing the channel can stop streaming without this
    // class knowing GVSP exists
    private Action? _controlChannelClosed;

    // where the free-running timestamp counter was last reset from
    private long _timestampOrigin = Stopwatch.GetTimestamp();

    // set by the device. firing a test packet needs a socket, which is the
    // transmitter's business rather than ours
    private Func<int, ushort>? _fireTestPacket;

    // set by the device, so we can tell whether a transfer is in progress
    private Func<bool>? _isStreaming;

    // set by the device: what it is currently configured to send. the payload
    // registers are bootstrap, but what belongs in them depends on geometry
    // registers the device owns and places wherever it likes.
    private Func<(uint Width, uint Height, uint PixelFormat)>? _geometry;

    // the packet sizes we can actually serve. a request outside this gets
    // rounded rather than refused
    private const uint MinPacketSize = 576;
    private const uint MaxPacketSize = 16384;


    // --------------------------------------------------------------- constructors

    // xmlContent is the device description this device serves, xmlFileName the
    // name an application sees in the first url, and xmlAddress where the blob
    // sits in the register map.
    public DeviceState(string xmlContent,
                       string xmlFileName,
                       uint xmlAddress,
                       string manufacturerName = "cenfra",
                       string modelName = "modelVirtual",
                       string deviceVersion = "1.0",
                       string manufacturerInfo = "C# cam",
                       string serialNumber = "S0001",
                       string deviceName = "virtualDev")
    {
        // version
        DefineUint(0x0000, RegAccess.ReadOnly, 0x00020002); // version 2.2

        // device mode
        uint deviceMode =
            Pack(value: 1, specBitStart: 0, width: 1) | // endianness
            Pack(value: 0, specBitStart: 1, width: 3) | // device_class (transmitter)
            Pack(value: 0, specBitStart: 6, width: 2) | // current_link_configuration (single link config for now)
            Pack(value: 2, specBitStart: 24, width: 8); // character_set_index
        DefineUint(0x0004, RegAccess.ReadOnly, deviceMode);

        // generate random mac (temp)
        var rnd = new Random();
        byte[] mac = new byte[6];
        rnd.NextBytes(mac);
        mac[0] = (byte)(mac[0] & 0xFE);

        // device mac address (high)
        DefineUint(0x0008, RegAccess.ReadOnly, (uint)((mac[0] << 8) | mac[1]));
        // device mac address (low)
        DefineUint(0x000C, RegAccess.ReadOnly, (uint)((mac[2] << 24) | (mac[3] << 16) | (mac[4] << 8) | mac[5]));

        // network interface capability
        uint networkInterfaceCapability =
            Pack(0, specBitStart: 0, width: 1) | // PAUSE_reception
            Pack(0, specBitStart: 1, width: 1) | // PAUSE_reneration
            Pack(1, specBitStart: 29, width: 1) | // LLA
            Pack(1, specBitStart: 30, width: 1) | // DHCP
            Pack(1, specBitStart: 31, width: 1); // Persistent_IP
        DefineUint(0x0010, RegAccess.ReadOnly, networkInterfaceCapability);

        // network interface configuration
        uint networkInterfaceConfiguration =
            Pack(0, specBitStart: 0, width: 1) | // PAUSE_reception
            Pack(0, specBitStart: 1, width: 1) | // PAUSE_reneration
            Pack(1, specBitStart: 29, width: 1) | // LLA
            Pack(1, specBitStart: 30, width: 1) | // DHCP
            Pack(1, specBitStart: 31, width: 1); // Persistent_IP
        DefineUint(0x0014, RegAccess.ReadWrite, networkInterfaceConfiguration);

        // current IP address. filled in by SetIP once the server knows its bind address
        DefineUint(0x0024, RegAccess.ReadOnly, 0);

        // current subnet mask
        DefineUint(0x0034, RegAccess.ReadOnly, 0xFFFFFF00);

        // current default gateway
        byte[] default_gateway = IPAddress.Parse("192.168.1.1").GetAddressBytes();
        DefineUint(0x0044, RegAccess.ReadOnly, BinaryPrimitives.ReadUInt32BigEndian(default_gateway));

        // manufacturer name
        DefineString(0x0048, 32, RegAccess.ReadOnly, manufacturerName); // "VIRTUAL"

        // model name
        DefineString(0x0068, 32, RegAccess.ReadOnly, modelName); // "MODEL"

        // device version
        DefineString(0x0088, 32, RegAccess.ReadOnly, deviceVersion); // "1.0"

        // manufacturer info
        DefineString(0x00A8, 48, RegAccess.ReadOnly, manufacturerInfo); // "C# GigEVision Cam"

        // serial number register
        DefineString(0x00D8, 16, RegAccess.ReadOnly, serialNumber); // "S0001"

        // user-defined name
        DefineString(0x00E8, 16, RegAccess.ReadWrite, deviceName); // "virtualDev"

        // first url and xml
        int xmlLength = Encoding.ASCII.GetBytes(xmlContent).Length;
        string firstUrl = $"Local:{xmlFileName};{xmlAddress:x};{xmlLength:x}";

        DefineString(xmlAddress, xmlLength, RegAccess.ReadOnly, xmlContent);
        DefineString(0x0200, 512, RegAccess.ReadOnly, firstUrl);

        // second url. we only serve one xml, but applications read this register
        // and it should come back empty rather than not existing
        DefineString(0x0400, 512, RegAccess.ReadOnly, "");

        // number of network interfaces
        DefineUint(0x0600, RegAccess.ReadOnly, 1);

        // number of message channels
        DefineUint(0x0900, RegAccess.ReadOnly, 0);

        // number of stream channels
        DefineUint(0x0904, RegAccess.ReadOnly, 1);

        // gvsp capability
        uint gvspCapability =
            Pack(1, specBitStart: 0, width: 1) | // SCSPx is supported
            Pack(0, specBitStart: 1, width: 1) | // legacy_16bit_block_id_supported
            Pack(1, specBitStart: 2, width: 1) | // SCMBSx_supported
            Pack(1, specBitStart: 3, width: 1);  // SCEBAx_supported
        DefineUint(0x092C, RegAccess.ReadOnly, gvspCapability);

        // gvcp capability
        uint gvcpCapability =
            Pack(1, specBitStart: 0, width: 1) |  // user_defined_name supported
            Pack(1, specBitStart: 1, width: 1) |  // serial_number supported
            Pack(1, specBitStart: 6, width: 1) |  // test packets carry LFSR data
            Pack(1, specBitStart: 30, width: 1) | // WRITEMEM supported
            Pack(1, specBitStart: 31, width: 1);  // concatenation supported
        DefineUint(0x0934, RegAccess.ReadOnly, gvcpCapability);

        // heartbeat timeout. spec says a value under 500 ms is raised to 500, and
        // the register must show the value actually in use
        DefineUint(0x0938, RegAccess.ReadWrite, 0x0BB8).OnWrite = (_, v) => // factory default 3000
        {
            if (ReadU32(v) < 500) BinaryPrimitives.WriteUInt32BigEndian(v, 500);
            return GVCPStatus.GEV_STATUS_SUCCESS;
        };

        // timestamp tick frequency, high then low. 1 GHz, so one tick is one
        // nanosecond. this is also the unit SCPD0 uses for its inter-packet delay,
        // and the spec says that register has no effect without a timestamp
        DefineUint(0x093C, RegAccess.ReadOnly, 0);
        DefineUint(0x0940, RegAccess.ReadOnly, 1_000_000_000);

        // timestamp control. the spec calls this write-only, but a self-clearing
        // register behaves the same from the application side: writing a 1 runs
        // the operation and there is no need to write 0 back afterwards
        DefineUint(0x0944, RegAccess.ReadWrite, 0, selfClearing: true).OnWrite = (_, v) =>
        {
            // latch is bit 30 and reset is bit 31 in spec numbering, so the low
            // two bits. spec says latch first when an application sets both
            uint control = ReadU32(v);

            if ((control & 0x2) != 0) LatchTimestamp();
            if ((control & 0x1) != 0) _timestampOrigin = Stopwatch.GetTimestamp();

            return GVCPStatus.GEV_STATUS_SUCCESS;
        };

        // latched timestamp value, high then low. latching is what makes two
        // 32-bit reads add up to one coherent 64-bit value
        DefineUint(0x0948, RegAccess.ReadOnly, 0);
        DefineUint(0x094C, RegAccess.ReadOnly, 0);

        // control channel privilege. writable without control, (how
        // control gets claimed in the first place)
        DefineUint(0x0A00, RegAccess.ReadWrite, 0, needsControl: false).OnWrite = HandleCCPWrite;

        // gvsp registers

        // stream channel port 0 (scp0)
        DefineUint(0x0D00, RegAccess.ReadWrite, 0);

        // stream channel packet size 0 (scps0). the low 16 bits are the size,
        // bit 0 asks for a test packet and bit 1 sets the ip don't fragment flag
        uint packetSize = 1500;
        Register scps0 = DefineUint(0x0D04, RegAccess.ReadWrite, packetSize);
        scps0.LockedWhileStreaming = true;
        scps0.OnWrite = (_, v) =>
        {
            uint value = ReadU32(v);
            uint requested = value & 0xFFFF;

            // spec: a size we cannot serve is rounded to the nearest we can, and
            // the register has to show what the application will actually get
            uint granted = Math.Clamp(requested, MinPacketSize, MaxPacketSize);

            if (_geometry is not null)
            {
                (uint width, uint height, uint pixelFormat) = _geometry();

                ushort status = UpdatePayloadSize(width, height, pixelFormat, granted);
                if (status != GVCPStatus.GEV_STATUS_SUCCESS) return status;
            }

            // fire_test_packet is bit 0 in spec numbering and self-clears, so
            // write back the granted size without it
            bool fire = (value & 0x80000000) != 0;
            BinaryPrimitives.WriteUInt32BigEndian(v, (value & 0x7FFF0000) | granted);

            // spec: do not fire when we had to round the size the application asked for
            if (fire && requested == granted)
                _fireTestPacket?.Invoke((int)granted);

            return GVCPStatus.GEV_STATUS_SUCCESS;
        };

        // stream channel packet delay 0 (scpd0)
        DefineUint(0x0D08, RegAccess.ReadWrite, 0);

        // stream channel destination address 0 (scda0)
        DefineUint(0x0D18, RegAccess.ReadWrite, 0).LockedWhileStreaming = true;

        // stream channel max packet count 0 (scmpc0)
        DefineUint(0x0D30, RegAccess.ReadOnly, 0);

        // stream channel max block size 0 (scmbs0), high then low
        DefineUint(0x0D34, RegAccess.ReadOnly, 0);
        DefineUint(0x0D38, RegAccess.ReadOnly, 0);

        // stream channel extended bootstrap address 0 (sceba0)
        DefineUint(0x0D3C, RegAccess.ReadOnly, 0);

    }

    // --------------------------------------------------------------- register map

    private Register Define(uint address, int length, RegAccess access, bool needsControl,
                            bool selfClearing, Endianness endianness = Endianness.Big)
    {
        var register = new Register
        {
            Address = address,
            // pad up to a 4 byte boundary so aligned reads can reach the last bytes
            Length = (length + 3) / 4 * 4,
            Access = access,
            NeedsControl = needsControl,
            SelfClearing = selfClearing,
            Endianness = endianness,
        };

        register.Data = new byte[register.Length];

        _registers.Add(address, register);
        return register;
    }

    // a device defines its own feature registers through these. everything the
    // bootstrap needs is already here by the time it gets a chance.
    public Register DefineUint(uint address, RegAccess access, uint value,
                               bool needsControl = true, bool selfClearing = false,
                               Func<IPEndPoint, byte[], ushort>? onWrite = null,
                               Endianness endianness = Endianness.Big)
    {
        // endianness has to be settled before the value goes in, or the default
        // lands the wrong way round
        Register register = Define(address, 4, access, needsControl, selfClearing, endianness);
        register.OnWrite = onWrite;
        PokeUint(address, value);
        return register;
    }

    public Register DefineFloat(uint address, RegAccess access, float value,
                                Func<IPEndPoint, byte[], ushort>? onWrite = null)
    {
        Register register = Define(address, 4, access, needsControl: true, selfClearing: false);
        register.OnWrite = onWrite;
        BinaryPrimitives.WriteSingleBigEndian(register.Data, value);
        return register;
    }

    public Register DefineString(uint address, int length, RegAccess access, string value,
                                 Func<IPEndPoint, byte[], ushort>? onWrite)
    {
        Register register = DefineString(address, length, access, value);
        register.OnWrite = onWrite;
        return register;
    }

    private Register DefineString(uint address, int length, RegAccess access, string value)
    {
        Register register = Define(address, length, access, needsControl: true, selfClearing: false);

        byte[] stringBytes = Encoding.ASCII.GetBytes(value);

        // need to leave at least 1 byte for NULL terminator, unless the value
        // fills the register exactly
        int copyLength = Math.Min(length, stringBytes.Length);
        if (copyLength < length || stringBytes.Length < length)
            copyLength = Math.Min(copyLength, length - 1);

        stringBytes.AsSpan(0, copyLength).CopyTo(register.Data);
        return register;
    }

    // adds every fixed-address device register the description declares. an
    // address we already define is left alone: those carry the bootstrap
    // behaviour and the hooks, which the description knows nothing about.
    // returns how many were added, and how many were already covered.
    public (int Added, int Skipped) DefineFromXml(string xml)
    {
        lock (_registersLock)
        {
            int added = 0, skipped = 0;

            foreach (XmlRegister register in GenICamXml.Registers(xml))
            {
                if (Resolve(register.Address) is not null ||
                    Overlaps(register.Address, register.Length))
                {
                    skipped++;
                    continue;
                }

                Define(register.Address, register.Length, register.Access,
                       needsControl: true, selfClearing: false, register.Endianness);
                added++;
            }

            return (added, skipped);
        }
    }

    // whether a proposed register would run into one that already exists. the
    // map only works if ranges never overlap.
    private bool Overlaps(uint address, int length)
    {
        uint end = address + (uint)((length + 3) / 4 * 4);

        for (uint position = address; position < end; position += 4)
            if (Resolve(position) is not null) return true;

        // and make sure we would not swallow the start of a later register
        foreach (uint existing in _registers.Keys)
            if (existing >= address && existing < end) return true;

        return false;
    }

    // attaches behaviour to an already-defined register. this is the seam a device
    // implementation uses, instead of GVCP special-casing addresses.
    public void OnWrite(uint address, Func<IPEndPoint, byte[], ushort> hook) =>
        _registers[address].OnWrite = hook;

    // finds the register containing an address, or null if nothing is mapped
    // there. reads landing in the middle of a register are normal (the xml gets
    // fetched in chunks), so this is a range lookup rather than an exact match
    private Register? Resolve(uint address)
    {
        IList<uint> keys = _registers.Keys;

        int low = 0, high = keys.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (keys[mid] <= address) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }

        if (found < 0) return null;

        Register register = _registers.Values[found];
        return address < register.Address + register.Length ? register : null;
    }

    // every register touched by [address, address + count), or null if any part
    // of that range is unmapped
    private List<Register>? Cover(uint address, int count)
    {
        var covered = new List<Register>();
        uint position = address;
        uint end = address + (uint)count;

        while (position < end)
        {
            Register? register = Resolve(position);
            if (register is null) return null;

            covered.Add(register);
            position = register.Address + (uint)register.Length;
        }

        return covered;
    }

    // copies a range out of however many registers it spans. Cover has already
    // established that the whole range is mapped and contiguous.
    private static byte[] Gather(List<Register> covered, uint address, int count)
    {
        byte[] result = new byte[count];
        uint end = address + (uint)count;

        foreach (Register register in covered)
        {
            uint from = Math.Max(address, register.Address);
            uint to = Math.Min(end, register.Address + (uint)register.Length);
            if (to <= from) continue;

            register.Data.AsSpan((int)(from - register.Address), (int)(to - from))
                .CopyTo(result.AsSpan((int)(from - address)));
        }

        return result;
    }

    // --------------------------------------------------------------- methods

    private static uint Pack(uint value, int specBitStart, int width)
    {
        int shift = 32 - specBitStart - width;
        uint mask = (1u << width) - 1;
        return (value & mask) << shift;
    }

    private static uint ReadU32(byte[] value) => BinaryPrimitives.ReadUInt32BigEndian(value);

    // the bytes behind an address, wherever they live. throws for an unmapped
    // address, which would be our own bug rather than an application's.
    private Span<byte> Bytes(uint address, int count)
    {
        Register register = Resolve(address)
            ?? throw new ArgumentOutOfRangeException(nameof(address), $"no register at 0x{address:X8}");

        return register.Data.AsSpan((int)(address - register.Address), count);
    }

    // unchecked access straight to the register bytes, for our own use. client
    // traffic goes through ReadMemory/WriteMemory instead.
    private uint PeekUint(uint address)
    {
        Span<byte> bytes = Bytes(address, 4);

        return EndiannessAt(address) == Endianness.Big
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private void PokeUint(uint address, uint value)
    {
        Span<byte> bytes = Bytes(address, 4);

        if (EndiannessAt(address) == Endianness.Big)
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    }

    private Endianness EndiannessAt(uint address) =>
        Resolve(address)?.Endianness ?? Endianness.Big;

    // the value of a 4-byte register, read the way that register stores it. this
    // is what anything outside DeviceState should use rather than parsing the
    // bytes itself and guessing at the byte order.
    public uint ReadUint(uint address)
    {
        lock (_registersLock) return PeekUint(address);
    }

    // float registers are always big-endian in the descriptions we serve. a
    // device that stores one the other way round can read the bytes itself.
    public float ReadFloat(uint address)
    {
        lock (_registersLock) return BinaryPrimitives.ReadSingleBigEndian(Bytes(address, 4));
    }

    public ushort ReadMemory(uint address, ushort count, out byte[]? value)
    {
        // we'll lock the whole method so alignment/bounds don't change,
        // for safer reading
        lock (_registersLock)
        {
            value = null;

            // spec (READMEM section) says number of addresses read must
            // be a multiple of 4, otherwise return bad alignment status
            if (address % 4 != 0 || count % 4 != 0)
                return GVCPStatus.GEV_STATUS_BAD_ALIGNMENT;

            List<Register>? covered = count == 0 ? null : Cover(address, count);
            if (covered is null)
                return GVCPStatus.GEV_STATUS_INVALID_ADDRESS;

            value = Gather(covered, address, count);
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }
    }

    // index reports what the ack needs: on success the number of bytes written,
    // on failure the byte offset where the write stopped. the spec has writes
    // before that point stand, so this applies register by register rather than
    // all at once.
    public ushort WriteMemory(IPEndPoint sender, uint address, byte[] value, out int index)
    {
        lock (_registersLock)
        {
            index = 0;

            if (address % 4 != 0 || value.Length % 4 != 0)
                return GVCPStatus.GEV_STATUS_BAD_ALIGNMENT;

            if (value.Length == 0)
                return GVCPStatus.GEV_STATUS_INVALID_ADDRESS;

            List<Register>? covered = Cover(address, value.Length);

            // reads may start mid-register, writes may not
            if (covered is null || covered[0].Address != address)
                return GVCPStatus.GEV_STATUS_INVALID_ADDRESS;

            foreach (Register register in covered)
            {
                index = (int)(register.Address - address);
                int length = Math.Min(register.Length, value.Length - index);
                byte[] slice = value[index..(index + length)];

                if (register.Access == RegAccess.ReadOnly)
                    return GVCPStatus.GEV_STATUS_WRITE_PROTECT;

                if (register.NeedsControl && !Equals(_primaryController, sender))
                    return GVCPStatus.GEV_STATUS_ACCESS_DENIED;

                if (register.LockedWhileStreaming && _isStreaming?.Invoke() == true)
                    return GVCPStatus.GEV_STATUS_BUSY;

                // the hook runs first so it can reject the value before it is stored
                if (register.OnWrite is not null)
                {
                    ushort status = register.OnWrite(sender, slice);
                    if (status != GVCPStatus.GEV_STATUS_SUCCESS) return status;
                }

                slice.CopyTo(register.Data);

                if (register.SelfClearing)
                    Array.Clear(register.Data);
            }

            index = value.Length;
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }
    }

    public ushort ReadRegister(uint address, out byte[]? value)
    {
        return ReadMemory(address, 4, out value);
    }

    // the overloads taking a sender are the ones client traffic must use. the
    // plain ones above are unchecked, for the device's own reads (GVSP setup)
    // and for DISCOVERY, which is answered no matter who asks.
    public ushort ReadMemory(IPEndPoint sender, uint address, ushort count, out byte[]? value)
    {
        lock (_registersLock)
        {
            // spec: while an application holds exclusive access nobody else gets
            // an answer. plain control access still lets others monitor
            bool exclusive = (PeekUint(0x0A00) & 0x1) != 0;

            if (exclusive && !Equals(_primaryController, sender))
            {
                value = null;
                return GVCPStatus.GEV_STATUS_ACCESS_DENIED;
            }

            return ReadMemory(address, count, out value);
        }
    }

    public ushort ReadRegister(IPEndPoint sender, uint address, out byte[]? value)
    {
        return ReadMemory(sender, address, 4, out value);
    }

    public ushort WriteRegister(IPEndPoint sender, uint address, byte[] value)
    {
        return WriteMemory(sender, address, value, out _);
    }

    // resolved from gvcp server, so server must set to device...
    public void SetIP(IPAddress ipLocal)
    {
        lock (_registersLock)
            ipLocal.GetAddressBytes().CopyTo(Bytes(0x0024, 4));
    }

    // recomputes SCMBS0 / SCMPC0. called whenever geometry, pixel format or
    // packet size changes, since the client sizes its buffers from these
    private ushort UpdatePayloadSize(uint width, uint height, uint pixelFormat, uint packetSize)
    {
        // a device enforces its own minimum, increment and maximum. all this
        // needs is enough to do the arithmetic below.
        if (width == 0 || height == 0)
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        PixelFormat? format = GVSPPixelFormats.Find(pixelFormat);
        if (format is null)
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        // callers clamp before getting here, this only guards the arithmetic below
        if (packetSize < MinPacketSize || packetSize > MaxPacketSize)
            return GVCPStatus.GEV_STATUS_INVALID_PARAMETER;

        ulong payloadSize = (ulong)width * height * (ulong)format.BitsPerPixel / 8;

        PokeUint(0x0D34, (uint)(payloadSize >> 32));
        PokeUint(0x0D38, (uint)(payloadSize & 0xFFFFFFFF));

        // same overhead the transmitter subtracts: IP + UDP + extended GVSP header
        uint usablePerPacket = packetSize - 20 - 8 - 20;
        uint maxPacketCount = (uint)Math.Ceiling((double)payloadSize / usablePerPacket) + 2; // leader/trailer
        PokeUint(0x0D30, maxPacketCount);

        return GVCPStatus.GEV_STATUS_SUCCESS;
    }

    private ushort HandleCCPWrite(IPEndPoint sender, byte[] value)
    {
        // control lives in bit 30 (control_access) and bit 31 (exclusive_access)
        // in spec numbering, so the low two bits of the value. bits 0-15 carry the
        // switchover key, which does not claim anything on its own.
        uint requested = ReadU32(value) & 0x3;

        if (requested == 0)
        {
            // closing control channel
            if (Equals(_primaryController, sender)) CloseControlChannel();
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }

        if (_primaryController == null || Equals(_primaryController, sender))
        {
            // same app re-requesting is allowed
            _primaryController = sender;

            _heartbeatTimer ??= new System.Threading.Timer(
                _ => CloseControlChannel(), null, Timeout.Infinite, Timeout.Infinite);

            ResetHeartbeat(sender);
            return GVCPStatus.GEV_STATUS_SUCCESS;
        }

        // someone else already has it
        return GVCPStatus.GEV_STATUS_ACCESS_DENIED;
    }

    // spec says any valid command from the primary application resets the heartbeat,
    // except DISCOVERY, FORCEIP, PACKETRESEND and ACTION. commands from secondary
    // applications never affect it
    public void ResetHeartbeat(IPEndPoint sender)
    {
        lock (_registersLock)
        {
            if (!Equals(_primaryController, sender)) return;
            _heartbeatTimer?.Change((int)PeekUint(0x0938), Timeout.Infinite);
        }
    }

    // runs when the primary application releases the channel, and when the
    // heartbeat runs out because it went away without releasing it. the hook
    // stops streaming, so it must not call back into this class.
    private void CloseControlChannel()
    {
        lock (_registersLock)
        {
            if (_primaryController is null) return;

            _primaryController = null;
            _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            // spec: the device resets its connection state and makes itself
            // available for another application, and its registers have to
            // represent that new state
            PokeUint(0x0A00, 0); // CCP
            PokeUint(0x0D00, 0); // SCP0, the stream channel port

            _controlChannelClosed?.Invoke();
        }
    }

    public void OnControlChannelClosed(Action hook) => _controlChannelClosed = hook;

    public void OnFireTestPacket(Func<int, ushort> hook) => _fireTestPacket = hook;

    public void StreamingCheck(Func<bool> check) => _isStreaming = check;

    public void GeometrySource(Func<(uint Width, uint Height, uint PixelFormat)> source) =>
        _geometry = source;

    // recomputes SCMBS0 and SCMPC0. a device calls this from its own geometry
    // hooks, passing the value being written rather than the one still stored,
    // since hooks run before the write lands.
    public ushort RecomputePayload(uint width, uint height, uint pixelFormat)
    {
        lock (_registersLock)
            return UpdatePayloadSize(width, height, pixelFormat, PeekUint(0x0D04) & 0xFFFF);
    }

    // free-running counter in the units the tick frequency register reports
    // we fix that at 1 GHz, so a tick is a nanosecond
    public ulong Timestamp()
    {
        long elapsed = Stopwatch.GetTimestamp() - _timestampOrigin;
        return (ulong)(elapsed * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    private void LatchTimestamp()
    {
        ulong now = Timestamp();
        PokeUint(0x0948, (uint)(now >> 32));
        PokeUint(0x094C, (uint)now);
    }
}