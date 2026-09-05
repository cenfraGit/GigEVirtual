// --------------------------------------------------------------------------------
// GigECameraTests.cs
//
// the virtual camera's own feature registers: geometry, pixel format, payload
// size and frame rate. these depend on where this camera puts things, which is
// its business rather than the register map's.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using GigEVirtual;
using Xunit;

namespace GigEVirtual.Tests;

public class GigECameraTests
{
    private static readonly IPEndPoint Controller = new(IPAddress.Parse("192.168.1.10"), 50000);

    private static byte[] U32(uint value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] F32(float value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        return buffer;
    }

    private static uint ReadU32(byte[] value) => BinaryPrimitives.ReadUInt32BigEndian(value);

    private static DeviceState Controlled()
    {
        DeviceState state = TestDevice.Camera();
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0A00, U32(2)));
        return state;
    }

    private static ulong PayloadSize(DeviceState state) =>
        ((ulong)state.ReadUint(0x0D34) << 32) | state.ReadUint(0x0D38);

    private static float FrameRate(DeviceState state) => state.ReadFloat(0xA018);

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
    public void FrameRateHasAUsableDefault()
    {
        Assert.Equal(10.0f, FrameRate(TestDevice.Camera()));
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
    public void ChangingPacketSizeRecomputesTheMaxPacketCount()
    {
        DeviceState state = Controlled();

        state.ReadRegister(0x0D30, out byte[]? before);

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.WriteRegister(Controller, 0x0D04, U32(9000)));

        state.ReadRegister(0x0D30, out byte[]? after);
        Assert.True(ReadU32(after!) < ReadU32(before!)); // jumbo frames, fewer packets
    }
}
