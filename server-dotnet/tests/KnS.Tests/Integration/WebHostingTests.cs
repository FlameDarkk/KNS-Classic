using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using KnS.Server.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

/// <summary>One process hosts everything: the browser build at KNS_PUBLIC_BASE and the API under &lt;base&gt;api/.</summary>
[Collection(SharedServer.Name)]
public sealed class WebHostingTests : IDisposable
{
    private const string Base = "/games/kns/play/";
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "kns-web-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public WebHostingTests()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "kns_data-0123456789abcdef"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>game</title>");
        using (var gz = new GZipStream(File.Create(Path.Combine(_webRoot, "index.html.gz")), CompressionLevel.Fastest))
        {
            gz.Write("<!doctype html><title>game</title>"u8);
        }

        File.WriteAllText(Path.Combine(_webRoot, "studio.html"), "<!doctype html><title>studio</title>");
        File.WriteAllText(Path.Combine(_webRoot, "kns_data-0123456789abcdef", "0.js"), "KNS_ASSET('a','');");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(new ServerOptions { Quiet = true, PublicBase = Base, WebRoot = _webRoot, AdminKey = "test-admin-key" })));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Fact]
    public async Task The_base_serves_the_game_studio_and_hashed_assets()
    {
        using HttpResponseMessage page = await _client.GetAsync(Base);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("game", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());

        Assert.Contains("studio", await _client.GetStringAsync(Base + "studio"), StringComparison.Ordinal);

        using HttpResponseMessage asset = await _client.GetAsync(Base + "kns_data-0123456789abcdef/0.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(Base + "missing.js")).StatusCode);
    }

    [Fact]
    public async Task A_gzip_sibling_is_sent_with_the_original_type()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Base);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using HttpResponseMessage page = await _client.SendAsync(request);
        Assert.Equal("gzip", Assert.Single(page.Content.Headers.ContentEncoding));
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        using var unzip = new GZipStream(await page.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        Assert.Contains("game", await new StreamReader(unzip).ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paths_outside_the_base_are_not_served_and_the_bare_base_redirects()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/account/login", JsonContent.Create(new { id = "x", password = "y" }))).StatusCode);
        using HttpResponseMessage bare = await _client.GetAsync("/games/kns/play");
        Assert.Equal(HttpStatusCode.PermanentRedirect, bare.StatusCode);
        Assert.Equal(Base, bare.Headers.Location?.OriginalString);
        Assert.NotEqual(HttpStatusCode.NotFound, (await _client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task The_api_lives_under_base_api()
    {
        using HttpResponseMessage login = await _client.PostAsJsonAsync(Base + "api/account/login", new { id = "nobody-web", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // The native game's platform paths are routed by the raw target below the base.
        using HttpResponseMessage check = await _client.PostAsync(Base + "api/common/protect/check.c2s", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        using HttpResponseMessage admin = await _client.GetAsync(Base + "api/admin/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
    }
}
