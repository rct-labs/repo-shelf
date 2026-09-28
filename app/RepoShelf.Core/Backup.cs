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
    public const int BackupVersion = 2;

    // Explicit persisted fields keep derived counts and credentials out of backups.
    private static readonly string[] ProjectColumns =
        "id name path paused needs queries languages dependencies profile_files profiled_at created_at updated_at".Split(' ');
    private static readonly string[] CandidateColumns =
        "project_id github_id full_name html_url description language license_id stars stars_per_month pushed_at created_at_upstream score matched_need cost reason reason_lang model state dismiss_reason run_id proposed_at decided_at".Split(' ');
    private static readonly HashSet<string> ArrayColumns =
        new() { "needs", "queries", "languages", "dependencies", "profile_files" };

    private static string JsonName(string column)
    {
        var parts = column.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private JsonArray ExportRows(string table, string[] columns, string order)
    {
        using var cmd = _store.Conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(",", columns)} FROM {table} ORDER BY {order}";
        using var reader = cmd.ExecuteReader();
        var rows = new JsonArray();
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < columns.Length; i++)
            {
                var column = columns[i];
                row[JsonName(column)] = reader.IsDBNull(i) ? null
                    : ArrayColumns.Contains(column) ? JsonNode.Parse(reader.GetString(i))
                    : column == "paused" ? JsonValue.Create(reader.GetInt64(i) != 0)
                    : JsonSerializer.SerializeToNode(reader.GetValue(i));
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <param name="dryRun">Validate inside a transaction that is rolled back, so a malformed
    /// discovery section rejects the whole file before any repository is written.</param>
    private void ImportDiscovery(JsonArray projects, JsonArray candidates, string mode, bool dryRun = false)
    {
        lock (_store.Sync)
        {
            using var transaction = _store.Conn.BeginTransaction();
            var projectIds = new Dictionary<long, long>();
            long Integer(JsonObject row, string key) => row[key] is JsonValue v && v.TryGetValue<long>(out var n)
                ? n : throw new BackupException("invalid_backup", $"{key} must be an integer");

            void Write(string table, string[] columns, JsonObject row, string conflict)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"INSERT INTO {table} ({string.Join(",", columns)}) VALUES ({string.Join(",", columns.Select(c => "$" + c))}) ON CONFLICT({conflict}) "
                    + (mode == "merge" ? "DO NOTHING" : "DO UPDATE SET " + string.Join(",", columns.Select(c => $"{c}=excluded.{c}")));
                foreach (var column in columns)
                {
                    var node = row[JsonName(column)];
                    object value = DBNull.Value;
                    if (ArrayColumns.Contains(column))
                    {
                        if (node is not JsonArray array || array.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _)))
                            throw new BackupException("invalid_backup", $"{JsonName(column)} must be an array of strings");
                        value = array.ToJsonString();
                    }
                    else if (node is JsonValue scalar)
                    {
                        if (scalar.TryGetValue<string>(out var s)) value = s;
                        else if (scalar.TryGetValue<bool>(out var b)) value = b ? 1 : 0;
                        else if (scalar.TryGetValue<long>(out var n)) value = n;
                        else if (scalar.TryGetValue<double>(out var d)) value = d;
                        else throw new BackupException("invalid_backup", $"Invalid {JsonName(column)}");
                    }
                    else if (node is not null) throw new BackupException("invalid_backup", $"Invalid {JsonName(column)}");
                    cmd.Parameters.AddWithValue("$" + column, value);
                }
                cmd.ExecuteNonQuery();
            }

            try
            {
                foreach (var node in projects)
                {
                    if (node is not JsonObject project || project["name"] is not JsonValue nameValue
                        || !nameValue.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                        throw new BackupException("invalid_backup", "Project must have a name");
                    var sourceId = Integer(project, "id");
                    if (projectIds.ContainsKey(sourceId)) throw new BackupException("invalid_backup", "Duplicate project id");
                    // Names are unique across a library; source ids are local to its database.
                    Write("projects", ProjectColumns[1..], project, "name");
                    using var find = _store.Conn.CreateCommand();
                    find.Transaction = transaction;
                    find.CommandText = "SELECT id FROM projects WHERE name = $name";
                    find.Parameters.AddWithValue("$name", name);
                    projectIds.Add(sourceId, (long)find.ExecuteScalar()!);
                }
                foreach (var node in candidates)
                {
                    if (node is not JsonObject candidate) throw new BackupException("invalid_backup", "Invalid candidate");
                    if (!projectIds.TryGetValue(Integer(candidate, "projectId"), out var localId))
                        throw new BackupException("invalid_backup", "Candidate references a missing project");
                    Integer(candidate, "githubId");
                    var mapped = (JsonObject)candidate.DeepClone();
                    mapped["projectId"] = localId;
                    Write("discovery_candidates", CandidateColumns, mapped, "project_id,github_id");
                }
                if (!dryRun) transaction.Commit();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new BackupException("invalid_backup", "Invalid discovery data: " + ex.Message);
            }
        }
    }

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
        JsonArray projects;
        JsonArray candidates;
        lock (_store.Sync)
        {
            projects = ExportRows("projects", ProjectColumns, "id");
            candidates = ExportRows("discovery_candidates", CandidateColumns, "project_id,github_id");
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
                var repoId = reader.GetInt64(reader.GetOrdinal("id"));
                var item = new JsonObject
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
                };
                // AI-generated text is part of the user's library data.
                using (var gen = _store.Conn.CreateCommand())
                {
                    gen.CommandText = "SELECT content, model, lang, created_at FROM generated WHERE repo_id = $id AND kind = 'summary'";
                    gen.Parameters.AddWithValue("$id", repoId);
                    using var gr = gen.ExecuteReader();
                    if (gr.Read())
                    {
                        item["generated"] = new JsonObject
                        {
                            ["summary"] = new JsonObject
                            {
                                ["content"] = gr.GetString(0),
                                ["model"] = gr.GetString(1),
                                ["lang"] = gr.GetString(2),
                                ["createdAt"] = gr.GetString(3),
                            },
                        };
                    }
                }
                repos.Add(item);
            }
        }
        return new JsonObject
        {
            ["app"] = "repo-shelf",
            ["version"] = BackupVersion,
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
            ["repos"] = repos,
            ["projects"] = projects,
            ["discoveryCandidates"] = candidates,
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
        if (version < 1) throw new BackupException("invalid_backup", "Backup version must be 1 or 2");
        if (version == 2 && (root["projects"] is not JsonArray || root["discoveryCandidates"] is not JsonArray))
            throw new BackupException("invalid_backup", "Version 2 requires projects and discoveryCandidates arrays");
        if (root["repos"] is not JsonArray items)
        {
            throw new BackupException("invalid_backup", "Export file has no repos array");
        }
        if (mode is not ("merge" or "overwrite"))
        {
            throw new BackupException("invalid_mode", $"Unknown restore mode \"{mode}\"");
        }
        if (version == 2)
            ImportDiscovery(root["projects"]!.AsArray(), root["discoveryCandidates"]!.AsArray(), mode, dryRun: true);

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

            if (item["generated"]?["summary"] is JsonObject g)
            {
                lock (_store.Sync)
                {
                    using var cmd = _store.Conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO generated (repo_id, kind, content, model, lang, created_at)
                        VALUES ($id, 'summary', $content, $model, $lang, $ts)
                        ON CONFLICT(repo_id, kind) DO UPDATE SET
                          content = excluded.content, model = excluded.model,
                          lang = excluded.lang, created_at = excluded.created_at
                        """;
                    cmd.Parameters.AddWithValue("$id", repoId);
                    cmd.Parameters.AddWithValue("$content", g["content"]?.GetValue<string>() ?? "");
                    cmd.Parameters.AddWithValue("$model", g["model"]?.GetValue<string>() ?? "unknown");
                    cmd.Parameters.AddWithValue("$lang", g["lang"]?.GetValue<string>() ?? "zh");
                    cmd.Parameters.AddWithValue("$ts", g["createdAt"]?.GetValue<string>() ?? ts);
                    cmd.ExecuteNonQuery();
                }
            }
        }
        if (version == 2)
            ImportDiscovery(root["projects"]!.AsArray(), root["discoveryCandidates"]!.AsArray(), mode);
        return new JsonObject
        {
            ["added"] = added,
            ["skipped"] = skipped,
            ["overwritten"] = overwritten,
        };
    }
}
