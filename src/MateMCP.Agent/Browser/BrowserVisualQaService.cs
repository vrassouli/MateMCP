using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace MateMCP.Agent.Browser;

public sealed record VisualViewportPreset(string Name, int Width, int Height, double DeviceScaleFactor = 1);
public sealed record VisualCaptureOptions(
    string? Preset = null,
    int? Width = null,
    int? Height = null,
    double DeviceScaleFactor = 1,
    int SettleMs = 250,
    int MaxElements = 500,
    bool FullPage = false,
    bool DisableAnimations = true,
    IReadOnlyList<string>? MaskCss = null);
public sealed record BrowserVisualState(BrowserScreenshot Screenshot, BrowserSnapshot Snapshot);
public sealed record VisualCaptureMetadata(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? Preset,
    string Url,
    string Title,
    BrowserViewport Viewport,
    int ImageWidth,
    int ImageHeight,
    int Bytes,
    int ElementCount,
    bool SnapshotTruncated,
    int MaskCount,
    BrowserSnapshot Snapshot);
public sealed record VisualCaptureResult(VisualCaptureMetadata Metadata, byte[] ImageBytes, string MimeType);
public sealed record VisualDiffRegion(int X, int Y, int Width, int Height, long ChangedPixels);
public sealed record VisualCompareResult(
    string BeforeId,
    string AfterId,
    bool Comparable,
    bool SizeMismatch,
    int BeforeWidth,
    int BeforeHeight,
    int AfterWidth,
    int AfterHeight,
    int Tolerance,
    long ChangedPixels,
    long TotalPixels,
    double ChangedPercent,
    IReadOnlyList<VisualDiffRegion> Regions);

internal sealed record VisualPixelBuffer(int Width, int Height, byte[] Rgba);
internal sealed record StoredVisualCapture(VisualCaptureMetadata Metadata, byte[] PngBytes, VisualPixelBuffer Pixels);

public sealed class BrowserVisualQaService
{
    public static BrowserVisualQaService Shared { get; } = new(BrowserAutomationService.Shared);

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const int MaxCaptures = 24;
    private const long MaxRetainedBytes = 128L * 1024 * 1024;
    private const long MaxDecodedPixels = 16_000_000;
    private readonly BrowserAutomationService _browser;
    private readonly ConcurrentDictionary<string, StoredVisualCapture> _captures = new(StringComparer.Ordinal);

    internal BrowserVisualQaService(BrowserAutomationService browser) => _browser = browser;

    public static IReadOnlyList<VisualViewportPreset> Presets { get; } =
    [
        new("desktop", 1440, 900),
        new("laptop", 1280, 800),
        new("tablet", 768, 1024),
        new("mobile", 390, 844)
    ];

    public static BrowserViewport ResolveViewport(string? preset, int? width, int? height, double deviceScaleFactor)
    {
        deviceScaleFactor = Math.Clamp(deviceScaleFactor, 0.5, 4);
        if (!string.IsNullOrWhiteSpace(preset))
        {
            var normalized = preset.Trim().ToLowerInvariant();
            var match = Presets.FirstOrDefault(value => value.Name == normalized)
                ?? throw new ArgumentException($"Unknown visual viewport preset '{preset}'. Use desktop, laptop, tablet, mobile, or explicit width/height.", nameof(preset));
            if (width is not null || height is not null)
                throw new ArgumentException("Use either a viewport preset or explicit width/height, not both.");
            return new BrowserViewport(match.Width, match.Height, deviceScaleFactor);
        }

        if (width is null && height is null)
            return new BrowserViewport(1440, 900, deviceScaleFactor);
        if (width is null || height is null)
            throw new ArgumentException("Custom visual viewport requires both width and height.");
        if (width is < 200 or > 5000 || height is < 200 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(width), "Visual viewport width/height must be between 200 and 5000 CSS pixels.");
        return new BrowserViewport(width.Value, height.Value, deviceScaleFactor);
    }

    public async Task<VisualCaptureResult> CaptureAsync(VisualCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        CleanupExpired();
        var viewport = ResolveViewport(options.Preset, options.Width, options.Height, options.DeviceScaleFactor);
        var settleMs = Math.Clamp(options.SettleMs, 0, 5000);
        var maxElements = Math.Clamp(options.MaxElements, 1, 1500);
        var masks = NormalizeMasks(options.MaskCss);
        var state = await _browser.CaptureVisualStateAsync(
            viewport,
            settleMs,
            maxElements,
            options.FullPage,
            options.DisableAnimations,
            masks,
            cancellationToken);
        var pixels = DecodePng(state.Screenshot.Bytes);
        var createdAt = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("n");
        var metadata = new VisualCaptureMetadata(
            id,
            createdAt,
            createdAt.Add(Lifetime),
            string.IsNullOrWhiteSpace(options.Preset) ? null : options.Preset.Trim().ToLowerInvariant(),
            state.Screenshot.Url,
            state.Screenshot.Title,
            state.Screenshot.Viewport,
            pixels.Width,
            pixels.Height,
            state.Screenshot.Bytes.Length,
            state.Snapshot.Elements.Count,
            state.Snapshot.Truncated,
            masks.Count,
            state.Snapshot);
        _captures[id] = new StoredVisualCapture(metadata, state.Screenshot.Bytes, pixels);
        EnforceBound();
        return new VisualCaptureResult(metadata, state.Screenshot.Bytes, state.Screenshot.MimeType);
    }

