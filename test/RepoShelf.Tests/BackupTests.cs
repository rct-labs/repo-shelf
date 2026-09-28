using System.Text.Json;
using System.Text.Json.Nodes;
using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

/// <summary>Export/restore round-trip and credential exclusion.</summary>
public class BackupTests
{
    private static BackupService Backup(Store store) =>
        new(store, new RepoService(store, new GitHubClient(baseUrl: "http://unused")));

    private static void SeedDiscovery(Store store)
    {
        using var cmd = store.Conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects VALUES (42, 'Project', '/offline/project', 1,
              '["need 中文"]', '["search"]', '["C#"]', '["xunit"]', '["README.md"]',
              'profile time', 'created time', 'updated time');
            INSERT INTO projects (id, name, path, created_at, updated_at)
              VALUES (43, 'Unprofiled', '/offline/other', 'created', 'updated');
            """;
        cmd.ExecuteNonQuery();
        foreach (var state in new[] { "pending", "later", "accepted", "dismissed" })
        {
            cmd.CommandText = """
                INSERT INTO discovery_candidates VALUES
                  (42, $id, 'o/repo', 'https://github.com/o/repo', 'description', 'Go', 'MIT',
                   123, 12.5, 'pushed', 'upstream created', 85, 'need 中文', 'low', 'AI reason',
                   'zh', 'model', $state, $dismiss, 'run', 'proposed', $decided);
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", Array.IndexOf(new[] { "pending", "later", "accepted", "dismissed" }, state) + 100);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$dismiss", state == "dismissed" ? "too_heavy" : DBNull.Value);
            cmd.Parameters.AddWithValue("$decided", state == "pending" ? DBNull.Value : "decided");
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void V2RoundTripsAllDiscoveryFieldsAndStatesWithoutLibraryPollution()
    {
        using var source = Store.Open(":memory:");
        SeedDiscovery(source);
        var exported = Backup(source).Export();
        Assert.Equal(2, exported["version"]!.GetValue<int>());
        Assert.Equal(2, exported["projects"]!.AsArray().Count);
        Assert.Equal(4, exported["discoveryCandidates"]!.AsArray().Count);
        Assert.Equal("need 中文", exported["projects"]![0]!["needs"]![0]!.GetValue<string>());
        Assert.Equal(12.5, exported["discoveryCandidates"]![0]!["starsPerMonth"]!.GetValue<double>());
        using var target = Store.Open(":memory:");
        var backup = Backup(target);
        backup.Import(JsonNode.Parse(exported.ToJsonString()));
        var restored = backup.Export();
        // Local project ids may change; compare all persisted fields and associations.
        for (var i = 0; i < 2; i++) exported["projects"]![i]!["id"] = restored["projects"]![i]!["id"]!.DeepClone();
        foreach (var candidate in exported["discoveryCandidates"]!.AsArray())
            candidate!["projectId"] = restored["projects"]![0]!["id"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(exported["projects"], restored["projects"]));
        Assert.True(JsonNode.DeepEquals(exported["discoveryCandidates"], restored["discoveryCandidates"]));
        Assert.Empty(restored["repos"]!.AsArray());
    }

    [Fact]
    public void DiscoveryMergeAndOverwriteRemapProjectIdsAndKeepUnrelatedData()
    {
        using var source = Store.Open(":memory:");
        SeedDiscovery(source);
        var payload = Backup(source).Export();
        using var target = Store.Open(":memory:");
        using var cmd = target.Conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, name, path, created_at, updated_at) VALUES
              (42, 'Unrelated', '/unrelated', 'now', 'now'),
              (99, 'Project', '/current', 'now', 'now');
            INSERT INTO discovery_candidates (project_id, github_id, state, proposed_at)
              VALUES (99, 100, 'later', 'now');
            """;
        cmd.ExecuteNonQuery();
        var backup = Backup(target);
        backup.Import(payload);
        backup.Import(payload);
        Assert.Equal("/current", new DiscoveryStore(target).GetProject(99)!["path"]!.GetValue<string>());
        var candidates = backup.Export()["discoveryCandidates"]!.AsArray();
        Assert.Equal(4, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(99, c!["projectId"]!.GetValue<long>()));
        Assert.Equal("later", candidates[0]!["state"]!.GetValue<string>());
        backup.Import(payload, "overwrite");
        Assert.Equal("/offline/project", new DiscoveryStore(target).GetProject(99)!["path"]!.GetValue<string>());
        Assert.Equal("pending", backup.Export()["discoveryCandidates"]![0]!["state"]!.GetValue<string>());
        Assert.Equal("Unrelated", new DiscoveryStore(target).GetProject(42)!["name"]!.GetValue<string>());
    }

    [Fact]
    public void InvalidDiscoveryReferenceRollsBackDiscoveryChanges()
    {
        using var source = Store.Open(":memory:");
        SeedDiscovery(source);
        var payload = Backup(source).Export();
        payload["discoveryCandidates"]![3]!["projectId"] = 999;
        using var target = Store.Open(":memory:");
        var backup = Backup(target);
        var error = Assert.Throws<BackupService.BackupException>(() => backup.Import(payload));
        Assert.Equal("invalid_backup", error.Code);
        Assert.Empty(backup.Export()["projects"]!.AsArray());
        Assert.Empty(backup.Export()["discoveryCandidates"]!.AsArray());
    }

    [Theory]
    [InlineData("{\"app\":\"repo-shelf\",\"version\":0,\"repos\":[]}")]
    [InlineData("{\"app\":\"repo-shelf\",\"version\":2,\"repos\":[]}")]
    [InlineData("{\"app\":\"repo-shelf\",\"version\":2,\"repos\":[],\"projects\":[],\"discoveryCandidates\":{}}")]
    public void InvalidVersionOrDiscoveryArraysAreRejected(string json)
    {
        using var store = Store.Open(":memory:");
        Assert.Equal("invalid_backup", Assert.Throws<BackupService.BackupException>(
            () => Backup(store).Import(JsonNode.Parse(json))).Code);
    }

    [Fact]
    public void V1RestoresIntoEmptyDatabaseAndLeavesDiscoveryUntouched()
    {
        using var store = Store.Open(":memory:");
        var backup = Backup(store);
        var payload = JsonNode.Parse("""{"app":"repo-shelf","version":1,"repos":[{"githubId":1,"fullName":"o/legacy","readme":"legacy"}]}""");
        Assert.Equal(1, backup.Import(payload)["added"]!.GetValue<int>());
        Assert.Empty(new DiscoveryStore(store).ListProjects());
        SeedDiscovery(store);
        var before = backup.Export();
        backup.Import(payload, "overwrite");
        var after = backup.Export();
        Assert.True(JsonNode.DeepEquals(before["projects"], after["projects"]));
        Assert.True(JsonNode.DeepEquals(before["discoveryCandidates"], after["discoveryCandidates"]));
    }

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
