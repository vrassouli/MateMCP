using System.Buffers.Binary;
using System.ComponentModel;
using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Projects;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class ProjectImageTools(ProjectRegistry projects, AuditLog audit)
{
    private const int DefaultMaxBytes = 5 * 1024 * 1024;
    private const int MaxAllowedBytes = 5 * 1024 * 1024;
    private const uint MaxDimension = 8_192;
    private const ulong MaxPixels = 40_000_000;

    [McpServerTool(
        Name = "filesystem_read_image",
        Title = "Read project image for multimodal inspection",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Reads a PNG or JPEG image file within a configured project and returns it as native MCP image content. The actual media type is detected from file bytes, not the extension. Project read permission applies. Images are bounded to 5 MiB, 8192 pixels per dimension, and 40 megapixels.")]
    public async Task<IEnumerable<ContentBlock>> ReadImage(
        [Description("Configured MateMCP project containing the image.")] string project,
        [Description("Project-relative path to the image file.")] string path,
        [Description("Maximum accepted file size in bytes for this call. Values are clamped to 1..5242880.")] int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        maxBytes = Math.Clamp(maxBytes, 1, MaxAllowedBytes);
        var resolved = projects.ResolvePath(project, path);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Image file '{path}' was not found in project '{project}'.", path);

        await using var stream = new FileStream(
            resolved,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length <= 0)
            throw new McpException("The selected image file is empty.");
        if (stream.Length > maxBytes)
            throw new McpException($"Image is {stream.Length} bytes; the configured limit for this call is {maxBytes} bytes.");

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken);

        ImageHeader header;
        try
        {
            header = InspectHeader(bytes);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        if (header.Width == 0 || header.Height == 0
            || header.Width > MaxDimension || header.Height > MaxDimension
            || (ulong)header.Width * header.Height > MaxPixels)
        {
            throw new McpException(
                $"Image dimensions {header.Width}x{header.Height} exceed MateMCP's safety limits ({MaxDimension}px per dimension and {MaxPixels} pixels total).");
        }

        var projectName = projects.Get(project).Name;
        var metadata = JsonSerializer.Serialize(new
        {
            project = projectName,
            path,
            mediaType = header.MediaType,
            width = header.Width,
            height = header.Height,
            sizeBytes = bytes.Length
        });

        await audit.WriteAsync(
            "filesystem.read-image",
            $"{projectName}:{path}",
            $"ok:{header.MediaType}:{header.Width}x{header.Height}:{bytes.Length}",
            cancellationToken,
            projectName);

        return
        [
            new TextContentBlock { Text = metadata },
            ImageContentBlock.FromBytes(bytes, header.MediaType)
        ];
    }

    internal static ImageHeader InspectHeader(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(pngSignature))
        {
            var ihdrLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4));
            if (ihdrLength != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new ArgumentException("The PNG file has an invalid IHDR header.");

            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
            return new ImageHeader("image/png", width, height);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            return InspectJpegHeader(bytes);

        throw new ArgumentException("Unsupported image format. Only PNG and JPEG files are currently supported.");
    }

    private static ImageHeader InspectJpegHeader(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
                throw new ArgumentException("The JPEG file contains an invalid marker sequence.");

            while (offset < bytes.Length && bytes[offset] == 0xFF)
                offset++;

            if (offset >= bytes.Length)
                break;

            var marker = bytes[offset++];
            if (marker is 0xD9 or 0xDA)
                break;

            // TEM and restart markers have no length field.
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;

            if (offset + 2 > bytes.Length)
                throw new ArgumentException("The JPEG file ends inside a marker segment.");

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
                throw new ArgumentException("The JPEG file contains an invalid marker segment length.");

            if (IsStartOfFrameMarker(marker))
            {
                if (segmentLength < 8)
                    throw new ArgumentException("The JPEG frame header is incomplete.");

                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                return new ImageHeader("image/jpeg", width, height);
            }

            offset += segmentLength;
        }

        throw new ArgumentException("The JPEG file does not contain a supported frame header.");
    }

    private static bool IsStartOfFrameMarker(byte marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
            or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB
            or 0xCD or 0xCE or 0xCF;

    internal readonly record struct ImageHeader(string MediaType, uint Width, uint Height);
}
