using System.Text.Json.Nodes;
using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

public class AiTests
{
    private static (Store store, RepoService repos, AiService ai, FakeGitHubServer gh, FakeAiServer aiServer) Setup(bool withKey = true)
    {
        var store = Store.Open(":memory:");
        var gh = new FakeGitHubServer();
        var aiServer = new FakeAiServer();
        var repos = new RepoService(store, new GitHubClient(baseUrl: gh.BaseUrl));
        var ds = new DeepSeekClient(getKey: () => withKey ? "sk-test" : null, baseUrl: aiServer.BaseUrl, model: "deepseek-chat");
        var ai = new AiService(store, repos, ds);
        return (store, repos, ai, gh, aiServer);
    }

    [Fact]
    public async Task SummaryIsStoredSeparatelyAndNeverTouchesAnnotations()
    {
        var (store, repos, ai, gh, aiServer) = Setup();
        gh.AddRepo(TestRepos.MakeRepo(1, "octocat", "Hello-World"), readme: "# Hello World\nA friendly repo.");
        var saved = await repos.SaveRepoAsync("https://github.com/octocat/Hello-World", "我的理由", new());
        var id = saved["repo"]!["id"]!.GetValue<long>();

        aiServer.SummaryText = "## 这是什么\n一个友好的示例仓库。";
        var summary = await ai.SummarizeAsync(id, "zh");
        Assert.Equal(aiServer.SummaryText, summary["content"]!.GetValue<string>());
        Assert.Equal("deepseek-chat", summary["model"]!.GetValue<string>());

        // Personal fields untouched; summary lives in the generated table.
        var repo = repos.GetRepo(id)!;
        Assert.Equal("我的理由", repo["annotation"]!["reason"]!.GetValue<string>());
        Assert.Equal(aiServer.SummaryText, ai.GetSummary(id)!["content"]!.GetValue<string>());

        // Export carries the summary but never the API key.
        var backup = new BackupService(store, repos);
        var text = backup.Export().ToJsonString();
        var exported = JsonNode.Parse(text)!;
        var exportedRepo = exported["repos"]!.AsArray().Single(r => r!["githubId"]!.GetValue<long>() == 1)!;
        Assert.Equal(aiServer.SummaryText, exportedRepo["generated"]!["summary"]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("sk-test", text);
    }

    [Fact]
    public async Task MissingKeyFailsClearly()
    {
        var (_, repos, ai, gh, _) = Setup(withKey: false);
        gh.AddRepo(TestRepos.MakeRepo(2, "a", "b"));
        var saved = await repos.SaveRepoAsync("https://github.com/a/b");
        var ex = await Assert.ThrowsAsync<ServiceException>(() => ai.SummarizeAsync(saved["repo"]!["id"]!.GetValue<long>(), "zh"));
        Assert.Equal("ai_not_configured", ex.Code);
        Assert.Null(ai.GetSummary(saved["repo"]!["id"]!.GetValue<long>()));
    }

    [Fact]
    public async Task AiFailureKeepsExistingSummary()
    {
        var (_, repos, ai, gh, aiServer) = Setup();
        gh.AddRepo(TestRepos.MakeRepo(3, "a", "c"));
        var saved = await repos.SaveRepoAsync("https://github.com/a/c");
        var id = saved["repo"]!["id"]!.GetValue<long>();
        aiServer.SummaryText = "第一版总结";
        await ai.SummarizeAsync(id, "zh");
        aiServer.Fail = true;
        await Assert.ThrowsAsync<ServiceException>(() => ai.SummarizeAsync(id, "zh"));
        Assert.Equal("第一版总结", ai.GetSummary(id)!["content"]!.GetValue<string>());
    }
}

public class BookmarksTests
{
    [Fact]
    public void ExtractsOnlyGitHubRepoUrlsDeduped()
    {
        var fixture = """
            { "roots": { "bookmark_bar": { "children": [
              { "type": "url", "name": "Hister", "url": "https://github.com/asciimoo/hister" },
              { "type": "url", "name": "subpage", "url": "https://github.com/asciimoo/hister/tree/main/webui" },
              { "type": "url", "name": "topic page", "url": "https://github.com/topics/javascript" },
              { "type": "url", "name": "other site", "url": "https://example.com/foo/bar" },
              { "type": "folder", "name": "AI", "children": [
                  { "type": "url", "name": "Charm", "url": "https://github.com/charmbracelet/charm" }
              ] }
            ] } } }
            """;
        var file = Path.Combine(Path.GetTempPath(), "bookmarks-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, fixture);
        try
        {
            var urls = ChromeBookmarks.ExtractRepoUrls(file, out _, out var error);
            Assert.Null(error);
            Assert.NotNull(urls);
            Assert.Equal(2, urls!.Count);
            Assert.Contains("https://github.com/asciimoo/hister", urls);
            Assert.Contains("https://github.com/charmbracelet/charm", urls);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ImportJobImportsBookmarkedRepos()
    {
        using var store = Store.Open(":memory:");
        using var gh = new FakeGitHubServer();
        var repos = new RepoService(store, new GitHubClient(baseUrl: gh.BaseUrl));
        var jobs = new JobManager(store, new GitHubClient(baseUrl: gh.BaseUrl), repos);
        var r1 = gh.AddRepo(TestRepos.MakeRepo(1, "a", "one"));
        var r2 = gh.AddRepo(TestRepos.MakeRepo(2, "b", "two"));

        var fixture = """
            { "roots": { "bookmark_bar": { "children": [
              { "type": "url", "url": "https://github.com/a/one" },
              { "type": "url", "url": "https://github.com/b/two" },
              { "type": "url", "url": "https://github.com/not/there" },
              { "type": "url", "url": "https://example.com/" }
            ] } } }
            """;
        var file = Path.Combine(Path.GetTempPath(), "bookmarks-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, fixture);
        try
        {
            var (job, error) = jobs.StartBookmarksImport(file);
            Assert.Null(error);
            Assert.NotNull(job);
            var done = await JobWaiter.WaitForJobAsync(jobs, job!["id"]!.GetValue<string>());
            Assert.Equal("done", done["status"]!.GetValue<string>());
            Assert.Equal(2, done["added"]!.GetValue<long>());
            Assert.Equal(1, done["failed"]!.GetValue<long>()); // not/there
            Assert.Equal(3, done["processed"]!.GetValue<long>());
        }
        finally
        {
            File.Delete(file);
        }
    }
}
