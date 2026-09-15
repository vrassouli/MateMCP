using System.Diagnostics;
using System.Text.Json;

namespace MateMCP.Agent.Desktop;

internal sealed class WindowsSemanticUiHelper
{
    private const string HelperName = "MateMCP.WindowsDesktopHelper.exe";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<UiSnapshot> SnapshotAsync(string windowId, int maxElements, CancellationToken ct)
        => InvokeAsync<UiSnapshot>(new Request("snapshot", windowId, maxElements, null, null, null, null, null, 0, 0), response => response.Snapshot, ct);

    public Task<UiElementInfo> ActAsync(string windowId, UiSelector selector, string action, string? text, CancellationToken ct)
        => InvokeAsync<UiElementInfo>(new Request("act", windowId, 0, action, selector, text, null, null, 0, 0), response => response.Element, ct);

    public Task<UiElementInfo> ClickAtAsync(string windowId, double x, double y, CancellationToken ct)
        => InvokeAsync<UiElementInfo>(new Request("click-at", windowId, 0, null, null, null, null, null, x, y), response => response.Element, ct);

    public Task<UiElementInfo> FillSecretAsync(string windowId, UiElementInfo expected, string secret, CancellationToken ct)
        => InvokeAsync<UiElementInfo>(new Request("fill-secret", windowId, 0, null, null, secret, expected.Id, expected, 0, 0), response => response.Element, ct);

    private static async Task<T> InvokeAsync<T>(Request request, Func<Response, T?> selector, CancellationToken ct) where T : class
    {
        var helper = ResolveHelperPath();
        if (!File.Exists(helper))
            throw new InvalidOperationException($"Windows semantic UI helper is not installed at '{helper}'. Repair/update the MateMCP Desktop package.");

        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(helper) ?? AppContext.BaseDirectory
        };

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start the Windows semantic UI helper.");

        await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, Json, ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string stdout;
        string stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Windows semantic UI helper timed out.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows semantic UI helper exited with code {process.ExitCode}: {Compact(stderr)}");
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"Windows semantic UI helper returned no response. {Compact(stderr)}");

        Response response;
        try { response = JsonSerializer.Deserialize<Response>(stdout, Json) ?? throw new JsonException("Empty response."); }
        catch (JsonException ex) { throw new InvalidOperationException("Windows semantic UI helper returned malformed JSON.", ex); }

        if (!response.Ok) ThrowHelperError(response);
        return selector(response) ?? throw new InvalidOperationException("Windows semantic UI helper returned an incomplete response.");
    }

    private static void ThrowHelperError(Response response)
    {
        var message = string.IsNullOrWhiteSpace(response.Error) ? "Windows semantic UI operation failed." : response.Error;
        switch (response.ErrorType?.Trim().ToLowerInvariant())
        {
            case "ambiguous": throw new UiSelectorAmbiguousException(message);
            case "not-found": throw new UiSelectorNotFoundException(message);
            default: throw new InvalidOperationException(message);
        }
    }

    private static string ResolveHelperPath()
    {
        var configured = Environment.GetEnvironmentVariable("MATEMCP_WINDOWS_DESKTOP_HELPER");
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, HelperName) : configured;
    }

    private static string Compact(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace('\r', ' ').Replace('\n', ' ').Trim()[..Math.Min(value.Trim().Length, 500)];

    private sealed record Request(string Command, string WindowId, int MaxElements, string? Action, UiSelector? Selector, string? Text, string? ElementId, UiElementInfo? Expected, double X, double Y);
    private sealed record Response(bool Ok, UiSnapshot? Snapshot, UiElementInfo? Element, string? Error, string? ErrorType);
}
