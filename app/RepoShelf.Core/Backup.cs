using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoShelf.Core;

/// <summary>
/// Versioned JSON export/import of the whole library: repository records,
/// personal fields and README snapshots. Credentials (pairing token, GitHub
/// token) live in the settings table and are never exported.
/// </summary>
public sealed class BackupService
{
    public const int BackupVersion = 1;

    public sealed class BackupException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private readonly Store _store;
    private readonly RepoService _repos;

    public BackupService(Store store, RepoService repos)
    {
        _store = store;
        _repos = repos;
    }

    public JsonObject Export()
    {
        var repos = new JsonArray();
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.*, a.reason, a.notes,
                       a.tags AS a_tags, a.projects AS a_projects, a.status,
                       a.created_at AS a_created_at, a.updated_at AS a_updated_at
                FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id
                ORDER BY r.id
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string? S(string c) => reader.IsDBNull(reader.GetOrdinal(c)) ? null : reader.GetString(reader.GetOrdinal(c));
                long? L(string c) => reader.IsDBNull(reader.GetOrdinal(c)) ? null : reader.GetInt64(reader.GetOrdinal(c));
                repos.Add(new JsonObject
                {
                    ["githubId"] = reader.GetInt64(reader.GetOrdinal("github_id")),
                    ["owner"] = S("owner"),
                    ["name"] = S("name"),
                    ["fullName"] = S("full_name"),
                    ["htmlUrl"] = S("html_url"),
                    ["description"] = S("description"),
                    ["topics"] = JsonNode.Parse(S("topics") ?? "[]"),
                    ["language"] = S("language") is { } lang ? JsonValue.Create(lang) : null,
                    ["licenseId"] = S("license_id") is { } lic ? JsonValue.Create(lic) : null,
                    ["archived"] = reader.GetInt64(reader.GetOrdinal("archived")) != 0,
                    ["pushedAt"] = S("pushed_at"),
                    ["stars"] = L("stars"),
                    ["defaultBranch"] = S("default_branch"),
                    ["readme"] = S("readme"),
                    ["readmeTruncated"] = reader.GetInt64(reader.GetOrdinal("readme_truncated")) != 0,
                    ["fetchedAt"] = S("fetched_at"),
                    ["refreshStatus"] = S("refresh_status"),
                    ["starredUpstream"] = reader.GetInt64(reader.GetOrdinal("starred_upstream")) != 0,
                    ["seenInImport"] = reader.GetInt64(reader.GetOrdinal("seen_in_import")) != 0,
                    ["createdAt"] = S("created_at"),
                    ["annotation"] = S("a_created_at") is null ? null : new JsonObject
                    {
                        ["reason"] = S("reason") ?? "",
                        ["notes"] = S("notes") ?? "",
                        ["tags"] = JsonNode.Parse(S("a_tags") ?? "[]"),
                        ["projects"] = JsonNode.Parse(S("a_projects") ?? "[]"),
                        ["status"] = S("status") ?? "inbox",
                        ["createdAt"] = S("a_created_at"),
                        ["updatedAt"] = S("a_updated_at"),
                    },
                });
            }
        }
        return new JsonObject
        {
            ["app"] = "repo-shelf",
            ["version"] = BackupVersion,
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
            ["repos"] = repos,
        };
    }

    /// <summary>mode "merge" keeps existing records; "overwrite" replaces them.</summary>
    public JsonObject Import(JsonNode? payload, string mode = "merge")
    {
        if (payload is not JsonObject root
            || root["app"]?.GetValue<string>() != "repo-shelf"
            || root["version"] is not JsonValue versionNode
            || !versionNode.TryGetValue<int>(out var version))
        {
            throw new BackupException("invalid_backup", "Not a Repo Shelf export file");
        }
        if (version > BackupVersion)
        {
            throw new BackupException("unsupported_version", $"Backup version {version} is newer than supported version {BackupVersion}");
        }
        if (root["repos"] is not JsonArray items)
        {
            throw new BackupException("invalid_backup", "Export file has no repos array");
        }
        if (mode is not ("merge" or "overwrite"))
        {
            throw new BackupException("invalid_mode", $"Unknown restore mode \"{mode}\"");
        }

        var added = 0;
        var skipped = 0;
        var overwritten = 0;
        var ts = DateTime.UtcNow.ToString("O");
        var index = 0;
        foreach (var node in items)
        {
            index++;
            if (node is not JsonObject item
                || item["githubId"] is not JsonValue idNode || !idNode.TryGetValue<long>(out var githubId))
            {
                throw new BackupException("invalid_backup", $"repos[{index}].githubId must be an integer");
            }
            var fullName = item["fullName"]?.GetValue<string>() ?? "";
            if (!fullName.Contains('/'))
            {
                throw new BackupException("invalid_backup", $"repos[{index}].fullName must look like \"owner/repo\"");
            }

            long? existingId = null;
            lock (_store.Sync)
            {
                using var find = _store.Conn.CreateCommand();
                find.CommandText = "SELECT id FROM repos WHERE github_id = $gid";
                find.Parameters.AddWithValue("$gid", githubId);
                existingId = find.ExecuteScalar() as long?;
            }
            if (existingId is not null && mode == "merge")
            {
                skipped++;
                continue;
            }

            var parts = fullName.Split('/');
            var meta = new RepoMeta(
                GithubId: githubId,
                Owner: item["owner"]?.GetValue<string>() ?? parts[0],
                Name: item["name"]?.GetValue<string>() ?? parts[1],
                FullName: fullName,
                HtmlUrl: item["htmlUrl"]?.GetValue<string>() ?? $"https://github.com/{fullName}",
                Description: item["description"]?.GetValue<string>() ?? "",
                Topics: item["topics"] is JsonArray t ? t.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() : new(),
                Language: item["language"]?.GetValue<string>(),
                LicenseId: item["licenseId"]?.GetValue<string>(),
                Archived: item["archived"]?.GetValue<bool>() ?? false,
                PushedAt: item["pushedAt"]?.GetValue<string>(),
                Stars: item["stars"] is JsonValue sv && sv.TryGetValue<long>(out var stars) ? stars : null,
                DefaultBranch: item["defaultBranch"]?.GetValue<string>());
            var (repoId, created, _) = _repos.UpsertSource(
                meta,
                item["readme"]?.GetValue<string>(),
                readmeProvided: true,
                readmeTruncated: item["readmeTruncated"]?.GetValue<bool>() ?? false,
                starred: item["starredUpstream"]?.GetValue<bool>() ?? false,
                refreshStatus: item["refreshStatus"]?.GetValue<string>() ?? "ok");
            if (created) added++; else overwritten++;

            lock (_store.Sync)
            {
                void Set(string column, object value)
                {
                    using var cmd = _store.Conn.CreateCommand();
                    cmd.CommandText = $"UPDATE repos SET {column} = $v WHERE id = $id";
                    cmd.Parameters.AddWithValue("$v", value);
                    cmd.Parameters.AddWithValue("$id", repoId);
                    cmd.ExecuteNonQuery();
                }
                if (item["createdAt"]?.GetValue<string>() is { } createdAt)
                {
                    Set("created_at", createdAt);
                }
                if (item["fetchedAt"]?.GetValue<string>() is { } fetchedAt)
                {
                    Set("fetched_at", fetchedAt);
                }
                if (item["seenInImport"]?.GetValue<bool>() == true)
                {
                    Set("seen_in_import", 1);
                }
            }

            if (item["annotation"] is JsonObject a)
            {
                lock (_store.Sync)
                {
                    using var cmd = _store.Conn.CreateCommand();
                    cmd.CommandText = """
                        UPDATE annotations SET reason = $reason, notes = $notes, tags = $tags,
                            projects = $projects, status = $status, updated_at = $ts
                        WHERE repo_id = $id
                        """;
                    cmd.Parameters.AddWithValue("$reason", a["reason"]?.GetValue<string>() ?? "");
                    cmd.Parameters.AddWithValue("$notes", a["notes"]?.GetValue<string>() ?? "");
                    cmd.Parameters.AddWithValue("$tags", a["tags"] is JsonArray tags ? tags.ToJsonString() : "[]");
                    cmd.Parameters.AddWithValue("$projects", a["projects"] is JsonArray pr ? pr.ToJsonString() : "[]");
                    cmd.Parameters.AddWithValue("$status", a["status"]?.GetValue<string>() ?? "inbox");
                    cmd.Parameters.AddWithValue("$ts", ts);
                    cmd.Parameters.AddWithValue("$id", repoId);
                    cmd.ExecuteNonQuery();
                }
                _repos.ReindexRepo(repoId);
            }
        }
        return new JsonObject
        {
            ["added"] = added,
            ["skipped"] = skipped,
            ["overwritten"] = overwritten,
        };
    }
}
