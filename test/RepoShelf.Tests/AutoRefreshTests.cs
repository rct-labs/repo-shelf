using System.Net;
using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

public class AutoRefreshTests
{
    private static void Age(Store store)
    {
        using var cmd = store.Conn.CreateCommand();
        cmd.CommandText = "UPDATE repos SET fetched_at = '2020-01-01T00:00:00Z'";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task RefreshesOnlyDuePreservesNotesAndPersistsCooldownAcrossRestart()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        var repos = new RepoService(store, new GitHubClient(baseUrl: fake.BaseUrl));
        fake.AddRepo(TestRepos.MakeRepo(1, name: "old"), "old README");
        var saved = await repos.SaveRepoAsync("https://github.com/octocat/old", "keep me");
        Age(store);
        fake.AddRepo(TestRepos.MakeRepo(2, name: "fresh"));
        await repos.SaveRepoAsync("https://github.com/octocat/fresh");
        fake.AddRepo(TestRepos.MakeRepo(1, name: "old", stars: 99), "new README");
        fake.Requests.Clear();
        await using var refresh = new AutoRefresh(store, repos);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await refresh.RunOnceAsync(now));
        Assert.DoesNotContain(fake.Requests, r => r.Contains("fresh"));
        var updated = repos.GetRepo(saved["repo"]!["id"]!.GetValue<long>())!;
        Assert.Equal(99, updated["stars"]!.GetValue<long>());
        Assert.Equal("new README", updated["readme"]!.GetValue<string>());
        Assert.Equal("keep me", updated["annotation"]!["reason"]!.GetValue<string>());
        Age(store);
        await using var restarted = new AutoRefresh(store, repos);
        Assert.Equal(0, await restarted.RunOnceAsync(now.AddMinutes(20)));
        Assert.Equal(2, await restarted.RunOnceAsync(now.AddHours(1)));
    }

    [Fact]
    public async Task RateLimitStopsBatchAndDefersRetry()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        var repos = new RepoService(store, new GitHubClient(baseUrl: fake.BaseUrl));
        for (var i = 1; i <= 2; i++)
        {
            fake.AddRepo(TestRepos.MakeRepo(i, name: $"repo{i}"));
            await repos.SaveRepoAsync($"https://github.com/octocat/repo{i}");
        }
        Age(store);
        fake.Requests.Clear();
        fake.FailNext(_ => true, 1, HttpStatusCode.Forbidden,
            new() { ["x-ratelimit-remaining"] = "0", ["retry-after"] = "7200" });
        await using var refresh = new AutoRefresh(store, repos);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await refresh.RunOnceAsync(now));
        Assert.Single(fake.Requests);
        Assert.Equal(0, await refresh.RunOnceAsync(now.AddHours(1)));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task BusyJobsAreSkippedAndBatchIsBounded()
    {
        using var store = Store.Open(":memory:");
        using var fake = new FakeGitHubServer();
        var repos = new RepoService(store, new GitHubClient(baseUrl: fake.BaseUrl));
        for (var i = 1; i <= 21; i++)
        {
            fake.AddRepo(TestRepos.MakeRepo(i, name: $"repo{i}"));
            await repos.SaveRepoAsync($"https://github.com/octocat/repo{i}");
        }
        Age(store);
        var busy = true;
        await using var refresh = new AutoRefresh(store, repos, () => busy);
        Assert.Equal(0, await refresh.RunOnceAsync(DateTimeOffset.UtcNow));
        busy = false;
        Assert.Equal(20, await refresh.RunOnceAsync(DateTimeOffset.UtcNow));
    }
}
