using System.Text.Json;
using System.Text.Json.Nodes;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Projects;

public sealed record ProjectUpdate(string Name, string Root, bool Read = true, bool Write = true, bool Shell = true);

public sealed class ProjectConfigurationService
{
    private readonly string _configurationPath = ConfigurationBootstrap.EnsureUserConfiguration();
    private readonly object _gate = new();

    public ProjectDefinition Add(ProjectUpdate update)
    {
        var normalized = Normalize(update, requireExistingRoot: true);
        var project = normalized with { Id = Guid.NewGuid().ToString("N") };
        lock (_gate)
        {
            var root = ReadRoot();
            var projects = GetProjects(root);
            EnsureUnique(projects, project, exceptName: null);
            projects.Add(ToNode(project));
            Save(root);
        }
        return project;
    }

    public ProjectDefinition Update(string existingName, ProjectUpdate update)
    {
        var project = Normalize(update, requireExistingRoot: false);
        lock (_gate)
        {
            var root = ReadRoot();
            var projects = GetProjects(root);
            var node = projects.FirstOrDefault(p => string.Equals(p?["Name"]?.GetValue<string>(), existingName, StringComparison.OrdinalIgnoreCase));
            if (node is null) throw new KeyNotFoundException($"Project '{existingName}' was not found.");

            var existingId = node["Id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(existingId))
                existingId = ProjectRegistry.LegacyId(node["Name"]!.GetValue<string>(), node["Root"]!.GetValue<string>());
            project = project with { Id = existingId };
            EnsureUnique(projects, project, existingName);
            projects[projects.IndexOf(node)] = ToNode(project);
            Save(root);
        }
        return project;
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var root = ReadRoot();
            var projects = GetProjects(root);
            var node = projects.FirstOrDefault(p => string.Equals(p?["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p?["Id"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (node is null) return false;
            projects.Remove(node);
            Save(root);
            return true;
        }
    }

    private static ProjectDefinition Normalize(ProjectUpdate update, bool requireExistingRoot)
    {
        var name = update.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.");
        if (name.Length > 80) throw new ArgumentException("Project name is too long.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Project name contains invalid characters.");

        var expanded = Environment.ExpandEnvironmentVariables(update.Root?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(expanded)) throw new ArgumentException("Project root is required.");
        var fullPath = Path.GetFullPath(expanded);
        if (requireExistingRoot && !Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Project root '{fullPath}' does not exist.");

        return new ProjectDefinition(string.Empty, name, fullPath, update.Read, update.Write, update.Shell, Directory.Exists(fullPath));
    }

    private static void EnsureUnique(JsonArray projects, ProjectDefinition project, string? exceptName)
    {
        foreach (var candidate in projects)
        {
            var candidateName = candidate?["Name"]?.GetValue<string>() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(exceptName) && string.Equals(candidateName, exceptName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(candidateName, project.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Project '{project.Name}' already exists.");
            var candidateRoot = candidate?["Root"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(candidateRoot) && PathsEqual(candidateRoot, project.Root))
                throw new InvalidOperationException($"Workspace '{project.Root}' is already registered as project '{candidateName}'.");
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(left)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison);
    }

    private JsonObject ReadRoot()
        => JsonNode.Parse(File.ReadAllText(_configurationPath))?.AsObject()
           ?? throw new InvalidOperationException("MateMCP configuration is invalid.");

    private static JsonArray GetProjects(JsonObject root)
    {
        var mate = root["Mate"]?.AsObject() ?? throw new InvalidOperationException("Mate configuration section is missing.");
        if (mate["Projects"] is JsonArray projects) return projects;
        projects = [];
        mate["Projects"] = projects;
        return projects;
    }

    private static JsonObject ToNode(ProjectDefinition project) => new()
    {
        ["Id"] = project.Id,
        ["Name"] = project.Name,
        ["Root"] = project.Root,
        ["Read"] = project.Read,
        ["Write"] = project.Write,
        ["Shell"] = project.Shell
    };

    private void Save(JsonObject root)
    {
        var temp = _configurationPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        ConfigurationBootstrap.TryRestrictPermissions(temp);
        File.Move(temp, _configurationPath, true);
        ConfigurationBootstrap.TryRestrictPermissions(_configurationPath);
    }
}
