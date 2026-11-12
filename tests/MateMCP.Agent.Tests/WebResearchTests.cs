using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MateMCP.Agent.Tools;
using MateMCP.Agent.WebResearch;

namespace MateMCP.Agent.Tests;

public sealed class WebResearchTests
{
    [Fact]
    public async Task Bing_results_are_parsed_and_redirects_are_unwrapped()
    {
        const string query = "dotnet 10 ویژگی‌ها";
        const string destination = "https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/overview";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(destination))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var html = $"""
            <html><body><ol id="b_results">
              <li class="b_algo">
                <h2><a href="https://www.bing.com/ck/a?u=a1{encoded}&amp;ntb=1">.NET 10 overview</a></h2>
                <div class="b_caption"><p>What's new in .NET 10.</p></div>
              </li>
            </ol></body></html>
            """;
        var factory = new FakeHttpClientFactory(name => new DelegateHandler((request, _) =>
        {
            Assert.Equal("www.bing.com", request.RequestUri!.Host);
            Assert.Equal(query, System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["q"]);
            return Task.FromResult(HtmlResponse(html));
        }));

        var result = await new WebSearchService(factory).SearchAsync(query);

        Assert.Equal("Bing", result.Provider);
        Assert.False(result.UsedFallback);
        var item = Assert.Single(result.Results);
        Assert.Equal(1, item.Rank);
        Assert.Equal(".NET 10 overview", item.Title);
        Assert.Equal(destination, item.Url);
        Assert.Equal("What's new in .NET 10.", item.Snippet);
    }

    [Fact]
    public async Task DuckDuckGo_Lite_is_used_when_Bing_returns_a_challenge()
    {
        const string query = "python programming language";
        const string destination = "https://www.python.org/";
        var html = $"""
            <html><body>
              <table>
                <tr><td><a class="result-link" href="//duckduckgo.com/l/?uddg={Uri.EscapeDataString(destination)}&amp;rut=abc">Welcome to Python.org</a></td></tr>
                <tr><td class="result-snippet">The Python Software Foundation advances Python.</td></tr>
              </table>
            </body></html>
            """;
        var factory = new FakeHttpClientFactory(name => new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "www.bing.com")
                return Task.FromResult(HtmlResponse("<html><div id='anomaly-modal'>verify</div></html>"));
            Assert.Equal("lite.duckduckgo.com", request.RequestUri.Host);
            return Task.FromResult(HtmlResponse(html));
        }));

        var result = await new WebSearchService(factory).SearchAsync(query);

        Assert.Equal("DuckDuckGo Lite", result.Provider);
        Assert.True(result.UsedFallback);
        Assert.Contains("Bing was unavailable", result.Notice);
        var item = Assert.Single(result.Results);
        Assert.Equal(destination, item.Url);
        Assert.Equal("Welcome to Python.org", item.Title);
        Assert.Equal("The Python Software Foundation advances Python.", item.Snippet);
    }

    [Fact]
    public async Task Search_rejects_empty_queries_and_invalid_result_counts()
    {
        var service = new WebSearchService(new FakeHttpClientFactory(_ => new DelegateHandler((_, _) => throw new InvalidOperationException("Request should not run."))));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync("  "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync("test", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(new string('x', 501)));
    }

    [Fact]
    public async Task Reader_extracts_main_content_and_drops_navigation_and_scripts()
    {
        var html = """
            <html><head><title>Example page</title></head><body>
              <nav>Navigation noise</nav>
              <article><h1>Useful heading</h1><p>Important paragraph.</p><script>ignoreThisSecret()</script></article>
              <footer>Footer noise</footer>
            </body></html>
            """;
        var factory = new FakeHttpClientFactory(_ => new DelegateHandler((_, _) => Task.FromResult(HtmlResponse(html))));
        var result = await new WebPageReader(factory).ReadAsync("https://example.com/article");

        Assert.Equal("Example page", result.Title);
        Assert.Contains("# Useful heading", result.Content);
        Assert.Contains("Important paragraph.", result.Content);
        Assert.DoesNotContain("Navigation noise", result.Content);
        Assert.DoesNotContain("Footer noise", result.Content);
        Assert.DoesNotContain("ignoreThisSecret", result.Content);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData("http://127.0.0.1/secret")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://example.com:8443/")]
    [InlineData("https://example.com:80/")]
    [InlineData("http://example.com:443/")]
    public async Task Reader_blocks_private_local_and_non_http_destinations(string url)
    {
        var reader = new WebPageReader(new FakeHttpClientFactory(_ => new DelegateHandler((_, _) => throw new InvalidOperationException("Request should not run."))));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(url));
    }

    [Fact]
    public async Task Reader_revalidates_redirect_destinations_before_following_them()
    {
        var factory = new FakeHttpClientFactory(_ => new DelegateHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/admin");
            return Task.FromResult(response);
        }));
        var reader = new WebPageReader(factory);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync("https://example.com/start"));
    }

    [Fact]
    public async Task Reader_supports_bounded_parallel_reads_with_isolated_failures()
    {
        var factory = new FakeHttpClientFactory(_ => new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/bad")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(HtmlResponse("<html><body><main><h1>Page</h1><p>Text.</p></main></body></html>"));
        }));
        var reader = new WebPageReader(factory);

        var results = await reader.ReadManyAsync(
            ["https://example.com/good", "https://example.com/bad"],
            maxCharsPerPage: 1000);

        Assert.Equal(2, results.Count);
        Assert.NotNull(results[0].Page);
        Assert.Null(results[0].Error);
        Assert.Null(results[1].Page);
        Assert.Contains("404", results[1].Error);
    }

    [Fact]
    public void Web_research_tools_are_in_the_MCP_catalog()
    {
        Assert.Contains("web_search", McpToolCatalog.Names);
        Assert.Contains("web_read", McpToolCatalog.Names);
        Assert.Contains("web_read_many", McpToolCatalog.Names);
    }

    private static HttpResponseMessage HtmlResponse(string html)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };

    private sealed class FakeHttpClientFactory(Func<string, HttpMessageHandler> createHandler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(createHandler(name), disposeHandler: true);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    [Fact]
    public async Task Reader_accepts_public_IPv6_url_syntax()
    {
        var factory = new FakeHttpClientFactory(_ => new DelegateHandler((_, _) =>
            Task.FromResult(HtmlResponse("<html><body><main><h1>IPv6 page</h1></main></body></html>"))));
        var result = await new WebPageReader(factory).ReadAsync("https://[2606:4700:4700::1111]/");
        Assert.Contains("IPv6 page", result.Content);
    }
}


