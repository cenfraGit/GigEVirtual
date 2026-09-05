// --------------------------------------------------------------------------------
// GVSPPixelFormats.cs
//
// pixel formats with their pixel_format_id (section 26.3 Pixel Formats) and what
// the transmitter needs to know to lay one out on the wire
// --------------------------------------------------------------------------------

namespace GigEVirtual;

// BitsPerPixel is the container on the wire, Depth is how many of those bits
// actually carry the value. Mono12 is 12 significant bits inside 16.
// Bayer is the 2x2 tile starting at the top-left pixel, or null when every pixel
// carries the same component
internal record PixelFormat(uint Id, string Name, int BitsPerPixel, int Depth, string? Bayer);

internal static class GVSPPixelFormats
{
    public const uint Mono8 = 0x01080001;
    public const uint Mono10 = 0x01100003;
    public const uint Mono12 = 0x01100005;
    public const uint Mono16 = 0x01100007;

    public const uint BayerGR8 = 0x01080008;
    public const uint BayerRG8 = 0x01080009;
    public const uint BayerGB8 = 0x0108000A;
    public const uint BayerBG8 = 0x0108000B;

    public const uint BayerGR10 = 0x0110000C;
    public const uint BayerRG10 = 0x0110000D;
    public const uint BayerGB10 = 0x0110000E;
    public const uint BayerBG10 = 0x0110000F;

    public const uint BayerGR12 = 0x01100010;
    public const uint BayerRG12 = 0x01100011;
    public const uint BayerGB12 = 0x01100012;
    public const uint BayerBG12 = 0x01100013;

    public const uint RGB8 = 0x02180014;

    private static readonly PixelFormat[] _formats =
    [
        new(Mono8,  "Mono8",  8,  8,  null),
        new(Mono10, "Mono10", 16, 10, null),
        new(Mono12, "Mono12", 16, 12, null),
        new(Mono16, "Mono16", 16, 16, null),

        new(BayerGR8, "BayerGR8", 8, 8, "GRBG"),
        new(BayerRG8, "BayerRG8", 8, 8, "RGGB"),
        new(BayerGB8, "BayerGB8", 8, 8, "GBRG"),
        new(BayerBG8, "BayerBG8", 8, 8, "BGGR"),

        new(BayerGR10, "BayerGR10", 16, 10, "GRBG"),
        new(BayerRG10, "BayerRG10", 16, 10, "RGGB"),
        new(BayerGB10, "BayerGB10", 16, 10, "GBRG"),
        new(BayerBG10, "BayerBG10", 16, 10, "BGGR"),

        new(BayerGR12, "BayerGR12", 16, 12, "GRBG"),
        new(BayerRG12, "BayerRG12", 16, 12, "RGGB"),
        new(BayerGB12, "BayerGB12", 16, 12, "GBRG"),
        new(BayerBG12, "BayerBG12", 16, 12, "BGGR"),

        new(RGB8, "RGB8", 24, 8, null),
    ];

    public static PixelFormat? Find(uint id) => _formats.FirstOrDefault(f => f.Id == id);

    public static IReadOnlyList<PixelFormat> All => _formats;

    // the naming convention packs the container size into bits 16-23 of the id,
    // so this is a cross-check that the table above matches the spec values
    public static int BitsPerPixelFromId(uint id) => (int)((id >> 16) & 0xFF);
}