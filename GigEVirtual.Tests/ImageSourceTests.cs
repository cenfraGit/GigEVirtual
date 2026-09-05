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