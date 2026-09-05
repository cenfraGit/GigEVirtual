// --------------------------------------------------------------------------------
// DeviceStateTests.cs
//
// covers the register map: which addresses exist, who may write to them, and
// what the hooks do.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using GigEVirtual;
using Xunit;

namespace GigEVirtual.Tests;

public class DeviceStateTests
{
    private static readonly IPEndPoint Controller = new(IPAddress.Parse("192.168.1.10"), 50000);
    private static readonly IPEndPoint Other = new(IPAddress.Parse("192.168.1.11"), 50000);

    private static byte[] U32(uint value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    private static uint ReadU32(byte[] value) => BinaryPrimitives.ReadUInt32BigEndian(value);

    // a device with Controller already holding the control channel
    private static DeviceState Controlled()
    {
        var state = new DeviceState();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        return state;
    }

    // --------------------------------------------------------------- address mapping

    [Fact]
    public void UnmappedAddressIsNotReadable()
    {
        var state = new DeviceState();

        // 0x0400 is the second url register, which we do not implement
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.ReadRegister(0x0400, out _));
    }

    [Fact]
    public void UnmappedAddressIsNotWritable()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.WriteRegister(Controller, 0x0400, U32(1)));
    }

    [Fact]
    public void MappedAddressReadsBackItsValue()
    {
        var state = new DeviceState();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0000, out byte[]? version));
        Assert.Equal(0x00020002u, ReadU32(version!)); // spec version 2.2
    }

    [Theory]
    [InlineData(0x0002u, (ushort)4)]  // unaligned address
    [InlineData(0x0000u, (ushort)2)]  // unaligned count
    public void UnalignedReadsAreRejected(uint address, ushort count)
    {
        var state = new DeviceState();

        Assert.Equal(GVCPStatus.GEV_STATUS_BAD_ALIGNMENT, state.ReadMemory(address, count, out _));
    }

    [Fact]
    public void ReadMayStartInsideARegister()
    {
        var state = new DeviceState();

        // the xml blob is fetched in chunks, so reads land mid-register
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadMemory(0xA200 + 512, 512, out byte[]? chunk));
        Assert.Equal(512, chunk!.Length);
    }

    [Fact]
    public void ReadMaySpanAdjacentRegisters()
    {
        var state = new DeviceState();

        // manufacturer name (32 bytes) straight into model name (32 bytes)
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadMemory(0x0048, 64, out byte[]? names));
        Assert.Equal(64, names!.Length);
    }

    [Fact]
    public void ReadRunningOffTheEndOfTheMapIsRejected()
    {
        var state = new DeviceState();

        // user-defined name ends at 0x00F8, nothing is mapped after it until 0x0200
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.ReadMemory(0x00E8, 32, out _));
    }

    [Fact]
    public void WriteMayNotStartInsideARegister()
    {
        DeviceState state = Controlled();

        // user-defined name lives at 0x00E8, so 0x00EC is mid-register
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.WriteMemory(Controller, 0x00EC, U32(0)));
    }

    // --------------------------------------------------------------- access modes

    [Fact]
    public void ReadOnlyRegisterRejectsWrites()
    {
        DeviceState state = Controlled();

        // manufacturer name is fixed at build time
        Assert.Equal(GVCPStatus.GEV_STATUS_WRITE_PROTECT, state.WriteRegister(Controller, 0x0048, U32(0)));
    }

    [Fact]
    public void WriteSpanningIntoAReadOnlyRegisterIsRejectedWholesale()
    {
        DeviceState state = Controlled();

        // user-defined name (RW, 16 bytes) followed by unmapped space
        byte[] before = ReadBytes(state, 0x00E8, 16);

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS,
            state.WriteMemory(Controller, 0x00E8, new byte[32]));

        Assert.Equal(before, ReadBytes(state, 0x00E8, 16));
    }

    // --------------------------------------------------------------- control channel

    [Fact]
    public void WriteWithoutControlIsDenied()
    {
        var state = new DeviceState();

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.WriteRegister(Other, 0xA000, U32(320)));
    }

    [Fact]
    public void CcpIsWritableWithoutHoldingControl()
    {
        var state = new DeviceState();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0A00, out byte[]? ccp));
        Assert.Equal(2u, ReadU32(ccp!));
    }

    [Fact]
    public void SecondApplicationCannotTakeControl()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.WriteRegister(Other, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.WriteRegister(Other, 0xA000, U32(320)));
    }

    [Fact]
    public void ReleasingControlLetsAnotherApplicationTakeIt()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0xA000, U32(320)));
    }

    [Fact]
    public void OtherApplicationCannotReleaseSomeoneElsesControl()
    {
        DeviceState state = Controlled();

        // Other does not hold control, so this must not clear it
        state.WriteRegister(Other, 0x0A00, U32(0));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0xA000, U32(320)));
    }

    // --------------------------------------------------------------- hooks

    [Fact]
    public void HookRejectsBadValueAndLeavesRegisterUnchanged()
    {
        DeviceState state = Controlled();

        // width must be a multiple of 4 and at least 64
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, state.WriteRegister(Controller, 0xA000, U32(7)));

        state.ReadRegister(0xA000, out byte[]? width);
        Assert.Equal(640u, ReadU32(width!));
    }

    [Fact]
    public void UnsupportedPixelFormatIsRejected()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER,
            state.WriteRegister(Controller, 0xA008, U32(0x02180014))); // RGB8

        state.ReadRegister(0xA008, out byte[]? format);
        Assert.Equal(GVSPPixelFormats.Mono8, ReadU32(format!));
    }

    [Fact]
    public void ChangingGeometryRecomputesThePayloadRegisters()
    {
        DeviceState state = Controlled();

        Assert.Equal(640u * 480u, PayloadSize(state));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0xA000, U32(1920)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0xA004, U32(1080)));

        Assert.Equal(1920u * 1080u, PayloadSize(state));
    }

    [Fact]
    public void ChangingPacketSizeRecomputesTheMaxPacketCount()
    {
        DeviceState state = Controlled();

        state.ReadRegister(0x0D30, out byte[]? before);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D04, U32(9000)));

        state.ReadRegister(0x0D30, out byte[]? after);
        Assert.True(ReadU32(after!) < ReadU32(before!)); // jumbo frames, fewer packets
    }

    [Fact]
    public void ImplausiblePacketSizeIsRejected()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, state.WriteRegister(Controller, 0x0D04, U32(0)));

        // packet_size is the low 16 bits of SCPS0, so 0xFFFF is the largest a
        // client can even ask for, and it is still well past a jumbo frame
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER, state.WriteRegister(Controller, 0x0D04, U32(0xFFFF)));
    }

    [Fact]
    public void CommandRegisterSelfClears()
    {
        DeviceState state = Controlled();

        // no hook attached here, so this only exercises the self-clearing behaviour
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0xA00C, U32(1)));

        state.ReadRegister(0xA00C, out byte[]? value);
        Assert.Equal(0u, ReadU32(value!));
    }

    [Fact]
    public void FailingHookAbortsTheWholeWrite()
    {
        DeviceState state = Controlled();

        state.OnWrite(0xA014, (_, _) => GVCPStatus.GEV_STATUS_BUSY);

        Assert.Equal(GVCPStatus.GEV_STATUS_BUSY, state.WriteRegister(Controller, 0xA014, U32(1)));

        state.ReadRegister(0xA014, out byte[]? mode);
        Assert.Equal(0u, ReadU32(mode!));
    }

    // --------------------------------------------------------------- helpers

    private static byte[] ReadBytes(DeviceState state, uint address, ushort count)
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadMemory(address, count, out byte[]? value));
        return value!;
    }

    private static ulong PayloadSize(DeviceState state)
    {
        state.ReadRegister(0x0D34, out byte[]? high);
        state.ReadRegister(0x0D38, out byte[]? low);
        return ((ulong)ReadU32(high!) << 32) | ReadU32(low!);
    }
}