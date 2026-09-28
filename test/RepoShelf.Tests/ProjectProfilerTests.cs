using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RepoShelf.Core;

namespace RepoShelf.Tests;

/// <summary>DeepSeek stand-in that records request bodies and replies with a settable content string.</summary>
internal sealed class RecordingAiServer : IDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public List<string> Bodies { get; } = new();
    public string Content { get; set; } = "{}";

    public RecordingAiServer()
    {
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        }
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls(BaseUrl);
        _app = builder.Build();
        _app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            lock (Bodies) Bodies.Add(body);
            await ctx.Response.WriteAsJsonAsync(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Content },
                }),
            });
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _app.StopAsync().GetAwaiter().GetResult();
}

public sealed class ProjectProfilerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "repo-shelf-profiler-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(_dir, "project");
    private readonly HttpClient _http = new();
    private RecordingAiServer _ai = null!;
    private FakeGitHubServer _github = null!;
    private ServiceHost _host = null!;
    private const string Token = "profiler-test-token";

    // Each forbidden file holds a unique sentinel that must never be read or sent.
    private static readonly (string Path, string Sentinel)[] Forbidden =
    {
        (".env", "SENTINEL_ENV_SECRET"),
        (".env.local", "SENTINEL_ENV_LOCAL"),
        ("memory/x.md", "SENTINEL_MEMORY"),
        ("data/y.db", "SENTINEL_DATA_DB"),
        ("config/z.yaml", "SENTINEL_CONFIG"),
        ("src/a.py", "SENTINEL_SOURCE"),
        (".git/config", "SENTINEL_GIT"),
        ("node_modules/pkg/package.json", "SENTINEL_NODE_MODULES"),
        ("docs/README.md", "SENTINEL_NESTED_README"),
        ("app/Deep/Nested/Deep.csproj", "SENTINEL_DEEP_CSPROJ"),
        ("notes.md", "SENTINEL_NOTES"),
    };

    private static readonly string[] Whitelisted =
    {
        "README.md", "AGENTS.md", "CONTEXT.md", "TASK_QUEUE.md", "package.json", "requirements-dev.txt", "app/Tool/Tool.csproj",
    };

    public async Task InitializeAsync()
    {
        Write("README.md", "# Widget\nA desktop widget tracker.");
        Write("AGENTS.md", "Agents build with dotnet.");
        Write("CONTEXT.md", "Goal: ship offline sync.");
        Write("TASK_QUEUE.md", """
            # TASK_QUEUE

            | # | ID | name | status | worker |
            |---|---|---|---|---|
            | 1 | T-1 | Finished parser work ROW_DONE | `DONE` | codex |
            | 2 | T-2 | Add offline sync ROW_OPEN | `TODO` | claude |
            | 3 | T-3 | Wire charts ROW_RUNNING | `IN_PROGRESS` | claude |

            <!-- task:T-1 files: SENTINEL_QUEUE_COMMENT -->
            """);
        Write("package.json", """{"name":"widget","scripts":{"build":"SENTINEL_SCRIPT"},"dependencies":{"react":"^18.0.0"},"devDependencies":{"vitest":"1.0.0"}}""");
        Write("requirements-dev.txt", "# dev\nrequests>=2.31  # http\n-r base.txt\npytest==8.0\n");
        Write("app/Tool/Tool.csproj", """<Project><ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0-SENTINEL_VERSION" /></ItemGroup></Project>""");
        foreach (var (path, sentinel) in Forbidden) Write(path, sentinel);

        _ai = new RecordingAiServer();
        _github = new FakeGitHubServer();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _host = ServiceHost.Create(new Dictionary<string, string?>
        {
            ["REPO_SHELF_DATA_DIR"] = Path.Combine(_dir, "shelf"),
            ["REPO_SHELF_PORT"] = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(),
            ["REPO_SHELF_TOKEN"] = Token,
            ["REPO_SHELF_GITHUB_API"] = _github.BaseUrl,
            ["REPO_SHELF_DEEPSEEK_API"] = _ai.BaseUrl,
        });
        listener.Stop();
        _host.Store.SetSetting("deepseek_api_key", "fake-ai-key");
        await _host.StartAsync();
        _http.BaseAddress = new Uri(_host.BaseUrl);
        _http.DefaultRequestHeaders.Add("x-reposhelf-token", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _ai.Dispose();
        _github.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(Project, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> Call(HttpMethod method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private async Task<long> AddProject()
    {
        var (status, body) = await Call(HttpMethod.Post, "/api/projects", new JsonObject { ["name"] = "Widget", ["path"] = Project });
        Assert.Equal(HttpStatusCode.Created, status);
        return body["data"]!["project"]!["id"]!.GetValue<long>();
    }

    private static string ValidReply(int needs = 6, int queries = 3) => new JsonObject
    {
        ["needs"] = new JsonArray(Enumerable.Range(1, needs).Select(i => (JsonNode?)$"需要第 {i} 项能力").ToArray()),
        ["queries"] = new JsonArray(Enumerable.Range(1, queries).Select(i => (JsonNode?)$"offline sync {i}").ToArray()),
        ["languages"] = new JsonArray("C#", "TypeScript"),
        ["dependencies"] = new JsonArray("react", "Microsoft.Data.Sqlite"),
    }.ToJsonString();

    [Fact]
    public void CollectOpensOnlyWhitelistedFilesAndListsExactlyThose()
    {
        var opened = new List<string>();
        var input = ProjectProfiler.Collect(Project, path =>
        {
            opened.Add(Path.GetRelativePath(Project, path).Replace('\\', '/'));
            return File.ReadAllText(path);
        });

        Assert.Equal(Whitelisted.OrderBy(f => f), opened.OrderBy(f => f));
        Assert.Equal(Whitelisted.OrderBy(f => f), input.Files.OrderBy(f => f));
        foreach (var (_, sentinel) in Forbidden) Assert.DoesNotContain(sentinel, input.Content);

        // Only open queue rows; manifests contribute names, never versions or scripts.
        Assert.Contains("ROW_OPEN", input.Content);
        Assert.Contains("ROW_RUNNING", input.Content);
        Assert.DoesNotContain("ROW_DONE", input.Content);
        Assert.DoesNotContain("SENTINEL_QUEUE_COMMENT", input.Content);
        Assert.DoesNotContain("SENTINEL_SCRIPT", input.Content);
        Assert.DoesNotContain("SENTINEL_VERSION", input.Content);
        Assert.DoesNotContain("18.0.0", input.Content);
        Assert.Equal(new[] { "react", "vitest", "requests", "pytest", "Microsoft.Data.Sqlite" }.OrderBy(d => d), input.Dependencies.OrderBy(d => d));
    }

    [Fact]
    public void CollectTruncatesEachFileAndCapsTheTotal()
    {
        Write("README.md", new string('r', 20_000));
        Write("AGENTS.md", new string('a', 20_000));
        Write("CONTEXT.md", new string('c', 20_000));
        Write("TASK_QUEUE.md", "| id | status |\n|---|---|\n| X | TODO |\n");
        var input = ProjectProfiler.Collect(Project);

        Assert.True(input.Content.Length <= ProjectProfiler.MaxTotalChars);
        Assert.DoesNotContain(new string('r', ProjectProfiler.MaxFileChars + 1), input.Content);
        // README, AGENTS and CONTEXT at 8 000 each fit; files that no longer fit are neither sent nor listed.
        foreach (var file in input.Files) Assert.Contains($"=== {file} ===", input.Content);
        Assert.Contains("README.md", input.Files);
        Assert.Contains("AGENTS.md", input.Files);
        Assert.Contains("CONTEXT.md", input.Files);
    }

    [Theory]
    [InlineData("pyproject.toml", """
        [project]
        name = "demo"
        dependencies = [
          "httpx>=0.27",
          "pydantic[email]==2.7",
        ]
        [project.optional-dependencies]
        dev = ["ruff"]
        [tool.poetry.dependencies]
        python = "^3.12"
        fastapi = "0.110"
        """, "httpx,pydantic,ruff,fastapi")]
    [InlineData("Cargo.toml", """
        [package]
        name = "demo"
        version = "0.1.0"
        [dependencies]
        serde = { version = "1", features = ["derive"] }
        tokio = "1"
        [dev-dependencies]
        insta = "1"
        [dependencies.reqwest]
        version = "0.12"
        """, "serde,tokio,insta,reqwest")]
    [InlineData("go.mod", """
        module example.com/demo
        go 1.22
        require github.com/spf13/cobra v1.8.0
        require (
            golang.org/x/sync v0.7.0 // indirect
            github.com/stretchr/testify v1.9.0
        )
        """, "github.com/spf13/cobra,golang.org/x/sync,github.com/stretchr/testify")]
    public void ManifestsYieldDependencyNamesOnly(string file, string text, string expected)
    {
        Assert.Equal(expected.Split(','), ProjectProfiler.DependencyNames(file, text));
    }

    [Fact]
    public void QueueRowsWithoutStatusHeaderDropDoneRows()
    {
        var rows = ProjectProfiler.OpenQueueRows("| a | b |\n|---|---|\n| one | DONE |\n| two | TODO |\n");
        Assert.DoesNotContain("one", rows);
        Assert.Contains("two", rows);
        Assert.Equal("", ProjectProfiler.OpenQueueRows("| id | status |\n|---|---|\n| one | `DONE` |\n"));
    }

    [Fact]
    public async Task AddingAProjectSendsNothingAndProfileStoresEditableResult()
    {
        var id = await AddProject();
        Assert.Empty(_ai.Bodies);
        Assert.Empty(_github.Requests);

        _ai.Content = ValidReply(needs: 14, queries: 6);
        var (status, body) = await Call(HttpMethod.Post, $"/api/projects/{id}/profile", new JsonObject { ["lang"] = "zh-CN" });
        Assert.True(status == HttpStatusCode.OK, body.ToJsonString());
        var project = body["data"]!["project"]!;
        Assert.Equal(ProjectProfiler.MaxNeeds, project["needs"]!.AsArray().Count);
        Assert.Equal(ProjectProfiler.MaxQueries, project["queries"]!.AsArray().Count);
        Assert.Equal("C#", project["languages"]![0]!.GetValue<string>());
        Assert.Equal(Whitelisted.OrderBy(f => f), project["profileFiles"]!.AsArray().Select(f => f!.GetValue<string>()).OrderBy(f => f));
        Assert.NotNull(project["profiledAt"]);

        // One JSON-mode request, containing open rows only and no forbidden content.
        var sent = Assert.Single(_ai.Bodies);
        var request = JsonNode.Parse(sent)!;
        Assert.Equal("json_object", request["response_format"]!["type"]!.GetValue<string>());
        Assert.Contains("json", request["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Simplified Chinese", request["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Contains("ROW_OPEN", sent);
        Assert.DoesNotContain("ROW_DONE", sent);
        foreach (var (_, sentinel) in Forbidden) Assert.DoesNotContain(sentinel, sent);
        Assert.Empty(_github.Requests);

        // The generated needs stay owner-editable through the DISC-1 API.
        var (editStatus, edited) = await Call(HttpMethod.Put, $"/api/projects/{id}", new JsonObject { ["needs"] = new JsonArray("my own need") });
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal("my own need", Assert.Single(edited["data"]!["project"]!["needs"]!.AsArray())!.GetValue<string>());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[\"an\", \"array\"]")]
    [InlineData("{\"needs\": [], \"queries\": [\"q\"]}")]
    [InlineData("{\"needs\": [\"n\"], \"queries\": \"not-an-array\"}")]
    public async Task InvalidAiReplyIsA502AndLeavesTheProjectUnchanged(string reply)
    {
        var id = await AddProject();
        _ai.Content = reply;
        var (status, body) = await Call(HttpMethod.Post, $"/api/projects/{id}/profile");
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("ai_invalid_response", body.ToJsonString());
        var (_, list) = await Call(HttpMethod.Get, "/api/projects");
        var project = list["data"]!["projects"]![0]!;
        Assert.Empty(project["needs"]!.AsArray());
        Assert.Empty(project["profileFiles"]!.AsArray());
        Assert.Null(project["profiledAt"]);
    }

    [Fact]
    public async Task FencedJsonReplyIsAccepted()
    {
        var id = await AddProject();
        _ai.Content = "```json\n" + ValidReply() + "\n```";
        var (status, body) = await Call(HttpMethod.Post, $"/api/projects/{id}/profile", new JsonObject { ["lang"] = "en" });
        Assert.True(status == HttpStatusCode.OK, body.ToJsonString());
        Assert.Equal(6, body["data"]!["project"]!["needs"]!.AsArray().Count);
        Assert.Contains("English", Assert.Single(_ai.Bodies));
    }

    [Fact]
    public async Task UnknownProjectAndMissingKeyFailBeforeReadingOrSending()
    {
        var (missing, _) = await Call(HttpMethod.Post, "/api/projects/424242/profile");
        Assert.Equal(HttpStatusCode.NotFound, missing);

        var id = await AddProject();
        _host.Store.SetSetting("deepseek_api_key", "");
        var (status, body) = await Call(HttpMethod.Post, $"/api/projects/{id}/profile");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("ai_not_configured", body.ToJsonString());
        Assert.Empty(_ai.Bodies);

        var (getStatus, _) = await Call(HttpMethod.Get, $"/api/projects/{id}/profile");
        Assert.Equal(HttpStatusCode.NotFound, getStatus);
    }

    [Fact]
    public async Task ChatJsonAsyncParsesObjectsAndRejectsOtherReplies()
    {
        var client = new DeepSeekClient(getKey: () => "k", baseUrl: _ai.BaseUrl, model: "deepseek-chat");
        _ai.Content = "{\"ok\": true}";
        Assert.True((await client.ChatJsonAsync("Return json.", "x"))["ok"]!.GetValue<bool>());
        _ai.Content = "\"just a string\"";
        var ex = await Assert.ThrowsAsync<ServiceException>(() => client.ChatJsonAsync("Return json.", "x"));
        Assert.Equal("ai_invalid_response", ex.Code);
        Assert.Equal(502, ex.HttpStatus);
        var unconfigured = new DeepSeekClient(getKey: () => null, baseUrl: _ai.BaseUrl);
        Assert.Equal("ai_not_configured", (await Assert.ThrowsAsync<ServiceException>(() => unconfigured.ChatJsonAsync("json", "x"))).Code);
    }
}
