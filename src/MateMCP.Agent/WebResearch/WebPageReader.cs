using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MateMCP.Agent.WebResearch;

public sealed record WebPageReadResult(string RequestedUrl, string FinalUrl, string Title, string ContentType, string Content, int Characters, bool Truncated);
public sealed record WebPageReadOutcome(string Url, WebPageReadResult? Page, string? Error);

public sealed class WebPageReader(IHttpClientFactory clients)
{
    public const string HttpClientName = "web-read";
    private const int MaxResponseBytes = 3 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly Regex Whitespace = new(@"[\t\r\n ]+", RegexOptions.Compiled);
    private readonly HtmlParser _htmlParser = new();

    public async Task<WebPageReadResult> ReadAsync(string url, int maxChars = 12000, CancellationToken cancellationToken = default)
    {
        if (maxChars is < 500 or > 30000) throw new ArgumentOutOfRangeException(nameof(maxChars), "maxChars must be between 500 and 30000.");
        var current = ValidateUrl(url);
        var requestedUrl = current.AbsoluteUri;
        using var client = clients.CreateClient(HttpClientName);

        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects) throw new HttpRequestException("The page exceeded the redirect limit.");
                var location = response.Headers.Location ?? throw new HttpRequestException("The page returned a redirect without a destination.");
                current = ValidateUrl(location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(current, location).AbsoluteUri);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Page returned HTTP {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (contentType is not ("text/html" or "application/xhtml+xml" or "text/plain"))
                throw new InvalidDataException($"Unsupported page content type '{contentType}'. Only HTML and plain text pages can be read.");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new InvalidDataException("Page exceeded the 3 MB response limit.");

            var body = await ReadBoundedBodyAsync(response.Content, cancellationToken);
            var title = current.Host;
            var content = body;
            if (contentType is "text/html" or "application/xhtml+xml")
            {
                var document = await new HtmlParser().ParseDocumentAsync(body, cancellationToken);
                title = Normalize(document.Title);
                content = ExtractMarkdown(document);
                if (content.Length == 0)
                    throw new InvalidDataException("The page returned no readable static content. It may require JavaScript or an interactive session.");
            }

            var truncated = content.Length > maxChars;
            if (truncated) content = content[..maxChars].TrimEnd() + "\n\n[Content truncated by MateMCP maxChars limit.]";
            return new WebPageReadResult(requestedUrl, current.AbsoluteUri, string.IsNullOrWhiteSpace(title) ? current.Host : title,
                contentType, content, content.Length, truncated);
        }

        throw new HttpRequestException("The page could not be read.");
    }

    public async Task<IReadOnlyList<WebPageReadOutcome>> ReadManyAsync(
        IReadOnlyList<string> urls,
        int maxCharsPerPage = 8000,
        CancellationToken cancellationToken = default)
    {
        if (urls.Count is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(urls), "Provide between 1 and 5 URLs.");
        using var gate = new SemaphoreSlim(3, 3);
        var tasks = urls.Select(async url =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return new WebPageReadOutcome(url, await ReadAsync(url, maxCharsPerPage, cancellationToken), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new WebPageReadOutcome(url, null, ex.Message);
            }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    public static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException) throw;
            }
        }

        throw new HttpRequestException("The destination did not resolve to a public internet address.");
    }

    private static Uri ValidateUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? uri.Port != 443 : uri.Port != 80)
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Only absolute public HTTP/HTTPS URLs on ports 80 and 443 are supported.", nameof(value));

        var host = uri.Host.TrimEnd('.');
        if (IPAddress.TryParse(host, out var address))
        {
            if (!IsPublicAddress(address))
                throw new ArgumentException("Private, loopback, link-local, and reserved IP addresses are not allowed.", nameof(value));
        }
        else if (!host.Contains('.') || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Local and single-label hostnames are not allowed.", nameof(value));
        }
        return uri;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0]; var b = bytes[1]; var c = bytes[2];
            if (a is 0 or 10 or 127 or >= 224) return false;
            if (a == 100 && b is >= 64 and <= 127) return false;
            if (a == 169 && b == 254) return false;
            if (a == 172 && b is >= 16 and <= 31) return false;
            if (a == 192 && (b == 168 || (b == 0 && (c == 0 || c == 2)) || (b == 88 && c == 99))) return false;
            if (a == 198 && (b is 18 or 19 || (b == 51 && c == 100))) return false;
            if (a == 203 && b == 0 && c == 113) return false;
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        if ((bytes[0] & 0xE0) != 0x20) return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) return false; // Teredo
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return false; // 6to4
        return true;
    }

    private async Task<string> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes) throw new InvalidDataException("Page exceeded the 3 MB response limit.");
            buffer.Write(chunk, 0, read);
        }

        var charset = content.Headers.ContentType?.CharSet;
        try { return Encoding.GetEncoding(string.IsNullOrWhiteSpace(charset) ? "utf-8" : charset.Trim('"')).GetString(buffer.ToArray()); }
        catch (ArgumentException) { return Encoding.UTF8.GetString(buffer.ToArray()); }
    }

    private static string ExtractMarkdown(IDocument document)
    {
        foreach (var node in document.QuerySelectorAll("script,style,nav,header,footer,aside,form,noscript,svg,iframe,canvas,template,dialog"))
            node.Remove();
        var root = document.QuerySelector("article") ?? document.QuerySelector("main") ?? document.Body;
        if (root is null) return string.Empty;

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in root.QuerySelectorAll("h1,h2,h3,h4,h5,p,li,pre,blockquote"))
        {
            var text = Normalize(element.TextContent);
            if (text.Length < 2 || !seen.Add(text)) continue;
            var line = element.LocalName switch
            {
                "h1" => "# " + text,
                "h2" => "## " + text,
                "h3" => "### " + text,
                "h4" or "h5" => "#### " + text,
                "li" => "- " + text,
                "blockquote" => "> " + text,
                "pre" => "```\n" + element.TextContent.Trim() + "\n```",
                _ => text
            };
            lines.Add(line);
        }
        if (lines.Count == 0)
        {
            var fallback = Normalize(root.TextContent);
            if (fallback.Length > 0) lines.Add(fallback);
        }
        return string.Join("\n\n", lines);
    }

    private static string Normalize(string? text) => Whitespace.Replace(text ?? string.Empty, " ").Trim();
    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}



