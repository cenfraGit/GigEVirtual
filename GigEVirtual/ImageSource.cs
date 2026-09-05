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
    private uint _cachedFormat;
    private float _cachedBrightness;
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

    // one frame in the format the device is currently reporting, scaled to the
    // size it reports. advances to the next file when there is more than one.
    public byte[] NextFrame(int width, int height, uint pixelFormat = GVSPPixelFormats.Mono8,
                            float brightness = 1.0f)
    {
        PixelFormat format = GVSPPixelFormats.Find(pixelFormat)
            ?? throw new NotSupportedException($"pixel format 0x{pixelFormat:X8} is not supported");

        byte[] frame = _files.Length == 0
            ? BuildPattern(width, height, format, brightness)
            : LoadFile(_frameNumber % _files.Length, width, height, format, brightness);

        _frameNumber++;
        return frame;
    }

    private byte[] LoadFile(int fileIndex, int width, int height, PixelFormat format, float brightness)
    {
        if (_cached is not null && _cachedFile == fileIndex && _cachedFormat == format.Id &&
            _cachedWidth == width && _cachedHeight == height && _cachedBrightness == brightness)
            return _cached;

        using Image<Rgb24> image = Image.Load<Rgb24>(_files[fileIndex]);
        image.Mutate(x => x.Resize(width, height));

        byte[] frame = Convert(image, width, height, format, brightness);

        _cached = frame;
        _cachedBrightness = brightness;
        _cachedFile = fileIndex;
        _cachedFormat = format.Id;
        _cachedWidth = width;
        _cachedHeight = height;

        return frame;
    }

    // lays the decoded image out the way the pixel format wants it
    private static byte[] Convert(Image<Rgb24> image, int width, int height,
                                  PixelFormat format, float brightness)
    {
        // rgb is already interleaved the way the wire wants it
        if (format.Id == GVSPPixelFormats.RGB8)
        {
            byte[] rgb = new byte[width * height * 3];
            image.CopyPixelDataTo(rgb);

            for (int i = 0; i < rgb.Length; i++) rgb[i] = Scale(rgb[i], brightness);
            return rgb;
        }

        byte[] frame = new byte[width * height * format.BitsPerPixel / 8];

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);

                for (int x = 0; x < width; x++)
                {
                    Rgb24 pixel = row[x];

                    // a bayer sensor only measures one component per pixel, so
                    // pick the one this position sits under. otherwise luminance.
                    byte sample = format.Bayer is null
                        ? Luma(pixel)
                        : format.Bayer[(y % 2) * 2 + (x % 2)] switch
                        {
                            'R' => pixel.R,
                            'G' => pixel.G,
                            _ => pixel.B,
                        };

                    Write(frame, (y * width + x), Scale(sample, brightness), format);
                }
            }
        });

        return frame;
    }

    // our source is 8 bits, so spread it across the format's full range
    private static void Write(byte[] frame, int index, byte sample, PixelFormat format)
    {
        int value = sample * ((1 << format.Depth) - 1) / 255;

        if (format.BitsPerPixel == 8)
        {
            frame[index] = (byte)value;
            return;
        }

        // spec says multi-byte pixel data is little-endian, the opposite of the
        // gvsp headers around it
        frame[index * 2] = (byte)value;
        frame[index * 2 + 1] = (byte)(value >> 8);
    }

    // more or less light than the sensor's normal exposure. clips at full scale
    // rather than wrapping round, the way a real sensor saturates.
    private static byte Scale(byte sample, float brightness)
    {
        if (brightness == 1.0f) return sample;

        float scaled = sample * brightness;
        return scaled >= 255f ? (byte)255 : (byte)scaled;
    }

    private static byte Luma(Rgb24 pixel) =>
        (byte)((pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000);

    // checkerboard that shifts every frame, so it is obvious from the client
    // whether blocks are actually arriving
    private byte[] BuildPattern(int width, int height, PixelFormat format, float brightness)
    {
        const int square = 32;
        int shift = _frameNumber * 4;

        using var image = new Image<Rgb24>(width, height);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);

                for (int x = 0; x < width; x++)
                {
                    byte v = ((x + shift) / square + y / square) % 2 == 0 ? (byte)200 : (byte)40;
                    row[x] = new Rgb24(v, v, v);
                }
            }
        });

        return Convert(image, width, height, format, brightness);
    }
}