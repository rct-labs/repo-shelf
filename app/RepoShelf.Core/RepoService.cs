using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RepoShelf.Core;

/// <summary>Service-layer failure with an HTTP status for the API layer.</summary>
public sealed class ServiceException : Exception
{
    public string Code { get; }
    public int HttpStatus { get; }
    public bool Retryable { get; }
    public long? RetryAfterMs { get; }

    public ServiceException(string code, int httpStatus, string message, bool retryable = false, long? retryAfterMs = null)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
        Retryable = retryable;
        RetryAfterMs = retryAfterMs;
    }
}

/// <summary>
/// Repository storage service: identity, capture, refresh and personal
/// annotations. Source metadata lives in `repos`, personal knowledge in
/// `annotations`; refresh paths only ever write to `repos`.
/// </summary>
public sealed class RepoService
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);
    private const int IndexTextLimit = 200_000;

    private readonly Store _store;
    private readonly GitHubClient _gh;

    public RepoService(Store store, GitHubClient gh)
    {
        _store = store;
        _gh = gh;
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    private static ServiceException MapGitHubError(GitHubException err, string? notFoundMessage = null) => err.Code switch
    {
        "not_found" => new ServiceException("repository_not_found", 404, notFoundMessage ?? err.Message),
        "rate_limited" => new ServiceException("github_rate_limited", 502, err.Message, retryable: true, retryAfterMs: err.RetryAfterMs),
        "forbidden" => new ServiceException("github_forbidden", 502, err.Message),
        "unauthorized" => new ServiceException("github_unauthorized", 502, err.Message),
        _ => new ServiceException("upstream_unavailable", 502, err.Message, retryable: true),
    };

    private static List<string> ParseJsonArray(string? text)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(text ?? "[]") ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Rebuild the FTS row for one repository from source + personal fields.</summary>
    public void ReindexRepo(long repoId)
    {
        lock (_store.Sync)
        {
            using var del = _store.Conn.CreateCommand();
            del.CommandText = "DELETE FROM search_fts WHERE rowid = $id";
            del.Parameters.AddWithValue("$id", repoId);
            del.ExecuteNonQuery();

            using var sel = _store.Conn.CreateCommand();
            sel.CommandText = """
                SELECT r.name, r.owner, r.description, r.topics, r.readme, a.reason, a.notes
                FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id
                WHERE r.id = $id
                """;
            sel.Parameters.AddWithValue("$id", repoId);
            using var reader = sel.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }
            string Col(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
            var topics = string.Join(' ', ParseJsonArray(Col(3)));
            var readme = Col(4);
            if (readme.Length > IndexTextLimit)
            {
                readme = readme[..IndexTextLimit];
            }
            using var ins = _store.Conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO search_fts (rowid, name, owner, description, topics, reason, notes, readme)
                VALUES ($id, $name, $owner, $desc, $topics, $reason, $notes, $readme)
                """;
            ins.Parameters.AddWithValue("$id", repoId);
            ins.Parameters.AddWithValue("$name", Tokenizer.TokenizeForIndex(Col(0)));
            ins.Parameters.AddWithValue("$owner", Tokenizer.TokenizeForIndex(Col(1)));
            ins.Parameters.AddWithValue("$desc", Tokenizer.TokenizeForIndex(Col(2)));
            ins.Parameters.AddWithValue("$topics", Tokenizer.TokenizeForIndex(topics));
            ins.Parameters.AddWithValue("$reason", Tokenizer.TokenizeForIndex(Col(5)));
            ins.Parameters.AddWithValue("$notes", Tokenizer.TokenizeForIndex(Col(6)));
            ins.Parameters.AddWithValue("$readme", Tokenizer.TokenizeForIndex(readme));
            ins.ExecuteNonQuery();
        }
    }

    private const string SelectJoined = """
        SELECT r.*, a.reason, a.notes,
               a.tags AS a_tags, a.projects AS a_projects, a.status,
               a.created_at AS a_created_at, a.updated_at AS a_updated_at
        FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id
        """;

    private static JsonObject ToRecord(SqliteDataReader r)
    {
        string? S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
        long? L(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));
        var fetchedAt = S("fetched_at");
        var stale = fetchedAt is null || DateTime.UtcNow - DateTime.Parse(fetchedAt).ToUniversalTime() > StaleAfter;
        return new JsonObject
        {
            ["id"] = r.GetInt64(r.GetOrdinal("id")),
            ["githubId"] = r.GetInt64(r.GetOrdinal("github_id")),
            ["owner"] = S("owner"),
            ["name"] = S("name"),
            ["fullName"] = S("full_name"),
            ["htmlUrl"] = S("html_url"),
            ["description"] = S("description") ?? "",
            ["topics"] = new JsonArray(ParseJsonArray(S("topics")).Select(t => JsonValue.Create(t)).ToArray()),
            ["language"] = (JsonValue?)S("language") ?? JsonValue.Create((string?)null),
            ["licenseId"] = (JsonValue?)S("license_id") ?? JsonValue.Create((string?)null),
            ["archived"] = r.GetInt64(r.GetOrdinal("archived")) != 0,
            ["pushedAt"] = S("pushed_at"),
            ["stars"] = L("stars"),
            ["defaultBranch"] = S("default_branch"),
            ["readme"] = S("readme"),
            ["readmeTruncated"] = r.GetInt64(r.GetOrdinal("readme_truncated")) != 0,
            ["fetchedAt"] = fetchedAt,
            ["refreshStatus"] = S("refresh_status"),
            ["stale"] = stale,
            ["starredUpstream"] = r.GetInt64(r.GetOrdinal("starred_upstream")) != 0,
            ["seenInImport"] = r.GetInt64(r.GetOrdinal("seen_in_import")) != 0,
            ["createdAt"] = S("created_at"),
            ["updatedAt"] = S("updated_at"),
            ["annotation"] = new JsonObject
            {
                ["reason"] = S("reason") ?? "",
                ["notes"] = S("notes") ?? "",
                ["tags"] = new JsonArray(ParseJsonArray(S("a_tags")).Select(t => JsonValue.Create(t)).ToArray()),
                ["projects"] = new JsonArray(ParseJsonArray(S("a_projects")).Select(p => JsonValue.Create(p)).ToArray()),
                ["status"] = S("status") ?? "inbox",
                ["createdAt"] = S("a_created_at"),
                ["updatedAt"] = S("a_updated_at"),
            },
        };
    }

    public JsonObject? GetRepo(long id)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = $"{SelectJoined} WHERE r.id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ToRecord(reader) : null;
        }
    }

    public JsonObject? FindByFullName(string fullName)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = $"{SelectJoined} WHERE r.full_name = $fn COLLATE NOCASE ORDER BY r.fetched_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$fn", fullName);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ToRecord(reader) : null;
        }
    }

    private void EnsureAnnotation(long repoId)
    {
        using var cmd = _store.Conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO annotations (repo_id, created_at, updated_at) VALUES ($id, $ts, $ts)
            ON CONFLICT(repo_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id", repoId);
        cmd.Parameters.AddWithValue("$ts", Now());
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Insert or update source metadata keyed by the durable GitHub repository
    /// id. Never touches annotation fields. readme == null with keepReadme
    /// semantics is controlled by <paramref name="readmeProvided"/>.
    /// </summary>
    public (long RepoId, bool Created, bool Renamed) UpsertSource(
        RepoMeta meta, string? readme = null, bool readmeProvided = false, bool readmeTruncated = false,
        bool? starred = null, string refreshStatus = "ok")
    {
        lock (_store.Sync)
        {
            var ts = Now();
            long? existingId = null;
            string? existingFullName = null;
            using (var find = _store.Conn.CreateCommand())
            {
                find.CommandText = "SELECT id, full_name FROM repos WHERE github_id = $gid";
                find.Parameters.AddWithValue("$gid", meta.GithubId);
                using var reader = find.ExecuteReader();
                if (reader.Read())
                {
                    existingId = reader.GetInt64(0);
                    existingFullName = reader.GetString(1);
                }
            }

            long repoId;
            var created = false;
            if (existingId is not null)
            {
                repoId = existingId.Value;
                using var upd = _store.Conn.CreateCommand();
                upd.CommandText = $"""
                    UPDATE repos SET owner = $owner, name = $name, full_name = $fn, html_url = $url,
                        description = $desc, topics = $topics, language = $lang, license_id = $lic,
                        archived = $arch, pushed_at = $pushed, stars = $stars, default_branch = $branch,
                        fetched_at = $ts, refresh_status = $rs, updated_at = $ts
                        {(starred is null ? "" : ", starred_upstream = $starred")}
                        {(starred == true ? ", seen_in_import = 1" : "")}
                        {(readmeProvided ? ", readme = $readme, readme_truncated = $rtrunc" : "")}
                    WHERE id = $id
                    """;
                AddMetaParams(upd, meta);
                upd.Parameters.AddWithValue("$ts", ts);
                upd.Parameters.AddWithValue("$rs", refreshStatus);
                if (starred is not null)
                {
                    upd.Parameters.AddWithValue("$starred", starred.Value ? 1 : 0);
                }
                if (readmeProvided)
                {
                    upd.Parameters.AddWithValue("$readme", (object?)readme ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$rtrunc", readmeTruncated ? 1 : 0);
                }
                upd.Parameters.AddWithValue("$id", repoId);
                upd.ExecuteNonQuery();
            }
            else
            {
                using var ins = _store.Conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO repos (github_id, owner, name, full_name, html_url, description, topics,
                        language, license_id, archived, pushed_at, stars, default_branch, readme,
                        readme_truncated, fetched_at, refresh_status, starred_upstream, seen_in_import,
                        created_at, updated_at)
                    VALUES ($gid, $owner, $name, $fn, $url, $desc, $topics, $lang, $lic, $arch, $pushed,
                        $stars, $branch, $readme, $rtrunc, $ts, $rs, $starred, $seen, $ts, $ts)
                    """;
                ins.Parameters.AddWithValue("$gid", meta.GithubId);
                AddMetaParams(ins, meta);
                ins.Parameters.AddWithValue("$readme", readmeProvided ? (object?)readme ?? DBNull.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$rtrunc", readmeTruncated ? 1 : 0);
                ins.Parameters.AddWithValue("$ts", ts);
                ins.Parameters.AddWithValue("$rs", refreshStatus);
                ins.Parameters.AddWithValue("$starred", starred == true ? 1 : 0);
                ins.Parameters.AddWithValue("$seen", starred == true ? 1 : 0);
                ins.ExecuteNonQuery();
                using (var idCmd = _store.Conn.CreateCommand())
                {
                    idCmd.CommandText = "SELECT last_insert_rowid()";
                    repoId = (long)(idCmd.ExecuteScalar() ?? 0L);
                }
                created = true;
            }
            EnsureAnnotation(repoId);
            ReindexRepo(repoId);
            return (repoId, created, existingFullName is not null && existingFullName != meta.FullName);
        }
    }

    private static void AddMetaParams(SqliteCommand cmd, RepoMeta meta)
    {
        cmd.Parameters.AddWithValue("$owner", meta.Owner);
        cmd.Parameters.AddWithValue("$name", meta.Name);
        cmd.Parameters.AddWithValue("$fn", meta.FullName);
        cmd.Parameters.AddWithValue("$url", meta.HtmlUrl);
        cmd.Parameters.AddWithValue("$desc", meta.Description);
        cmd.Parameters.AddWithValue("$topics", JsonSerializer.Serialize(meta.Topics));
        cmd.Parameters.AddWithValue("$lang", (object?)meta.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lic", (object?)meta.LicenseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$arch", meta.Archived ? 1 : 0);
        cmd.Parameters.AddWithValue("$pushed", (object?)meta.PushedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stars", (object?)meta.Stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)meta.DefaultBranch ?? DBNull.Value);
    }

    private bool MergeAnnotationOnSave(long repoId, string reason, List<string> tags)
    {
        lock (_store.Sync)
        {
            EnsureAnnotation(repoId);
            string currentReason;
            List<string> currentTags;
            using (var cmd = _store.Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT reason, tags FROM annotations WHERE repo_id = $id";
                cmd.Parameters.AddWithValue("$id", repoId);
                using var reader = cmd.ExecuteReader();
                reader.Read();
                currentReason = reader.GetString(0);
                currentTags = ParseJsonArray(reader.GetString(1));
            }
            var changed = false;
            reason = reason.Trim();
            if (reason.Length > 2000)
            {
                reason = reason[..2000];
            }
            if (reason.Length > 0 && currentReason.Trim().Length == 0)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.CommandText = "UPDATE annotations SET reason = $v WHERE repo_id = $id";
                cmd.Parameters.AddWithValue("$v", reason);
                cmd.Parameters.AddWithValue("$id", repoId);
                cmd.ExecuteNonQuery();
                changed = true;
            }
            var merged = currentTags.Union(tags.Where(t => t.Trim().Length > 0).Take(30)).ToList();
            if (merged.Count != currentTags.Count)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.CommandText = "UPDATE annotations SET tags = $v WHERE repo_id = $id";
                cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(merged));
                cmd.Parameters.AddWithValue("$id", repoId);
                cmd.ExecuteNonQuery();
                changed = true;
            }
            if (changed)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.CommandText = "UPDATE annotations SET updated_at = $ts WHERE repo_id = $id";
                cmd.Parameters.AddWithValue("$ts", Now());
                cmd.Parameters.AddWithValue("$id", repoId);
                cmd.ExecuteNonQuery();
                ReindexRepo(repoId);
            }
            return changed;
        }
    }

    /// <summary>Save a repository by URL. See the Node reference for semantics.</summary>
    public async Task<JsonObject> SaveRepoAsync(string? url, string reason = "", List<string>? tags = null)
    {
        RepoUrlParser.Parsed parsed;
        try
        {
            parsed = RepoUrlParser.Parse(url);
        }
        catch (RepoUrlParser.InvalidUrlException ex)
        {
            throw new ServiceException("invalid_url", 400, ex.Message);
        }

        var existing = FindByFullName(parsed.FullName);
        if (existing is not null)
        {
            // Already in the library: never auto-refresh. GitHub quota is a
            // budget; the user refreshes explicitly from the detail view.
            var changed = MergeAnnotationOnSave(existing["id"]!.GetValue<long>(), reason, tags ?? new());
            return new JsonObject
            {
                ["outcome"] = "already_exists",
                ["repo"] = GetRepo(existing["id"]!.GetValue<long>()),
                ["refreshed"] = false,
                ["refreshError"] = null,
                ["annotationChanged"] = changed,
            };
        }

        RepoMeta meta;
        try
        {
            meta = await _gh.FetchRepoAsync(parsed.Owner, parsed.Repo);
        }
        catch (GitHubException ex)
        {
            throw MapGitHubError(ex, $"Repository {parsed.FullName} was not found on GitHub");
        }
        string? readme = null;
        var readmeTruncated = false;
        string? readmeError = null;
        try
        {
            var result = await _gh.FetchReadmeAsync(meta.Owner, meta.Name);
            if (result is { } r)
            {
                readme = r.Text;
                readmeTruncated = r.Truncated;
            }
        }
        catch (GitHubException ex)
        {
            readmeError = ex.Code;
        }
        var (repoId, _, _) = UpsertSource(meta, readme, readmeProvided: true, readmeTruncated: readmeTruncated);
        MergeAnnotationOnSave(repoId, reason, tags ?? new());
        return new JsonObject
        {
            ["outcome"] = "created",
            ["repo"] = GetRepo(repoId),
            ["readmeError"] = readmeError,
        };
    }

    private void MarkRefreshStatus(long repoId, string status)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "UPDATE repos SET refresh_status = $s, updated_at = $ts WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$ts", Now());
            cmd.Parameters.AddWithValue("$id", repoId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Refresh source metadata/README from GitHub. Personal fields are untouched.</summary>
    public async Task<(JsonObject Repo, bool Renamed)> RefreshRepoAsync(long repoId)
    {
        var repo = GetRepo(repoId) ?? throw new ServiceException("not_found", 404, $"No repository with id {repoId}");
        var owner = repo["owner"]!.GetValue<string>();
        var name = repo["name"]!.GetValue<string>();
        var githubId = repo["githubId"]!.GetValue<long>();

        RepoMeta meta;
        try
        {
            meta = await _gh.FetchRepoAsync(owner, name);
        }
        catch (GitHubException ex) when (ex.Code == "not_found")
        {
            // Possibly renamed or transferred: resolve the durable id instead.
            try
            {
                meta = await _gh.FetchRepoByIdAsync(githubId);
            }
            catch (GitHubException ex2)
            {
                var status = ex2.Code == "not_found" ? "not_found"
                    : ex2.Code is "forbidden" or "unauthorized" ? "inaccessible" : "error";
                MarkRefreshStatus(repoId, status);
                throw MapGitHubError(ex2, $"{repo["fullName"]!.GetValue<string>()} no longer exists on GitHub (deleted or made private)");
            }
        }
        catch (GitHubException ex)
        {
            MarkRefreshStatus(repoId, ex.Code is "forbidden" or "unauthorized" ? "inaccessible" : "error");
            throw MapGitHubError(ex);
        }

        string? readme = null;
        var readmeProvided = false;
        var readmeTruncated = false;
        try
        {
            var result = await _gh.FetchReadmeAsync(meta.Owner, meta.Name);
            readmeProvided = true;
            if (result is { } r)
            {
                readme = r.Text;
                readmeTruncated = r.Truncated;
            }
        }
        catch (GitHubException)
        {
            // Keep the previous snapshot when the README fetch fails.
        }
        var (_, _, renamed) = UpsertSource(meta, readme, readmeProvided, readmeTruncated);
        return (GetRepo(repoId)!, renamed);
    }

    public JsonObject UpdateAnnotation(long repoId, JsonObject patch)
    {
        lock (_store.Sync)
        {
            using (var exists = _store.Conn.CreateCommand())
            {
                exists.CommandText = "SELECT id FROM repos WHERE id = $id";
                exists.Parameters.AddWithValue("$id", repoId);
                if (exists.ExecuteScalar() is null)
                {
                    throw new ServiceException("not_found", 404, $"No repository with id {repoId}");
                }
            }
            EnsureAnnotation(repoId);

            var sets = new List<string>();
            var values = new List<object>();
            if (patch.TryGetPropertyValue("reason", out var reason))
            {
                if (reason is not JsonValue rv || !rv.TryGetValue<string>(out var v) || v.Length > 2000)
                {
                    throw new ServiceException("invalid_field", 400, "reason must be a string of at most 2000 characters");
                }
                sets.Add("reason = $v" + values.Count);
                values.Add(v);
            }
            if (patch.TryGetPropertyValue("notes", out var notes))
            {
                if (notes is not JsonValue nv || !nv.TryGetValue<string>(out var v) || v.Length > 100_000)
                {
                    throw new ServiceException("invalid_field", 400, "notes must be a string of at most 100000 characters");
                }
                sets.Add("notes = $v" + values.Count);
                values.Add(v);
            }
            if (patch.TryGetPropertyValue("status", out var status))
            {
                if (status is not JsonValue sv || !sv.TryGetValue<string>(out var v) || !Store.Statuses.Contains(v))
                {
                    throw new ServiceException("invalid_field", 400, $"status must be one of: {string.Join(", ", Store.Statuses)}");
                }
                sets.Add("status = $v" + values.Count);
                values.Add(v);
            }
            foreach (var field in new[] { "tags", "projects" })
            {
                if (patch.TryGetPropertyValue(field, out var node))
                {
                    var arr = node as JsonArray;
                    var valid = arr is not null && arr.Count <= 50 && arr.All(x =>
                        x is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length <= 100);
                    if (!valid)
                    {
                        throw new ServiceException("invalid_field", 400, $"{field} must be an array of up to 50 strings of at most 100 characters");
                    }
                    var cleaned = arr!.Select(x => x!.AsValue().GetValue<string>().Trim()).Where(s => s.Length > 0).Distinct().ToList();
                    sets.Add($"{field} = $v{values.Count}");
                    values.Add(JsonSerializer.Serialize(cleaned));
                }
            }
            if (sets.Count == 0)
            {
                throw new ServiceException("invalid_field", 400, "No updatable fields supplied");
            }
            using (var cmd = _store.Conn.CreateCommand())
            {
                cmd.CommandText = $"UPDATE annotations SET {string.Join(", ", sets)}, updated_at = $ts WHERE repo_id = $id";
                for (var i = 0; i < values.Count; i++)
                {
                    cmd.Parameters.AddWithValue($"$v{i}", values[i]);
                }
                cmd.Parameters.AddWithValue("$ts", Now());
                cmd.Parameters.AddWithValue("$id", repoId);
                cmd.ExecuteNonQuery();
            }
            ReindexRepo(repoId);
        }
        return GetRepo(repoId)!["annotation"]!.AsObject();
    }

    public void DeleteRepo(long repoId)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM repos WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", repoId);
            if (cmd.ExecuteNonQuery() == 0)
            {
                throw new ServiceException("not_found", 404, $"No repository with id {repoId}");
            }
            using var fts = _store.Conn.CreateCommand();
            fts.CommandText = "DELETE FROM search_fts WHERE rowid = $id";
            fts.Parameters.AddWithValue("$id", repoId);
            fts.ExecuteNonQuery();
        }
    }

    /// <summary>Distinct values used to populate filter dropdowns in the UI.</summary>
    public JsonObject ListFilterOptions()
    {
        lock (_store.Sync)
        {
            List<string> Query(string sql)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.CommandText = sql;
                using var reader = cmd.ExecuteReader();
                var list = new List<string>();
                while (reader.Read())
                {
                    list.Add(reader.GetString(0));
                }
                return list;
            }
            var languages = Query("SELECT DISTINCT language FROM repos WHERE language IS NOT NULL ORDER BY language COLLATE NOCASE");
            var tags = Query("SELECT DISTINCT je.value FROM annotations a, json_each(a.tags) je WHERE a.tags != '[]' ORDER BY 1");
            var projects = Query("SELECT DISTINCT je.value FROM annotations a, json_each(a.projects) je WHERE a.projects != '[]' ORDER BY 1");
            using var countCmd = _store.Conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM repos";
            var total = (long)(countCmd.ExecuteScalar() ?? 0L);
            return new JsonObject
            {
                ["languages"] = new JsonArray(languages.Select(x => JsonValue.Create(x)).ToArray()),
                ["tags"] = new JsonArray(tags.Select(x => JsonValue.Create(x)).ToArray()),
                ["projects"] = new JsonArray(projects.Select(x => JsonValue.Create(x)).ToArray()),
                ["total"] = total,
            };
        }
    }
}