    public VisualCompareResult Compare(string beforeId, string afterId, int tolerance = 8)
    {
        CleanupExpired();
        tolerance = Math.Clamp(tolerance, 0, 255);
        var before = Get(beforeId);
        var after = Get(afterId);
        return ComparePixels(before.Metadata.Id, before.Pixels, after.Metadata.Id, after.Pixels, tolerance);
    }

    internal static VisualCompareResult ComparePixels(string beforeId, VisualPixelBuffer before, string afterId, VisualPixelBuffer after, int tolerance)
    {
        tolerance = Math.Clamp(tolerance, 0, 255);
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return new VisualCompareResult(
                beforeId, afterId, false, true,
                before.Width, before.Height, after.Width, after.Height,
                tolerance, 0, 0, 0, []);
        }

        var width = before.Width;
        var height = before.Height;
        var total = (long)width * height;
        if (before.Rgba.Length != total * 4 || after.Rgba.Length != total * 4)
            throw new InvalidOperationException("Visual pixel buffer length is invalid.");

        const int tileSize = 16;
        var tileColumns = (width + tileSize - 1) / tileSize;
        var tileRows = (height + tileSize - 1) / tileSize;
        var changedTiles = new long[tileColumns * tileRows];
        long changed = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var delta = Math.Max(
                    Math.Max(Math.Abs(before.Rgba[i] - after.Rgba[i]), Math.Abs(before.Rgba[i + 1] - after.Rgba[i + 1])),
                    Math.Max(Math.Abs(before.Rgba[i + 2] - after.Rgba[i + 2]), Math.Abs(before.Rgba[i + 3] - after.Rgba[i + 3])));
                if (delta <= tolerance) continue;
                changed++;
                changedTiles[(y / tileSize) * tileColumns + (x / tileSize)]++;
            }
        }

        var regions = BuildRegions(changedTiles, tileColumns, tileRows, width, height, tileSize);
        return new VisualCompareResult(
            beforeId, afterId, true, false,
            width, height, width, height,
            tolerance, changed, total,
            total == 0 ? 0 : Math.Round(changed * 100d / total, 6),
            regions);
    }

    private StoredVisualCapture Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_captures.TryGetValue(id.Trim(), out var capture))
            throw new KeyNotFoundException($"Visual capture '{id}' was not found or has expired.");
        return capture;
    }

    private static IReadOnlyList<string> NormalizeMasks(IReadOnlyList<string>? masks)
    {
        if (masks is null || masks.Count == 0) return [];
        if (masks.Count > 32) throw new ArgumentOutOfRangeException(nameof(masks), "At most 32 CSS mask selectors are allowed.");
        var result = new List<string>(masks.Count);
        foreach (var mask in masks)
        {
            if (string.IsNullOrWhiteSpace(mask)) continue;
            var value = mask.Trim();
            if (value.Length > 500) throw new ArgumentOutOfRangeException(nameof(masks), "Each CSS mask selector is limited to 500 characters.");
            result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<VisualDiffRegion> BuildRegions(long[] tiles, int columns, int rows, int imageWidth, int imageHeight, int tileSize)
    {
        var visited = new bool[tiles.Length];
        var regions = new List<VisualDiffRegion>();
        for (var index = 0; index < tiles.Length; index++)
        {
            if (visited[index] || tiles[index] == 0) continue;
            var queue = new Queue<int>();
            queue.Enqueue(index);
            visited[index] = true;
            var minX = columns;
            var minY = rows;
            var maxX = 0;
            var maxY = 0;
            long changed = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % columns;
                var y = current / columns;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                changed += tiles[current];
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }
            var left = minX * tileSize;
            var top = minY * tileSize;
            var right = Math.Min(imageWidth, (maxX + 1) * tileSize);
            var bottom = Math.Min(imageHeight, (maxY + 1) * tileSize);
            regions.Add(new VisualDiffRegion(left, top, right - left, bottom - top, changed));

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= columns || y >= rows) return;
                var next = y * columns + x;
                if (visited[next] || tiles[next] == 0) return;
                visited[next] = true;
                queue.Enqueue(next);
            }
        }
        return regions
            .OrderByDescending(region => region.ChangedPixels)
            .ThenBy(region => region.Y)
            .ThenBy(region => region.X)
            .Take(64)
            .ToArray();
    }

    internal static VisualPixelBuffer DecodePng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidOperationException("Visual capture is not a valid PNG image.");

        var offset = 8;
        var width = 0;
        var height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        using var idat = new MemoryStream();
        while (offset + 12 <= png.Length)
        {
            var length = ReadBigEndianInt32(png, offset);
            offset += 4;
            if (length < 0 || offset + 8L + length > png.Length)
                throw new InvalidOperationException("PNG chunk length is invalid.");
            var type = Encoding.ASCII.GetString(png, offset, 4);
            offset += 4;
            if (type == "IHDR")
            {
                if (length != 13) throw new InvalidOperationException("PNG IHDR length is invalid.");
                width = ReadBigEndianInt32(png, offset);
                height = ReadBigEndianInt32(png, offset + 4);
                bitDepth = png[offset + 8];
                colorType = png[offset + 9];
                var compression = png[offset + 10];
                var filter = png[offset + 11];
                var interlace = png[offset + 12];
                if (width <= 0 || height <= 0 || width > 10000 || height > 10000 || (long)width * height > MaxDecodedPixels)
                    throw new InvalidOperationException("PNG dimensions are outside MateMCP visual QA limits.");
                if (bitDepth != 8 || colorType is not (2 or 6) || compression != 0 || filter != 0 || interlace != 0)
                    throw new InvalidOperationException($"Unsupported PNG format for visual QA (bitDepth={bitDepth}, colorType={colorType}, interlace={interlace}).");
            }
            else if (type == "IDAT")
            {
                idat.Write(png, offset, length);
            }
            offset += length + 4; // data + CRC
            if (type == "IEND") break;
        }

        if (width == 0 || height == 0 || idat.Length == 0)
            throw new InvalidOperationException("PNG is missing IHDR or IDAT data.");
        var channels = colorType == 6 ? 4 : 3;
        var stride = checked(width * channels);
        var expected = checked((stride + 1) * height);
        var filtered = new byte[expected];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            var read = 0;
            while (read < expected)
            {
                var count = zlib.Read(filtered, read, expected - read);
                if (count == 0) break;
                read += count;
            }
            if (read != expected || zlib.ReadByte() != -1)
                throw new InvalidOperationException("PNG decompressed payload length is invalid.");
        }

        var raw = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var source = y * (stride + 1);
            var filterType = filtered[source];
            var sourceRow = filtered.AsSpan(source + 1, stride);
            var destinationRow = raw.AsSpan(y * stride, stride);
            var previousRow = y == 0 ? ReadOnlySpan<byte>.Empty : raw.AsSpan((y - 1) * stride, stride);
            Unfilter(filterType, sourceRow, destinationRow, previousRow, channels);
        }

        var rgba = new byte[checked(width * height * 4)];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var source = pixel * channels;
            var destination = pixel * 4;
            rgba[destination] = raw[source];
            rgba[destination + 1] = raw[source + 1];
            rgba[destination + 2] = raw[source + 2];
            rgba[destination + 3] = channels == 4 ? raw[source + 3] : (byte)255;
        }
        return new VisualPixelBuffer(width, height, rgba);
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> previous, int bytesPerPixel)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var left = i >= bytesPerPixel ? destination[i - bytesPerPixel] : (byte)0;
            var up = previous.IsEmpty ? (byte)0 : previous[i];
            var upLeft = previous.IsEmpty || i < bytesPerPixel ? (byte)0 : previous[i - bytesPerPixel];
            destination[i] = filter switch
            {
                0 => source[i],
                1 => unchecked((byte)(source[i] + left)),
                2 => unchecked((byte)(source[i] + up)),
                3 => unchecked((byte)(source[i] + ((left + up) / 2))),
                4 => unchecked((byte)(source[i] + Paeth(left, up, upLeft))),
                _ => throw new InvalidOperationException($"Unsupported PNG filter type {filter}.")
            };
        }
    }

    private static byte Paeth(byte left, byte up, byte upLeft)
    {
        var p = left + up - upLeft;
        var pa = Math.Abs(p - left);
        var pb = Math.Abs(p - up);
        var pc = Math.Abs(p - upLeft);
        return pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
        => checked((int)(((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3]));

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _captures)
            if (pair.Value.Metadata.ExpiresAt <= now) _captures.TryRemove(pair.Key, out _);
    }

    private void EnforceBound()
    {
        var ordered = _captures.Values.OrderBy(value => value.Metadata.CreatedAt).ToArray();
        var retainedBytes = ordered.Sum(value => (long)value.PngBytes.Length + value.Pixels.Rgba.Length);
        var removeCount = Math.Max(0, ordered.Length - MaxCaptures);
        var index = 0;
        while (index < ordered.Length && (index < removeCount || retainedBytes > MaxRetainedBytes))
        {
            var capture = ordered[index++];
            if (_captures.TryRemove(capture.Metadata.Id, out _))
                retainedBytes -= (long)capture.PngBytes.Length + capture.Pixels.Rgba.Length;
        }
    }
}
