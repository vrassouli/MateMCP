using System.Security.Cryptography;
using System.Text;
using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Projects;

public sealed record ProjectDefinition(string Id, string Name, string Root, bool Read, bool Write, bool Shell, bool Available);

public sealed class ProjectRegistry(IOptionsMonitor<MateOptions> options)
{
    public IEnumerable<ProjectDefinition> All => Build(options.CurrentValue.Projects).Values;

    public ProjectDefinition Get(string nameOrId)
    {
        var projects = Build(options.CurrentValue.Projects);
        if (projects.TryGetValue(nameOrId, out var project)) return project;
        project = projects.Values.FirstOrDefault(p => string.Equals(p.Id, nameOrId, StringComparison.OrdinalIgnoreCase));
        return project ?? throw new InvalidOperationException($"Unknown project '{nameOrId}'.");
    }

    public ProjectDefinition? ResolveWorkspace(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        return All.OrderByDescending(p => p.Root.Length).FirstOrDefault(p => IsWithin(p.Root, fullPath));
    }

    public string ResolvePath(string projectName, string relativePath, bool requireWrite = false)
    {
        var project = Get(projectName);
        if (requireWrite && !project.Write) throw new UnauthorizedAccessException($"Project '{project.Name}' is read-only.");
        if (!requireWrite && !project.Read) throw new UnauthorizedAccessException($"Project '{project.Name}' does not allow reads.");

        var root = Path.GetFullPath(project.Root);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, PathComparison) && !candidate.StartsWith(prefix, PathComparison))
            throw new UnauthorizedAccessException("Path escapes the configured project root.");

        EnsureNoSymlinkEscape(root, candidate);
        return candidate;
    }

    internal static string GetStableId(ProjectOptions project)
        => string.IsNullOrWhiteSpace(project.Id) ? LegacyId(project.Name, project.Root) : project.Id.Trim();

    internal static string LegacyId(string name, string root)
    {
        var canonical = name.Trim().ToLowerInvariant() + "\n" + Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
    }

    private static Dictionary<string, ProjectDefinition> Build(IEnumerable<ProjectOptions> projects)
        => projects.Select(p =>
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Root));
            return new ProjectDefinition(GetStableId(p), p.Name, root, p.Read, p.Write, p.Shell, Directory.Exists(root));
        }).ToDictionary(p => p.Name, PathComparer);

    private static void EnsureNoSymlinkEscape(string root, string candidate)
    {
        var rootInfo = new DirectoryInfo(root);
        var rootReal = rootInfo.Exists ? rootInfo.ResolveLinkTarget(true)?.FullName ?? rootInfo.FullName : rootInfo.FullName;
        var current = new FileInfo(candidate);
        FileSystemInfo? cursor = current.Exists ? current : current.Directory;
        while (cursor is not null && cursor.FullName.Length >= root.Length)
        {
            if (cursor.LinkTarget is not null)
            {
                var target = cursor.ResolveLinkTarget(true)?.FullName;
                if (target is not null && !IsWithin(rootReal, target))
                    throw new UnauthorizedAccessException("Path traverses a symbolic link outside the project root.");
            }
            cursor = cursor switch { DirectoryInfo d => d.Parent, FileInfo f => f.Directory, _ => null };
        }
    }

    private static bool IsWithin(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = Path.GetFullPath(path);
        var prefix = root + Path.DirectorySeparatorChar;
        return path.Equals(root, PathComparison) || path.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
