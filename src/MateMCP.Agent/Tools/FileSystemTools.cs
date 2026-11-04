using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Context;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class FileSystemTools(ProjectRegistry projects, SkillMemoryStore memory, AuditLog audit, IOptions<MateOptions> options)
{
    private const int MaxReadChars = 1_000_000;
    private const int MaxListedEntries = 1_000;

    [McpServerTool(Name = "filesystem_projects"), Description("Lists configured projects available to MateMCP.")]
    public object ListProjects() => projects.All.Select(p => new { p.Name, p.Read, p.Write, p.Shell });

    [McpServerTool(Name = "filesystem_list"), Description("Lists entries in a directory relative to a configured project root. When includeMetadata is true, each entry includes bounded file metadata.")]
    public async Task<object> List(
        string project,
        string path = ".",
        bool includeMetadata = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = projects.ResolvePath(project, path);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory '{path}' was not found in project '{project}'.");

        var projectDefinition = projects.Get(project);
        var entries = new List<object>(capacity: 64);
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(resolved).Take(MaxListedEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(projectDefinition.Root, entryPath);
            var safePath = projects.ResolvePath(project, relativePath);
            FileSystemInfo info = Directory.Exists(safePath)
                ? new DirectoryInfo(safePath)
                : new FileInfo(safePath);
            info.Refresh();

            var isDirectory = info is DirectoryInfo;
            var fileInfo = info as FileInfo;
            entries.Add(new
            {
                name = info.Name,
                directory = isDirectory,
                sizeBytes = includeMetadata && !isDirectory && fileInfo!.Exists ? fileInfo.Length : (long?)null,
                lastModifiedUtc = includeMetadata && info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc) : (DateTimeOffset?)null,
                mediaType = includeMetadata && !isDirectory ? GetMediaType(info.Extension) : null
            });
        }

        await audit.WriteAsync("filesystem.list", $"{project}:{path}", $"ok:{entries.Count}:metadata={includeMetadata}", cancellationToken, project);
        return entries;
    }

    [McpServerTool(Name = "filesystem_stat"), Description("Returns metadata for a file or directory relative to a configured project root. SHA-256 is computed only when explicitly requested.")]
    public async Task<object> Stat(
        string project,
        string path,
        bool includeSha256 = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = projects.ResolvePath(project, path);
        FileSystemInfo info;
        if (Directory.Exists(resolved))
            info = new DirectoryInfo(resolved);
        else if (File.Exists(resolved))
            info = new FileInfo(resolved);
        else
            throw new FileNotFoundException($"Path '{path}' was not found in project '{project}'.", path);

        info.Refresh();
        var file = info as FileInfo;
        string? sha256 = null;
        if (includeSha256)
        {
            if (file is null)
                throw new ArgumentException("SHA-256 can only be requested for files.", nameof(includeSha256));

            await using var stream = new FileStream(
                resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        await audit.WriteAsync(
            "filesystem.stat",
            $"{project}:{path}",
            file is null ? "ok:directory" : $"ok:file:{file.Length}:sha256={includeSha256}",
            cancellationToken,
            project);

        return new
        {
            name = info.Name,
            kind = file is null ? "directory" : "file",
            isDirectory = file is null,
            sizeBytes = file?.Length,
            lastModifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc),
            extension = file is null ? null : info.Extension,
            mediaType = file is null ? null : GetMediaType(info.Extension),
            sha256
        };
    }

    [McpServerTool(Name = "filesystem_read"), Description("Reads a bounded UTF-8 text range relative to a configured project root. offsetBytes is a UTF-8 byte offset; negative values are relative to end-of-file. The offset must be on a UTF-8 character boundary.")]
    public async Task<string> Read(
        string project,
        string path,
        int maxChars = 200_000,
        long offsetBytes = 0,
        CancellationToken cancellationToken = default)
    {
        maxChars = Math.Clamp(maxChars, 1, MaxReadChars);
        var resolved = projects.ResolvePath(project, path);

        await using var stream = new FileStream(
            resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4_096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var offset = ResolveOffset(stream.Length, offsetBytes);
        stream.Position = offset;

        try
        {
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: offset == 0,
                bufferSize: 4_096,
                leaveOpen: true);

            var buffer = new char[maxChars];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);

            // Do not return half of a UTF-16 surrogate pair when the requested character limit
            // happens to land between the two code units representing one Unicode scalar value.
            if (count > 0 && char.IsHighSurrogate(buffer[count - 1]))
                count--;

            await audit.WriteAsync("filesystem.read", $"{project}:{path}", $"ok:chars={count}:offsetBytes={offset}", cancellationToken, project);
            return new string(buffer, 0, count);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException(
                "The requested byte offset is not on a valid UTF-8 character boundary, or the selected file range contains invalid UTF-8.",
                nameof(offsetBytes));
        }
    }

    [McpServerTool(Name = "filesystem_write"), Description("Writes a UTF-8 text file relative to a configured project root. Before the first project mutation, MateMCP may return status=context_required with repository instructions, relevant repository Skills and global Skills & Memory, and a contextLease; read that context and retry the same call with the supplied contextLease. Creates parent directories when needed only after context preflight succeeds.")]
    public async Task<object> Write(
        string project,
        string path,
        string content,
        [Description("Context lease previously returned by MateMCP for this project. When status=context_required is returned, read the supplied context and retry with that lease.")] string? contextLease = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = projects.ResolvePath(project, path, requireWrite: true);
        var bootstrap = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, options, "filesystem_write", project, path, path, contextLease, cancellationToken);
        if (bootstrap.Required)
        {
            return new
            {
                status = "context_required",
                project,
                path,
                contextLease = bootstrap.Lease,
                contextHash = bootstrap.ContextHash,
                contextSources = bootstrap.Sources,
                contextId = bootstrap.ContextId,
                context = bootstrap.Context,
                written = false
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        await File.WriteAllTextAsync(resolved, content, cancellationToken);
        await audit.WriteAsync("filesystem.write", $"{project}:{path}", "ok", cancellationToken, project);
        return new { status = "written", project, path, contextLease, written = true };
    }

    private static long ResolveOffset(long fileLength, long offsetBytes)
    {
        if (offsetBytes < 0)
        {
            if (offsetBytes < -fileLength)
                throw new ArgumentOutOfRangeException(nameof(offsetBytes), "Negative offset is outside the file.");
            return fileLength + offsetBytes;
        }

        if (offsetBytes > fileLength)
            throw new ArgumentOutOfRangeException(nameof(offsetBytes), "Offset is beyond the end of the file.");

        return offsetBytes;
    }

    private static string GetMediaType(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".heic" => "image/heic",
            ".jpeg" or ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".tif" or ".tiff" => "image/tiff",
            ".webp" => "image/webp",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".txt" or ".log" => "text/plain",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
}
