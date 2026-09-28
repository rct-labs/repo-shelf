using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using RepoShelf.Core;

namespace RepoShelf.Tests;

public sealed class DiscoveryRecallTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");

    private static RepoMeta Meta(JsonObject raw) => RepoMeta.FromJson(JsonSerializer.SerializeToElement(raw));
    private static string Query(string keywords, string? language = "C#") =>
        keywords + (language is null ? "" : $" language:{language}") + " pushed:>=2026-04-01 archived:false fork:false";

    private static JsonObject Project(Store store, params string[] queries)
    {
        var project = new DiscoveryStore(store).AddProject(new JsonObject
        {
            ["name"] = Guid.NewGuid().ToString("N"), ["path"] = Path.GetTempPath(),
        });
        project["queries"] = new JsonArray(queries.Select(q => JsonValue.Create(q)).ToArray());
        project["languages"] = new JsonArray("C#", "Python");
        return project;
    }

    private static void Candidate(Store store, long projectId, long githubId, string state)
    {
        lock (store.Sync)
        {
            using var cmd = store.Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO discovery_candidates (project_id, github_id, state, proposed_at)
                VALUES ($project, $github, $state, '2026-09-28T00:00:00Z')
                """;
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$github", githubId);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.ExecuteNonQuery();
        }
    }

    private static async Task<List<RecalledRepo>> Collect(IAsyncEnumerable<RecalledRepo> source)
    {
        var result = new List<RecalledRepo>();
        await foreach (var item in source) result.Add(item);
        return result;
    }

    [Fact]
    public async Task SearchUsesEncodedQueryAndOnePageOfMetadata()
    {
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var client = new GitHubClient(http, baseUrl: fake.BaseUrl);
        const string query = "sqlite & search language:C# pushed:>=2026-04-01 archived:false fork:false";
        var raw = TestRepos.MakeRepo(17, name: "search", createdAt: "2026-06-01T00:00:00Z", fork: true);
        fake.SetSearch(query, new[] { raw, TestRepos.MakeRepo(18) });

        var result = await client.SearchReposAsync(query, 1);

        var repo = Assert.Single(result);
        Assert.Equal(17, repo.GithubId);
        Assert.Equal("2026-06-01T00:00:00Z", repo.CreatedAt);
        Assert.True(repo.Fork);
        Assert.Equal("MIT", repo.LicenseId);
        Assert.Equal("octocat/search", repo.FullName);
        var request = Assert.Single(fake.Requests);
        Assert.StartsWith("/search/repositories?", request);
        var parameters = QueryHelpers.ParseQuery(new Uri(fake.BaseUrl + request).Query);
        Assert.Equal(query, parameters["q"].ToString());
        Assert.Equal("stars", parameters["sort"].ToString());
        Assert.Equal("desc", parameters["order"].ToString());
        Assert.Equal("1", parameters["per_page"].ToString());
        Assert.False(parameters.ContainsKey("page"));
    }

    [Fact]
    public async Task SearchDoesNotRetryTransientFailuresAndSpendExtraQuota()
    {
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var client = new GitHubClient(http, baseUrl: fake.BaseUrl);
        fake.FailNext(_ => true, 1, HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.SearchReposAsync("search", 30));

        Assert.True(error.Retryable);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public void RankingUsesMonthlyGrowthWithOneMonthFloorAndStableTies()
    {
        RepoMeta Repo(long id, long stars, double ageDays) => Meta(TestRepos.MakeRepo(id,
            stars: stars, createdAt: Now.AddDays(-ageDays).ToString("O")));
        var repos = new[] { Repo(1, 1000, 300), Repo(2, 400, 60), Repo(4, 200, 1), Repo(3, 200, 30), Repo(5, 50, -1) };

        var ranked = DiscoveryRecall.Rank(repos, Now, 10);

        Assert.Equal(new long[] { 2, 3, 4, 1, 5 }, ranked.Select(x => x.Repo.GithubId));
        Assert.Equal(new double[] { 200, 200, 200, 100, 50 }, ranked.Select(x => x.StarsPerMonth));
        Assert.Equal(new long[] { 2, 3 }, DiscoveryRecall.Rank(repos, Now, 2).Select(x => x.Repo.GithubId));
    }

    [Fact]
    public void RankingDropsUnlicensedStaleArchivedForkedAndUnrankableMetadata()
    {
        var good = Meta(TestRepos.MakeRepo(1, pushedAt: Now.AddDays(-180).ToString("O")));
        var repos = new[]
        {
            good, good,
            good with { GithubId = 2, LicenseId = null },
            good with { GithubId = 3, LicenseId = " " },
            good with { GithubId = 4, PushedAt = Now.AddDays(-180).AddSeconds(-1).ToString("O") },
            good with { GithubId = 5, PushedAt = null },
            good with { GithubId = 6, PushedAt = "invalid" },
            good with { GithubId = 7, Archived = true },
            good with { GithubId = 8, Fork = true },
            good with { GithubId = 9, CreatedAt = null },
            good with { GithubId = 10, CreatedAt = "invalid" },
        };

        Assert.Equal(1, Assert.Single(DiscoveryRecall.Rank(repos, Now, 10)).Repo.GithubId);
    }

    [Fact]
    public async Task DefaultBudgetCapsSearchResultsAndFetchesReadmesOnlyForUniqueTopTen()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var client = new GitHubClient(http, baseUrl: fake.BaseUrl);
        var queries = Enumerable.Range(1, 6).Select(i => $"query{i}").ToArray();
        var project = Project(store, queries);
        var repos = Enumerable.Range(1, 40).Select(i => fake.AddRepo(
            TestRepos.MakeRepo(i, name: $"repo{i}", stars: i), $"README {i}")).ToList();
        foreach (var query in queries) fake.SetSearch(Query(query), repos);

        var result = await Collect(new DiscoveryRecall(store, client, clock: () => Now).RecallAsync(project));

        Assert.Equal(Enumerable.Range(21, 10).Reverse().Select(i => (long)i), result.Select(x => x.Repo.GithubId));
        Assert.All(result, x => Assert.Equal($"README {x.Repo.GithubId}", x.Readme));
        Assert.Equal(14, fake.Requests.Count);
        Assert.All(fake.Requests.Take(4), request =>
        {
            Assert.StartsWith("/search/repositories?", request);
            var parameters = QueryHelpers.ParseQuery(new Uri(fake.BaseUrl + request).Query);
            Assert.Equal("30", parameters["per_page"].ToString());
            Assert.Contains("language:C#", parameters["q"].ToString());
            Assert.DoesNotContain("Python", parameters["q"].ToString());
        });
        Assert.Equal(result.Select(x => $"/repos/{x.Repo.FullName}/readme"), fake.Requests.Skip(4));
    }

    [Fact]
    public async Task SavedIdentityAndEveryExistingProjectStateAreExcludedBeforeAnyRepoRequest()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var client = new GitHubClient(http, baseUrl: fake.BaseUrl);
        var project = Project(store, "search");
        var other = Project(store, "other");
        var repos = Enumerable.Range(1, 6).Select(i => fake.AddRepo(TestRepos.MakeRepo(i, name: $"new{i}"), "README")).ToList();
        new RepoService(store, client).UpsertSource(Meta(TestRepos.MakeRepo(1, name: "old-name")));
        var states = new[] { "pending", "later", "accepted", "dismissed" };
        for (var i = 0; i < states.Length; i++) Candidate(store, project["id"]!.GetValue<long>(), i + 2, states[i]);
        Candidate(store, other["id"]!.GetValue<long>(), 6, "dismissed");
        fake.SetSearch(Query("search"), repos);

        var result = await Collect(new DiscoveryRecall(store, client, clock: () => Now).RecallAsync(project));

        Assert.Equal(6, Assert.Single(result).Repo.GithubId);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("/repos/octocat/new6/readme", fake.Requests[1]);
    }

    [Fact]
    public async Task BudgetCanBeReducedAndLanguageQualifierIsOptional()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var project = Project(store, "one", "two", "three");
        project["languages"] = new JsonArray();
        foreach (var query in new[] { "one", "two", "three" })
            fake.SetSearch(Query(query, null), Enumerable.Range(1, 5).Select(i => TestRepos.MakeRepo(i, stars: i)));
        var recall = new DiscoveryRecall(store, new GitHubClient(http, baseUrl: fake.BaseUrl),
            new DiscoveryBudget(2, 3, 1), () => Now);

        var result = await Collect(recall.RecallAsync(project));

        Assert.Equal(3, Assert.Single(result).Repo.GithubId);
        Assert.Null(result[0].Readme); // A missing README is still a valid metadata candidate.
        Assert.Equal(3, fake.Requests.Count);
        Assert.All(fake.Requests.Take(2), request => Assert.Contains("per_page=3", request));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task SearchRateLimitStopsImmediatelyAndPreservesResetMetadata(int status)
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var project = Project(store, "one", "two", "three");
        fake.SetSearch(Query("one"), new[] { TestRepos.MakeRepo(1) });
        var reset = Now.AddHours(1);
        fake.FailNext(path => path.Contains("q=two"), 1, (HttpStatusCode)status,
            new() { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = reset.ToUnixTimeSeconds().ToString() });
        var recall = new DiscoveryRecall(store, new GitHubClient(http, baseUrl: fake.BaseUrl), clock: () => Now);

        var error = await Assert.ThrowsAsync<GitHubException>(() => Collect(recall.RecallAsync(project)));

        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(reset, error.RateLimitResetAt);
        Assert.Contains(reset.ToString("O"), error.Message);
        Assert.Equal(2, fake.Requests.Count);
        Assert.All(fake.Requests, request => Assert.StartsWith("/search/repositories?", request));
    }

    [Fact]
    public async Task ReadmeRateLimitKeepsAlreadyYieldedItemsAndStopsBeforeTheNextRepo()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var project = Project(store, "search");
        var repos = Enumerable.Range(1, 3).Select(i => fake.AddRepo(TestRepos.MakeRepo(i, name: $"repo{i}"), "README")).ToList();
        fake.SetSearch(Query("search"), repos);
        var reset = Now.AddHours(1);
        fake.FailNext(path => path == "/repos/octocat/repo2/readme", 1, HttpStatusCode.Forbidden,
            new() { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = reset.ToUnixTimeSeconds().ToString() });
        var recall = new DiscoveryRecall(store, new GitHubClient(http, baseUrl: fake.BaseUrl), clock: () => Now);
        var received = new List<RecalledRepo>();

        var error = await Assert.ThrowsAsync<GitHubException>(async () =>
        {
            await foreach (var item in recall.RecallAsync(project))
            {
                received.Add(item);
                Candidate(store, project["id"]!.GetValue<long>(), item.Repo.GithubId, "pending");
            }
        });

        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(reset, error.RateLimitResetAt);
        Assert.Equal(1, Assert.Single(received).Repo.GithubId);
        Assert.Equal(1, new DiscoveryStore(store).GetProject(project["id"]!.GetValue<long>())!["counts"]!["pending"]!.GetValue<long>());
        Assert.Equal(3, fake.Requests.Count);
        Assert.DoesNotContain("/repos/octocat/repo3/readme", fake.Requests);
    }

    [Fact]
    public async Task ReadmeTransientFailureDoesNotSpendExtraBudget()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var project = Project(store, "search");
        fake.SetSearch(Query("search"), new[] { TestRepos.MakeRepo(1) });
        fake.FailNext(path => path.EndsWith("/readme"), 1, HttpStatusCode.ServiceUnavailable);
        var recall = new DiscoveryRecall(store, new GitHubClient(http, baseUrl: fake.BaseUrl), clock: () => Now);

        await Assert.ThrowsAsync<GitHubException>(() => Collect(recall.RecallAsync(project)));

        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task EmptyQueriesAndCancellationSendNoRequests()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var recall = new DiscoveryRecall(store, new GitHubClient(http, baseUrl: fake.BaseUrl), clock: () => Now);

        Assert.Empty(await Collect(recall.RecallAsync(Project(store, "", " "))));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(recall.RecallAsync(Project(store, "search"), cancel.Token)));

        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task MultiWordLanguageStaysOneQualifier()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var project = Project(store, "notebooks");
        project["languages"] = new JsonArray("Jupyter Notebook", "Python");
        fake.SetSearch(Query("notebooks", "\"Jupyter Notebook\""), new[] { TestRepos.MakeRepo(1) });

        var result = await Collect(new DiscoveryRecall(store, new GitHubClient(http, baseUrl: fake.BaseUrl),
            clock: () => Now).RecallAsync(project));

        Assert.Single(result);
        var parameters = QueryHelpers.ParseQuery(new Uri(fake.BaseUrl + fake.Requests[0]).Query);
        Assert.Equal(Query("notebooks", "\"Jupyter Notebook\""), parameters["q"].ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExclusionsAreRecheckedBeforeEachReadme(bool saveToLibrary)
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        var project = Project(store, "search");
        var first = fake.AddRepo(TestRepos.MakeRepo(1, name: "first"), "README");
        var second = fake.AddRepo(TestRepos.MakeRepo(2, name: "second"), "README");
        fake.SetSearch(Query("search"), new[] { first, second });
        var client = new GitHubClient(http, baseUrl: fake.BaseUrl);
        var recall = new DiscoveryRecall(store, client, clock: () => Now);
        var received = new List<RecalledRepo>();

        await foreach (var item in recall.RecallAsync(project))
        {
            received.Add(item);
            if (item.Repo.GithubId != 1) continue;
            // Triage can change while the runner scores the previous item.
            if (saveToLibrary) new RepoService(store, client).UpsertSource(Meta(second));
            else Candidate(store, project["id"]!.GetValue<long>(), 2, "dismissed");
        }

        Assert.Equal(1, Assert.Single(received).Repo.GithubId);
        Assert.Equal(2, fake.Requests.Count);
        Assert.DoesNotContain("/repos/octocat/second/readme", fake.Requests);
    }

    [Fact]
    public async Task CancellationBetweenItemsStopsReadmesWithoutPrefetching()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        using var http = new HttpClient();
        using var cancel = new CancellationTokenSource();
        var project = Project(store, "search");
        fake.SetSearch(Query("search"), new[] { TestRepos.MakeRepo(1, name: "first"), TestRepos.MakeRepo(2, name: "second") });
        var recall = new DiscoveryRecall(store, new GitHubClient(http, baseUrl: fake.BaseUrl), clock: () => Now);
        await using var enumerator = recall.RecallAsync(project, cancel.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, fake.Requests.Count);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(2, fake.Requests.Count);
    }
}
