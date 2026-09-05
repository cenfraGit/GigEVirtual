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
        var state = TestDevice.Bare();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        return state;
    }

    // --------------------------------------------------------------- address mapping

    [Fact]
    public void UnmappedAddressIsNotReadable()
    {
        var state = TestDevice.Bare();

        // the control switchover key. an optional register, and we do not claim
        // the switchover capability that would make it mean anything.
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.ReadRegister(0x095C, out _));
    }

    [Fact]
    public void UnmappedAddressIsNotWritable()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.WriteRegister(Controller, 0x095C, U32(1)));
    }

    [Fact]
    public void MappedAddressReadsBackItsValue()
    {
        var state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0000, out byte[]? version));
        Assert.Equal(0x00020002u, ReadU32(version!)); // spec version 2.2
    }

    [Theory]
    [InlineData(0x0002u, (ushort)4)]  // unaligned address
    [InlineData(0x0000u, (ushort)2)]  // unaligned count
    public void UnalignedReadsAreRejected(uint address, ushort count)
    {
        var state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_BAD_ALIGNMENT, state.ReadMemory(address, count, out _));
    }

    [Fact]
    public void ReadMayStartInsideARegister()
    {
        var state = TestDevice.Bare();

        // the xml blob is fetched in chunks, so reads land mid-register
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadMemory(0xA200 + 512, 512, out byte[]? chunk));
        Assert.Equal(512, chunk!.Length);
    }

    [Fact]
    public void ReadMaySpanAdjacentRegisters()
    {
        var state = TestDevice.Bare();

        // manufacturer name (32 bytes) straight into model name (32 bytes)
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadMemory(0x0048, 64, out byte[]? names));
        Assert.Equal(64, names!.Length);
    }

    [Fact]
    public void ReadRunningOffTheEndOfTheMapIsRejected()
    {
        var state = TestDevice.Bare();

        // user-defined name ends at 0x00F8, nothing is mapped after it until 0x0200
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.ReadMemory(0x00E8, 32, out _));
    }

    [Fact]
    public void SecondUrlRegisterExistsButIsEmpty()
    {
        var state = TestDevice.Bare();

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
        DeviceState state = TestDevice.Bare();
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
    public void SwitchoverKeyAloneDoesNotClaimControl()
    {
        var state = TestDevice.Bare();

        // bits 0-15 are the switchover key, control lives in the low two bits
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0A00, U32(0xABCD0000)));

        // nobody took control, so Controller can still claim it
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0014, U32(320)));
    }

    // --------------------------------------------------------------- control channel

    [Fact]
    public void WriteWithoutControlIsDenied()
    {
        var state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.WriteRegister(Other, 0x0014, U32(320)));
    }

    [Fact]
    public void CcpIsWritableWithoutHoldingControl()
    {
        var state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0A00, out byte[]? ccp));
        Assert.Equal(2u, ReadU32(ccp!));
    }

    [Fact]
    public void SecondApplicationCannotTakeControl()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.WriteRegister(Other, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.WriteRegister(Other, 0x0014, U32(320)));
    }

    [Fact]
    public void ReleasingControlLetsAnotherApplicationTakeIt()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0A00, U32(2)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0014, U32(320)));
    }

    [Fact]
    public void OtherApplicationCannotReleaseSomeoneElsesControl()
    {
        DeviceState state = Controlled();

        // Other does not hold control, so this must not clear it
        state.WriteRegister(Other, 0x0A00, U32(0));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0014, U32(320)));
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
        var state = TestDevice.Bare();

        state.ReadRegister(0x093C, out byte[]? high);
        state.ReadRegister(0x0940, out byte[]? low);

        Assert.Equal(0u, ReadU32(high!));
        Assert.Equal(1_000_000_000u, ReadU32(low!));
    }

    [Fact]
    public void TimestampAdvances()
    {
        var state = TestDevice.Bare();

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
        var state = TestDevice.Bare();

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
        var state = TestDevice.Bare();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(Control)));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Other, 0x0000, out byte[]? version));
        Assert.Equal(0x00020002u, ReadU32(version!));
    }

    [Fact]
    public void ExclusiveAccessRefusesReadsFromOthers()
    {
        var state = TestDevice.Bare();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(Exclusive)));

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.ReadRegister(Other, 0x0000, out byte[]? value));
        Assert.Null(value);

        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.ReadMemory(Other, 0x0048, 32, out _));
    }

    [Fact]
    public void ExclusiveAccessStillAnswersThePrimary()
    {
        var state = TestDevice.Bare();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(Exclusive)));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Controller, 0x0000, out _));
    }

    [Fact]
    public void ReleasingExclusiveAccessLetsOthersReadAgain()
    {
        var state = TestDevice.Bare();
        state.WriteRegister(Controller, 0x0A00, U32(Exclusive));
        Assert.Equal(GVCPStatus.GEV_STATUS_ACCESS_DENIED, state.ReadRegister(Other, 0x0000, out _));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Other, 0x0000, out _));
    }

    [Fact]
    public void NobodyIsRefusedWhenNoApplicationHoldsTheDevice()
    {
        var state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(Other, 0x0000, out _));
    }

    [Fact]
    public void DeviceReadsAreNotGatedByExclusiveAccess()
    {
        var state = TestDevice.Bare();
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
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Other, 0x0014, U32(320)));
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
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0014, U32(320)));
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
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0944, U32(1)));

        state.ReadRegister(0x0944, out byte[]? value);
        Assert.Equal(0u, ReadU32(value!));
    }

    [Fact]
    public void FailingHookAbortsTheWholeWrite()
    {
        DeviceState state = Controlled();

        uint before = state.ReadUint(0x0014);

        state.OnWrite(0x0014, (_, _) => GVCPStatus.GEV_STATUS_BUSY);

        Assert.Equal(GVCPStatus.GEV_STATUS_BUSY, state.WriteRegister(Controller, 0x0014, U32(1)));

        Assert.Equal(before, state.ReadUint(0x0014));
    }

    // --------------------------------------------------------------- helpers

    private static byte[] ReadBytes(DeviceState state, uint address, ushort count)
    {
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadMemory(address, count, out byte[]? value));
        return value!;
    }

    // an application is not allowed to ask for a resend unless the device says
    // it can serve one, and that promise is bit 29 of the gvcp capability
    // register. the spec numbers bit 0 as the msb, so it sits two up from the
    // bottom of the word.
    [Fact]
    public void TheDeviceAdvertisesPacketResend()
    {
        DeviceState state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0934, out byte[]? capability));
        Assert.NotEqual(0u, ReadU32(capability!) & 0x00000004);
    }

    // an application checks all three before it tries to open a message channel:
    // that there is one, that EVENT messages come out of it, and that we report
    // the source port it needs to let our traffic back in through
    [Fact]
    public void TheDeviceAdvertisesItsMessageChannel()
    {
        DeviceState state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0900, out byte[]? channels));
        Assert.Equal(1u, ReadU32(channels!));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0934, out byte[]? capability));
        Assert.NotEqual(0u, ReadU32(capability!) & 0x00000008); // EVENT, bit 28

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0930, out byte[]? message));
        Assert.NotEqual(0u, ReadU32(message!) & 0x80000000); // MCSP, bit 0
    }

    // --------------------------------------------------------------- conformance

    // every one of these is a register the spec says a device MUST implement,
    // and every one of them was asked for by a real application
    [Theory]
    [InlineData(0x0910u)] // number of active links
    [InlineData(0x0958u)] // pending timeout
    [InlineData(0x0964u)] // physical link configuration capability
    [InlineData(0x0968u)] // physical link configuration
    [InlineData(0x0D20u)] // stream channel capability 0
    [InlineData(0x0D24u)] // stream channel configuration 0
    public void MandatoryRegistersExist(uint address)
    {
        DeviceState state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(address, out _));
    }

    // a capability bit is a promise. SCEBAx says extended bootstrap registers
    // live at the address in SCEBA0, and ours is 0, so the bit stays clear.
    [Fact]
    public void WeDoNotClaimCapabilitiesWeDoNotHave()
    {
        DeviceState state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x092C, out byte[]? gvsp));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0D3C, out byte[]? sceba0));

        bool claimed = (ReadU32(gvsp!) & 0x10000000) != 0; // SCEBAx, bit 3

        Assert.Equal(0u, ReadU32(sceba0!));
        Assert.False(claimed, "SCEBAx is advertised but SCEBA0 points nowhere");
    }

    // the resend path answers with the status codes introduced in 1.1, so the
    // capability that says we can generate them has to be set
    [Fact]
    public void TheExtendedStatusCodesWeEmitAreAdvertised()
    {
        DeviceState state = TestDevice.Bare();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0934, out byte[]? capability));

        Assert.NotEqual(0u, ReadU32(capability!) & 0x00400000); // bit 9
        Assert.NotEqual(0u, ReadU32(capability!) & 0x20000000); // bit 2, heartbeat_disable
    }

    // an application turns this on while it sits in a debugger, so it does not
    // lose the device to a heartbeat it is too busy to send
    [Fact]
    public void DisablingTheHeartbeatStopsTheTimer()
    {
        DeviceState state = Controlled();

        // bit 31 in spec numbering, so the low bit
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0954, U32(1)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0938, U32(500)));

        state.ResetHeartbeat(Controller);
        Thread.Sleep(900);

        // with the heartbeat running this would have closed by now
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x00E8, new byte[16]));
    }

    // spec: the configuration register goes back to its factory default when the
    // control channel closes, so the next application does not inherit it
    [Fact]
    public void ReleasingControlResetsTheConfiguration()
    {
        DeviceState state = Controlled();

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0954, U32(1)));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x0954, out byte[]? config));
        Assert.Equal(0u, ReadU32(config!));
    }

    // the stream channel configuration describes a transfer in progress, so the
    // spec has it answer BUSY rather than change underneath one
    [Fact]
    public void TheStreamChannelConfigurationLocksDuringATransfer()
    {
        DeviceState state = Controlled();
        bool streaming = false;

        state.StreamingCheck(() => streaming);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D24, U32(0)));

        streaming = true;
        Assert.Equal(GVCPStatus.GEV_STATUS_BUSY, state.WriteRegister(Controller, 0x0D24, U32(0)));
    }

    // --------------------------------------------------------------- forceip

    private static byte[] ForceIpPayload(byte[] mac, string staticIp, string mask, string gateway)
    {
        byte[] payload = new byte[56];

        mac.CopyTo(payload, 2);
        IPAddress.Parse(staticIp).GetAddressBytes().CopyTo(payload, 20);
        IPAddress.Parse(mask).GetAddressBytes().CopyTo(payload, 36);
        IPAddress.Parse(gateway).GetAddressBytes().CopyTo(payload, 52);

        return payload;
    }

    [Fact]
    public void ForceIpCarriesTheMacAndThreeAddresses()
    {
        byte[] mac = [0x02, 0x11, 0x22, 0x33, 0x44, 0x55];

        var forced = GVCPServer.ParseForceIp(
            ForceIpPayload(mac, "192.168.1.50", "255.255.255.0", "192.168.1.1"));

        Assert.Equal(mac, forced!.Value.Mac);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), forced.Value.StaticIp);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), forced.Value.SubnetMask);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), forced.Value.Gateway);
    }

    [Fact]
    public void ATruncatedForceIpIsNotOne()
    {
        Assert.Null(GVCPServer.ParseForceIp(new byte[55]));
    }

    // the command is broadcast, so every device on the wire sees every one of
    // them and has to work out from the mac whether it was the one meant
    [Fact]
    public void TheMacIsHowADeviceKnowsItWasMeant()
    {
        DeviceState state = TestDevice.Bare();
        byte[] mine = state.Mac();

        Assert.Equal(6, mine.Length);

        // locally administered and unicast, which is what a made-up mac has to be
        Assert.Equal(0x02, mine[0] & 0x03);

        byte[] theirs = [.. mine];
        theirs[5] ^= 0xFF;

        Assert.NotEqual(mine, theirs);
    }

    // the whole point of forceip is recovering a device nobody is talking to, so
    // it stays out of the way of one that is
    [Fact]
    public void ADeviceUnderControlReportsThatItIsTaken()
    {
        DeviceState state = TestDevice.Bare();
        Assert.False(state.HasController);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        Assert.True(state.HasController);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(0)));
        Assert.False(state.HasController);
    }
}
