using System.Text.Json;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Desktop;

public sealed record AgentPowerSettings(bool PreventSleepWhileInUse = false);

public sealed class AgentPowerSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MateMCP",
        "power-settings.json");

    public async Task<AgentPowerSettings> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new AgentPowerSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AgentPowerSettings>(stream, Json, ct)
                   ?? new AgentPowerSettings();
        }
        catch (JsonException)
        {
            return new AgentPowerSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPreventSleepWhileInUseAsync(bool enabled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, new AgentPowerSettings(enabled), Json, ct);
            await stream.WriteAsync("\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
            ConfigurationBootstrap.TryRestrictPermissions(_path);
        }
        finally
        {
            _gate.Release();
        }
    }
}
