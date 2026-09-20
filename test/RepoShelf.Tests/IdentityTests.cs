using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

/// <summary>Capture, identity (rename/transfer/unstar/delete-upstream) and annotation preservation.</summary>
public class IdentityTests
{
    private static (Store store, RepoService repos, FakeGitHubServer fake) Setup()
    {
        var store = Store.Open(":memory:");
        var fake = new FakeGitHubServer();
        var gh = new GitHubClient(baseUrl: fake.BaseUrl);
        return (store, new RepoService(store, gh), fake);
    }

    [Fact]
    public async Task SaveCreatesRecordWithReadmeAndInboxStatus()
    {
        var (store, repos, fake) = Setup();
        fake.AddRepo(TestRepos.MakeRepo(1, name: "Hello-World"), readme: "# Hello");
        var result = await repos.SaveRepoAsync("https://github.com/octocat/Hello-World", "looks useful", new() { "demo" });
        Assert.Equal("created", result["outcome"]!.GetValue<string>());
        var repo = result["repo"]!;
        Assert.Equal(1, repo["githubId"]!.GetValue<long>());
        Assert.Equal("# Hello", repo["readme"]!.GetValue<string>());
        Assert.Equal("inbox", repo["annotation"]!["status"]!.GetValue<string>());
        Assert.Equal("looks useful", repo["annotation"]!["reason"]!.GetValue<string>());
        Assert.Equal("ok", repo["refreshStatus"]!.GetValue<string>());
    }

