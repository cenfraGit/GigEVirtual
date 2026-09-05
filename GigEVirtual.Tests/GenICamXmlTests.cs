// --------------------------------------------------------------------------------
// GenICamXmlTests.cs
//
// covers reading register declarations out of a device description. the fixtures
// are hand written, so the suite does not depend on a vendor file being present.
// --------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using GigEVirtual;
using Xunit;

namespace GigEVirtual.Tests;

public class GenICamXmlTests
{
    private const string Ns = "http://www.genicam.org/GenApi/Version_1_1";

    private static string Xml(string body) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><RegisterDescription xmlns="{Ns}">{body}</RegisterDescription>""";

    private static List<XmlRegister> Read(string body) => GenICamXml.Registers(Xml(body));

    // --------------------------------------------------------------- addresses

    [Fact]
    public void ReadsARegisterWithALiteralAddress()
    {
        List<XmlRegister> registers = Read("""
            <IntReg Name="Width">
                <Address>0x0000A000</Address>
                <Length>4</Length>
                <AccessMode>RW</AccessMode>
                <pPort>Device</pPort>
            </IntReg>
            """);

        XmlRegister register = Assert.Single(registers);
        Assert.Equal(0xA000u, register.Address);
        Assert.Equal(4, register.Length);
        Assert.Equal(RegAccess.ReadWrite, register.Access);
        Assert.Equal("Width", register.Name);
    }

    [Fact]
    public void FollowsAPAddressToAConstant()
    {
        List<XmlRegister> registers = Read("""
            <Integer Name="Base"><Value>0x20005000</Value></Integer>
            <IntReg Name="Exposure">
                <pAddress>Base</pAddress>
                <Length>4</Length>
                <AccessMode>RW</AccessMode>
                <pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Equal(0x20005000u, Assert.Single(registers).Address);
    }

    [Fact]
    public void AddsUpEveryAddressPart()
    {
        // base plus offset is how a description expresses a register inside a block
        List<XmlRegister> registers = Read("""
            <Integer Name="Base"><Value>0x200053F0</Value></Integer>
            <Integer Name="Offset"><Value>12</Value></Integer>
            <IntReg Name="ExposureDelay">
                <pAddress>Base</pAddress>
                <pAddress>Offset</pAddress>
                <Length>4</Length>
                <AccessMode>RW</AccessMode>
                <pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Equal(0x200053FCu, Assert.Single(registers).Address);
    }

    [Fact]
    public void MixesALiteralAddressWithAPAddressOffset()
    {
        List<XmlRegister> registers = Read("""
            <Integer Name="Offset"><Value>0x10</Value></Integer>
            <IntReg Name="Mixed">
                <Address>0x18000000</Address>
                <pAddress>Offset</pAddress>
                <Length>4</Length>
                <AccessMode>RO</AccessMode>
                <pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Equal(0x18000010u, Assert.Single(registers).Address);
    }

    [Fact]
    public void ASelectorDrivenAddressExistsAtEveryOptionItLists()
    {
        // which one the selector points at is only knowable at runtime, so define
        // the register at all of them rather than evaluate the selector
        List<XmlRegister> registers = Read("""
            <Integer Name="TempAddr">
                <pIndex>temperatureSelector</pIndex>
                <ValueIndexed Index="0">0x200001B0</ValueIndexed>
                <ValueIndexed Index="1">0x18000090</ValueIndexed>
                <ValueDefault>0x200001B0</ValueDefault>
            </Integer>
            <IntReg Name="DeviceTemperature">
                <pAddress>TempAddr</pAddress>
                <Length>4</Length>
                <AccessMode>RO</AccessMode>
                <pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Equal([0x18000090u, 0x200001B0u], registers.Select(r => r.Address));
    }

    [Fact]
    public void ASelectorCombinesWithAFixedOffset()
    {
        // base picked by a selector, plus a constant offset into the block
        List<XmlRegister> registers = Read("""
            <Integer Name="Base">
                <pIndex>sel</pIndex>
                <ValueIndexed Index="0">0x20000000</ValueIndexed>
                <ValueIndexed Index="1">0x20001000</ValueIndexed>
            </Integer>
            <Integer Name="Offset"><Value>0x10</Value></Integer>
            <IntReg Name="Thing">
                <pAddress>Base</pAddress>
                <pAddress>Offset</pAddress>
                <Length>4</Length>
                <AccessMode>RW</AccessMode>
                <pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Equal([0x20000010u, 0x20001010u], registers.Select(r => r.Address));
    }

    [Fact]
    public void SkipsAnUnresolvablePAddress()
    {
        List<XmlRegister> registers = Read("""
            <IntSwissKnife Name="Computed">
                <pVariable Name="X">Something</pVariable>
                <Formula>X * 4</Formula>
            </IntSwissKnife>
            <IntReg Name="Odd">
                <pAddress>Computed</pAddress>
                <Length>4</Length>
                <AccessMode>RO</AccessMode>
                <pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Empty(registers);
    }

    // --------------------------------------------------------------- what counts

    [Fact]
    public void IgnoresRegistersOnOtherPorts()
    {
        // chunk data and events are separate address spaces, not reachable by GVCP
        List<XmlRegister> registers = Read("""
            <IntReg Name="OnDevice">
                <Address>0x1000</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </IntReg>
            <IntReg Name="OnChunk">
                <Address>0x2000</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>chunkPort</pPort>
            </IntReg>
            """);

        Assert.Equal("OnDevice", Assert.Single(registers).Name);
    }

    [Fact]
    public void TakesStructRegAndMaskedIntRegButNotStructEntries()
    {
        // a StructEntry is a bit field inside its parent, not memory of its own
        List<XmlRegister> registers = Read("""
            <StructReg Comment="GevVersion">
                <Address>0x00000000</Address>
                <Length>4</Length>
                <AccessMode>RO</AccessMode>
                <pPort>Device</pPort>
                <StructEntry Name="Major"><LSB>15</LSB><MSB>0</MSB></StructEntry>
                <StructEntry Name="Minor"><LSB>31</LSB><MSB>16</MSB></StructEntry>
            </StructReg>
            <MaskedIntReg Name="Flag">
                <Address>0x00000010</Address>
                <Length>4</Length>
                <AccessMode>RO</AccessMode>
                <pPort>Device</pPort>
                <Bit>31</Bit>
            </MaskedIntReg>
            """);

        Assert.Equal(2, registers.Count);
        Assert.Equal("GevVersion", registers[0].Name); // falls back to Comment
        Assert.Equal("Flag", registers[1].Name);
    }

    [Fact]
    public void CollapsesSeveralFeaturesThatDescribeTheSameRegister()
    {
        // one register, one field each. they agree on address and length.
        List<XmlRegister> registers = Read("""
            <MaskedIntReg Name="CharacterSet">
                <Address>0x0004</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </MaskedIntReg>
            <MaskedIntReg Name="IsBigEndian">
                <Address>0x0004</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </MaskedIntReg>
            """);

        Assert.Equal(0x0004u, Assert.Single(registers).Address);
    }

    [Fact]
    public void SkipsARegisterWithNoLength()
    {
        List<XmlRegister> registers = Read("""
            <IntReg Name="Lengthless">
                <Address>0x1000</Address>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Empty(registers);
    }

    [Theory]
    [InlineData("RO", true)]
    [InlineData("RW", false)]
    [InlineData("WO", false)] // we have no write-only mode
    public void MapsTheAccessMode(string mode, bool readOnly)
    {
        RegAccess expected = readOnly ? RegAccess.ReadOnly : RegAccess.ReadWrite;

        List<XmlRegister> registers = Read($"""
            <IntReg Name="R">
                <Address>0x1000</Address><Length>4</Length>
                <AccessMode>{mode}</AccessMode><pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Equal(expected, Assert.Single(registers).Access);
    }

    [Fact]
    public void ReturnsRegistersInAddressOrder()
    {
        List<XmlRegister> registers = Read("""
            <IntReg Name="High">
                <Address>0xB0000000</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </IntReg>
            <IntReg Name="Low">
                <Address>0x00000100</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </IntReg>
            """);

        Assert.Equal(["Low", "High"], registers.Select(r => r.Name));
    }

    // --------------------------------------------------------------- byte order

    [Theory]
    [InlineData("<Endianess>BigEndian</Endianess>", true)]
    [InlineData("<Endianess>LittleEndian</Endianess>", false)]
    [InlineData("", false)] // genicam's default when the element is absent
    public void ReadsTheByteOrder(string element, bool bigEndian)
    {
        List<XmlRegister> registers = Read($"""
            <IntReg Name="R">
                <Address>0x20000000</Address><Length>4</Length>
                <AccessMode>RW</AccessMode><pPort>Device</pPort>
                {element}
            </IntReg>
            """);

        Assert.Equal(bigEndian ? Endianness.Big : Endianness.Little,
                     Assert.Single(registers).Endianness);
    }

    [Fact]
    public void ALittleEndianRegisterReadsBackTheWayItStoresIts()
    {
        var state = TestDevice.Bare();
        IPEndPoint controller = new(IPAddress.Parse("192.168.1.10"), 50000);
        state.WriteRegister(controller, 0x0A00, [0, 0, 0, 2]);

        // this is how the genie nano lays out its own registers
        state.DefineFromXml(Xml("""
            <IntReg Name="Little">
                <Address>0x20000000</Address><Length>4</Length>
                <AccessMode>RW</AccessMode><pPort>Device</pPort>
                <Endianess>LittleEndian</Endianess>
            </IntReg>
            """));

        // an application writes 0x000004D2 as the device stores it, low byte first
        state.WriteRegister(controller, 0x20000000, [0xD2, 0x04, 0x00, 0x00]);

        // the bytes come back untouched, and we read the value as 1234
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x20000000, out byte[]? raw));
        Assert.Equal<byte[]>([0xD2, 0x04, 0x00, 0x00], raw!);
        Assert.Equal(1234u, state.ReadUint(0x20000000));
    }

    [Fact]
    public void ABigEndianBootstrapRegisterIsUnaffected()
    {
        var state = TestDevice.Bare();

        // spec version, which we define big-endian
        Assert.Equal(0x00020002u, state.ReadUint(0x0000));
    }

    // --------------------------------------------------------------- taking them on

    [Fact]
    public void DefiningFromXmlLeavesExistingRegistersAlone()
    {
        var state = TestDevice.Bare();

        // 0x0000 is the version register, which we define ourselves with a value.
        // a description declaring the same address must not blank it.
        (int added, int skipped) = state.DefineFromXml(Xml("""
            <StructReg Comment="GevVersion">
                <Address>0x00000000</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </StructReg>
            <IntReg Name="VendorThing">
                <Address>0x20000000</Address><Length>4</Length>
                <AccessMode>RW</AccessMode><pPort>Device</pPort>
            </IntReg>
            """));

        Assert.Equal(1, added);
        Assert.Equal(1, skipped);

        state.ReadRegister(0x0000, out byte[]? version);
        Assert.Equal(0x00020002u, BinaryPrimitives.ReadUInt32BigEndian(version!));
    }

    [Fact]
    public void RegistersFromXmlAreReachableAndWritable()
    {
        var state = TestDevice.Bare();
        IPEndPoint controller = new(IPAddress.Parse("192.168.1.10"), 50000);
        state.WriteRegister(controller, 0x0A00, [0, 0, 0, 2]); // take control

        state.DefineFromXml(Xml("""
            <IntReg Name="WayUpHigh">
                <Address>0xB0000000</Address><Length>4</Length>
                <AccessMode>RW</AccessMode><pPort>Device</pPort>
            </IntReg>
            """));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS,
            state.WriteRegister(controller, 0xB0000000, [0xDE, 0xAD, 0xBE, 0xEF]));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0xB0000000, out byte[]? value));
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], value);
    }

    [Fact]
    public void AReadOnlyRegisterFromXmlIsWriteProtected()
    {
        var state = TestDevice.Bare();
        IPEndPoint controller = new(IPAddress.Parse("192.168.1.10"), 50000);
        state.WriteRegister(controller, 0x0A00, [0, 0, 0, 2]);

        state.DefineFromXml(Xml("""
            <IntReg Name="Fixed">
                <Address>0x20000000</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </IntReg>
            """));

        Assert.Equal(GVCPStatus.GEV_STATUS_WRITE_PROTECT,
            state.WriteRegister(controller, 0x20000000, [1, 2, 3, 4]));
    }

    [Fact]
    public void GapsBetweenXmlRegistersStillDoNotExist()
    {
        var state = TestDevice.Bare();

        state.DefineFromXml(Xml("""
            <IntReg Name="A">
                <Address>0x20000000</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </IntReg>
            <IntReg Name="B">
                <Address>0x30000000</Address><Length>4</Length>
                <AccessMode>RO</AccessMode><pPort>Device</pPort>
            </IntReg>
            """));

        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x20000000, out _));
        Assert.Equal(GVCPStatus.GEV_STATUS_SUCCESS, state.ReadRegister(0x30000000, out _));
        Assert.Equal(GVCPStatus.GEV_STATUS_INVALID_ADDRESS, state.ReadRegister(0x28000000, out _));
    }
}
