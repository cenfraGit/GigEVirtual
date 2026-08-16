// --------------------------------------------------------------------------------
// DeviceState.cs
//
// accessed by GVCP and GVSP. holds the device memory for the bootstrap and
// manufacturer-specific registers, plus helper methods to read from these.
// --------------------------------------------------------------------------------

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
    }

    // --------------------------------------------------------------- methods

    public bool TryRegisterRead(uint address, out byte[]? value)
    {
        lock (_registersLock)
        {
            return _registers.TryGetValue(address, out value);
        }
    }

    public void TryRegisterWrite(uint address, byte[] value)
    {
        lock (_registersLock)
        {
            _registers[address] = value;
        }
    }
}