    [Fact]
    public async Task SavingTwiceKeepsSingleRecordAndPersonalFields()
    {
        var (store, repos, fake) = Setup();
        fake.AddRepo(TestRepos.MakeRepo(1, name: "Hello-World"));
        var first = await repos.SaveRepoAsync("https://github.com/octocat/Hello-World", "first reason", null);
        var second = await repos.SaveRepoAsync("https://github.com/octocat/Hello-World/tree/main", "ignored reason", new() { "extra" });
        Assert.Equal("already_exists", second["outcome"]!.GetValue<string>());
        Assert.Equal(first["repo"]!["id"]!.GetValue<long>(), second["repo"]!["id"]!.GetValue<long>());
        Assert.Equal("first reason", second["repo"]!["annotation"]!["reason"]!.GetValue<string>());
        lock (store.Sync)
        {
            using var cmd = store.Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM repos";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public async Task RenameKeepsIdentityWithoutDuplicates()
    {
        var (store, repos, fake) = Setup();
        var raw = fake.AddRepo(TestRepos.MakeRepo(7, "oldowner", "oldname"));
        var saved = await repos.SaveRepoAsync("https://github.com/oldowner/oldname");
        var id = saved["repo"]!["id"]!.GetValue<long>();
        repos.UpdateAnnotation(id, new System.Text.Json.Nodes.JsonObject
        {
            ["notes"] = "改名后笔记必须还在",
            ["status"] = "adopted",
        });

        fake.MoveRepo(raw, "newowner", "newname");
        var again = await repos.SaveRepoAsync("https://github.com/newowner/newname");
        Assert.Equal(id, again["repo"]!["id"]!.GetValue<long>());

        var (repo, _) = await repos.RefreshRepoAsync(id);
        Assert.Equal("newowner/newname", repo["fullName"]!.GetValue<string>());
        Assert.Equal("改名后笔记必须还在", repo["annotation"]!["notes"]!.GetValue<string>());
        Assert.Equal("adopted", repo["annotation"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefreshFollowsDurableIdWhenOldName404s()
    {
        var (_, repos, fake) = Setup();
        var raw = fake.AddRepo(TestRepos.MakeRepo(9, "a", "proj"));
        var saved = await repos.SaveRepoAsync("https://github.com/a/proj");
        fake.MoveRepo(raw, "b", "proj");
        fake.FailNext(url => url.Contains("/repos/a/proj"), times: 4, System.Net.HttpStatusCode.NotFound);
        // 404 is not transient: first 404 triggers the by-id fallback.
        var (repo, _) = await repos.RefreshRepoAsync(saved["repo"]!["id"]!.GetValue<long>());
        Assert.Equal("b/proj", repo["fullName"]!.GetValue<string>());
        Assert.Equal("ok", repo["refreshStatus"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeletedUpstreamIsKeptAndMarkedNotFound()
    {
        var (_, repos, fake) = Setup();
        var raw = fake.AddRepo(TestRepos.MakeRepo(11, "gone", "proj"));
        var saved = await repos.SaveRepoAsync("https://github.com/gone/proj", "keep me", null);
        fake.DeleteRepo(raw);
        await Assert.ThrowsAsync<ServiceException>(async () =>
            await repos.RefreshRepoAsync(saved["repo"]!["id"]!.GetValue<long>()));
        var repo = repos.GetRepo(saved["repo"]!["id"]!.GetValue<long>())!;
        Assert.Equal("not_found", repo["refreshStatus"]!.GetValue<string>());
        Assert.Equal("keep me", repo["annotation"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefreshUpdatesSourceButNeverPersonalFields()
    {
        var (_, repos, fake) = Setup();
        var raw = fake.AddRepo(TestRepos.MakeRepo(12, "o", "r", description: "old"));
        var saved = await repos.SaveRepoAsync("https://github.com/o/r");
        var id = saved["repo"]!["id"]!.GetValue<long>();
        repos.UpdateAnnotation(id, new System.Text.Json.Nodes.JsonObject
        {
            ["notes"] = "私人笔记",
            ["tags"] = new System.Text.Json.Nodes.JsonArray("t"),
            ["status"] = "tried",
        });
        raw["description"] = "new description";
        raw["archived"] = true;
        await repos.RefreshRepoAsync(id);
        var repo = repos.GetRepo(id)!;
        Assert.Equal("new description", repo["description"]!.GetValue<string>());
        Assert.True(repo["archived"]!.GetValue<bool>());
        Assert.Equal("私人笔记", repo["annotation"]!["notes"]!.GetValue<string>());
        Assert.Equal("tried", repo["annotation"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveWithGitHubUnreachableFailsRetryablyAndStoresNothing()
    {
        var (store, repos, fake) = Setup();
        fake.FailNext(_ => true, times: 4, System.Net.HttpStatusCode.ServiceUnavailable);
        var ex = await Assert.ThrowsAsync<ServiceException>(async () =>
            await repos.SaveRepoAsync("https://github.com/octocat/Hello-World"));
        Assert.Equal("upstream_unavailable", ex.Code);
        Assert.True(ex.Retryable);
        lock (store.Sync)
        {
            using var cmd = store.Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM repos";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public async Task InvalidAndUnknownReposRejectedClearly()
    {
        var (_, repos, _) = Setup();
        var e1 = await Assert.ThrowsAsync<ServiceException>(() => repos.SaveRepoAsync("https://example.com/x/y"));
        Assert.Equal("invalid_url", e1.Code);
        var e2 = await Assert.ThrowsAsync<ServiceException>(() => repos.SaveRepoAsync("https://github.com/no/such"));
        Assert.Equal("repository_not_found", e2.Code);
    }

    [Fact]
    public async Task AnnotationValidationAndDelete()
    {
        var (_, repos, fake) = Setup();
        fake.AddRepo(TestRepos.MakeRepo(21, "o", "r"));
        var saved = await repos.SaveRepoAsync("https://github.com/o/r");
        var id = saved["repo"]!["id"]!.GetValue<long>();
        Assert.Throws<ServiceException>(() => repos.UpdateAnnotation(id, new System.Text.Json.Nodes.JsonObject { ["status"] = "nope" }));
        var annotation = repos.UpdateAnnotation(id, new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = "dismissed",
            ["projects"] = new System.Text.Json.Nodes.JsonArray("工作"),
        });
        Assert.Equal("dismissed", annotation["status"]!.GetValue<string>());
        repos.DeleteRepo(id);
        Assert.Null(repos.GetRepo(id));
    }
}
