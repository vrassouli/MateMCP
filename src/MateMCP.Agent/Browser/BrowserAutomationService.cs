using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

namespace MateMCP.Agent.Browser;

public sealed record BrowserViewport(int Width, int Height, double DeviceScaleFactor);
public sealed record BrowserBounds(double X, double Y, double Width, double Height);
public sealed record BrowserComputedStyle(
    string? Display,
    string? Visibility,
    string? Overflow,
    string? FontSize,
    string? FontFamily,
    string? FontWeight,
    string? Margin,
    string? Padding);
public sealed record BrowserElementInfo(
    string Id,
    string Tag,
    string Role,
    string? Name,
    string? Text,
    string? Label,
    string? TestId,
    string? Value,
    bool Protected,
    bool Enabled,
    bool Visible,
    BrowserBounds? Bounds,
    BrowserComputedStyle? Styles);
public sealed record BrowserSnapshot(
    string Url,
    string Title,
    BrowserViewport Viewport,
    IReadOnlyList<BrowserElementInfo> Elements,
    bool Truncated);
public sealed record BrowserSelector(
    string? Css = null,
    string? Role = null,
    string? Name = null,
    string? Text = null,
    string? Label = null,
    string? TestId = null,
    int? Index = null);
public sealed record BrowserActionResult(
    bool Ok,
    string? Error,
    int MatchCount,
    string? Role,
    string? Name,
    BrowserBounds? Bounds);
public sealed record BrowserSessionStatus(
    bool Active,
    string? Channel,
    string? Url,
    string? Title,
    BrowserViewport? Viewport);
public sealed record BrowserScreenshot(byte[] Bytes, string MimeType, string Url, string Title, BrowserViewport Viewport);

/// <summary>
/// Lightweight Chromium automation over the Chrome DevTools Protocol. MateMCP launches a dedicated temporary
/// Chrome/Edge profile, so browser automation does not silently inherit the user's normal cookies/session.
/// No Chromium binary or Playwright runtime is bundled with the Agent.
/// </summary>
public sealed class BrowserAutomationService : IAsyncDisposable
{
    public static BrowserAutomationService Shared { get; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private CdpClient? _cdp;
    private string? _profileDirectory;
    private string? _channel;
    private BrowserViewport? _viewport;

    public static string NormalizeChannel(string? channel)
        => (channel ?? "auto").Trim().ToLowerInvariant() switch
        {
            "" or "auto" => "auto",
            "chrome" or "google-chrome" => "chrome",
            "edge" or "msedge" or "microsoft-edge" => "msedge",
            _ => throw new ArgumentException("Browser channel must be auto, chrome, or msedge.", nameof(channel))
        };

    public static Uri ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is required.", nameof(url));
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Browser URL must be an absolute http:// or https:// URL.", nameof(url));
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Credentials must not be embedded in browser URLs.", nameof(url));
        return uri;
    }

    public async Task<BrowserSessionStatus> OpenAsync(string url, string channel = "auto", CancellationToken cancellationToken = default)
    {
        var uri = ValidateUrl(url);
        channel = NormalizeChannel(channel);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cdp is null)
                await LaunchAsync(channel, cancellationToken);
            else if (channel != "auto" && !string.Equals(channel, _channel, StringComparison.Ordinal))
                throw new InvalidOperationException($"MateMCP already owns an active {_channel} browser session. Close it before switching channels.");

