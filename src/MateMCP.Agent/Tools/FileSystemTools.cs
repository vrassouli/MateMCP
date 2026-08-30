using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Projects;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class FileSystemTools(ProjectRegistry projects, AuditLog audit)
{
    [McpServerTool(Name = "filesystem_projects"), Description("Lists configured projects available to MateMCP.")]
    public object ListProjects() => projects.All.Select(p => new { p.Name, p.Read, p.Write, p.Shell });

    [McpServerTool(Name = "filesystem_list"), Description("Lists entries in a directory relative to a configured project root.")]
    public async Task<object> List(string project, string path = ".")
    {
        var resolved = projects.ResolvePath(project, path);
        var entries = Directory.EnumerateFileSystemEntries(resolved).Take(1000).Select(x => new { name = Path.GetFileName(x), directory = Directory.Exists(x) }).ToArray();
        await audit.WriteAsync("filesystem.list", $"{project}:{path}", "ok");
        return entries;
    }

    [McpServerTool(Name = "filesystem_read"), Description("Reads a UTF-8 text file relative to a configured project root.")]
    public async Task<string> Read(string project, string path, int maxChars = 200_000)
    {
        maxChars = Math.Clamp(maxChars, 1, 1_000_000);
        var resolved = projects.ResolvePath(project, path);
        using var reader = new StreamReader(resolved);
        var buffer = new char[maxChars];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(0, maxChars));
        await audit.WriteAsync("filesystem.read", $"{project}:{path}", "ok");
        return new string(buffer, 0, count);
    }

    [McpServerTool(Name = "filesystem_write"), Description("Writes a UTF-8 text file relative to a configured project root. Creates parent directories when needed.")]
    public async Task<string> Write(string project, string path, string content)
    {
        var resolved = projects.ResolvePath(project, path, requireWrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        await File.WriteAllTextAsync(resolved, content);
        await audit.WriteAsync("filesystem.write", $"{project}:{path}", "ok");
        return "written";
    }
}
