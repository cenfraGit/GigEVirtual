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

        // nothing is mapped at the gvcp configuration register
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.ReadRegister(0x0954, out _));
    }

    [Fact]
    public void UnmappedAddressIsNotWritable()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.WriteRegister(Controller, 0x0954, U32(1)));
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
    public void SecondUrlRegisterExistsButIsEmpty()
    {
        var state = new DeviceState();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadMemory(0x0400, 512, out byte[]? url));
        Assert.All(url!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void WriteMayNotStartInsideARegister()
    {
        DeviceState state = Controlled();

        // user-defined name lives at 0x00E8, so 0x00EC is mid-register
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.WriteMemory(Controller, 0x00EC, U32(0), out _));
    }

    [Fact]
    public void StorageFollowsTheRegistersRatherThanTheAddressSpace()
    {
        // a real device scatters registers across the 32-bit range: the genie
        // nano reaches 0xB0000000 while holding about 8 KB of actual register
        // data. address-indexed storage would have to allocate the whole span,
        // so what we hold has to track the registers instead.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        DeviceState state = new();
        long after = GC.GetTotalAllocatedBytes(precise: true);

        // the xml is the largest thing in here by far. the flat array this
        // replaced was a megabyte on its own.
        Assert.InRange(after - before, 0, 500_000);

        // and it still works
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0000, out _));
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
    public void WriteRunningOffTheEndOfTheMapChangesNothing()
    {
        DeviceState state = Controlled();

        // user-defined name (RW, 16 bytes) followed by unmapped space
        byte[] before = ReadBytes(state, 0x00E8, 16);

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS,
            state.WriteMemory(Controller, 0x00E8, new byte[32], out int index));

        Assert.Equal(0, index);
        Assert.Equal(before, ReadBytes(state, 0x00E8, 16));
    }

    [Fact]
    public void SuccessfulWriteReportsTheByteCount()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteMemory(Controller, 0x00E8, new byte[16], out int index));

        Assert.Equal(16, index);
    }

    [Fact]
    public void FailedWriteReportsTheOffsetItStoppedAtAndKeepsEarlierWrites()
    {
        DeviceState state = Controlled();

        // width (valid) then height (0, which the hook rejects) in one write
        byte[] value = [.. U32(320), .. U32(0)];

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER,
            state.WriteMemory(Controller, 0xA000, value, out int index));

        // height is 4 bytes in
        Assert.Equal(4, index);

        // the spec has writes before the failure stand
        state.ReadRegister(0xA000, out byte[]? width);
        Assert.Equal(320u, ReadU32(width!));

        state.ReadRegister(0xA004, out byte[]? height);
        Assert.Equal(480u, ReadU32(height!));
    }

    [Fact]
    public void SwitchoverKeyAloneDoesNotClaimControl()
    {
        var state = new DeviceState();

        // bits 0-15 are the switchover key, control lives in the low two bits
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0A00, U32(0xABCD0000)));

        // nobody took control, so Controller can still claim it
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0xA000, U32(320)));
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

    // --------------------------------------------------------------- timestamp

    // latch is bit 30 and reset is bit 31 in spec numbering
    private const uint Latch = 2;
    private const uint Reset = 1;

    private static ulong LatchedTimestamp(DeviceState state)
    {
        state.ReadRegister(0x0948, out byte[]? high);
        state.ReadRegister(0x094C, out byte[]? low);
        return ((ulong)ReadU32(high!) << 32) | ReadU32(low!);
    }

    [Fact]
    public void TickFrequencyIsOneGigahertz()
    {
        var state = new DeviceState();

        state.ReadRegister(0x093C, out byte[]? high);
        state.ReadRegister(0x0940, out byte[]? low);

        Assert.Equal(0u, ReadU32(high!));
        Assert.Equal(1_000_000_000u, ReadU32(low!));
    }

    [Fact]
    public void TimestampAdvances()
    {
        var state = new DeviceState();

        ulong first = state.Timestamp();
        Thread.Sleep(50);
        ulong second = state.Timestamp();

        // Sleep only ever overshoots, so 50 ms is a safe floor. no ceiling, a
        // loaded machine can take as long as it likes.
        Assert.True(second - first >= 50_000_000ul, $"only advanced {second - first} ns");
    }

    [Fact]
    public void ValueRegistersStayZeroUntilLatched()
    {
        var state = new DeviceState();

        Thread.Sleep(20);

        Assert.Equal(0ul, LatchedTimestamp(state));
    }

    [Fact]
    public void LatchCopiesTheCounterIntoTheValueRegisters()
    {
        DeviceState state = Controlled();

        Thread.Sleep(50);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0944, U32(Latch)));

        ulong latched = LatchedTimestamp(state);
        Assert.True(latched >= 50_000_000ul, $"latched {latched} ns");

        // and it holds still until the next latch
        Thread.Sleep(50);
        Assert.Equal(latched, LatchedTimestamp(state));
    }

    [Fact]
    public void ControlRegisterSelfClears()
    {
        DeviceState state = Controlled();

        state.WriteRegister(Controller, 0x0944, U32(Latch));

        state.ReadRegister(0x0944, out byte[]? control);
        Assert.Equal(0u, ReadU32(control!));
    }

    [Fact]
    public void ResetPutsTheCounterBackToZero()
    {
        DeviceState state = Controlled();

        Thread.Sleep(50);
        ulong before = state.Timestamp();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0944, U32(Reset)));

        // the counter restarted, so it now reads less than it did before
        Assert.True(state.Timestamp() < before, "counter did not restart");
    }

    [Fact]
    public void LatchAndResetTogetherLatchFirst()
    {
        DeviceState state = Controlled();

        Thread.Sleep(50);
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0x0944, U32(Latch | Reset)));

        // spec: the latched value is the counter as it stood before the reset,
        // so it has to be ahead of what the counter reads now
        ulong latched = LatchedTimestamp(state);
        Assert.True(latched >= 50_000_000ul, $"latched {latched} ns");
        Assert.True(state.Timestamp() < latched, "counter did not restart");
    }

    // --------------------------------------------------------------- exclusive access

    // exclusive_access is CCP bit 31 in spec numbering, so the low bit
    private const uint Exclusive = 1;
    private const uint Control = 2;

    [Fact]
    public void ControlAccessStillLetsOthersRead()
    {
        var state = new DeviceState();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(Control)));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Other, 0x0000, out byte[]? version));
        Assert.Equal(0x00020002u, ReadU32(version!));
    }

    [Fact]
    public void ExclusiveAccessRefusesReadsFromOthers()
    {
        var state = new DeviceState();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(Exclusive)));

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.ReadRegister(Other, 0x0000, out byte[]? value));
        Assert.Null(value);

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.ReadMemory(Other, 0x0048, 32, out _));
    }

    [Fact]
    public void ExclusiveAccessStillAnswersThePrimary()
    {
        var state = new DeviceState();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(Exclusive)));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Controller, 0x0000, out _));
    }

    [Fact]
    public void ReleasingExclusiveAccessLetsOthersReadAgain()
    {
        var state = new DeviceState();
        state.WriteRegister(Controller, 0x0A00, U32(Exclusive));
        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.ReadRegister(Other, 0x0000, out _));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Other, 0x0000, out _));
    }

    [Fact]
    public void NobodyIsRefusedWhenNoApplicationHoldsTheDevice()
    {
        var state = new DeviceState();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Other, 0x0000, out _));
    }

    [Fact]
    public void DeviceReadsAreNotGatedByExclusiveAccess()
    {
        var state = new DeviceState();
        state.WriteRegister(Controller, 0x0A00, U32(Exclusive));

        // the unchecked overload is what GVSP setup and the discovery ack use
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0000, out _));
    }

    // --------------------------------------------------------------- heartbeat

    // the shortest timeout the spec allows, so the tests stay quick
    private const int MinTimeout = 500;

    private static DeviceState ControlledWithShortHeartbeat()
    {
        DeviceState state = Controlled();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0x0938, U32(MinTimeout)));

        // re-arm so the shortened timeout takes effect
        state.ResetHeartbeat(Controller);
        return state;
    }

    private static uint Ccp(DeviceState state)
    {
        state.ReadRegister(0x0A00, out byte[]? value);
        return ReadU32(value!);
    }

    [Fact]
    public void TimeoutBelowFiveHundredIsRaised()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0938, U32(100)));

        state.ReadRegister(0x0938, out byte[]? timeout);
        Assert.Equal(500u, ReadU32(timeout!));
    }

    [Fact]
    public void HeartbeatExpiryClosesTheControlChannel()
    {
        DeviceState state = ControlledWithShortHeartbeat();

        Thread.Sleep(MinTimeout * 3);

        Assert.Equal(0u, Ccp(state));

        // the device is available again
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0xA000, U32(320)));
    }

    [Fact]
    public void CommandsFromThePrimaryKeepTheChannelOpen()
    {
        DeviceState state = ControlledWithShortHeartbeat();

        // keep it alive well past the timeout
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(MinTimeout / 2);
            state.ResetHeartbeat(Controller);
        }

        Assert.Equal(2u, Ccp(state));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0xA000, U32(320)));
    }

    [Fact]
    public void CommandsFromSecondaryApplicationsDoNotKeepItOpen()
    {
        DeviceState state = ControlledWithShortHeartbeat();

        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(MinTimeout / 2);
            state.ResetHeartbeat(Other);
        }

        Assert.Equal(0u, Ccp(state));
    }

    [Fact]
    public void ClosingTheChannelResetsTheStreamChannelPort()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D00, U32(50010)));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));

        state.ReadRegister(0x0D00, out byte[]? port);
        Assert.Equal(0u, ReadU32(port!));
        Assert.Equal(0u, Ccp(state));
    }

    [Fact]
    public void ClosingTheChannelRunsTheHook()
    {
        DeviceState state = Controlled();

        int closed = 0;
        state.OnControlChannelClosed(() => closed++);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));
        Assert.Equal(1, closed);

        // already closed, so releasing again must not fire it a second time
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0A00, U32(0)));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void HeartbeatExpiryRunsTheHook()
    {
        DeviceState state = ControlledWithShortHeartbeat();

        int closed = 0;
        state.OnControlChannelClosed(() => closed++);

        Thread.Sleep(MinTimeout * 3);

        Assert.Equal(1, closed);
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

        // Mono12Packed. a real format, but we do not do bit packing
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER,
            state.WriteRegister(Controller, 0xA008, U32(0x010C0006)));

        state.ReadRegister(0xA008, out byte[]? format);
        Assert.Equal(GVSPPixelFormats.Mono8, ReadU32(format!));
    }

    [Fact]
    public void PayloadSizeFollowsTheBitsPerPixel()
    {
        DeviceState state = Controlled();

        Assert.Equal(640u * 480u, PayloadSize(state));

        // Mono12 rides in a 16-bit container, so two bytes per pixel
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0xA008, U32(GVSPPixelFormats.Mono12)));
        Assert.Equal(640u * 480u * 2, PayloadSize(state));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0xA008, U32(GVSPPixelFormats.RGB8)));
        Assert.Equal(640u * 480u * 3, PayloadSize(state));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0xA008, U32(GVSPPixelFormats.BayerRG8)));
        Assert.Equal(640u * 480u, PayloadSize(state));
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
    public void ImplausiblePacketSizeIsRoundedRatherThanRefused()
    {
        DeviceState state = Controlled();

        // spec: round to the nearest size we support and show that in the register
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D04, U32(0)));
        Assert.Equal(576u, PacketSize(state));

        // packet_size is the low 16 bits, so 0xFFFF is the largest an application
        // can even ask for, and it is still well past a jumbo frame
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D04, U32(0xFFFF)));
        Assert.Equal(16384u, PacketSize(state));
    }

    // --------------------------------------------------------------- streaming locks

    [Fact]
    public void StreamChannelRegistersAreLockedWhileStreaming()
    {
        DeviceState state = Controlled();
        state.StreamingCheck(() => true);

        Assert.Equal(GVCPStatus.GEV_STATUS_BUSY, state.WriteRegister(Controller, 0x0D18, U32(0x7F000001)));
        Assert.Equal(GVCPStatus.GEV_STATUS_BUSY, state.WriteRegister(Controller, 0x0D04, U32(9000)));
    }

    [Fact]
    public void StreamChannelPortStaysWritableWhileStreaming()
    {
        DeviceState state = Controlled();
        state.StreamingCheck(() => true);

        // writing 0 to SCP0 is how an application closes the channel, so locking
        // it would leave no way out
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D00, U32(0)));
    }

    [Fact]
    public void StreamChannelRegistersAreWritableWhenIdle()
    {
        DeviceState state = Controlled();
        state.StreamingCheck(() => false);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D18, U32(0x7F000001)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D04, U32(9000)));
    }

    [Fact]
    public void ALockedRegisterDoesNotRunItsHook()
    {
        DeviceState state = Controlled();
        state.StreamingCheck(() => true);

        var fired = new List<int>();
        state.OnFireTestPacket(size => { fired.Add(size); return GVCPStatus.GEV_STATUS_SUCCESS; });

        Assert.Equal(GVCPStatus.GEV_STATUS_BUSY,
            state.WriteRegister(Controller, 0x0D04, U32(0x80000000 | 1500)));

        Assert.Empty(fired);
    }

    // --------------------------------------------------------------- frame rate

    private static byte[] F32(float value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        return buffer;
    }

    private static float FrameRate(DeviceState state)
    {
        state.ReadRegister(0xA018, out byte[]? value);
        return BinaryPrimitives.ReadSingleBigEndian(value!);
    }

    [Fact]
    public void FrameRateHasAUsableDefault()
    {
        Assert.Equal(10.0f, FrameRate(new DeviceState()));
    }

    [Fact]
    public void FrameRateCanBeChanged()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0xA018, F32(30.0f)));
        Assert.Equal(30.0f, FrameRate(state));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-5.0f)]
    [InlineData(5000.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ImplausibleFrameRatesAreRejected(float rate)
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_PARAMETER,
            state.WriteRegister(Controller, 0xA018, F32(rate)));

        // and the old value survives
        Assert.Equal(10.0f, FrameRate(state));
    }

    // --------------------------------------------------------------- test packet

    // fire_test_packet is bit 0 and do_not_fragment is bit 1, in spec numbering
    private const uint FireTestPacket = 0x80000000;
    private const uint DoNotFragment = 0x40000000;

    private static uint PacketSize(DeviceState state)
    {
        state.ReadRegister(0x0D04, out byte[]? value);
        return ReadU32(value!) & 0xFFFF;
    }

    [Fact]
    public void AskingForATestPacketFiresOne()
    {
        DeviceState state = Controlled();

        var fired = new List<int>();
        state.OnFireTestPacket(size => { fired.Add(size); return GVCPStatus.GEV_STATUS_SUCCESS; });

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0x0D04, U32(FireTestPacket | DoNotFragment | 1500)));

        Assert.Equal([1500], fired);
    }

    [Fact]
    public void SettingTheSizeWithoutTheFireBitSendsNothing()
    {
        DeviceState state = Controlled();

        var fired = new List<int>();
        state.OnFireTestPacket(size => { fired.Add(size); return GVCPStatus.GEV_STATUS_SUCCESS; });

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0x0D04, U32(DoNotFragment | 1500)));

        Assert.Empty(fired);
    }

    [Fact]
    public void NoTestPacketWhenTheSizeHadToBeRounded()
    {
        DeviceState state = Controlled();

        var fired = new List<int>();
        state.OnFireTestPacket(size => { fired.Add(size); return GVCPStatus.GEV_STATUS_SUCCESS; });

        // spec: a transmitter that cannot serve the requested size must not fire
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(Controller, 0x0D04, U32(FireTestPacket | 100)));

        Assert.Empty(fired);
        Assert.Equal(576u, PacketSize(state));
    }

    [Fact]
    public void FireBitSelfClearsButTheOtherBitsStay()
    {
        DeviceState state = Controlled();
        state.OnFireTestPacket(_ => GVCPStatus.GEV_STATUS_SUCCESS);

        state.WriteRegister(Controller, 0x0D04, U32(FireTestPacket | DoNotFragment | 1500));

        state.ReadRegister(0x0D04, out byte[]? value);
        uint scps = ReadU32(value!);

        Assert.Equal(0u, scps & FireTestPacket);
        Assert.Equal(DoNotFragment, scps & DoNotFragment);
        Assert.Equal(1500u, scps & 0xFFFF);
    }

    [Fact]
    public void TestPacketDataFollowsTheSpecsLfsr()
    {
        const int period = 65535; // a maximal 16-bit lfsr, so 2^16 - 1

        // one full period plus a couple of bytes, so we can see it wrap
        byte[] data = new byte[period + 2];
        GVSPTransmitter.FillLfsr(data);

        // spec: the first byte is the lsb of the initial value
        Assert.Equal(0xFF, data[0]);

        Assert.Equal(data[0], data[period]);
        Assert.Equal(data[1], data[period + 1]);

        // and it is genuinely varying, not stuck on one value
        Assert.True(data.Take(256).Distinct().Count() > 100);
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