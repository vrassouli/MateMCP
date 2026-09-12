using System.Net;
using System.Net.Sockets;
using System.Text;
using MateMCP.Agent.Browser;

namespace MateMCP.Agent.Tests;

public sealed class BrowserRuntimeIntegrationTests
{
    [Fact]
    public async Task Dedicated_browser_executes_real_semantic_flow()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATEMCP_BROWSER_INTEGRATION"), "1", StringComparison.Ordinal))
            return;

        var channel = Environment.GetEnvironmentVariable("MATEMCP_BROWSER_CHANNEL") ??
            (OperatingSystem.IsWindows() ? "msedge" : "chrome");

        await using var server = new LoopbackPageServer();
        var lastStep = "starting";
        var flow = RunFlowAsync();
        var completed = await Task.WhenAny(flow, Task.Delay(TimeSpan.FromSeconds(60)));
        if (completed != flow)
            throw new TimeoutException($"Browser runtime integration timed out after step '{lastStep}'.");
        await flow;

        async Task RunFlowAsync()
        {
            var browser = new BrowserAutomationService();

            Mark("open");
            var opened = await browser.OpenAsync(server.Url, channel);
            Assert.True(opened.Active);
            Assert.Equal(channel, opened.Channel);

            Mark("initial snapshot");
            var initial = await browser.SnapshotAsync();
            Assert.Equal(server.Url, initial.Url);
            Assert.Contains(initial.Elements, element => element.Role == "textbox" && element.Name == "Name");
            Assert.Contains(initial.Elements, element => element.Role == "button" && element.Name == "Increment");
            Assert.Contains(initial.Elements, element => element.Role == "heading" && element.Name == "Count 0");

            Mark("fill");
            var filled = await browser.FillAsync(new BrowserSelector(Role: "textbox", Name: "Name"), "MateMCP");
            Assert.True(filled.Ok);

            Mark("snapshot after fill");
            var afterFill = await browser.SnapshotAsync();
            Assert.Contains(afterFill.Elements, element =>
                element.Role == "textbox" && element.Name == "Name" && element.Value == "MateMCP");

            Mark("click");
            var clicked = await browser.ClickAsync(new BrowserSelector(Role: "button", Name: "Increment"));
            Assert.True(clicked.Ok);

            Mark("snapshot after click");
            var afterClick = await browser.SnapshotAsync();
            Assert.Contains(afterClick.Elements, element => element.Role == "heading" && element.Name == "Count 1");

            Mark("viewport");
            var resized = await browser.SetViewportAsync(840, 620, 1);
            Assert.Equal(840, resized.Viewport?.Width);
            Assert.Equal(620, resized.Viewport?.Height);

            Mark("screenshot");
            var screenshot = await browser.ScreenshotAsync();
            Assert.Equal("image/png", screenshot.MimeType);
            Assert.True(screenshot.Bytes.Length > 8);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, screenshot.Bytes[..4]);

            Mark("close");
            await browser.CloseAsync();

            Mark("status after close");
            var closed = await browser.GetStatusAsync();
            Assert.False(closed.Active);
            Mark("complete");
        }

        void Mark(string step)
        {
            lastStep = step;
            Console.WriteLine($"[MateMCP browser integration] {OperatingSystem.IsMacOS() switch { true => "macOS", false => OperatingSystem.IsWindows() ? "Windows" : "other" }} / {channel}: {step}");
        }
    }

    private sealed class LoopbackPageServer : IAsyncDisposable
    {
        private static readonly byte[] Body = Encoding.UTF8.GetBytes("""
<!doctype html>
<html>
<head><meta charset="utf-8"><title>MateMCP browser integration</title></head>
<body>
  <label for="name">Name</label>
  <input id="name" type="text">
  <button aria-label="Increment" onclick="const h=document.getElementById('count');const n=Number(h.dataset.count)+1;h.dataset.count=String(n);h.textContent='Count '+n;">Increment</button>
  <h1 id="count" data-count="0">Count 0</h1>
</body>
</html>
""");

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveTask;

        public LoopbackPageServer()
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Url = $"http://127.0.0.1:{endpoint.Port}/";
            _serveTask = ServeAsync(_shutdown.Token);
        }

        public string Url { get; }

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

                _ = HandleAsync(client, cancellationToken);
            }
        }

        private static async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = new byte[8192];
                _ = await stream.ReadAsync(request, cancellationToken);

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(Body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
    }
}
