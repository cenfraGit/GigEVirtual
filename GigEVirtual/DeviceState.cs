// --------------------------------------------------------------------------------
// DeviceState.cs
//
// represents the virtual device state, accessed by HVCP and HVSP
// --------------------------------------------------------------------------------

namespace GigEVirtual;

internal class DeviceState
{
    // --------------------------------------------------------------- fields and properties

    private readonly Dictionary<uint, byte[]> _registers = [];
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