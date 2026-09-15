using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MateMCP.Agent.Browser;

namespace MateMCP.Agent.Tests;

[Collection("Browser runtime serial")]
public sealed class BrowserEndToEndWorkflowTests
{
    [Fact]
    public async Task Frontend_visual_development_workflow_runs_end_to_end()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATEMCP_BROWSER_INTEGRATION"), "1", StringComparison.Ordinal)) return;
        var channel = Environment.GetEnvironmentVariable("MATEMCP_BROWSER_CHANNEL") ?? (OperatingSystem.IsWindows() ? "msedge" : "chrome");
        await using var server = new LoopbackVisualAppServer();
        await using var browser = new BrowserAutomationService();
        var visual = new BrowserVisualQaService(browser);
        var started = Stopwatch.StartNew();

        var opened = await browser.OpenAsync(server.Url, channel);
        Assert.True(opened.Active);
        await NavigateToDevicesAsync(browser);
        await browser.FillAsync(new BrowserSelector(Role: "textbox", Name: "Search devices"), "router");
        await browser.FillAsync(new BrowserSelector(Role: "combobox", Name: "Device type"), "switch");

        var populated = await browser.SnapshotAsync(300);
        Assert.Contains(populated.Elements, e => e.Role == "textbox" && e.Name == "Search devices" && e.Value == "router");
        Assert.Contains(populated.Elements, e => e.Role == "combobox" && e.Name == "Device type" && e.Value == "switch");
        var secure = Assert.Single(populated.Elements, e => e.Role == "password" && e.Name == "Admin password");
        Assert.True(secure.Protected);
        Assert.Null(secure.Value);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            browser.FillAsync(new BrowserSelector(Role: "password", Name: "Admin password"), "must-still-be-refused"));

        var passwordTarget = await browser.BindSecretTargetAsync(new BrowserSelector(Role: "password", Name: "Admin password"));
        Assert.True(passwordTarget.Protected);
        var passwordResult = await browser.FillBoundSecretAsync(passwordTarget, "browser-e2e-password-secret");
        Assert.True(passwordResult.Protected);
        var afterPasswordSecret = await browser.SnapshotAsync(300);
        var passwordAfterSecret = Assert.Single(afterPasswordSecret.Elements, e => e.Role == "password" && e.Name == "Admin password");
        Assert.True(passwordAfterSecret.Protected);
        Assert.Null(passwordAfterSecret.Value);

        var visibleTarget = await browser.BindSecretTargetAsync(new BrowserSelector(Role: "textbox", Name: "Search devices"));
        Assert.False(visibleTarget.Protected);
        var visibleResult = await browser.FillBoundSecretAsync(visibleTarget, "browser-e2e-visible-secret");
        Assert.False(visibleResult.Protected);
        var whileGuarded = await browser.SnapshotAsync(300);
        var guardedSearch = Assert.Single(whileGuarded.Elements, e => e.Role == "textbox" && e.Name == "Search devices");
        Assert.True(guardedSearch.Protected);
        Assert.Null(guardedSearch.Value);

        await browser.FillAsync(new BrowserSelector(Role: "textbox", Name: "Search devices"), "router");
        var afterGuardClear = await browser.SnapshotAsync(300);
        var restoredSearch = Assert.Single(afterGuardClear.Elements, e => e.Role == "textbox" && e.Name == "Search devices");
        Assert.False(restoredSearch.Protected);
        Assert.Equal("router", restoredSearch.Value);

        var beforeDesktop = await visual.CaptureAsync(new VisualCaptureOptions(Preset: "desktop", SettleMs: 50, MaxElements: 300));
        var beforeMobile = await visual.CaptureAsync(new VisualCaptureOptions(Preset: "mobile", SettleMs: 50, MaxElements: 300));
        AssertCaptureBounded(beforeDesktop);
        AssertCaptureBounded(beforeMobile);

        server.SetVariant(2);
        await browser.ReloadAsync();
        await NavigateToDevicesAsync(browser);
        await browser.FillAsync(new BrowserSelector(Role: "textbox", Name: "Search devices"), "router");
        await browser.FillAsync(new BrowserSelector(Role: "combobox", Name: "Device type"), "switch");

        var afterDesktop = await visual.CaptureAsync(new VisualCaptureOptions(Preset: "desktop", SettleMs: 50, MaxElements: 300));
        var afterMobile = await visual.CaptureAsync(new VisualCaptureOptions(Preset: "mobile", SettleMs: 50, MaxElements: 300));
        var desktopDiff = visual.Compare(beforeDesktop.Metadata.Id, afterDesktop.Metadata.Id, 8);
        var mobileDiff = visual.Compare(beforeMobile.Metadata.Id, afterMobile.Metadata.Id, 8);
        Assert.True(desktopDiff.Comparable && desktopDiff.ChangedPixels > 0);
        Assert.True(mobileDiff.Comparable && mobileDiff.ChangedPixels > 0);
        Assert.NotEmpty(desktopDiff.Regions);
        Assert.NotEmpty(mobileDiff.Regions);

        var finalSnapshot = afterMobile.Metadata.Snapshot;
        Assert.Equal(390, finalSnapshot.Viewport.Width);
        Assert.Contains(finalSnapshot.Elements, e => e.Role == "heading" && e.Name == "Devices");
        Assert.Contains(finalSnapshot.Elements, e => e.TestId == "device-card" && e.Bounds is { Width: > 0 } && e.Styles?.FontSize is not null);
        Console.WriteLine($"[MateMCP frontend E2E] {channel}: elapsed={started.ElapsedMilliseconds}ms desktop={beforeDesktop.Metadata.Bytes}/{afterDesktop.Metadata.Bytes} mobile={beforeMobile.Metadata.Bytes}/{afterMobile.Metadata.Bytes} elements={finalSnapshot.Elements.Count} desktopChanged={desktopDiff.ChangedPercent:0.###}% mobileChanged={mobileDiff.ChangedPercent:0.###}%");
    }

    private static async Task NavigateToDevicesAsync(BrowserAutomationService browser)
    {
        await browser.ClickAsync(new BrowserSelector(Role: "button", Name: "Management"));
        await WaitUntilAsync(async () => (await browser.SnapshotAsync(200)).Elements.Any(e => e.Role == "heading" && e.Name == "Management"));
        await browser.ClickAsync(new BrowserSelector(Role: "button", Name: "Devices"));
        await WaitUntilAsync(async () => (await browser.SnapshotAsync(200)).Elements.Any(e => e.Role == "heading" && e.Name == "Devices"));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Frontend E2E page did not reach the expected semantic state within 5 seconds.");
    }

    private static void AssertCaptureBounded(VisualCaptureResult capture)
    {
        Assert.InRange(capture.Metadata.Bytes, 1, 16 * 1024 * 1024);
        Assert.InRange(capture.Metadata.ElementCount, 1, 300);
        Assert.False(capture.Metadata.SnapshotTruncated);
        Assert.InRange(capture.Metadata.ImageWidth, 1, 5000);
        Assert.InRange(capture.Metadata.ImageHeight, 1, 5000);
    }

    private sealed class LoopbackVisualAppServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveTask;
        private int _variant = 1;

        public LoopbackVisualAppServer()
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Url = $"http://127.0.0.1:{endpoint.Port}/";
            _serveTask = ServeAsync(_shutdown.Token);
        }

        public string Url { get; }
        public void SetVariant(int variant) => Volatile.Write(ref _variant, variant);

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try { await _serveTask; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            _shutdown.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = HandleAsync(client, Volatile.Read(ref _variant), cancellationToken);
            }
        }

        private static async Task HandleAsync(TcpClient client, int variant, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = new byte[8192];
                _ = await stream.ReadAsync(request, cancellationToken);
                var body = Encoding.UTF8.GetBytes(Page(variant));
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        private static string Page(int variant)
        {
            var cardWidth = variant == 1 ? 260 : 320;
            var cardPadding = variant == 1 ? 12 : 20;
            var badge = variant == 1 ? "Current" : "Updated";
            var html = """
<!doctype html>
<html><head><meta charset="utf-8"><title>MateMCP E2E Visual App</title>
<style>
body{font-family:Arial,sans-serif;margin:24px}.view[hidden]{display:none}.toolbar{display:flex;gap:12px;align-items:center;margin:16px 0}.device-card{width:__CARD_WIDTH__px;padding:__CARD_PADDING__px;border:1px solid #999;border-radius:8px}.badge{font-size:12px}.nav{display:flex;gap:8px}label{display:block;margin-top:8px}
@media(max-width:500px){body{margin:12px}.device-card{width:auto}.toolbar{display:block}.toolbar>*{margin-bottom:8px}}
</style></head><body>
<section id="home" class="view"><h1>Home</h1><button aria-label="Management" onclick="show('management')">Management</button></section>
<section id="management" class="view" hidden><h1>Management</h1><div class="nav"><button aria-label="Devices" onclick="show('devices')">Devices</button></div></section>
<section id="devices" class="view" hidden><h1>Devices</h1>
<div class="toolbar"><div><label for="search">Search devices</label><input id="search" type="text"></div><div><label for="type">Device type</label><select id="type"><option value="router">Router</option><option value="switch">Switch</option></select></div></div>
<label for="password">Admin password</label><input id="password" type="password" value="fixture-value">
<div class="device-card" data-testid="device-card"><strong>Core Switch</strong><div class="badge">__BADGE__</div></div>
</section>
<script>function show(id){document.querySelectorAll('.view').forEach(x=>x.hidden=true);document.getElementById(id).hidden=false;history.replaceState({},'', '#'+id)}</script>
</body></html>
""";
            return html
                .Replace("__CARD_WIDTH__", cardWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__CARD_PADDING__", cardPadding.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__BADGE__", badge, StringComparison.Ordinal);
        }
    }
}