            await NavigateCoreAsync(uri.AbsoluteUri, cancellationToken);
            return await GetStatusCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserSessionStatus> ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cdp = RequireCdp();
            await cdp.SendAsync("Page.reload", new { ignoreCache = false }, cancellationToken);
            await WaitForReadyAsync(cancellationToken);
            return await GetStatusCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserSnapshot> SnapshotAsync(int maxElements = 500, CancellationToken cancellationToken = default)
    {
        maxElements = Math.Clamp(maxElements, 1, 1500);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var json = await EvaluateStringAsync(BuildSnapshotScript(maxElements), cancellationToken);
            return JsonSerializer.Deserialize<BrowserSnapshot>(json, Json)
                ?? throw new InvalidOperationException("Browser returned an invalid DOM snapshot.");
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserActionResult> ClickAsync(BrowserSelector selector, CancellationToken cancellationToken = default)
    {
        ValidateSelector(selector);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await EvaluateActionAsync(selector, "click", null, cancellationToken);
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Browser click failed.");
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserActionResult> FillAsync(BrowserSelector selector, string text, CancellationToken cancellationToken = default)
    {
        ValidateSelector(selector);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 20_000)
            throw new ArgumentOutOfRangeException(nameof(text), "Browser text entry is limited to 20,000 characters per call.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await EvaluateActionAsync(selector, "fill", text, cancellationToken);
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Browser fill failed.");
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserSessionStatus> SetViewportAsync(
        int width,
        int height,
        double deviceScaleFactor = 1,
        CancellationToken cancellationToken = default)
    {
        width = Math.Clamp(width, 200, 5000);
        height = Math.Clamp(height, 200, 5000);
        deviceScaleFactor = Math.Clamp(deviceScaleFactor, 0.5, 4);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cdp = RequireCdp();
            await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new
            {
                width,
                height,
                deviceScaleFactor,
                mobile = false,
                screenWidth = width,
                screenHeight = height
            }, cancellationToken);
            _viewport = new BrowserViewport(width, height, deviceScaleFactor);
            return await GetStatusCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserScreenshot> ScreenshotAsync(bool fullPage = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cdp = RequireCdp();
            var result = await cdp.SendAsync("Page.captureScreenshot", new
            {
                format = "png",
                fromSurface = true,
                captureBeyondViewport = fullPage
            }, cancellationToken);
            if (!result.TryGetProperty("data", out var dataElement) || string.IsNullOrWhiteSpace(dataElement.GetString()))
                throw new InvalidOperationException("Browser did not return screenshot data.");
            var bytes = Convert.FromBase64String(dataElement.GetString()!);
            if (bytes.Length > 16 * 1024 * 1024)
                throw new InvalidOperationException("Browser screenshot exceeded the 16 MiB MateMCP limit.");
            var status = await GetStatusCoreAsync(cancellationToken);
            return new BrowserScreenshot(
                bytes,
                "image/png",
                status.Url ?? string.Empty,
                status.Title ?? string.Empty,
                status.Viewport ?? new BrowserViewport(0, 0, 1));
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cdp is null) return new BrowserSessionStatus(false, null, null, null, null);
            return await GetStatusCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await CloseCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task LaunchAsync(string requestedChannel, CancellationToken cancellationToken)
    {
        var (channel, executable) = ResolveBrowser(requestedChannel);
        var profile = Path.Combine(Path.GetTempPath(), "MateMCP", "browser", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(profile);

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };
        psi.ArgumentList.Add("--remote-debugging-port=0");
        psi.ArgumentList.Add($"--user-data-dir={profile}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("about:blank");

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {channel}.");
        try
        {
            var port = await WaitForDevToolsPortAsync(profile, process, cancellationToken);
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(5)
            };
            var targets = await http.GetFromJsonAsync<List<DevToolsTarget>>("json/list", Json, cancellationToken) ?? [];
            var page = targets.FirstOrDefault(target => string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase));
            if (page is null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, "json/new?about%3Ablank");
                using var response = await http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                page = await response.Content.ReadFromJsonAsync<DevToolsTarget>(Json, cancellationToken);
            }
            if (page is null || string.IsNullOrWhiteSpace(page.WebSocketDebuggerUrl))
                throw new InvalidOperationException("Browser started but no debuggable page target was available.");

            var cdp = await CdpClient.ConnectAsync(new Uri(page.WebSocketDebuggerUrl), cancellationToken);
            await cdp.SendAsync("Page.enable", new { }, cancellationToken);
            await cdp.SendAsync("Runtime.enable", new { }, cancellationToken);

            _process = process;
            _profileDirectory = profile;
            _channel = channel;
            _cdp = cdp;
            _viewport = null;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            TryDeleteDirectory(profile);
            throw;
        }
    }

    private async Task NavigateCoreAsync(string url, CancellationToken cancellationToken)
    {
        var result = await RequireCdp().SendAsync("Page.navigate", new { url }, cancellationToken);
        if (result.TryGetProperty("errorText", out var errorText) && !string.IsNullOrWhiteSpace(errorText.GetString()))
            throw new InvalidOperationException($"Browser navigation failed: {errorText.GetString()}");
        await WaitForReadyAsync(cancellationToken);
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var state = await EvaluateStringAsync("document.readyState", cancellationToken);
                if (state is "interactive" or "complete") return;
            }
            catch when (attempt < 99) { }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("Browser page did not reach an interactive ready state within 10 seconds.");
    }

