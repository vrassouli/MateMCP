using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace MateMCP.Agent.Tests;

public sealed class ProjectImageToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-image-tools-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _auditPath;

    public ProjectImageToolsTests()
    {
        Directory.CreateDirectory(_root);
        _auditPath = Path.Combine(_root, "audit.jsonl");
    }

    [Fact]
    public async Task ReadImage_returns_native_png_content_and_detects_type_from_bytes()
    {
        var png = CreatePng();
        await File.WriteAllBytesAsync(Path.Combine(_root, "misleading.jpg"), png);
        var tools = CreateTools();

        var content = (await tools.ReadImage("Demo", "misleading.jpg")).ToArray();
        using var metadata = JsonDocument.Parse(Assert.IsType<TextContentBlock>(content[0]).Text);
        var image = Assert.IsType<ImageContentBlock>(content[1]);

        Assert.Equal("image/png", metadata.RootElement.GetProperty("mediaType").GetString());
        Assert.Equal(1, metadata.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(1, metadata.RootElement.GetProperty("height").GetInt32());
        Assert.Equal(png, image.DecodedData.ToArray());
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public async Task ReadImage_returns_jpeg_content_for_jpeg_frame_header()
    {
        var jpeg = CreateJpegWithSof(width: 2, height: 3);
        await File.WriteAllBytesAsync(Path.Combine(_root, "image.data"), jpeg);

        var content = (await CreateTools().ReadImage("Demo", "image.data")).ToArray();
        using var metadata = JsonDocument.Parse(Assert.IsType<TextContentBlock>(content[0]).Text);
        var image = Assert.IsType<ImageContentBlock>(content[1]);

        Assert.Equal("image/jpeg", metadata.RootElement.GetProperty("mediaType").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(3, metadata.RootElement.GetProperty("height").GetInt32());
        Assert.Equal("image/jpeg", image.MimeType);
    }

    [Fact]
    public async Task ReadImage_round_trips_through_the_MCP_protocol_serializer()
    {
        var png = CreatePng();
        await File.WriteAllBytesAsync(Path.Combine(_root, "sample.png"), png);

        var result = new CallToolResult { Content = (await CreateTools().ReadImage("Demo", "sample.png")).ToList() };
        var json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        var roundTrip = JsonSerializer.Deserialize<CallToolResult>(json, McpJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(2, roundTrip.Content.Count);
        var image = Assert.IsType<ImageContentBlock>(roundTrip.Content[1]);
        Assert.Equal(png, image.DecodedData.ToArray());
    }

    [Fact]
    public async Task ReadImage_rejects_unsupported_or_invalid_image_content()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "not-image.png"), "not an image");
        await File.WriteAllBytesAsync(Path.Combine(_root, "bad.jpg"), [0xFF, 0xD8, 0x00, 0x01]);

        var tools = CreateTools();
        await Assert.ThrowsAsync<McpException>(() => tools.ReadImage("Demo", "not-image.png"));
        await Assert.ThrowsAsync<McpException>(() => tools.ReadImage("Demo", "bad.jpg"));
    }

    [Fact]
    public async Task ReadImage_enforces_file_size_limit_before_reading_payload()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "large.png"), CreatePng());

        await Assert.ThrowsAsync<McpException>(() => CreateTools().ReadImage("Demo", "large.png", maxBytes: 16));
    }

    [Fact]
    public async Task ReadImage_rejects_excessive_dimensions()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "wide.png"), CreatePngHeader(width: 10_000, height: 1));

        await Assert.ThrowsAsync<McpException>(() => CreateTools().ReadImage("Demo", "wide.png"));
    }

    [Fact]
    public async Task ReadImage_respects_project_read_permission_and_project_root()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "sample.png"), CreatePng());

        var noRead = CreateTools(new MateOptions
        {
            Projects = [new ProjectOptions { Name = "Demo", Root = _root, Read = false, Write = false, Shell = false }]
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noRead.ReadImage("Demo", "sample.png"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => CreateTools().ReadImage("Demo", Path.Combine(_root, "..", "outside.png")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => CreateTools().ReadImage("Demo", "missing.png"));
    }

    [Fact]
    public async Task ReadImage_audits_metadata_without_recording_binary_contents()
    {
        var png = CreatePng();
        await File.WriteAllBytesAsync(Path.Combine(_root, "sample.png"), png);

        await CreateTools().ReadImage("Demo", "sample.png");

        var audit = await File.ReadAllTextAsync(_auditPath);
        Assert.Contains("filesystem.read-image", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(png), audit, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_file_tool_is_published_in_the_MCP_catalog()
        => Assert.Contains("filesystem_read_image", McpToolCatalog.Names);

    private ProjectImageTools CreateTools(MateOptions? options = null)
    {
        options ??= new MateOptions
        {
            Projects = [new ProjectOptions { Name = "Demo", Root = _root, Read = true, Write = true, Shell = true }]
        };
        return new ProjectImageTools(new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options)), new AuditLog(_auditPath));
    }

    private static byte[] CreatePng()
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WritePngChunk(output, "IHDR", CreatePngIhdr(width: 1, height: 1));

        using var raw = new MemoryStream();
        raw.Write([0, 0, 0, 0, 0]); // Filter byte + one transparent RGBA pixel.
        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            raw.CopyTo(zlib);

        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] CreatePngIhdr(uint width, uint height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        return ihdr;
    }

    private static byte[] CreatePngHeader(uint width, uint height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput.AsSpan(typeBytes.Length));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] CreateJpegWithSof(ushort width, ushort height)
    {
        byte[] bytes =
        [
            0xFF, 0xD8,             // SOI
            0xFF, 0xC0,             // SOF0
            0x00, 0x11,             // Segment length
            0x08,                   // Sample precision
            0x00, 0x00,             // Height (patched below)
            0x00, 0x00,             // Width (patched below)
            0x03,                   // Components
            0x01, 0x11, 0x00,
            0x02, 0x11, 0x00,
            0x03, 0x11, 0x00,
            0xFF, 0xD9              // EOI
        ];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(7, 2), height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9, 2), width);
        return bytes;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}


