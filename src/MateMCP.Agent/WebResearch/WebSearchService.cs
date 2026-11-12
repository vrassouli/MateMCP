using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;

namespace MateMCP.Agent.WebResearch;

public sealed record WebSearchResult(int Rank, string Title, string Url, string Snippet, string Provider);

public sealed record WebSearchResponse(
    string Query,
    string Provider,
    bool UsedFallback,
    IReadOnlyList<WebSearchResult> Results,
    string? Notice);

public sealed class WebSearchService(IHttpClientFactory clients)
{
    public const string HttpClientName = "web-search";
    private const int MaxQueryLength = 500;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private readonly HtmlParser _htmlParser = new();

    public async Task<WebSearchResponse> SearchAsync(
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0) throw new ArgumentException("A search query is required.", nameof(query));
        if (query.Length > MaxQueryLength) throw new ArgumentOutOfRangeException(nameof(query), $"Search queries must be {MaxQueryLength} characters or fewer.");
        if (maxResults is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be between 1 and 10.");

        string? bingFailure = null;
        try
        {
            var bingResults = await SearchBingAsync(query, maxResults, cancellationToken);
            if (bingResults.Count > 0)
                return new WebSearchResponse(query, "Bing", false, bingResults, null);

            bingFailure = "Bing returned no parseable results.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            bingFailure = ToSafeFailure(ex);
        }

        try
        {
            var duckResults = await SearchDuckDuckGoLiteAsync(query, maxResults, cancellationToken);
            if (duckResults.Count > 0)
                return new WebSearchResponse(query, "DuckDuckGo Lite", true, duckResults,
                    $"Bing was unavailable ({bingFailure}); DuckDuckGo Lite was used as fallback.");

            throw new InvalidDataException("DuckDuckGo Lite returned no parseable results.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            throw new InvalidOperationException(
                $"Web search failed. Bing: {bingFailure ?? "no results"}. DuckDuckGo Lite: {ToSafeFailure(ex)}.", ex);
        }
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchBingAsync(string query, int maxResults, CancellationToken ct)
    {
        var isPersian = query.Any(c => c is >= '\u0600' and <= '\u06FF');
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = query,
            ["mkt"] = isPersian ? "fa-IR" : "en-US",
            ["setlang"] = isPersian ? "fa" : "en"
        };
        var uri = "https://www.bing.com/search" + QueryHelpers.AddQueryString(string.Empty, parameters);
        var html = await DownloadHtmlAsync(uri, ct);
        if (LooksLikeChallenge(html)) throw new InvalidDataException("Bing returned a bot-check or challenge page.");

        var document = await new HtmlParser().ParseDocumentAsync(html, ct);
        var results = new List<WebSearchResult>();
        foreach (var item in document.QuerySelectorAll("li.b_algo"))
        {
            var anchor = item.QuerySelector("h2 a");
            var title = Normalize(anchor?.TextContent);
            var href = ResolveBingDestination(anchor?.GetAttribute("href"));
            if (title.Length == 0 || href is null) continue;

            var snippet = Normalize(item.QuerySelector(".b_caption p")?.TextContent
                ?? item.QuerySelector(".b_snippet")?.TextContent);
            AddUnique(results, new WebSearchResult(results.Count + 1, title, href, snippet, "Bing"));
            if (results.Count >= maxResults) break;
        }

        return results;
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchDuckDuckGoLiteAsync(string query, int maxResults, CancellationToken ct)
    {
        var uri = "https://lite.duckduckgo.com/lite/" + QueryHelpers.AddQueryString(string.Empty,
            new Dictionary<string, string?> { ["q"] = query, ["kl"] = "wt-wt" });
        var html = await DownloadHtmlAsync(uri, ct);
        if (LooksLikeChallenge(html)) throw new InvalidDataException("DuckDuckGo Lite returned a bot-check or challenge page.");

        var document = await new HtmlParser().ParseDocumentAsync(html, ct);
        var results = new List<WebSearchResult>();
        foreach (var anchor in document.QuerySelectorAll("a.result-link"))
        {
            var title = Normalize(anchor.TextContent);
            var href = ResolveDuckDuckGoDestination(anchor.GetAttribute("href"));
            if (title.Length == 0 || href is null) continue;

            var row = anchor.ParentElement?.ParentElement;
            var snippet = Normalize(row?.NextElementSibling?.QuerySelector(".result-snippet")?.TextContent);
            AddUnique(results, new WebSearchResult(results.Count + 1, title, href, snippet, "DuckDuckGo Lite"));
            if (results.Count >= maxResults) break;
        }

        return results;
    }

    private async Task<string> DownloadHtmlAsync(string url, CancellationToken ct)
    {
        using var response = await clients.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Search provider returned HTTP {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Search provider returned a non-HTML response.");

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("Search provider response exceeded the size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("Search provider response exceeded the size limit.");
            buffer.Write(chunk, 0, read);
        }

        return response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset
            ? Decode(buffer.ToArray(), charset)
            : Decode(buffer.ToArray(), "utf-8");
    }

    private static string Decode(byte[] bytes, string charset)
    {
        try { return System.Text.Encoding.GetEncoding(charset.Trim('"')).GetString(bytes); }
        catch (ArgumentException) { return System.Text.Encoding.UTF8.GetString(bytes); }
    }

    private static string? ResolveBingDestination(string? href)
    {
        if (!Uri.TryCreate(WebUtility.HtmlDecode(href), UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Equals("www.bing.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Equals("/ck/a", StringComparison.OrdinalIgnoreCase))
        {
            var values = QueryHelpers.ParseQuery(uri.Query);
            if (!values.TryGetValue("u", out var encoded) || encoded.Count == 0) return null;
            var payload = encoded[0] ?? string.Empty;
            if (!payload.StartsWith("a1", StringComparison.Ordinal)) return null;
            try
            {
                var base64 = payload[2..].Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
                href = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException) { return null; }
        }
        return NormalizeExternalUrl(href);
    }

    private static string? ResolveDuckDuckGoDestination(string? href)
    {
        Uri? uri = null;
        if (href?.StartsWith("//", StringComparison.Ordinal) == true)
            Uri.TryCreate("https:" + href, UriKind.Absolute, out uri);
        else
            Uri.TryCreate(href, UriKind.Absolute, out uri);
        if (uri is null) return null;

        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Equals("/l/", StringComparison.OrdinalIgnoreCase))
        {
            var values = QueryHelpers.ParseQuery(uri.Query);
            if (!values.TryGetValue("uddg", out var target) || target.Count == 0) return null;
            href = target[0];
        }
        return NormalizeExternalUrl(href);
    }

    private static string? NormalizeExternalUrl(string? value)
    {
        if (!Uri.TryCreate(WebUtility.HtmlDecode(value), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0) return null;
        if (uri.Host.Equals("www.bing.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("bing.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.duckduckgo.com", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsoluteUri;
    }

    private static bool LooksLikeChallenge(string html)
        => html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase)
            || html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)
            || html.Contains("verify you are human", StringComparison.OrdinalIgnoreCase)
            || html.Contains("captcha", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => Whitespace.Replace(WebUtility.HtmlDecode(value ?? string.Empty), " ").Trim();

    private static void AddUnique(List<WebSearchResult> results, WebSearchResult candidate)
    {
        if (results.Any(x => string.Equals(x.Url.TrimEnd('/'), candidate.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) return;
        results.Add(candidate with { Rank = results.Count + 1 });
    }

    private static string ToSafeFailure(Exception exception)
        => exception is HttpRequestException { StatusCode: { } status }
            ? $"HTTP {(int)status} ({status})"
            : exception is TaskCanceledException ? "request timed out" : exception.Message;
}


