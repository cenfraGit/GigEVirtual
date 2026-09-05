// --------------------------------------------------------------------------------
// ImageSourceTests.cs
//
// covers frame generation: files, directories, scaling and the fallback pattern.
// --------------------------------------------------------------------------------

using GigEVirtual;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace GigEVirtual.Tests;

public class ImageSourceTests : IDisposable
{
    private readonly string _dir;

    public ImageSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gigevirtual-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // a uniform greyscale png, so a frame read back is easy to assert on
    private string WriteGrey(string name, byte value, int width = 16, int height = 16)
    {
        string path = Path.Combine(_dir, name);
        using var image = new Image<L8>(width, height, new L8(value));
        image.SaveAsPng(path);
        return path;
    }

    // --------------------------------------------------------------- pattern

    [Fact]
    public void PatternFillsTheRequestedSize()
    {
        var source = new ImageSource();

        byte[] frame = source.NextFrame(64, 48);

        Assert.Equal(64 * 48, frame.Length);
    }

    [Fact]
    public void PatternMovesBetweenFrames()
    {
        var source = new ImageSource();

        byte[] first = source.NextFrame(64, 48);
        byte[] second = source.NextFrame(64, 48);

        Assert.NotEqual(first, second);
    }

    // --------------------------------------------------------------- single file

    [Fact]
    public void SingleFileIsScaledToTheDeviceGeometry()
    {
        var source = new ImageSource(WriteGrey("one.png", 128));

        byte[] frame = source.NextFrame(32, 24);

        Assert.Equal(32 * 24, frame.Length);
        Assert.All(frame, b => Assert.Equal(128, b));
    }

    [Fact]
    public void SingleFileRepeatsForEveryFrame()
    {
        var source = new ImageSource(WriteGrey("one.png", 90));

        Assert.Equal(source.NextFrame(16, 16), source.NextFrame(16, 16));
    }

    [Fact]
    public void ChangingGeometryRescalesTheSameFile()
    {
        var source = new ImageSource(WriteGrey("one.png", 77));

        Assert.Equal(16 * 16, source.NextFrame(16, 16).Length);

        byte[] bigger = source.NextFrame(64, 64);
        Assert.Equal(64 * 64, bigger.Length);
        Assert.All(bigger, b => Assert.Equal(77, b));
    }

    [Fact]
    public void ColourImagesAreConvertedToGreyscale()
    {
        string path = Path.Combine(_dir, "red.png");
        using (var image = new Image<Rgba32>(16, 16, new Rgba32(255, 0, 0)))
            image.SaveAsPng(path);

        byte[] frame = new ImageSource(path).NextFrame(16, 16);

        // luma, so red lands somewhere between black and white rather than
        // just taking the red channel
        Assert.All(frame, b => Assert.InRange(b, 1, 254));
        Assert.All(frame, b => Assert.Equal(frame[0], b));
    }

    // --------------------------------------------------------------- pixel formats

    // a uniform colour png, so each component is easy to pick out of a frame
    private string WriteColour(string name, byte r, byte g, byte b, int size = 16)
    {
        string path = Path.Combine(_dir, name);
        using var image = new Image<Rgb24>(size, size, new Rgb24(r, g, b));
        image.SaveAsPng(path);
        return path;
    }

    private static ushort ReadSample(byte[] frame, int index) =>
        (ushort)(frame[index * 2] | (frame[index * 2 + 1] << 8));

    [Theory]
    [InlineData(GVSPPixelFormats.Mono8, 8)]
    [InlineData(GVSPPixelFormats.Mono10, 16)]
    [InlineData(GVSPPixelFormats.Mono12, 16)]
    [InlineData(GVSPPixelFormats.Mono16, 16)]
    [InlineData(GVSPPixelFormats.BayerRG8, 8)]
    [InlineData(GVSPPixelFormats.BayerRG12, 16)]
    [InlineData(GVSPPixelFormats.RGB8, 24)]
    public void FrameSizeMatchesTheFormatsBitsPerPixel(uint format, int bitsPerPixel)
    {
        var source = new ImageSource(WriteGrey("one.png", 128));

        byte[] frame = source.NextFrame(32, 24, format);

        Assert.Equal(32 * 24 * bitsPerPixel / 8, frame.Length);
    }

    [Fact]
    public void TableAgreesWithTheIdsEncodedBitsPerPixel()
    {
        // the naming convention puts the container size in bits 16-23 of the id,
        // so the table and the constants have to agree
        foreach (PixelFormat format in GVSPPixelFormats.All)
            Assert.Equal(GVSPPixelFormats.BitsPerPixelFromId(format.Id), format.BitsPerPixel);
    }

