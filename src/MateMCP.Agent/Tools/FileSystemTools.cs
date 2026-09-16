using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Context;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class FileSystemTools(ProjectRegistry projects, SkillMemoryStore memory, AuditLog audit, IOptions<MateOptions> options)
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

    [McpServerTool(Name = "filesystem_write"), Description("Writes a UTF-8 text file relative to a configured project root. Before the first project mutation, MateMCP may return status=context_required with repository instructions, relevant Skills & Memory, and a contextLease; read that context and retry the same call with the supplied contextLease. Creates parent directories when needed only after context preflight succeeds.")]
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
                context = bootstrap.Context,
                written = false
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        await File.WriteAllTextAsync(resolved, content, cancellationToken);
        await audit.WriteAsync("filesystem.write", $"{project}:{path}", "ok", cancellationToken);
        return new { status = "written", project, path, contextLease, written = true };
    }
}
