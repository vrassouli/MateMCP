using System.IO.Compression;
using System.Text;
using MateMCP.Agent.Browser;

namespace MateMCP.Agent.Tests;

public sealed class BrowserVisualQaTests
{
    [Theory]
    [InlineData("desktop", 1440, 900)]
    [InlineData("laptop", 1280, 800)]
    [InlineData("tablet", 768, 1024)]
    [InlineData("mobile", 390, 844)]
    public void Resolves_standard_viewport_presets(string preset, int width, int height)
    {
        var viewport = BrowserVisualQaService.ResolveViewport(preset, null, null, 1);
        Assert.Equal(width, viewport.Width);
        Assert.Equal(height, viewport.Height);
        Assert.Equal(1, viewport.DeviceScaleFactor);
    }

    [Fact]
    public void Resolves_custom_viewport_and_rejects_partial_dimensions()
    {
        var viewport = BrowserVisualQaService.ResolveViewport(null, 1111, 777, 2);
        Assert.Equal(new BrowserViewport(1111, 777, 2), viewport);
        Assert.Throws<ArgumentException>(() => BrowserVisualQaService.ResolveViewport(null, 1111, null, 1));
        Assert.Throws<ArgumentException>(() => BrowserVisualQaService.ResolveViewport("mobile", 390, 844, 1));
    }

    [Fact]
    public void Compare_is_deterministic_and_reports_changed_region()
    {
        var before = Buffer(32, 32, 10);
        var after = Buffer(32, 32, 10);
        SetPixel(after, 20, 18, 200, 10, 10, 255);
        SetPixel(after, 21, 18, 200, 10, 10, 255);

        var first = BrowserVisualQaService.ComparePixels("before", before, "after", after, 0);
        var second = BrowserVisualQaService.ComparePixels("before", before, "after", after, 0);

        Assert.Equal(first.BeforeId, second.BeforeId);
        Assert.Equal(first.AfterId, second.AfterId);
        Assert.Equal(first.ChangedPixels, second.ChangedPixels);
        Assert.Equal(first.ChangedPercent, second.ChangedPercent);
        Assert.Equal(first.Regions, second.Regions);
        Assert.True(first.Comparable);
        Assert.False(first.SizeMismatch);
        Assert.Equal(2, first.ChangedPixels);
        Assert.Equal(1024, first.TotalPixels);
        var region = Assert.Single(first.Regions);
        Assert.Equal(new VisualDiffRegion(16, 16, 16, 16, 2), region);
    }

    [Fact]
    public void Compare_tolerance_ignores_small_antialiasing_noise()
    {
        var before = Buffer(2, 1, 100);
        var after = Buffer(2, 1, 100);
        SetPixel(after, 0, 0, 106, 96, 103, 255);
        SetPixel(after, 1, 0, 120, 100, 100, 255);

        var tolerant = BrowserVisualQaService.ComparePixels("a", before, "b", after, 8);
        var strict = BrowserVisualQaService.ComparePixels("a", before, "b", after, 0);

        Assert.Equal(1, tolerant.ChangedPixels);
        Assert.Equal(2, strict.ChangedPixels);
    }

    [Fact]
    public void Compare_reports_size_mismatch_without_resampling()
    {
        var result = BrowserVisualQaService.ComparePixels(
            "desktop-before", Buffer(10, 10, 0),
            "mobile-after", Buffer(9, 10, 0),
            8);

        Assert.False(result.Comparable);
        Assert.True(result.SizeMismatch);
        Assert.Equal(10, result.BeforeWidth);
        Assert.Equal(9, result.AfterWidth);
        Assert.Empty(result.Regions);
    }

    [Fact]
    public void Png_decoder_decodes_truecolor_rgba_pixels()
    {
        var rgba = new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 128,
            0, 0, 255, 255,
            1, 2, 3, 4
        };
        var png = EncodeRgbaPng(2, 2, rgba);

        var decoded = BrowserVisualQaService.DecodePng(png);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba);
    }

    private static VisualPixelBuffer Buffer(int width, int height, byte value)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = value;
            rgba[i + 1] = value;
            rgba[i + 2] = value;
            rgba[i + 3] = 255;
        }
        return new VisualPixelBuffer(width, height, rgba);
    }

    private static void SetPixel(VisualPixelBuffer buffer, int x, int y, byte r, byte g, byte b, byte a)
    {
        var offset = (y * buffer.Width + x) * 4;
        buffer.Rgba[offset] = r;
        buffer.Rgba[offset + 1] = g;
        buffer.Rgba[offset + 2] = b;
        buffer.Rgba[offset + 3] = a;
    }

    private static byte[] EncodeRgbaPng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(output, "IHDR", ihdr);

        using var raw = new MemoryStream();
        var stride = width * 4;
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * stride, stride);
        }
        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            raw.CopyTo(zlib);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        // Decoder intentionally does not depend on CRC, so zero is sufficient for this unit fixture.
        stream.Write(new byte[4]);
    }

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }
}
