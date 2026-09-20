using System.Net;
using System.Text.Json.Nodes;
using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

/// <summary>Stars import: pagination, mid-import failure + resume, rate limits, unstar reconciliation.</summary>
public class ImportTests
{
    private static (Store store, FakeGitHubServer fake, RepoService repos, JobManager jobs) Setup(int maxRateLimitWaitMs = 0)
    {
        var store = Store.Open(":memory:");
        var fake = new FakeGitHubServer();
        var gh = new GitHubClient(baseUrl: fake.BaseUrl);
        var repos = new RepoService(store, gh);
        var jobs = new JobManager(store, gh, repos, maxRateLimitWaitMs);
        return (store, fake, repos, jobs);
    }

    private static List<JsonObject> StarredBatch(string prefix, int count, int startId, FakeGitHubServer fake)
    {
        return Enumerable.Range(startId, count).Select(i =>
            fake.AddRepo(TestRepos.MakeRepo(i, "community", $"{prefix}-{i}", $"Repository number {i}"), readme: $"# {prefix}-{i}")).ToList();
    }

    private static long RepoCount(Store store)
    {
        lock (store.Sync)
        {
            using var cmd = store.Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM repos";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    [Fact]
    public async Task ImportsMultiplePagesWithNoDuplicates()
    {
        var (store, fake, _, jobs) = Setup();
        var list = StarredBatch("proj", 205, 1000, fake);
        fake.SetStarred("someone", list);

        var job = jobs.StartStarsImport("someone");
        var done = await JobWaiter.WaitForJobAsync(jobs, job["id"]!.GetValue<string>());
        Assert.Equal("done", done["status"]!.GetValue<string>());
        Assert.Equal(205, done["processed"]!.GetValue<long>());
        Assert.Equal(205, done["added"]!.GetValue<long>());
        Assert.Equal(0, done["failed"]!.GetValue<long>());
        Assert.Equal(205, RepoCount(store));

        // Repeating the import updates in place instead of duplicating.
        var again = jobs.StartStarsImport("someone");
        var againDone = await JobWaiter.WaitForJobAsync(jobs, again["id"]!.GetValue<string>());
        Assert.Equal(0, againDone["added"]!.GetValue<long>());
        Assert.Equal(205, againDone["updated"]!.GetValue<long>());
        Assert.Equal(205, RepoCount(store));
    }

    [Fact]
    public async Task MidImportFailurePreservesProgressAndResumeCompletes()
    {
        var (store, fake, _, jobs) = Setup();
        var list = StarredBatch("item", 150, 2000, fake);
        fake.SetStarred("flaky", list);
        fake.FailNext(url => url.Contains("page=2"), times: 4, HttpStatusCode.ServiceUnavailable);

        var job = jobs.StartStarsImport("flaky", includeReadme: false);
        var failed = await JobWaiter.WaitForJobAsync(jobs, job["id"]!.GetValue<string>());
        Assert.Equal("failed", failed["status"]!.GetValue<string>());
        Assert.Contains("unreachable", failed["error"]!.GetValue<string>());
        Assert.Equal(2, failed["nextPage"]!.GetValue<long>());
        Assert.Equal(100, failed["processed"]!.GetValue<long>());
        Assert.Equal(100, RepoCount(store));

        var id = job["id"]!.GetValue<string>();
        var resumed = jobs.ResumeJob(id)!;
        Assert.Equal("running", resumed["status"]!.GetValue<string>());
        var done = await JobWaiter.WaitForJobAsync(jobs, id);
        Assert.Equal("done", done["status"]!.GetValue<string>());
        Assert.Equal(150, done["processed"]!.GetValue<long>());
        Assert.Equal(150, RepoCount(store));
    }

    [Fact]
    public async Task RateLimitAbortsJobCleanly()
    {
        var (_, fake, _, jobs) = Setup();
        var list = StarredBatch("rl", 101, 3000, fake);
        fake.SetStarred("limited", list);
        var resetAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        fake.FailNext(url => url.Contains("page=2"), times: 1, HttpStatusCode.Forbidden,
            new() { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = resetAt.ToString() });

        var job = jobs.StartStarsImport("limited", includeReadme: false);
        var done = await JobWaiter.WaitForJobAsync(jobs, job["id"]!.GetValue<string>());
        Assert.Equal("failed", done["status"]!.GetValue<string>());
        Assert.Contains("rate limit", done["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, done["nextPage"]!.GetValue<long>());
    }

    [Fact]
    public async Task ReimportPreservesAnnotationsAndUnstarKeepsRecord()
    {
        var (store, fake, repos, jobs) = Setup();
        var kept = fake.AddRepo(TestRepos.MakeRepo(5, "u", "kept"));
        var unstarred = fake.AddRepo(TestRepos.MakeRepo(6, "u", "unstarred"));
        fake.SetStarred("fan", new() { kept, unstarred });

        var job = jobs.StartStarsImport("fan", includeReadme: false);
        await JobWaiter.WaitForJobAsync(jobs, job["id"]!.GetValue<string>());

        long LocalId(long githubId)
        {
            lock (store.Sync)
            {
                using var cmd = store.Conn.CreateCommand();
                cmd.CommandText = "SELECT id FROM repos WHERE github_id = $g";
                cmd.Parameters.AddWithValue("$g", githubId);
                return (long)cmd.ExecuteScalar()!;
            }
        }
        repos.UpdateAnnotation(LocalId(6), new JsonObject { ["notes"] = "已取消收藏但笔记要留下", ["status"] = "dismissed" });
        repos.UpdateAnnotation(LocalId(5), new JsonObject { ["reason"] = "my reason", ["status"] = "adopted" });

        kept["description"] = "changed upstream";
        fake.SetStarred("fan", new() { kept });

        job = jobs.StartStarsImport("fan", includeReadme: false);
        var done = await JobWaiter.WaitForJobAsync(jobs, job["id"]!.GetValue<string>());
        Assert.Equal("done", done["status"]!.GetValue<string>());

        var after = repos.GetRepo(LocalId(6))!;
        Assert.False(after["starredUpstream"]!.GetValue<bool>());
        Assert.Equal("已取消收藏但笔记要留下", after["annotation"]!["notes"]!.GetValue<string>());
        Assert.Equal("dismissed", after["annotation"]!["status"]!.GetValue<string>());

        var keptAfter = repos.GetRepo(LocalId(5))!;
        Assert.Equal("changed upstream", keptAfter["description"]!.GetValue<string>());
        Assert.Equal("my reason", keptAfter["annotation"]!["reason"]!.GetValue<string>());
        Assert.True(keptAfter["starredUpstream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ImportingAlreadySavedRepoUpdatesInPlace()
    {
        var (store, fake, repos, jobs) = Setup();
        var raw = fake.AddRepo(TestRepos.MakeRepo(42, "o", "shared"));
        var saved = await repos.SaveRepoAsync("https://github.com/o/shared", "saved manually", null);

        fake.SetStarred("me", new() { raw });
        var job = jobs.StartStarsImport("me", includeReadme: false);
        await JobWaiter.WaitForJobAsync(jobs, job["id"]!.GetValue<string>());

        Assert.Equal(1, RepoCount(store));
        var repo = repos.GetRepo(saved["repo"]!["id"]!.GetValue<long>())!;
        Assert.True(repo["starredUpstream"]!.GetValue<bool>());
        Assert.Equal("saved manually", repo["annotation"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownUserFailsCleanly()
    {
        var (_, _, _, jobs) = Setup();
        var job = jobs.StartStarsImport("ghost-user", includeReadme: false);
        var done = await JobWaiter.WaitForJobAsync(jobs, job["id"]!.GetValue<string>());
        Assert.Equal("failed", done["status"]!.GetValue<string>());
        Assert.Contains("not found", done["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }
}
