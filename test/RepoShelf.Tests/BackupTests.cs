using System.Text.Json;
using System.Text.Json.Nodes;
using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

/// <summary>Export/restore round-trip and credential exclusion.</summary>
public class BackupTests
{
    private static RepoMeta Meta(long id, string name) =>
        new(id, "o", name, $"o/{name}", $"https://github.com/o/{name}", $"desc {name}", new() { "t1", "t2" }, "Go", "MIT", false, "2026-09-01T00:00:00Z", 5, "main");

    [Fact]
    public void ExportWipeRestoreRoundTripsWithoutCredentials()
    {
        using var store = Store.Open(":memory:");
        var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
        var backup = new BackupService(store, repos);
        store.SetSetting("github_token", "ghp_secret_token_value");
        store.SetSetting("pairing_token", "pairing_secret_value");

        var (aId, _, _) = repos.UpsertSource(Meta(1, "alpha"), "# alpha readme 中文内容", readmeProvided: true, starred: true);
        repos.UpdateAnnotation(aId, new JsonObject
        {
            ["reason"] = "why alpha",
            ["notes"] = "中文笔记 <b>html</b>",
            ["tags"] = new JsonArray("x", "y"),
            ["projects"] = new JsonArray("proj"),
            ["status"] = "adopted",
        });
        var (bId, _, _) = repos.UpsertSource(Meta(2, "beta"), "# beta", readmeProvided: true);
        repos.UpdateAnnotation(bId, new JsonObject { ["notes"] = "beta notes", ["status"] = "to_investigate" });
        repos.UpsertSource(Meta(3, "gamma"));

        var exported = backup.Export();
        var serialized = exported.ToJsonString();
        Assert.DoesNotContain("ghp_secret_token_value", serialized);
        Assert.DoesNotContain("pairing_secret_value", serialized);
        Assert.Equal(3, exported["repos"]!.AsArray().Count);

        using var fresh = Store.Open(":memory:");
        var freshRepos = new RepoService(fresh, new GitHubClient(baseUrl: "http://unused"));
        var freshBackup = new BackupService(fresh, freshRepos);
        var counts = freshBackup.Import(JsonNode.Parse(serialized));
        Assert.Equal(3, counts["added"]!.GetValue<int>());
        Assert.Equal(0, counts["skipped"]!.GetValue<int>());

        long LocalId(long githubId)
        {
            lock (fresh.Sync)
            {
                using var cmd = fresh.Conn.CreateCommand();
                cmd.CommandText = "SELECT id FROM repos WHERE github_id = $g";
                cmd.Parameters.AddWithValue("$g", githubId);
                return (long)cmd.ExecuteScalar()!;
            }
        }
        var restoredA = freshRepos.GetRepo(LocalId(1))!;
        Assert.Equal("why alpha", restoredA["annotation"]!["reason"]!.GetValue<string>());
        Assert.Equal("中文笔记 <b>html</b>", restoredA["annotation"]!["notes"]!.GetValue<string>());
        Assert.Equal("adopted", restoredA["annotation"]!["status"]!.GetValue<string>());
        Assert.Equal("# alpha readme 中文内容", restoredA["readme"]!.GetValue<string>());
        Assert.True(restoredA["starredUpstream"]!.GetValue<bool>());
        Assert.True(restoredA["seenInImport"]!.GetValue<bool>());

        var search = new SearchService(fresh);
        Assert.Equal(1, search.Search(new SearchService.SearchParams(Query: "中文笔记")).Total);
        Assert.Null(fresh.GetSetting("github_token"));
    }

    [Fact]
    public void MergeKeepsExistingAndOverwriteReplaces()
    {
        using var store = Store.Open(":memory:");
        var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
        var backup = new BackupService(store, repos);
        var (repoId, _, _) = repos.UpsertSource(Meta(1, "alpha"), "# new", readmeProvided: true);
        repos.UpdateAnnotation(repoId, new JsonObject { ["notes"] = "current notes" });

        var payload = JsonNode.Parse("""
            { "app": "repo-shelf", "version": 1, "repos": [{
                "githubId": 1, "owner": "o", "name": "alpha", "fullName": "o/alpha",
                "htmlUrl": "https://github.com/o/alpha", "description": "old desc",
                "topics": [], "language": "Go", "licenseId": "MIT", "archived": false,
                "pushedAt": null, "stars": 5, "defaultBranch": "main",
                "readme": "# old", "readmeTruncated": false, "starredUpstream": true,
                "annotation": { "reason": "old reason", "notes": "old notes", "tags": ["old"], "projects": [], "status": "tried" }
            }] }
            """);

        var merged = backup.Import(payload, "merge");
        Assert.Equal(1, merged["skipped"]!.GetValue<int>());
        Assert.Equal("current notes", repos.GetRepo(repoId)!["annotation"]!["notes"]!.GetValue<string>());

        var overwritten = backup.Import(payload, "overwrite");
        Assert.Equal(1, overwritten["overwritten"]!.GetValue<int>());
        var after = repos.GetRepo(repoId)!;
        Assert.Equal("old notes", after["annotation"]!["notes"]!.GetValue<string>());
        Assert.Equal("old desc", after["description"]!.GetValue<string>());
        Assert.Equal("# old", after["readme"]!.GetValue<string>());
    }

    [Fact]
    public void InvalidBackupsRejected()
    {
        using var store = Store.Open(":memory:");
        var backup = new BackupService(store, new RepoService(store, new GitHubClient(baseUrl: "http://unused")));
        Assert.Throws<BackupService.BackupException>(() => backup.Import(null));
        Assert.Throws<BackupService.BackupException>(() => backup.Import(JsonNode.Parse("""{"app":"other","version":1,"repos":[]}""")));
        Assert.Throws<BackupService.BackupException>(() => backup.Import(JsonNode.Parse("""{"app":"repo-shelf","version":99,"repos":[]}""")));
        Assert.Throws<BackupService.BackupException>(() => backup.Import(JsonNode.Parse("""{"app":"repo-shelf","version":1,"repos":[{"fullName":"x/y"}]}""")));
    }
}
