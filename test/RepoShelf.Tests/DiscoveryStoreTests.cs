using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RepoShelf.Core;

namespace RepoShelf.Tests;

public sealed class DiscoveryStoreTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "repo-shelf-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private FakeGitHubServer _upstream = null!;
    private ServiceHost _host = null!;
    private const string Token = "discovery-test-token";

    public async Task InitializeAsync()
    {
        _upstream = new FakeGitHubServer();
        _host = ServiceHost.Create(new Dictionary<string, string?>
        {
            ["REPO_SHELF_DATA_DIR"] = _dir,
            ["REPO_SHELF_PORT"] = FreePort().ToString(),
            ["REPO_SHELF_TOKEN"] = Token,
            ["REPO_SHELF_GITHUB_API"] = _upstream.BaseUrl,
            ["REPO_SHELF_DEEPSEEK_API"] = _upstream.BaseUrl,
        });
        _host.Store.SetSetting("github_token", "fake-github-token");
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
        _upstream.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<JsonObject> Call(HttpMethod method, string path, JsonNode? body = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task<JsonObject> Add(string name = "Example") =>
        (await Call(HttpMethod.Post, "/api/projects", new JsonObject { ["name"] = name, ["path"] = _dir }, HttpStatusCode.Created))["data"]!["project"]!.AsObject();

    private void Sql(string sql, params (string Key, object Value)[] parameters)
    {
        lock (_host.Store.Sync)
        {
            using var cmd = _host.Store.Conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value);
            cmd.ExecuteNonQuery();
        }
    }

    private long Count(string table)
    {
        lock (_host.Store.Sync)
        {
            using var cmd = _host.Store.Conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    private void Candidate(long projectId, long githubId, string state = "pending") => Sql("""
        INSERT INTO discovery_candidates (project_id, github_id, full_name, state, proposed_at)
        VALUES ($project, $github, 'owner/candidate', $state, '2026-09-28T00:00:00Z')
        """, ("$project", projectId), ("$github", githubId), ("$state", state));

    [Theory]
    [InlineData("projects", "id,name,path,paused,needs,queries,languages,dependencies,profile_files,profiled_at,created_at,updated_at")]
    [InlineData("discovery_candidates", "project_id,github_id,full_name,html_url,description,language,license_id,stars,stars_per_month,pushed_at,created_at_upstream,score,matched_need,cost,reason,reason_lang,model,state,dismiss_reason,run_id,proposed_at,decided_at")]
    [InlineData("discovery_runs", "id,status,trigger,started_at,finished_at,progress,error,new_candidates")]
    public void SchemaMatchesContract(string table, string columns)
    {
        lock (_host.Store.Sync)
        {
            using var cmd = _host.Store.Conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            var actual = new List<string>();
            while (reader.Read()) actual.Add(reader.GetString(1));
            Assert.Equal(columns.Split(','), actual);
        }
    }

    [Fact]
    public async Task ProjectsCrudIsLocalAndReturnsEditableProfileAndCounts()
    {
        var project = await Add("  Example  ");
        var id = project["id"]!.GetValue<long>();
        Assert.Equal("Example", project["name"]!.GetValue<string>());
        Assert.Equal(_dir, project["path"]!.GetValue<string>());
        Assert.False(project["paused"]!.GetValue<bool>());
        Assert.Null(project["profiledAt"]);
        foreach (var key in new[] { "needs", "queries", "languages", "dependencies", "profileFiles" })
            Assert.Empty(project[key]!.AsArray());
        foreach (var state in new[] { "pending", "later", "accepted", "dismissed" })
            Assert.Equal(0, project["counts"]![state]!.GetValue<long>());
        Assert.NotEmpty(project["createdAt"]!.GetValue<string>());

        var newPath = Directory.CreateDirectory(Path.Combine(_dir, "project")).FullName;
        var changes = new JsonObject
        {
            ["name"] = "Renamed", ["path"] = newPath, ["paused"] = true,
            ["needs"] = new JsonArray("中文搜索", "Offline storage"),
            ["queries"] = new JsonArray("sqlite search", "offline index"),
        };
        var edited = (await Call(HttpMethod.Put, $"/api/projects/{id}", changes))["data"]!["project"]!;
        foreach (var (key, value) in changes) Assert.True(JsonNode.DeepEquals(value, edited[key]));
        Assert.Equal(project["createdAt"]!.GetValue<string>(), edited["createdAt"]!.GetValue<string>());
        Assert.NotEqual(project["updatedAt"]!.GetValue<string>(), edited["updatedAt"]!.GetValue<string>());
        var listed = (await Call(HttpMethod.Get, "/api/projects"))["data"]!["projects"]!.AsArray();
        Assert.Single(listed);
        Assert.True(JsonNode.DeepEquals(edited, listed[0]));
        var partial = (await Call(HttpMethod.Put, $"/api/projects/{id}", new JsonObject { ["paused"] = false }))["data"]!["project"]!;
        Assert.Equal("Renamed", partial["name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(changes["needs"], partial["needs"]));

        Assert.True((await Call(HttpMethod.Delete, $"/api/projects/{id}"))["data"]!["deleted"]!.GetValue<bool>());
        Assert.Empty((await Call(HttpMethod.Get, "/api/projects"))["data"]!["projects"]!.AsArray());
        Assert.Empty(_upstream.Requests);
        Assert.Equal(0, Count("repos"));
        Assert.Equal(0, Count("discovery_runs"));
    }

    [Fact]
    public async Task CandidatesHavePerProjectIdentityCountsAndCascadeWithoutTouchingLibrary()
    {
        var a = (await Add("A"))["id"]!.GetValue<long>();
        var b = (await Add("B"))["id"]!.GetValue<long>();
        var states = new[] { "pending", "later", "accepted", "dismissed" };
        for (var i = 0; i < states.Length; i++) Candidate(a, i + 1, states[i]);
        Candidate(b, 1);
        Assert.Throws<SqliteException>(() => Candidate(a, 1));
        Assert.Throws<SqliteException>(() => Candidate(99999, 1));

        var repo = new RepoMeta(1, "owner", "saved", "owner/saved", "https://github.com/owner/saved", "kept",
            new(), "C#", "MIT", false, null, 10, "main");
        var (repoId, _, _) = _host.Repos.UpsertSource(repo, "searchable content", readmeProvided: true);
        _host.Repos.UpdateAnnotation(repoId, new JsonObject { ["reason"] = "my reason", ["notes"] = "my notes" });
        var before = _host.Repos.GetRepo(repoId)!.ToJsonString();
        var projects = (await Call(HttpMethod.Get, "/api/projects"))["data"]!["projects"]!.AsArray();
        var first = projects.Single(p => p!["id"]!.GetValue<long>() == a)!;
        foreach (var state in states) Assert.Equal(1, first["counts"]![state]!.GetValue<long>());
        Assert.Equal(1, _host.Search.Search(new()).Total);
        Assert.Equal(0, _host.Search.Search(new(Query: "candidate")).Total);

        await Call(HttpMethod.Delete, $"/api/projects/{a}");
        Assert.Equal(1, Count("discovery_candidates"));
        Assert.Equal(before, _host.Repos.GetRepo(repoId)!.ToJsonString());
        Assert.Equal(1, _host.Search.Search(new(Query: "searchable")).Total);
        Assert.Throws<SqliteException>(() => Candidate(a, 5));
    }

    [Fact]
    public async Task DuplicateNamesAndUnknownProjectsUseServiceErrors()
    {
        var a = (await Add())["id"]!.GetValue<long>();
        var b = (await Add("Other"))["id"]!.GetValue<long>();
        var dupe = await Call(HttpMethod.Post, "/api/projects", new JsonObject { ["name"] = "Example", ["path"] = _dir }, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_field", dupe["error"]!["code"]!.GetValue<string>());
        var edit = await Call(HttpMethod.Put, $"/api/projects/{b}", new JsonObject { ["name"] = "Example" }, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_field", edit["error"]!["code"]!.GetValue<string>());
        await Call(HttpMethod.Put, $"/api/projects/{a}", new JsonObject { ["name"] = "Example" });
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            var missing = await Call(method, "/api/projects/999999", method == HttpMethod.Put ? new JsonObject() : null, HttpStatusCode.NotFound);
            Assert.Equal("not_found", missing["error"]!["code"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("name", "null")]
    [InlineData("name", "42")]
    [InlineData("name", "\"  \"")]
    [InlineData("path", "null")]
    [InlineData("path", "[]")]
    [InlineData("path", "\"missing-project-folder\"")]
    [InlineData("paused", "\"false\"")]
    [InlineData("paused", "null")]
    [InlineData("needs", "\"text\"")]
    [InlineData("needs", "[1]")]
    [InlineData("needs", "[null]")]
    [InlineData("needs", "[\" \"]")]
    [InlineData("queries", "null")]
    [InlineData("queries", "[\"a\",\"b\",\"c\",\"d\",\"e\"]")]
    public async Task InvalidEditsAreAtomicAndReturn400(string field, string value)
    {
        var project = await Add();
        var changes = new JsonObject { ["name"] = "Do not save", [field] = JsonNode.Parse(value) };
        var error = await Call(HttpMethod.Put, $"/api/projects/{project["id"]}", changes, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_field", error["error"]!["code"]!.GetValue<string>());
        var listed = (await Call(HttpMethod.Get, "/api/projects"))["data"]!["projects"]![0];
        Assert.True(JsonNode.DeepEquals(project, listed));
    }

    [Fact]
    public async Task AddRequiresNameAndExistingDirectoryAndRejectsMalformedJson()
    {
        foreach (var body in new[]
        {
            new JsonObject(), new JsonObject { ["name"] = "Test" },
            new JsonObject { ["name"] = 1, ["path"] = _dir },
            new JsonObject { ["name"] = "Test", ["path"] = Path.Combine(_dir, "repo-shelf.db") },
        }) await Call(HttpMethod.Post, "/api/projects", body, HttpStatusCode.BadRequest);
        foreach (var text in new[] { "{", "[]", "null", "" })
        {
            using var response = await _http.PostAsync("/api/projects", new StringContent(text, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, Count("projects"));
        Assert.Empty(_upstream.Requests);
    }

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/discovery/runs/latest")]
    [InlineData("/api/repos/1/recommendations")]
    public async Task DiscoveryRoutesKeepAuthOriginAndHostChecks(string path)
    {
        using var http = new HttpClient { BaseAddress = _http.BaseAddress };
        using var noToken = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        http.DefaultRequestHeaders.Add("x-reposhelf-token", Token);
        http.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        using var foreign = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        http.DefaultRequestHeaders.Host = "foreign.example";
        using var badHost = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, badHost.StatusCode);
    }

    [Fact]
    public async Task SettingsAdvertiseDiscoveryAndUnimplementedRoutesStay404()
    {
        var settings = (await Call(HttpMethod.Get, "/api/settings"))["data"]!;
        Assert.True(settings["features"]!["discover"]!.GetValue<bool>());
        foreach (var path in new[] { "/api/projects/1/profile", "/api/discovery/runs", "/api/repos/1/recommendations", "/api/projects/999999999999999999999" })
            await Call(HttpMethod.Post, path, status: HttpStatusCode.NotFound);
    }

    [Fact]
    public void OpeningLegacyDatabaseIsAdditiveAndProjectDataSurvivesReopen()
    {
        var file = Path.Combine(_dir, "legacy.db");
        string library;
        using (var legacy = Store.Open(file))
        {
            var repos = new RepoService(legacy, _host.GitHub);
            var (id, _, _) = repos.UpsertSource(new RepoMeta(77, "owner", "legacy", "owner/legacy",
                "https://github.com/owner/legacy", "existing description", new(), "C#", "MIT", false, null, 1, "main"),
                "legacy search content", readmeProvided: true);
            repos.UpdateAnnotation(id, new JsonObject { ["reason"] = "keep this", ["notes"] = "private notes" });
            library = repos.GetRepo(id)!.ToJsonString();
            legacy.SetSetting("example", "preserved");
            using var cmd = legacy.Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO generated (repo_id, kind, content, model, lang, created_at)
                VALUES (1, 'summary', 'keep summary', 'fake', 'en', '2026-09-28');
                DROP TABLE discovery_candidates;
                DROP TABLE discovery_runs;
                DROP TABLE projects;
                """;
            cmd.ExecuteNonQuery();
        }

        long projectId;
        using (var upgraded = Store.Open(file))
        {
            var repos = new RepoService(upgraded, _host.GitHub);
            Assert.Equal(library, repos.GetRepo(1)!.ToJsonString());
            Assert.Equal(1, new SearchService(upgraded).Search(new(Query: "legacy")).Total);
            Assert.Equal("preserved", upgraded.GetSetting("example"));
            using var cmd = upgraded.Conn.CreateCommand();
            cmd.CommandText = "SELECT content FROM generated WHERE repo_id = 1";
            Assert.Equal("keep summary", cmd.ExecuteScalar());
            var discovery = new DiscoveryStore(upgraded);
            projectId = discovery.AddProject(new JsonObject { ["name"] = "Persistent", ["path"] = _dir })["id"]!.GetValue<long>();
            discovery.UpdateProject(projectId, new JsonObject { ["needs"] = new JsonArray("persistent need"), ["queries"] = new JsonArray("search") });
            cmd.CommandText = """
                UPDATE projects SET languages = '["C#"]', dependencies = '["sqlite"]',
                  profile_files = '["README.md"]', profiled_at = '2026-09-28' WHERE id = $id;
                INSERT INTO discovery_candidates (project_id, github_id, proposed_at) VALUES ($id, 77, '2026-09-28');
                INSERT INTO discovery_runs (id, status, trigger, progress) VALUES ('run-1', 'done', 'manual', '{"projects":1}');
                """;
            cmd.Parameters.AddWithValue("$id", projectId);
            cmd.ExecuteNonQuery();
        }
        using var reopened = Store.Open(file);
        var store = new DiscoveryStore(reopened);
        var saved = store.GetProject(projectId)!;
        Assert.Equal("Persistent", saved["name"]!.GetValue<string>());
        Assert.Equal("persistent need", saved["needs"]![0]!.GetValue<string>());
        Assert.Equal("C#", saved["languages"]![0]!.GetValue<string>());
        Assert.Equal("sqlite", saved["dependencies"]![0]!.GetValue<string>());
        Assert.Equal("README.md", saved["profileFiles"]![0]!.GetValue<string>());
        Assert.Equal("2026-09-28", saved["profiledAt"]!.GetValue<string>());
        Assert.Equal(1, saved["counts"]!["pending"]!.GetValue<long>());
        store.UpdateProject(projectId, new JsonObject { ["paused"] = true });
        Assert.True(JsonNode.DeepEquals(saved["profileFiles"], store.GetProject(projectId)!["profileFiles"]));
        using var run = reopened.Conn.CreateCommand();
        run.CommandText = "SELECT new_candidates FROM discovery_runs WHERE id = 'run-1'";
        Assert.Equal(0L, run.ExecuteScalar());
        Assert.Null(store.GetProject(9999));
    }

    [Fact]
    public async Task MissingProjectDirectoryCanStillBePausedAndUnregistered()
    {
        var path = Directory.CreateDirectory(Path.Combine(_dir, "removed-project")).FullName;
        var created = await Call(HttpMethod.Post, "/api/projects", new JsonObject { ["name"] = "Offline", ["path"] = path }, HttpStatusCode.Created);
        var id = created["data"]!["project"]!["id"]!.GetValue<long>();
        Directory.Delete(path);
        await Call(HttpMethod.Put, $"/api/projects/{id}", new JsonObject { ["paused"] = true });
        await Call(HttpMethod.Delete, $"/api/projects/{id}");
    }
}

/// <summary>Exercises the same registration contract used by later discovery modules.</summary>
internal static class DiscoveryTestModule
{
    internal const string Token = "discovery-extension-test";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        DiscoveryRoutes.Register(host =>
        {
            var probe = host.Feature(h => new DiscoveryProbe(h));
            probe.FactoryCalls++;
            return probe;
        });
        DiscoveryRoutes.RegisterStartHook(host =>
        {
            if (host.PairingToken != Token) return;
            var probe = host.Feature(h => new DiscoveryProbe(h));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = http.GetAsync(host.BaseUrl + "/api/health").GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            probe.StartedWithHttpListening = true;
        });
    }
}

internal sealed class DiscoveryProbe(ServiceHost host) : IDiscoveryRoutes, IDisposable
{
    public int FactoryCalls { get; set; }
    public bool StartedWithHttpListening { get; set; }
    public bool DisposedWithStoreOpen { get; private set; }
    public int DisposeCalls { get; private set; }

    public async Task<bool> TryHandleAsync(Microsoft.AspNetCore.Http.HttpContext ctx, string method, string path)
    {
        if (host.PairingToken != DiscoveryTestModule.Token) return false;
        if (path == "/api/discovery/probe-error") throw new ServiceException("invalid_field", 400, "Probe error");
        if (method != "GET" || path is not ("/api/projects/0/probe" or "/api/discovery/probe" or "/api/repos/0/recommendations")) return false;
        await DiscoveryApi.SendDataAsync(ctx, new JsonObject { ["factoryCalls"] = FactoryCalls });
        return true;
    }

    public void Dispose()
    {
        DisposeCalls++;
        _ = host.Store.GetSetting("pairing_token");
        DisposedWithStoreOpen = true;
    }
}

public sealed class DiscoveryExtensionTests
{
    [Fact]
    public async Task ModulesRouteThroughAuthAndAreSingleInstancesPerHostWithOwnedLifetime()
    {
        var dirs = Enumerable.Range(0, 2).Select(_ => Path.Combine(Path.GetTempPath(), "repo-shelf-features-" + Guid.NewGuid().ToString("N"))).ToArray();
        ServiceHost Create(int index)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ServiceHost.Create(new Dictionary<string, string?>
            {
                ["REPO_SHELF_DATA_DIR"] = dirs[index],
                ["REPO_SHELF_PORT"] = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(),
                ["REPO_SHELF_TOKEN"] = DiscoveryTestModule.Token,
            });
        }
        using var first = Create(0);
        using var second = Create(1);
        try
        {
            var creations = 0;
            var features = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => first.Feature(h =>
            {
                Interlocked.Increment(ref creations);
                return new DiscoveryProbe(h);
            }))));
            Assert.Equal(1, creations);
            Assert.All(features, feature => Assert.Same(features[0], feature));
            var probe = features[0];
            Assert.NotSame(probe, second.Feature(h => new DiscoveryProbe(h)));
            Assert.False(probe.StartedWithHttpListening);
            await first.StartAsync();
            Assert.True(probe.StartedWithHttpListening);
            Assert.False(second.Feature(h => new DiscoveryProbe(h)).StartedWithHttpListening);

            using var http = new HttpClient { BaseAddress = new Uri(first.BaseUrl) };
            using var unauthenticated = await http.GetAsync("/api/discovery/probe");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            http.DefaultRequestHeaders.Add("x-reposhelf-token", DiscoveryTestModule.Token);
            foreach (var path in new[] { "/api/projects/0/probe", "/api/discovery/probe", "/api/repos/0/recommendations", "/api/discovery/probe" })
            {
                var response = JsonNode.Parse(await http.GetStringAsync(path))!;
                Assert.Equal(1, response["data"]!["factoryCalls"]!.GetValue<int>());
            }
            using var error = await http.GetAsync("/api/discovery/probe-error");
            Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
            Assert.Contains("invalid_field", await error.Content.ReadAsStringAsync());
            using var unknown = await http.GetAsync("/api/discovery/unknown");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using var unrelated = await http.GetAsync("/api/projects-other");
            Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
            Assert.Equal(1, probe.FactoryCalls);
            first.Dispose();
            first.Dispose();
            Assert.Equal(1, probe.DisposeCalls);
            Assert.True(probe.DisposedWithStoreOpen);
            Assert.Throws<ObjectDisposedException>(() => first.Feature(h => new DiscoveryProbe(h)));
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var dir in dirs) Directory.Delete(dir, recursive: true);
        }
    }
}
