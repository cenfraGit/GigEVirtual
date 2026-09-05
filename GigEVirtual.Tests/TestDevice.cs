// --------------------------------------------------------------------------------
// TestDevice.cs
//
// device state for tests to work against. Camera() builds the real virtual
// camera's registers rather than a copy, so the tests exercise what ships.
// --------------------------------------------------------------------------------

using GigEVirtual;

namespace GigEVirtual.Tests;

internal static class TestDevice
{
    // enough of a description to fill the first url and the blob register. tests
    // never touch a vendor file, so this stands in for one.
    public const string MinimalXml =
        """<?xml version="1.0" encoding="UTF-8"?>""" +
        """<RegisterDescription xmlns="http://www.genicam.org/GenApi/Version_1_1">""" +
        """<Port Name="Device" /></RegisterDescription>""";

    // bootstrap registers only, the way a device with no features of its own
    // would look
    public static DeviceState Bare() => new(MinimalXml, "test.xml", 0xA200);

    // the virtual camera: bootstrap plus its 0xA000 feature block
    public static DeviceState Camera() => GigECamera.BuildState();
}
