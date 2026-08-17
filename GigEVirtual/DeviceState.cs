// --------------------------------------------------------------------------------
// DeviceState.cs
//
// accessed by GVCP and GVSP. holds the device memory for the bootstrap and
// manufacturer-specific registers, plus helper methods to read from these.
// --------------------------------------------------------------------------------

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

    // --------------------------------------------------------------- constructors

    public DeviceState()
    {
        // specify big endian at least
        byte[] en = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(en, 0b10000000000000000000000000000000);
        WriteMemory(0x0004, en);

        byte[] width = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(width, 4);
        byte[] height = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(height, 5);
        byte[] pixelFormat = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(pixelFormat, 6);
        WriteMemory(0xA000, width);
        WriteMemory(0xA004, height);
        WriteMemory(0xA008, pixelFormat);

        static byte[] PadTo4ByteMultiple(byte[] data)
        {
            int paddedLength = ((data.Length + 3) / 4) * 4;
            byte[] padded = new byte[paddedLength]; // extra bytes default to 0
            Array.Copy(data, padded, data.Length);
            return padded;
        }

        byte[] xmlBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "GigEVirtual.xml"));
        uint xmlAddress = 0xA200;
        string firstUrl = $"Local:GigEVirtual.xml;{xmlAddress:x};{xmlBytes.Length:x}";

        WriteMemory(xmlAddress, PadTo4ByteMultiple(xmlBytes));
        WriteMemory(0x0200, PadTo4ByteMultiple(Encoding.ASCII.GetBytes(firstUrl)));
    }

    // --------------------------------------------------------------- methods

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

    public ushort ReadRegister(uint address, out byte[]? value)
    {
        return ReadMemory(address, 4, out value);
    }

    public ushort WriteRegister(uint address, byte[] value)
    {
        return WriteMemory(address, value);
    }
}