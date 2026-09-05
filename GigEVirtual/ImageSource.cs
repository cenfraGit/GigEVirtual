// --------------------------------------------------------------------------------
// ImageSource.cs
//
// supplies the mono8 frames the transmitter streams. either from image files on
// disk, or from a generated pattern when no path is given.
// --------------------------------------------------------------------------------

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GigEVirtual;

internal class ImageSource
{
    // --------------------------------------------------------------- fields and properties

    private static readonly string[] _extensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    // empty when no path was given, which means the generated pattern
    private readonly string[] _files;

    private int _frameNumber;

    // the last frame we converted. a single image would otherwise be decoded
    // again for every block we send.
    private byte[]? _cached;
    private int _cachedFile = -1;
    private int _cachedWidth;
    private int _cachedHeight;

    // --------------------------------------------------------------- constructors

    // path can be a single image, a directory of images to loop through, or null
    // for the generated pattern.
    public ImageSource(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _files = [];
            return;
        }

        if (File.Exists(path))
        {
            _files = [path];
            return;
        }

        if (!Directory.Exists(path))
            throw new FileNotFoundException($"image source not found: {path}");

        _files = [.. Directory.EnumerateFiles(path)
            .Where(f => _extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order()];

        if (_files.Length == 0)
            throw new FileNotFoundException($"no images in directory: {path}");
    }

    // --------------------------------------------------------------- methods

    // one mono8 frame, scaled to the size the device is currently reporting.
    // advances to the next file when there is more than one.
    public byte[] NextFrame(int width, int height)
    {
        byte[] frame = _files.Length == 0
            ? BuildPattern(width, height)
            : LoadFile(_frameNumber % _files.Length, width, height);

        _frameNumber++;
        return frame;
    }

    private byte[] LoadFile(int fileIndex, int width, int height)
    {
        if (_cached is not null && _cachedFile == fileIndex &&
            _cachedWidth == width && _cachedHeight == height)
            return _cached;

        // L8 is 8 bits of luminance per pixel, so ImageSharp does the greyscale
        // conversion for us if the file is colour
        using Image<L8> image = Image.Load<L8>(_files[fileIndex]);
        image.Mutate(x => x.Resize(width, height));

        byte[] frame = new byte[width * height];
        image.CopyPixelDataTo(frame);

        _cached = frame;
        _cachedFile = fileIndex;
        _cachedWidth = width;
        _cachedHeight = height;

        return frame;
    }

    // checkerboard that shifts every frame, so it is obvious from the client
    // whether blocks are actually arriving
    private byte[] BuildPattern(int width, int height)
    {
        const int square = 32;
        int shift = _frameNumber * 4;

        byte[] frame = new byte[width * height];

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                frame[y * width + x] =
                    ((x + shift) / square + y / square) % 2 == 0 ? (byte)200 : (byte)40;

        return frame;
    }
}