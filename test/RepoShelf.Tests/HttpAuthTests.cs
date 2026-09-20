using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

/// <summary>HTTP boundary: pairing token, origin/host validation, CORS, capture, export.</summary>
public class HttpAuthTests : IAsyncLifetime
{
    private ServiceHost _host = null!;
    private FakeGitHubServer _fake = null!;
    private string _base = null!;
    private string _dataDir = null!;
    private const string Token = "test-pairing-token";

    public async Task InitializeAsync()
    {
        _fake = new FakeGitHubServer();
        _dataDir = Path.Combine(Path.GetTempPath(), "repo-shelf-test-" + Guid.NewGuid().ToString("N"));
        var port = FreePort();
        _host = ServiceHost.Create(new Dictionary<string, string?>
        {
            ["REPO_SHELF_PORT"] = port.ToString(),
            ["REPO_SHELF_DATA_DIR"] = _dataDir,
            ["REPO_SHELF_TOKEN"] = Token,
            ["REPO_SHELF_GITHUB_API"] = _fake.BaseUrl,
        });
        await _host.StartAsync();
        _base = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _fake.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* WAL files may linger briefly */ }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string? token = Token, string? origin = null, string? host = null, string? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token is not null) req.Headers.Add("x-reposhelf-token", token);
        if (origin is not null) req.Headers.Add("Origin", origin);
        if (host is not null) req.Headers.Host = host;
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    [Fact]
    public async Task RequestsWithoutTokenAreRejected()
    {
        using var http = new HttpClient();
        var noToken = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/repos", token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        var wrong = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/repos", token: "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var body = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("unauthorized", body);
    }

    [Fact]
    public async Task ForeignWebsiteOriginsAreRejected()
    {
        using var http = new HttpClient();
        var res = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/repos", origin: "https://evil.example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("origin_not_allowed", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExtensionOriginsAcceptedWithCors()
    {
        using var http = new HttpClient();
        const string origin = "chrome-extension://abcdefghijklmnop";
        var res = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/repos", origin: origin));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(origin, res.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var preflight = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, $"{_base}/api/repos")
        {
            Headers = { { "Origin", origin } },
        });
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal(origin, preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var webPreflight = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, $"{_base}/api/repos")
        {
            Headers = { { "Origin", "https://evil.example.com" } },
        });
        Assert.False(webPreflight.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task SpoofedHostHeadersAreRejected()
    {
        using var http = new HttpClient();
        var res = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/repos", host: "attacker.example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task HealthIsPublicButMinimalAndUiCarriesToken()
    {
        using var http = new HttpClient();
        var health = await http.GetStringAsync($"{_base}/api/health");
        Assert.Contains("\"ok\":true", health);
        Assert.DoesNotContain(Token, health);

        var html = await http.GetStringAsync($"{_base}/");
        Assert.Contains(Token, html); // meta tag for same-origin UI calls
        Assert.Contains("Repo Shelf", html);
    }

    [Fact]
    public async Task CaptureAnnotateSearchOverHttp()
    {
        _fake.AddRepo(TestRepos.MakeRepo(100, "octocat", "Hello-World"), readme: "# Hello World");
        using var http = new HttpClient();

        var created = await http.SendAsync(Req(HttpMethod.Post, $"{_base}/api/repos",
            body: """{"url":"https://github.com/octocat/Hello-World","reason":"api 测试","tags":["demo"]}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdJson = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var repoId = createdJson["data"]!["repo"]!["id"]!.GetValue<long>();

        var dupe = await http.SendAsync(Req(HttpMethod.Post, $"{_base}/api/repos",
            body: """{"url":"https://github.com/octocat/Hello-World/pulls"}"""));
        Assert.Equal(HttpStatusCode.OK, dupe.StatusCode);
        Assert.Equal(repoId, JsonNode.Parse(await dupe.Content.ReadAsStringAsync())!["data"]!["repo"]!["id"]!.GetValue<long>());

        var patched = await http.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"{_base}/api/repos/{repoId}/annotation")
        {
            Headers = { { "x-reposhelf-token", Token } },
            Content = new StringContent("""{"notes":"中文搜索词","status":"tried"}""", Encoding.UTF8, "application/json"),
        });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var found = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/repos?q={Uri.EscapeDataString("中文搜索")}"));
        var foundJson = JsonNode.Parse(await found.Content.ReadAsStringAsync())!;
        Assert.Equal(1, foundJson["data"]!["total"]!.GetValue<long>());
        var item = foundJson["data"]!["items"]![0]!;
        Assert.Equal(repoId, item["repo"]!["id"]!.GetValue<long>());
        Assert.Contains("<mark>", item["snippet"]!.GetValue<string>());
        Assert.Null(item["repo"]!["readme"]);

        var detail = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/repos/{repoId}"));
        var detailJson = JsonNode.Parse(await detail.Content.ReadAsStringAsync())!;
        Assert.Equal("# Hello World", detailJson["data"]!["repo"]!["readme"]!.GetValue<string>());
        Assert.Equal("tried", detailJson["data"]!["repo"]!["annotation"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExportExcludesCredentialsAndRestoreRoundTrips()
    {
        _fake.AddRepo(TestRepos.MakeRepo(200, "a", "b"), readme: "# B");
        using var http = new HttpClient();
        await http.SendAsync(Req(HttpMethod.Post, $"{_base}/api/repos",
            body: """{"url":"https://github.com/a/b","reason":"backup test"}"""));
        await http.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{_base}/api/settings/github-token")
        {
            Headers = { { "x-reposhelf-token", Token } },
            Content = new StringContent("""{"token":"ghp_super_secret"}""", Encoding.UTF8, "application/json"),
        });

        var exported = await http.SendAsync(Req(HttpMethod.Get, $"{_base}/api/export"));
        var text = await exported.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ghp_super_secret", text);
        Assert.DoesNotContain(Token, text);
        Assert.Contains("attachment", exported.Content.Headers.ContentDisposition?.ToString());

        // Wipe and restore through the API.
        var id = JsonNode.Parse(text)!["repos"]![0]!["githubId"]!.GetValue<long>();
        long localId;
        lock (_host.Store.Sync)
        {
            using var cmd = _host.Store.Conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM repos WHERE github_id = $g";
            cmd.Parameters.AddWithValue("$g", id);
            localId = (long)cmd.ExecuteScalar()!;
        }
        await http.SendAsync(Req(HttpMethod.Delete, $"{_base}/api/repos/{localId}"));
        var restored = await http.SendAsync(Req(HttpMethod.Post, $"{_base}/api/restore?mode=overwrite", body: text));
        var restoredJson = JsonNode.Parse(await restored.Content.ReadAsStringAsync())!;
        Assert.Equal(1, restoredJson["data"]!["added"]!.GetValue<int>());
    }
}