    [Fact]
    public void DeeperFormatsSpreadTheSampleOverTheirFullRange()
    {
        var source = new ImageSource(WriteGrey("white.png", 255));

        Assert.Equal(255, source.NextFrame(8, 8, GVSPPixelFormats.Mono8)[0]);
        Assert.Equal(1023, ReadSample(source.NextFrame(8, 8, GVSPPixelFormats.Mono10), 0));
        Assert.Equal(4095, ReadSample(source.NextFrame(8, 8, GVSPPixelFormats.Mono12), 0));
        Assert.Equal(65535, ReadSample(source.NextFrame(8, 8, GVSPPixelFormats.Mono16), 0));
    }

    [Fact]
    public void MultiByteSamplesAreLittleEndian()
    {
        // spec says pixel data is little-endian, the opposite of the gvsp headers
        byte[] frame = new ImageSource(WriteGrey("white.png", 255))
            .NextFrame(8, 8, GVSPPixelFormats.Mono12);

        Assert.Equal(0xFF, frame[0]); // low byte first
        Assert.Equal(0x0F, frame[1]);
    }

    [Fact]
    public void Rgb8KeepsEveryComponent()
    {
        byte[] frame = new ImageSource(WriteColour("rgb.png", 10, 20, 30))
            .NextFrame(8, 8, GVSPPixelFormats.RGB8);

        Assert.Equal(10, frame[0]);
        Assert.Equal(20, frame[1]);
        Assert.Equal(30, frame[2]);
    }

    [Fact]
    public void BayerPicksOneComponentPerPixelPosition()
    {
        var source = new ImageSource(WriteColour("rgb.png", 10, 20, 30));

        // BayerRG8 is an RGGB tile, so row 0 is R G and row 1 is G B
        byte[] frame = source.NextFrame(4, 4, GVSPPixelFormats.BayerRG8);

        Assert.Equal(10, frame[0]); // (0,0) red
        Assert.Equal(20, frame[1]); // (1,0) green
        Assert.Equal(20, frame[4]); // (0,1) green
        Assert.Equal(30, frame[5]); // (1,1) blue
    }

    [Fact]
    public void BayerTilesDifferBetweenFormats()
    {
        var source = new ImageSource(WriteColour("rgb.png", 10, 20, 30));

        // BGGR starts on blue where RGGB starts on red
        Assert.Equal(30, source.NextFrame(4, 4, GVSPPixelFormats.BayerBG8)[0]);
        Assert.Equal(10, source.NextFrame(4, 4, GVSPPixelFormats.BayerRG8)[0]);
    }

    [Fact]
    public void ChangingFormatRebuildsTheFrame()
    {
        var source = new ImageSource(WriteGrey("one.png", 255));

        Assert.Equal(8 * 8, source.NextFrame(8, 8, GVSPPixelFormats.Mono8).Length);
        Assert.Equal(8 * 8 * 2, source.NextFrame(8, 8, GVSPPixelFormats.Mono16).Length);
        Assert.Equal(8 * 8, source.NextFrame(8, 8, GVSPPixelFormats.Mono8).Length);
    }

    [Fact]
    public void PatternHonoursTheFormatToo()
    {
        var source = new ImageSource();

        Assert.Equal(16 * 16 * 2, source.NextFrame(16, 16, GVSPPixelFormats.Mono12).Length);
        Assert.Equal(16 * 16 * 3, source.NextFrame(16, 16, GVSPPixelFormats.RGB8).Length);
    }

    [Fact]
    public void UnknownFormatIsRejected()
    {
        var source = new ImageSource();

        Assert.Throws<NotSupportedException>(() => source.NextFrame(8, 8, 0xDEADBEEF));
    }

    // --------------------------------------------------------------- directory

    [Fact]
    public void DirectoryLoopsThroughItsImagesInOrder()
    {
        WriteGrey("a.png", 10);
        WriteGrey("b.png", 20);
        WriteGrey("c.png", 30);

        var source = new ImageSource(_dir);

        Assert.Equal(10, source.NextFrame(8, 8)[0]);
        Assert.Equal(20, source.NextFrame(8, 8)[0]);
        Assert.Equal(30, source.NextFrame(8, 8)[0]);

        // wraps back around
        Assert.Equal(10, source.NextFrame(8, 8)[0]);
    }

    [Fact]
    public void DirectoryIgnoresNonImageFiles()
    {
        WriteGrey("a.png", 10);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not an image");

        var source = new ImageSource(_dir);

        Assert.Equal(10, source.NextFrame(8, 8)[0]);
        Assert.Equal(10, source.NextFrame(8, 8)[0]);
    }

    // --------------------------------------------------------------- failures

    [Fact]
    public void MissingPathFailsImmediately()
    {
        Assert.Throws<FileNotFoundException>(
            () => new ImageSource(Path.Combine(_dir, "nope.png")));
    }

    [Fact]
    public void DirectoryWithNoImagesFailsImmediately()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not an image");

        Assert.Throws<FileNotFoundException>(() => new ImageSource(_dir));
    }
}