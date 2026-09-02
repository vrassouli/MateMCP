using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

public static class McpToolCatalog
{
    private static readonly ToolDefinition[] Definitions = typeof(McpToolCatalog).Assembly
        .GetTypes()
        .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        .Select(method => new { Method = method, Tool = method.GetCustomAttribute<McpServerToolAttribute>() })
        .Where(x => x.Tool?.Name is not null)
        .Select(x => new ToolDefinition(x.Tool!.Name!, Signature(x.Method)))
        .OrderBy(x => x.Name, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> Names { get; } = Definitions.Select(x => x.Name).ToArray();
    public static string Revision { get; } = ComputeRevision(Definitions);

    private static string Signature(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Select(parameter => $"{parameter.Name}:{TypeName(parameter.ParameterType)}:{(parameter.HasDefaultValue ? "optional" : "required")}");
        return string.Join('|', parameters);
    }

    private static string TypeName(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) return TypeName(nullable) + "?";
        return type.FullName ?? type.Name;
    }

    private static string ComputeRevision(IEnumerable<ToolDefinition> definitions)
    {
        var canonical = string.Join('\n', definitions.Select(x => $"{x.Name}({x.Signature})"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
    }

    private sealed record ToolDefinition(string Name, string Signature);
}
