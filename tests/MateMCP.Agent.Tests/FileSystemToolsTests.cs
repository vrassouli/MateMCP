using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class FileSystemToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-filesystem-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _auditPath;

    public FileSystemToolsTests()
    {
        Directory.CreateDirectory(_root);
        _auditPath = Path.Combine(_root, "audit.jsonl");
    }

    [Fact]
    public async Task Stat_returns_file_metadata_and_hash_only_when_requested()
    {
        var content = "MateMCP file metadata";
        await File.WriteAllTextAsync(Path.Combine(_root, "sample.txt"), content, new UTF8Encoding(false));
        var tools = CreateTools();

        var basic = JsonSerializer.SerializeToElement(await tools.Stat("Demo", "sample.txt"));
        Assert.Equal("file", basic.GetProperty("kind").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(content), basic.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("text/plain", basic.GetProperty("mediaType").GetString());
        Assert.Equal(JsonValueKind.Null, basic.GetProperty("sha256").ValueKind);

        var hashed = JsonSerializer.SerializeToElement(await tools.Stat("Demo", "sample.txt", includeSha256: true));
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        Assert.Equal(expectedHash, hashed.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Stat_returns_directory_metadata_without_file_fields()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        var result = JsonSerializer.SerializeToElement(await CreateTools().Stat("Demo", "nested"));

        Assert.Equal("directory", result.GetProperty("kind").GetString());
        Assert.True(result.GetProperty("isDirectory").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("sizeBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("mediaType").ValueKind);
    }

    [Fact]
    public async Task Stat_rejects_hash_for_directory_and_missing_path()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        var tools = CreateTools();

        await Assert.ThrowsAsync<ArgumentException>(() => tools.Stat("Demo", "nested", includeSha256: true));
        await Assert.ThrowsAsync<FileNotFoundException>(() => tools.Stat("Demo", "missing.txt"));
    }

    [Fact]
    public async Task List_includes_metadata_only_when_requested()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "hello");
        var tools = CreateTools();

        var result = JsonSerializer.SerializeToElement(await tools.List("Demo", includeMetadata: true));
        var file = Assert.Single(result.EnumerateArray(), x => x.GetProperty("name").GetString() == "note.txt");
        Assert.Equal(5, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("text/plain", file.GetProperty("mediaType").GetString());
        Assert.NotEqual(JsonValueKind.Null, file.GetProperty("lastModifiedUtc").ValueKind);
    }

    [Fact]
    public async Task Read_supports_positive_and_negative_byte_offsets_without_corrupting_utf8()
    {
        const string content = "alpha café 😀 omega";
        await File.WriteAllTextAsync(Path.Combine(_root, "utf8.txt"), content, new UTF8Encoding(false));
        var tools = CreateTools();
        var start = Encoding.UTF8.GetByteCount("alpha ");

        var middle = await tools.Read("Demo", "utf8.txt", maxChars: 10, offsetBytes: start);
        Assert.Equal("café 😀 om", middle);

        var tailBytes = Encoding.UTF8.GetByteCount("omega");
        var tail = await tools.Read("Demo", "utf8.txt", offsetBytes: -tailBytes);
        Assert.Equal("omega", tail);
    }

    [Fact]
    public async Task Read_rejects_offsets_outside_file_and_offsets_inside_utf8_sequence()
    {
        const string content = "alpha café omega";
        var path = Path.Combine(_root, "utf8.txt");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        var tools = CreateTools();
        var bytes = Encoding.UTF8.GetByteCount(content);
        var insideAccent = Encoding.UTF8.GetByteCount("alpha caf") + 1;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tools.Read("Demo", "utf8.txt", offsetBytes: bytes + 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tools.Read("Demo", "utf8.txt", offsetBytes: -bytes - 1));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.Read("Demo", "utf8.txt", offsetBytes: insideAccent));
    }

    [Fact]
    public async Task Read_is_bounded_and_allows_reading_from_end_of_large_file()
    {
        var content = new string('x', 250_000) + "END";
        await File.WriteAllTextAsync(Path.Combine(_root, "large.txt"), content, new UTF8Encoding(false));
        var result = await CreateTools().Read("Demo", "large.txt", maxChars: 3, offsetBytes: -3);
        Assert.Equal("END", result);
    }

    [Fact]
    public async Task Filesystem_access_respects_project_read_permission_and_root_scope()
    {
        var readOnlyOptions = new MateOptions
        {
            Projects = [new ProjectOptions { Name = "Demo", Root = _root, Read = false, Write = false, Shell = false }]
        };
        var readOnlyTools = CreateTools(readOnlyOptions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => readOnlyTools.Stat("Demo", "any.txt"));

        var normalTools = CreateTools();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => normalTools.Stat("Demo", Path.Combine(_root, "..", "outside.txt")));
    }

    [Fact]
    public async Task Audit_records_metadata_without_file_contents()
    {
        const string secretContent = "do-not-write-file-content-to-audit";
        await File.WriteAllTextAsync(Path.Combine(_root, "private.txt"), secretContent);
        var tools = CreateTools();

        await tools.Stat("Demo", "private.txt");
        await tools.Read("Demo", "private.txt");

        var auditText = await File.ReadAllTextAsync(_auditPath);
        Assert.DoesNotContain(secretContent, auditText, StringComparison.Ordinal);
        Assert.Contains("filesystem.stat", auditText, StringComparison.Ordinal);
        Assert.Contains("filesystem.read", auditText, StringComparison.Ordinal);
    }


    [Fact]
    public void File_system_capabilities_are_published_in_the_MCP_catalog()
    {
        Assert.Contains("filesystem_stat", McpToolCatalog.Names);
        Assert.Contains("filesystem_list", McpToolCatalog.Names);
        Assert.Contains("filesystem_read", McpToolCatalog.Names);
    }
    private FileSystemTools CreateTools(MateOptions? options = null)
    {
        options ??= new MateOptions
        {
            Projects = [new ProjectOptions { Name = "Demo", Root = _root, Read = true, Write = true, Shell = true }]
        };
        var registry = new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
        return new FileSystemTools(registry, null!, new AuditLog(_auditPath), Options.Create(options));
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
