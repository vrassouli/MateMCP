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
        var project = Normalize(update);
        lock (_gate)
        {
            var root = ReadRoot();
            var projects = GetProjects(root);
            if (projects.Any(p => string.Equals(p?["Name"]?.GetValue<string>(), project.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Project '{project.Name}' already exists.");
            projects.Add(ToNode(project));
            Save(root);
        }
        return project;
    }

    public ProjectDefinition Update(string existingName, ProjectUpdate update)
    {
        var project = Normalize(update);
        lock (_gate)
        {
            var root = ReadRoot();
            var projects = GetProjects(root);
            var node = projects.FirstOrDefault(p => string.Equals(p?["Name"]?.GetValue<string>(), existingName, StringComparison.OrdinalIgnoreCase));
            if (node is null) throw new KeyNotFoundException($"Project '{existingName}' was not found.");
            if (!string.Equals(existingName, project.Name, StringComparison.OrdinalIgnoreCase) &&
                projects.Any(p => string.Equals(p?["Name"]?.GetValue<string>(), project.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Project '{project.Name}' already exists.");
            var index = projects.IndexOf(node);
            projects[index] = ToNode(project);
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
            var node = projects.FirstOrDefault(p => string.Equals(p?["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (node is null) return false;
            projects.Remove(node);
            Save(root);
            return true;
        }
    }

    private static ProjectDefinition Normalize(ProjectUpdate update)
    {
        var name = update.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.");
        if (name.Length > 80) throw new ArgumentException("Project name is too long.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Project name contains invalid characters.");

        var expanded = Environment.ExpandEnvironmentVariables(update.Root?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(expanded)) throw new ArgumentException("Project root is required.");
        var fullPath = Path.GetFullPath(expanded);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Project root '{fullPath}' does not exist.");

        return new ProjectDefinition(name, fullPath, update.Read, update.Write, update.Shell);
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
