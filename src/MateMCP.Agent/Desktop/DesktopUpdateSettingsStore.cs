using System.Text.Json;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Desktop;

public sealed record DesktopUpdateSettings(bool AutoUpdateEnabled = false);

public sealed class DesktopUpdateSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MateMCP",
        "desktop-update.json");

    public async Task<DesktopUpdateSettings> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new DesktopUpdateSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<DesktopUpdateSettings>(stream, Json, ct)
                   ?? new DesktopUpdateSettings();
        }
        catch (JsonException)
        {
            return new DesktopUpdateSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAutoUpdateEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, new DesktopUpdateSettings(enabled), Json, ct);
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
