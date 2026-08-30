using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Projects;

public sealed record ProjectDefinition(string Name, string Root, bool Read, bool Write, bool Shell);

public sealed class ProjectRegistry(IOptions<MateOptions> options)
{
    private readonly Dictionary<string, ProjectDefinition> _projects = Build(options.Value.Projects);

    public IEnumerable<ProjectDefinition> All => _projects.Values;

    public ProjectDefinition Get(string name)
        => _projects.TryGetValue(name, out var project)
            ? project
            : throw new InvalidOperationException($"Unknown project '{name}'.");

    public string ResolvePath(string projectName, string relativePath, bool requireWrite = false)
    {
        var project = Get(projectName);
        if (requireWrite && !project.Write) throw new UnauthorizedAccessException($"Project '{projectName}' is read-only.");
        if (!requireWrite && !project.Read) throw new UnauthorizedAccessException($"Project '{projectName}' does not allow reads.");

        var root = Path.GetFullPath(project.Root);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.Ordinal) && !candidate.StartsWith(prefix, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the configured project root.");

        EnsureNoSymlinkEscape(root, candidate);
        return candidate;
    }

    private static Dictionary<string, ProjectDefinition> Build(IEnumerable<ProjectOptions> projects)
        => projects.Select(p => new ProjectDefinition(p.Name, Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Root)), p.Read, p.Write, p.Shell))
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

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
        root = Path.GetFullPath(root);
        path = Path.GetFullPath(path);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.Ordinal) || path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
