using System.Text.Json;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Security;

public enum AgentAccessMode
{
    Ask,
    Trusted,
    FullAccess
}

public sealed record AgentAccessModeState(AgentAccessMode Mode, DateTimeOffset UpdatedAt);

public sealed class AgentAccessModeStore
{
    public const string FileName = "agent-access-mode.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public AgentAccessModeStore() : this(GetDefaultPath())
    {
    }

    public AgentAccessModeStore(string path)
    {
        _path = path;
    }

    public static string GetDefaultPath() => Path.Combine(
        ConfigurationBootstrap.GetUserDataDirectory(),
        FileName);

    public async Task<AgentAccessModeState> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                return new AgentAccessModeState(AgentAccessMode.Ask, DateTimeOffset.MinValue);

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AgentAccessModeState>(stream, Json, cancellationToken)
                ?? new AgentAccessModeState(AgentAccessMode.Ask, DateTimeOffset.MinValue);
        }
        catch (JsonException)
        {
            return new AgentAccessModeState(AgentAccessMode.Ask, DateTimeOffset.MinValue);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentAccessModeState> SetAsync(AgentAccessMode mode, CancellationToken cancellationToken = default)
    {
        var state = new AgentAccessModeState(mode, DateTimeOffset.UtcNow);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, state, Json, cancellationToken);

            File.Move(temporaryPath, _path, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }
}