    private async Task<BrowserSessionStatus> GetStatusCoreAsync(CancellationToken cancellationToken)
    {
        const string expression = "JSON.stringify({url:location.href,title:document.title,width:innerWidth,height:innerHeight,scale:devicePixelRatio})";
        var json = await EvaluateStringAsync(expression, cancellationToken);
        var state = JsonSerializer.Deserialize<PageState>(json, Json) ?? new PageState(null, null, 0, 0, 1);
        var viewport = _viewport ?? new BrowserViewport(state.Width, state.Height, state.Scale <= 0 ? 1 : state.Scale);
        return new BrowserSessionStatus(true, _channel, state.Url, state.Title, viewport);
    }

    private async Task<string> EvaluateStringAsync(string expression, CancellationToken cancellationToken)
    {
        var response = await RequireCdp().SendAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
            userGesture = true
        }, cancellationToken);
        if (response.TryGetProperty("exceptionDetails", out var exception))
            throw new InvalidOperationException($"Browser script failed: {Trim(exception.ToString(), 1000)}");
        if (!response.TryGetProperty("result", out var remote))
            throw new InvalidOperationException("Browser script returned no result.");
        if (remote.TryGetProperty("value", out var value))
        {
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            return value.ToString();
        }
        return string.Empty;
    }

    private async Task<BrowserActionResult> EvaluateActionAsync(
        BrowserSelector selector,
        string action,
        string? text,
        CancellationToken cancellationToken)
    {
        var expression = BuildActionScript(
            JsonSerializer.Serialize(selector, Json),
            JsonSerializer.Serialize(action, Json),
            JsonSerializer.Serialize(text, Json));
        var json = await EvaluateStringAsync(expression, cancellationToken);
        return JsonSerializer.Deserialize<BrowserActionResult>(json, Json)
            ?? throw new InvalidOperationException("Browser returned an invalid action result.");
    }

    private CdpClient RequireCdp()
        => _cdp ?? throw new InvalidOperationException("No MateMCP browser session is active. Call browser_open first.");

    private static void ValidateSelector(BrowserSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (string.IsNullOrWhiteSpace(selector.Css) &&
            string.IsNullOrWhiteSpace(selector.Role) &&
            string.IsNullOrWhiteSpace(selector.Name) &&
            string.IsNullOrWhiteSpace(selector.Text) &&
            string.IsNullOrWhiteSpace(selector.Label) &&
            string.IsNullOrWhiteSpace(selector.TestId))
            throw new ArgumentException(
                "A browser selector requires at least one of css, role, name, text, label, or testId.",
                nameof(selector));
        if (selector.Index is < 0)
            throw new ArgumentException("Browser selector index must be zero or greater.", nameof(selector));
    }

    private static (string Channel, string Executable) ResolveBrowser(string requestedChannel)
    {
        var candidates = new List<(string Channel, string Path)>();
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Add("chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
            Add("chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
            Add("chrome", Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
            Add("msedge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            Add("msedge", Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add("chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            Add("chrome", Path.Combine(home, "Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome"));
            Add("msedge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            Add("msedge", Path.Combine(home, "Applications", "Microsoft Edge.app", "Contents", "MacOS", "Microsoft Edge"));
        }
        else
        {
            throw new PlatformNotSupportedException("MateMCP browser automation currently supports Windows and macOS Agents.");
        }

        var match = requestedChannel == "auto"
            ? candidates.FirstOrDefault(candidate => File.Exists(candidate.Path))
            : candidates.FirstOrDefault(candidate => candidate.Channel == requestedChannel && File.Exists(candidate.Path));
        if (string.IsNullOrWhiteSpace(match.Path))
            throw new InvalidOperationException(requestedChannel == "auto"
                ? "No supported Chrome or Microsoft Edge installation was found."
                : $"The requested browser channel '{requestedChannel}' is not installed.");
        return match;

        void Add(string channel, string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) candidates.Add((channel, path));
        }
    }

    private static async Task<int> WaitForDevToolsPortAsync(
        string profileDirectory,
        Process process,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(profileDirectory, "DevToolsActivePort");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"Browser exited before DevTools became available (exit {process.ExitCode}).");
            if (File.Exists(path))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(path, cancellationToken);
                    if (lines.Length > 0 && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
                        port is > 0 and <= 65535)
                        return port;
                }
                catch (IOException) { }
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("Browser did not expose a DevTools port within 10 seconds.");
    }

    private static string BuildSnapshotScript(int maxElements)
        => SnapshotScriptTemplate.Replace("__MAX__", maxElements.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string BuildActionScript(string selectorJson, string actionJson, string textJson)
        => ActionScriptTemplate
            .Replace("__SELECTOR__", selectorJson, StringComparison.Ordinal)
            .Replace("__ACTION__", actionJson, StringComparison.Ordinal)
            .Replace("__TEXT__", textJson, StringComparison.Ordinal);

    private const string SnapshotScriptTemplate = """
JSON.stringify((() => {
  const max = __MAX__;
  const norm = v => (v == null ? '' : String(v).replace(/\s+/g,' ').trim());
  const roleOf = el => {
    const explicit = norm(el.getAttribute('role')); if (explicit) return explicit.toLowerCase();
    const tag = el.tagName.toLowerCase(); const type = norm(el.getAttribute('type')).toLowerCase();
    if (tag === 'button') return 'button';
    if (tag === 'a' && el.hasAttribute('href')) return 'link';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'select') return el.multiple ? 'listbox' : 'combobox';
    if (tag === 'input') {
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (type === 'button' || type === 'submit' || type === 'reset') return 'button';
      return type === 'password' ? 'password' : 'textbox';
    }
    if (/^h[1-6]$/.test(tag)) return 'heading';
    if (tag === 'img') return 'img';
    if (tag === 'option') return 'option';
    return 'generic';
  };
  const labelOf = el => el.labels && el.labels.length ? norm(Array.from(el.labels).map(x => x.innerText).join(' ')) : '';
  const nameOf = el => {
    const aria = norm(el.getAttribute('aria-label')); if (aria) return aria;
    const labelled = norm(el.getAttribute('aria-labelledby'));
    if (labelled) {
      const text = labelled.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(x => x.innerText).join(' ');
      if (norm(text)) return norm(text);
    }
    const label = labelOf(el); if (label) return label;
    const alt = norm(el.getAttribute('alt')); if (alt) return alt;
    const title = norm(el.getAttribute('title')); if (title) return title;
    if (el.tagName === 'INPUT' && ['button','submit','reset'].includes(norm(el.type).toLowerCase())) return norm(el.value);
    return norm(el.innerText);
  };
  const visible = el => {
    const r = el.getBoundingClientRect(), s = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
  };
  const elements = [];
  for (const el of Array.from(document.querySelectorAll('*'))) {
    if (elements.length >= max) break;
    const tag = el.tagName.toLowerCase(), role = roleOf(el), name = nameOf(el), text = norm(el.innerText), label = labelOf(el);
    const testId = norm(el.getAttribute('data-testid') || el.getAttribute('data-test-id'));
    const interactive = ['button','link','textbox','password','checkbox','radio','combobox','listbox','option'].includes(role) || el.tabIndex >= 0;
    if (!interactive && !name && !(/^h[1-6]$/.test(tag))) continue;
    const r = el.getBoundingClientRect(), s = getComputedStyle(el);
    const protectedValue = tag === 'input' && norm(el.type).toLowerCase() === 'password';
    let value = null;
    if (!protectedValue && ('value' in el) && typeof el.value !== 'object') value = norm(el.value);
    elements.push({
      id: 'dom:' + elements.length,
      tag,
      role,
      name: name || null,
      text: text || null,
      label: label || null,
      testId: testId || null,
      value: value || null,
      protected: protectedValue,
      enabled: !el.disabled,
      visible: visible(el),
      bounds: {x:r.x,y:r.y,width:r.width,height:r.height},
      styles: {display:s.display,visibility:s.visibility,overflow:s.overflow,fontSize:s.fontSize,fontFamily:s.fontFamily,fontWeight:s.fontWeight,margin:s.margin,padding:s.padding}
    });
  }
  return {
    url: location.href,
    title: document.title,
    viewport: {width:innerWidth,height:innerHeight,deviceScaleFactor:devicePixelRatio},
    elements,
    truncated: elements.length >= max
  };
})())
""";

    private const string ActionScriptTemplate = """
JSON.stringify((() => {
  const s = __SELECTOR__, action = __ACTION__, text = __TEXT__;
  const norm = v => (v == null ? '' : String(v).replace(/\s+/g,' ').trim());
  const same = (a,b) => norm(a).toLowerCase() === norm(b).toLowerCase();
  const roleOf = el => {
    const explicit = norm(el.getAttribute('role')); if (explicit) return explicit.toLowerCase();
    const tag = el.tagName.toLowerCase(), type = norm(el.getAttribute('type')).toLowerCase();
    if (tag === 'button') return 'button';
    if (tag === 'a' && el.hasAttribute('href')) return 'link';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'select') return el.multiple ? 'listbox' : 'combobox';
    if (tag === 'input') {
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (type === 'button' || type === 'submit' || type === 'reset') return 'button';
      return type === 'password' ? 'password' : 'textbox';
    }
    if (/^h[1-6]$/.test(tag)) return 'heading';
    return 'generic';
  };
  const labelOf = el => el.labels && el.labels.length ? norm(Array.from(el.labels).map(x => x.innerText).join(' ')) : '';
  const nameOf = el => {
    const aria = norm(el.getAttribute('aria-label')); if (aria) return aria;
    const labelled = norm(el.getAttribute('aria-labelledby'));
    if (labelled) {
      const value = labelled.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(x => x.innerText).join(' ');
      if (norm(value)) return norm(value);
    }
    const label = labelOf(el); if (label) return label;
    const alt = norm(el.getAttribute('alt')); if (alt) return alt;
    const title = norm(el.getAttribute('title')); if (title) return title;
    return norm(el.innerText);
  };
  let candidates = [];
  try { candidates = s.css ? Array.from(document.querySelectorAll(s.css)) : Array.from(document.querySelectorAll('*')); }
  catch (e) { return {ok:false,error:'Invalid CSS selector: '+e.message,matchCount:0}; }
  candidates = candidates.filter(el =>
    (!s.role || same(roleOf(el),s.role)) &&
    (!s.name || same(nameOf(el),s.name)) &&
    (!s.text || same(norm(el.innerText),s.text)) &&
    (!s.label || same(labelOf(el),s.label)) &&
    (!s.testId || same(el.getAttribute('data-testid') || el.getAttribute('data-test-id'),s.testId)));
  const count = candidates.length;
  let el = null;
  if (s.index != null) {
    if (s.index < 0 || s.index >= count)
      return {ok:false,error:`Selector matched ${count} element(s), index ${s.index} is out of range.`,matchCount:count};
    el = candidates[s.index];
  } else {
    if (count !== 1)
      return {ok:false,error:count===0?'No browser element matched the selector.':`Browser selector matched ${count} elements; add css/testId/label/index to disambiguate.`,matchCount:count};
    el = candidates[0];
  }
  const r = el.getBoundingClientRect();
  const result = {ok:true,error:null,matchCount:count,role:roleOf(el),name:nameOf(el)||null,bounds:{x:r.x,y:r.y,width:r.width,height:r.height}};
  el.scrollIntoView({block:'center',inline:'center'});
  if (action === 'click') { el.click(); return result; }
  if (action === 'fill') {
    const protectedValue = el.tagName.toLowerCase() === 'input' && norm(el.type).toLowerCase() === 'password';
    if (protectedValue) return {...result,ok:false,error:'MateMCP refuses to fill password/protected browser fields.'};
    el.focus();
    if (el.isContentEditable) {
      el.textContent = text ?? '';
      el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:text??''}));
      return result;
    }
    if (!('value' in el)) return {...result,ok:false,error:'Selected element is not editable.'};
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    const descriptor = Object.getOwnPropertyDescriptor(proto,'value');
    if (descriptor && descriptor.set) descriptor.set.call(el,text??''); else el.value = text??'';
    el.dispatchEvent(new Event('input',{bubbles:true}));
    el.dispatchEvent(new Event('change',{bubbles:true}));
    return result;
  }
  return {...result,ok:false,error:'Unknown browser action.'};
})())
""";

    private async Task CloseCoreAsync()
    {
        var cdp = _cdp;
        _cdp = null;
        if (cdp is not null) await cdp.DisposeAsync();
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
        if (_profileDirectory is { } profile) TryDeleteDirectory(profile);
        _profileDirectory = null;
        _channel = null;
        _viewport = null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await CloseCoreAsync(); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private sealed record DevToolsTarget(string? Id, string? Type, string? Url, string? Title, string? WebSocketDebuggerUrl);
    private sealed record PageState(string? Url, string? Title, int Width, int Height, double Scale);

    private sealed class CdpClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _receiveTask;
        private int _nextId;

        public static async Task<CdpClient> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            var client = new CdpClient();
            await client._socket.ConnectAsync(endpoint, cancellationToken);
            client._receiveTask = client.ReceiveLoopAsync();
            return client;
        }

        public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs))
                throw new InvalidOperationException("Could not allocate a CDP request id.");
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters }, Json);
                await _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken);
                return await tcs.Task.WaitAsync(cancellationToken);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > 8 * 1024 * 1024)
                            throw new InvalidOperationException("CDP message exceeded the 8 MiB MateMCP limit.");
                    } while (!result.EndOfMessage);

                    using var document = JsonDocument.Parse(message.ToArray());
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var idElement) ||
                        !idElement.TryGetInt32(out var id) ||
                        !_pending.TryGetValue(id, out var tcs))
                        continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        tcs.TrySetException(new InvalidOperationException($"CDP {Trim(error.ToString(), 1000)}"));
                        continue;
                    }
                    tcs.TrySetResult(root.TryGetProperty("result", out var response) ? response.Clone() : default);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                foreach (var pending in _pending.Values) pending.TrySetException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "MateMCP browser session closed",
                        CancellationToken.None);
            }
            catch { }
            if (_receiveTask is not null)
            {
                try { await _receiveTask; } catch { }
            }
            _socket.Dispose();
            _lifetime.Dispose();
        }
    }
}
