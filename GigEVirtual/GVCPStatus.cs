// --------------------------------------------------------------------------------
// GVCPStatus.cs
//
// holds 16-bit status codes defined in "Status Codes" section in gigevision spec.
// --------------------------------------------------------------------------------

using System.Reflection;

namespace GigEVirtual;

internal static class GVCPStatus
{
    public const ushort GEV_STATUS_SUCCESS = 0x0000;
    public const ushort GEV_STATUS_PACKET_RESEND = 0x0100;
    public const ushort GEV_STATUS_NOT_IMPLEMENTED = 0x8001;
    public const ushort GEV_STATUS_INVALID_PARAMETER = 0x8002;
    public const ushort GEV_STATUS_INVALID_ADDRESS = 0x8003;
    public const ushort GEV_STATUS_WRITE_PROTECT = 0x8004;
    public const ushort GEV_STATUS_BAD_ALIGNMENT = 0x8005;
    public const ushort GEV_STATUS_ACCESS_DENIED = 0x8006;
    public const ushort GEV_STATUS_BUSY = 0x8007;
    public const ushort GEV_STATUS_PACKET_UNAVAILABLE = 0x800C;
    public const ushort GEV_STATUS_DATA_OVERRUN = 0x800D;
    public const ushort GEV_STATUS_INVALID_HEADER = 0x800E;
    public const ushort GEV_STATUS_PACKET_NOT_YET_AVAILABLE = 0x8010;
    public const ushort GEV_STATUS_PACKET_AND_PREV_REMOVED_FROM_MEMORY = 0x8011;
    public const ushort GEV_STATUS_PACKET_REMOVED_FROM_MEMORY = 0x8012;
    public const ushort GEV_STATUS_NO_REF_TIME = 0x8013;
    public const ushort GEV_STATUS_PACKET_TEMPORARILY_UNAVAILABLE = 0x8014;
    public const ushort GEV_STATUS_OVERFLOW = 0x8015;
    public const ushort GEV_STATUS_ACTION_LATE = 0x8016;
    public const ushort GEV_STATUS_LEADER_TRAILER_OVERFLOW = 0x8017;
    public const ushort GEV_STATUS_ERROR = 0x8FFF;

    public static string GetName(ushort value)
    {
        FieldInfo? field = typeof(GVCPStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(i => i.FieldType == typeof(ushort) && (ushort)i.GetValue(null)! == value);
        return field?.Name ?? $"UNKNOWN GVCP STATUS: 0x{value:X4}";
    }
}