using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RepoShelf.Core;

namespace RepoShelf.Tests;

public sealed class DiscoveryTriageTests : IAsyncLifetime
{
    private const string Token = "triage-test-token";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "repo-shelf-triage-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private FakeGitHubServer _github = null!;
    private ServiceHost _host = null!;

    public async Task InitializeAsync()
    {
        _github = new FakeGitHubServer();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _host = ServiceHost.Create(new Dictionary<string, string?>
        {
            ["REPO_SHELF_DATA_DIR"] = _dir, ["REPO_SHELF_PORT"] = port.ToString(),
            ["REPO_SHELF_TOKEN"] = Token, ["REPO_SHELF_GITHUB_API"] = _github.BaseUrl,
        });
        await _host.StartAsync();
        _http.BaseAddress = new Uri(_host.BaseUrl);
        _http.DefaultRequestHeaders.Add("x-reposhelf-token", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _github.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
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

    private long Project(string name = "Example") => new DiscoveryStore(_host.Store)
        .AddProject(new JsonObject { ["name"] = name, ["path"] = _dir })["id"]!.GetValue<long>();

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

    private JsonObject Candidate(long project, long githubId = 700, string state = "pending")
    {
        var raw = _github.AddRepo(TestRepos.MakeRepo(githubId, name: "triage" + githubId), "Saved README content");
        Sql("""
            INSERT INTO discovery_candidates (project_id, github_id, full_name, html_url,
              description, score, matched_need, cost, reason, reason_lang, model, state, proposed_at)
            VALUES ($project, $github, $name, $url, 'candidate only', 90, 'search', 'low',
              'AI rationale only', 'en', 'fake-model', $state, '2026-09-28T00:00:00Z')
            """, ("$project", project), ("$github", githubId), ("$name", raw["full_name"]!.GetValue<string>()),
            ("$url", raw["html_url"]!.GetValue<string>()), ("$state", state));
        return raw;
    }

    private async Task<JsonObject> GetCandidate(long project, long githubId = 700)
    {
        var data = await Call(HttpMethod.Get, $"/api/discovery/candidates?state=pending,later,accepted,dismissed&project={project}");
        return data["data"]!["candidates"]!.AsArray().Single(c => c!["githubId"]!.GetValue<long>() == githubId)!.AsObject();
    }

    private async Task<JsonObject> Accept(long project, long githubId = 700) =>
        (await Call(HttpMethod.Post, $"/api/discovery/candidates/{project}/{githubId}/accept"))["data"]!["repo"]!.AsObject();

    [Fact]
    public async Task AcceptUsesSavePathCreatesOneLibraryRepoAndKeepsAiOutOfPersonalFields()
    {
        var project = Project();
        Candidate(project);
        var pending = (await Call(HttpMethod.Get, "/api/repos?q=triage700"))["data"]!;
        Assert.Equal(0, pending["total"]!.GetValue<long>());
        Assert.Equal(0, Count("repos"));
        Assert.Equal(0, Count("annotations"));
        Assert.Equal(0, Count("search_fts"));

        var repo = await Accept(project);
        var id = repo["id"]!.GetValue<long>();
        Assert.Equal(700, repo["githubId"]!.GetValue<long>());
        Assert.Equal("Saved README content", repo["readme"]!.GetValue<string>());
        Assert.Equal("A sample repository", repo["description"]!.GetValue<string>());
        Assert.Equal("to_investigate", repo["annotation"]!["status"]!.GetValue<string>());
        Assert.Equal(new[] { "Example" }, repo["annotation"]!["projects"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("", repo["annotation"]!["reason"]!.GetValue<string>());
        Assert.Equal("", repo["annotation"]!["notes"]!.GetValue<string>());
        Assert.Equal(new[] { "/repos/octocat/triage700", "/repos/octocat/triage700/readme" }, _github.Requests);
        var accepted = await GetCandidate(project);
        Assert.Equal("accepted", accepted["state"]!.GetValue<string>());
        Assert.NotNull(accepted["decidedAt"]);
        Assert.Equal("AI rationale only", accepted["reason"]!.GetValue<string>());

        _host.Repos.UpdateAnnotation(id, new JsonObject { ["status"] = "adopted", ["notes"] = "my later notes" });
        var repeated = await Accept(project);
        Assert.Equal(id, repeated["id"]!.GetValue<long>());
        Assert.Equal("adopted", repeated["annotation"]!["status"]!.GetValue<string>());
        Assert.Equal("my later notes", repeated["annotation"]!["notes"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(accepted, await GetCandidate(project)));
        Assert.Equal(2, _github.Requests.Count);
        foreach (var table in new[] { "repos", "annotations", "search_fts" }) Assert.Equal(1, Count(table));
        Assert.Equal(0, Count("generated"));
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/repos?q=triage700&project=Example&status=adopted"))["data"]!["total"]!.GetValue<long>());
        Assert.Equal(0, (await Call(HttpMethod.Get, "/api/repos?q=rationale"))["data"]!["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task ConcurrentAcceptsMergeProjectLabelsWithoutDuplicatingRepoOrNetwork()
    {
        var a = Project("A");
        var b = Project("B");
        Candidate(a);
        Candidate(b);
        var results = await Task.WhenAll(Accept(a), Accept(b), Accept(a));
        Assert.Single(results.Select(r => r["id"]!.GetValue<long>()).Distinct());
        var repo = _host.Repos.GetRepo(results[0]["id"]!.GetValue<long>())!;
        Assert.Equal(new[] { "A", "B" }, repo["annotation"]!["projects"]!.AsArray().Select(p => p!.GetValue<string>()).Order());
        Assert.Equal(1, Count("repos"));
        Assert.Equal(2, _github.Requests.Count);
        Assert.Equal("accepted", (await GetCandidate(a))["state"]!.GetValue<string>());
        Assert.Equal("accepted", (await GetCandidate(b))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task AcceptExistingRenamedRepoPreservesPersonalFieldsAndOtherLabelsWithoutRefresh()
    {
        var project = Project();
        var raw = Candidate(project);
        var moved = _github.MoveRepo(raw, "new-owner", "new-name");
        var saved = await _host.Repos.SaveRepoAsync(moved["html_url"]!.GetValue<string>());
        var id = saved["repo"]!["id"]!.GetValue<long>();
        _host.Repos.UpdateAnnotation(id, new JsonObject
        {
            ["reason"] = "my reason", ["notes"] = "my notes", ["tags"] = new JsonArray("manual"),
            ["projects"] = new JsonArray("Existing"), ["status"] = "adopted",
        });
        _github.Requests.Clear();
        var repo = await Accept(project);
        Assert.Equal(id, repo["id"]!.GetValue<long>());
        Assert.Equal("new-owner/new-name", repo["fullName"]!.GetValue<string>());
        Assert.Equal("my reason", repo["annotation"]!["reason"]!.GetValue<string>());
        Assert.Equal("my notes", repo["annotation"]!["notes"]!.GetValue<string>());
        Assert.Equal("manual", repo["annotation"]!["tags"]![0]!.GetValue<string>());
        // The owner already judged this repository; accepting must not reset it.
        Assert.Equal("adopted", repo["annotation"]!["status"]!.GetValue<string>());
        Assert.Equal(new[] { "Existing", "Example" }, repo["annotation"]!["projects"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task LaterOnlyChangesCandidateAndCanBeAccepted()
    {
        var project = Project();
        Candidate(project);
        var result = await Call(HttpMethod.Post, $"/api/discovery/candidates/{project}/700/later");
        Assert.Equal("later", result["data"]!["candidate"]!["state"]!.GetValue<string>());
        Assert.NotNull(result["data"]!["candidate"]!["decidedAt"]);
        Assert.Equal(0, Count("repos"));
        Assert.Empty(_github.Requests);
        Assert.Empty((await Call(HttpMethod.Get, "/api/discovery/candidates"))["data"]!["candidates"]!.AsArray());
        Assert.Single((await Call(HttpMethod.Get, "/api/discovery/candidates?state=later"))["data"]!["candidates"]!.AsArray());
        await Accept(project);
        Assert.Equal("accepted", (await GetCandidate(project))["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not_relevant")]
    [InlineData("too_heavy")]
    [InlineData("already_have")]
    [InlineData("low_quality")]
    public async Task DismissStoresOptionalReasonWithoutSaving(string? reason)
    {
        var project = Project();
        Candidate(project);
        var result = await Call(HttpMethod.Post, $"/api/discovery/candidates/{project}/700/dismiss",
            reason is null ? null : new JsonObject { ["reason"] = reason });
        var candidate = result["data"]!["candidate"]!;
        Assert.Equal("dismissed", candidate["state"]!.GetValue<string>());
        Assert.Equal(reason, candidate["dismissReason"]?.GetValue<string>());
        Assert.NotNull(candidate["decidedAt"]);
        Assert.Equal(0, Count("repos"));
        Assert.Empty(_github.Requests);
    }

    [Theory]
    [InlineData("\"anything\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task InvalidDismissReasonDoesNotChangeCandidate(string json)
    {
        var project = Project();
        Candidate(project);
        var before = await GetCandidate(project);
        var error = await Call(HttpMethod.Post, $"/api/discovery/candidates/{project}/700/dismiss",
            new JsonObject { ["reason"] = JsonNode.Parse(json) }, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_field", error["error"]!["code"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(before, await GetCandidate(project)));
        Assert.Empty(_github.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reason\":null}")]
    public async Task DismissAcceptsEmptyObjectOrNullReasonAndLaterClearsIt(string json)
    {
        var project = Project();
        Candidate(project);
        var path = $"/api/discovery/candidates/{project}/700";
        await Call(HttpMethod.Post, path + "/dismiss", JsonNode.Parse(json));
        Assert.Null((await GetCandidate(project))["dismissReason"]);
        await Call(HttpMethod.Post, path + "/dismiss", new JsonObject { ["reason"] = "low_quality" });
        await Call(HttpMethod.Post, path + "/later");
        Assert.Null((await GetCandidate(project))["dismissReason"]);
        await Accept(project);
        Assert.Null((await GetCandidate(project))["dismissReason"]);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task MalformedDismissBodyUsesJsonErrorAndLeavesCandidatePending(string json)
    {
        var project = Project();
        Candidate(project);
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"/api/discovery/candidates/{project}/700/dismiss", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("invalid_json", error["error"]!["code"]!.GetValue<string>());
        Assert.Equal("pending", (await GetCandidate(project))["state"]!.GetValue<string>());
        Assert.Null((await GetCandidate(project))["decidedAt"]);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task InvalidProjectLabelFailsBeforeSaveAndDecision()
    {
        var project = Project(new string('x', 101));
        Candidate(project);
        var error = await Call(HttpMethod.Post, $"/api/discovery/candidates/{project}/700/accept", status: HttpStatusCode.BadRequest);
        Assert.Equal("invalid_field", error["error"]!["code"]!.GetValue<string>());
        Assert.Equal("pending", (await GetCandidate(project))["state"]!.GetValue<string>());
        Assert.Equal(0, Count("repos"));
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task DismissedRepoIsExcludedFromNextRunForThatProjectOnly()
    {
        var a = Project("A");
        var b = Project("B");
        var raw = Candidate(a);
        await Call(HttpMethod.Post, $"/api/discovery/candidates/{a}/700/dismiss", new JsonObject { ["reason"] = "too_heavy" });
        Sql("UPDATE projects SET needs = '[\"search\"]', queries = '[\"lookup\"]', profiled_at = '2026-09-28T00:00:00Z'");
        _github.SetSearch("lookup pushed:>=2026-04-01 archived:false fork:false", new[] { raw });
        using var ai = new ScriptedAiServer { Reply = _ => """{"score":90,"matchedNeed":"search","cost":"low","reason":"fit"}""" };
        using var runner = new DiscoveryRunner(_host.Store, _host.GitHub, new DeepSeekClient(_http, () => "fake-key", ai.BaseUrl),
            clock: () => DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var run = runner.Start();
        await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("done", runner.GetRun(run["id"]!.GetValue<string>())!["status"]!.GetValue<string>());
        Assert.Equal("dismissed", (await GetCandidate(a))["state"]!.GetValue<string>());
        Assert.Equal("too_heavy", (await GetCandidate(a))["dismissReason"]!.GetValue<string>());
        Assert.Equal("pending", (await GetCandidate(b))["state"]!.GetValue<string>());
        Assert.Single(ai.Prompts);
        Assert.Single(_github.Requests, r => r.EndsWith("/readme"));
        Assert.Equal(0, (await Call(HttpMethod.Get, "/api/repos?q=triage700"))["data"]!["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task FailedSaveLeavesDecisionPendingAndCanBeRetried()
    {
        var project = Project();
        Candidate(project);
        _github.FailNext(p => p == "/repos/octocat/triage700", 1, HttpStatusCode.NotFound);
        var error = await Call(HttpMethod.Post, $"/api/discovery/candidates/{project}/700/accept", status: HttpStatusCode.NotFound);
        Assert.Equal("repository_not_found", error["error"]!["code"]!.GetValue<string>());
        Assert.Equal("pending", (await GetCandidate(project))["state"]!.GetValue<string>());
        Assert.Null((await GetCandidate(project))["decidedAt"]);
        Assert.Equal(0, Count("repos"));
        await Accept(project);
        Assert.Equal(1, Count("repos"));
    }

    [Fact]
    public async Task RecommendationsResolveLibraryIdToGithubIdAcrossAllProjectsAndStates()
    {
        var states = new[] { "pending", "later", "accepted", "dismissed" };
        foreach (var state in states) Candidate(Project(state), state: state);
        Candidate(Project("unrelated"), githubId: 701);
        var repo = (await _host.Repos.SaveRepoAsync("https://github.com/octocat/triage700"))["repo"]!;
        var id = repo["id"]!.GetValue<long>();
        Assert.NotEqual(700, id);
        _github.Requests.Clear();
        var rows = (await Call(HttpMethod.Get, $"/api/repos/{id}/recommendations"))["data"]!["recommendations"]!.AsArray();
        Assert.Equal(4, rows.Count);
        Assert.Equal(states.Order(), rows.Select(r => r!["state"]!.GetValue<string>()).Order());
        Assert.All(rows, r =>
        {
            Assert.Equal(700, r!["githubId"]!.GetValue<long>());
            Assert.Equal(r["state"]!.GetValue<string>(), r["projectName"]!.GetValue<string>());
            Assert.Equal("AI rationale only", r["reason"]!.GetValue<string>());
            Assert.Equal("fake-model", r["model"]!.GetValue<string>());
        });
        await Call(HttpMethod.Get, "/api/repos/9999/recommendations", status: HttpStatusCode.NotFound);
        await Call(HttpMethod.Get, "/api/repos/999999999999999999999/recommendations", status: HttpStatusCode.NotFound);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task ManuallySavedRepoWithoutCandidatesHasEmptyRecommendations()
    {
        var raw = _github.AddRepo(TestRepos.MakeRepo(800));
        var repo = (await _host.Repos.SaveRepoAsync(raw["html_url"]!.GetValue<string>()))["repo"]!;
        _github.Requests.Clear();
        var result = await Call(HttpMethod.Get, $"/api/repos/{repo["id"]}/recommendations");
        Assert.Empty(result["data"]!["recommendations"]!.AsArray());
        Assert.Empty(_github.Requests);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("later")]
    [InlineData("dismiss")]
    public async Task TriageRequiresAuthAndLocalOriginAndReturns404ForMissingCandidate(string action)
    {
        var project = Project();
        Candidate(project);
        var path = $"/api/discovery/candidates/{project}/700/{action}";
        using var http = new HttpClient { BaseAddress = _http.BaseAddress };
        using var noToken = await http.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        http.DefaultRequestHeaders.Add("x-reposhelf-token", Token);
        http.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        using var foreign = await http.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        foreach (var missing in new[] { $"{project}/9999", "9999/700", "999999999999999999999/700" })
            await Call(HttpMethod.Post, $"/api/discovery/candidates/{missing}/{action}", status: HttpStatusCode.NotFound);
        await Call(HttpMethod.Get, path, status: HttpStatusCode.NotFound);
        Assert.Equal("pending", (await GetCandidate(project))["state"]!.GetValue<string>());
        Assert.Empty(_github.Requests);
    }
}
