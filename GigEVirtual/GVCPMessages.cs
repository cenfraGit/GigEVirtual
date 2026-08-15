// --------------------------------------------------------------------------------
// GVCPMessages.cs
//
// 16-bit values for gvcp messages, pulled from "Command and Acknowledge Values"
// section in gigevision 2.2 spec.
// --------------------------------------------------------------------------------

using System.Reflection;

namespace GigEVirtual;

internal static class GVCPMessages
{
    public const ushort DISCOVERY_CMD = 0x0002;
    public const ushort DISCOVERY_ACK = 0x0003;
    public const ushort FORCEIP_CMD = 0x0004;
    public const ushort FORCEIP_ACK = 0x0005;
    public const ushort PACKETRESEND_CMD = 0x0040;
    public const ushort READREG_CMD = 0x0080;
    public const ushort READREG_ACK = 0x0081;
    public const ushort WRITEREG_CMD = 0x0082;
    public const ushort WRITEREG_ACK = 0x0083;
    public const ushort READMEM_CMD = 0x0084;
    public const ushort READMEM_ACK = 0x0085;
    public const ushort WRITEMEM_CMD = 0x0086;
    public const ushort WRITEMEM_ACK = 0x0087;
    public const ushort PENDING_ACK = 0x0089;
    public const ushort EVENT_CMD = 0x00C0;
    public const ushort EVENT_ACK = 0x00C1;
    public const ushort EVENTDATA_CMD = 0x00C2;
    public const ushort EVENTDATA_ACK = 0x00C3;
    public const ushort ACTION_CMD = 0x0100;
    public const ushort ACTION_ACK = 0x0101;

    public static string GetName(ushort value)
    {
        FieldInfo? field = typeof(GVCPMessages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(i => i.FieldType == typeof(ushort) && (ushort)i.GetValue(null)! == value);
        return field?.Name ?? $"UNKNOWN GVCP MESSAGE: 0x{value:X4}";
    }
}