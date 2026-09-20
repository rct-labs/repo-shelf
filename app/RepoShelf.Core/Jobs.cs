using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoShelf.Core;

/// <summary>
/// Background jobs: GitHub Stars import and refresh-all. Jobs run on threads,
/// persist progress in `import_jobs` for resume, and support cancellation
/// between units of work.
/// </summary>
public sealed class JobManager
{
    private const int FailureListCap = 100;
    private static string Now() => DateTime.UtcNow.ToString("O");

    private readonly Store _store;
    private readonly GitHubClient _gh;
    private readonly RepoService _repos;
    private readonly int _maxRateLimitWaitMs;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public JobManager(Store store, GitHubClient gh, RepoService repos, int maxRateLimitWaitMs = 90_000)
    {
        _store = store;
        _gh = gh;
        _repos = repos;
        _maxRateLimitWaitMs = maxRateLimitWaitMs;
    }

    private JsonObject? GetRow(string id)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM import_jobs WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            string? S(string c) => reader.IsDBNull(reader.GetOrdinal(c)) ? null : reader.GetString(reader.GetOrdinal(c));
            long L(string c) => reader.GetInt64(reader.GetOrdinal(c));
            return new JsonObject
            {
                ["id"] = S("id"),
                ["type"] = S("type"),
                ["username"] = S("username"),
                ["status"] = S("status"),
                ["options"] = JsonNode.Parse(S("options") ?? "{}"),
                ["nextPage"] = L("next_page"),
                ["processed"] = L("processed"),
                ["added"] = L("added"),
                ["updated"] = L("updated"),
                ["failed"] = L("failed"),
                ["failures"] = JsonNode.Parse(S("failures") ?? "[]"),
                ["error"] = S("error"),
                ["createdAt"] = S("created_at"),
                ["finishedAt"] = S("finished_at"),
            };
        }
    }

    public JsonObject? GetJob(string id) => GetRow(id);

    public JsonArray ListJobs()
    {
        lock (_store.Sync)
        {
            var ids = new List<string>();
            using (var cmd = _store.Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM import_jobs ORDER BY created_at DESC LIMIT 20";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetString(0));
                }
            }
            return new JsonArray(ids.Select(id => (JsonNode?)GetRow(id)).ToArray());
        }
    }

    private void Update(string id, Action<SqliteUpdate> fill)
    {
        lock (_store.Sync)
        {
            var u = new SqliteUpdate(_store.Conn, id);
            fill(u);
            u.Execute();
        }
    }

    private sealed class SqliteUpdate
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
        private readonly string _id;
        private readonly List<string> _sets = new();
        private readonly List<object?> _values = new();

        public SqliteUpdate(Microsoft.Data.Sqlite.SqliteConnection conn, string id)
        {
            _conn = conn;
            _id = id;
        }

        public void Set(string column, object? value)
        {
            _sets.Add($"{column} = $p{_values.Count}");
            _values.Add(value);
        }

        public void Execute()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"UPDATE import_jobs SET {string.Join(", ", _sets)} WHERE id = $id";
            for (var i = 0; i < _values.Count; i++)
            {
                cmd.Parameters.AddWithValue($"$p{i}", _values[i] ?? DBNull.Value);
            }
            cmd.Parameters.AddWithValue("$id", _id);
            cmd.ExecuteNonQuery();
        }
    }

    private string InsertJob(string type, string? username, JsonObject options)
    {
        var id = Guid.NewGuid().ToString();
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO import_jobs (id, type, username, status, options, created_at)
                VALUES ($id, $type, $user, 'running', $options, $ts)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$user", (object?)username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$options", options.ToJsonString());
            cmd.Parameters.AddWithValue("$ts", Now());
            cmd.ExecuteNonQuery();
        }
        return id;
    }

    public JsonObject StartStarsImport(string username, bool includeReadme = true)
    {
        var id = InsertJob("stars", username, new JsonObject { ["includeReadme"] = includeReadme });
        Launch(id, RunStarsImportAsync);
        return GetRow(id)!;
    }

    public JsonObject StartRefreshAll()
    {
        var id = InsertJob("refresh_all", null, new JsonObject());
        Launch(id, RunRefreshAllAsync);
        return GetRow(id)!;
    }

    /// <summary>Import GitHub repository URLs found in a Chromium bookmarks file.</summary>
    public (JsonObject? Job, string? Error) StartBookmarksImport(string? bookmarksPath = null)
    {
        var urls = ChromeBookmarks.ExtractRepoUrls(bookmarksPath, out var sourcePath, out var error);
        if (urls is null)
        {
            return (null, error);
        }
        var id = InsertJob("bookmarks", null, new JsonObject { ["path"] = sourcePath, ["total"] = urls.Count });
        _bookmarkUrls[id] = urls;
        Launch(id, RunBookmarksImportAsync);
        return (GetRow(id), null);
    }

    private readonly ConcurrentDictionary<string, List<string>> _bookmarkUrls = new();

    private async Task RunBookmarksImportAsync(string id, CancellationToken cancel)
    {
        var urls = _bookmarkUrls.TryRemove(id, out var u) ? u : new List<string>();
        var p = LoadProgress(GetRow(id)!);
        try
        {
            foreach (var url in urls)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    // Already in the library: skip instead of refreshing, so a
                    // resumed import spends API quota only on missing repos.
                    var fullName = RepoUrlParser.Parse(url).FullName;
                    if (_repos.FindByFullName(fullName) is not null)
                    {
                        p.Updated += 1;
                        p.Processed += 1;
                        continue;
                    }
                    var result = await _repos.SaveRepoAsync(url);
                    if (result["outcome"]?.GetValue<string>() == "created") p.Added += 1; else p.Updated += 1;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    p.Failed += 1;
                    p.PushFailure(url, ex is ServiceException se ? se.Code : ex is GitHubException ge ? ge.Code : "error");
                }
                p.Processed += 1;
                if (p.Processed % 5 == 0)
                {
                    Flush(id, p);
                }
            }
            Flush(id, p, u2 =>
            {
                u2.Set("status", "done");
                u2.Set("finished_at", Now());
            });
        }
        catch (OperationCanceledException)
        {
            Flush(id, p, u2 =>
            {
                u2.Set("status", "cancelled");
                u2.Set("finished_at", Now());
            });
        }
        await Task.CompletedTask;
    }

    /// <summary>Resume a failed or cancelled stars import from its last committed page.</summary>
    public JsonObject? ResumeJob(string id)
    {
        var job = GetRow(id);
        if (job is null)
        {
            return null;
        }
        if (job["status"]!.GetValue<string>() == "running" || job["type"]!.GetValue<string>() != "stars")
        {
            return job;
        }
        Update(id, u =>
        {
            u.Set("status", "running");
            u.Set("error", null);
            u.Set("finished_at", null);
        });
        Launch(id, RunStarsImportAsync);
        return GetRow(id);
    }

    public bool CancelJob(string id)
    {
        if (_running.TryGetValue(id, out var cts))
        {
            cts.Cancel();
            return true;
        }
        var job = GetRow(id);
        if (job is not null && job["status"]!.GetValue<string>() == "running")
        {
            Update(id, u =>
            {
                u.Set("status", "cancelled");
                u.Set("finished_at", Now());
            });
            return true;
        }
        return false;
    }

    private void Launch(string id, Func<string, CancellationToken, Task> run)
    {
        var cts = new CancellationTokenSource();
        _running[id] = cts;
        Task.Run(async () =>
        {
            try
            {
                await run(id, cts.Token);
            }
            catch (Exception ex)
            {
                Update(id, u =>
                {
                    u.Set("status", "failed");
                    u.Set("error", ex.Message);
                    u.Set("finished_at", Now());
                });
            }
            finally
            {
                _running.TryRemove(id, out _);
                cts.Dispose();
            }
        });
    }

    private sealed class Progress
    {
        public int NextPage;
        public int Processed;
        public int Added;
        public int Updated;
        public int Failed;
        public readonly List<(string Subject, string Code)> Failures = new();

        public void PushFailure(string subject, string code)
        {
            if (Failures.Count < FailureListCap)
            {
                Failures.Add((subject, code));
            }
        }

        public string FailuresJson() => JsonSerializer.Serialize(Failures.Select(f => new { subject = f.Subject, code = f.Code }));
    }

    private Progress LoadProgress(JsonObject job)
    {
        var failures = new List<(string, string)>();
        if (job["failures"] is JsonArray arr)
        {
            foreach (var f in arr)
            {
                failures.Add((f!["subject"]!.GetValue<string>(), f["code"]!.GetValue<string>()));
            }
        }
        var p = new Progress
        {
            NextPage = (int)job["nextPage"]!.GetValue<long>(),
            Processed = (int)job["processed"]!.GetValue<long>(),
            Added = (int)job["added"]!.GetValue<long>(),
            Updated = (int)job["updated"]!.GetValue<long>(),
            Failed = (int)job["failed"]!.GetValue<long>(),
        };
        p.Failures.AddRange(failures);
        return p;
    }

    private void Flush(string id, Progress p, Action<SqliteUpdate>? extra = null) => Update(id, u =>
    {
        u.Set("next_page", p.NextPage);
        u.Set("processed", p.Processed);
        u.Set("added", p.Added);
        u.Set("updated", p.Updated);
        u.Set("failed", p.Failed);
        u.Set("failures", p.FailuresJson());
        extra?.Invoke(u);
    });

    private async Task RunStarsImportAsync(string id, CancellationToken cancel)
    {
        var job = GetRow(id)!;
        var includeReadme = job["options"]?["includeReadme"]?.GetValue<bool>() ?? true;
        var username = job["username"]!.GetValue<string>()!;
        var p = LoadProgress(job);
        var page = p.NextPage;
        var readmeRateLimited = false;

        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                (List<RepoMeta> Items, int? NextPage) pageData;
                try
                {
                    pageData = await _gh.FetchStarredPageAsync(username, page);
                }
                catch (GitHubException ex) when (ex.Code == "rate_limited"
                    && ex.RetryAfterMs is not null && ex.RetryAfterMs <= _maxRateLimitWaitMs)
                {
                    // Honor the server's wait time, but only when bounded.
                    await Task.Delay((int)ex.RetryAfterMs.Value + 250, cancel);
                    pageData = await _gh.FetchStarredPageAsync(username, page);
                }
                if (pageData.Items.Count == 0)
                {
                    break;
                }

                foreach (var meta in pageData.Items)
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        string? readme = null;
                        var readmeTruncated = false;
                        var readmeProvided = false;
                        if (includeReadme && !readmeRateLimited)
                        {
                            readmeProvided = true;
                            try
                            {
                                var readmeResult = await _gh.FetchReadmeAsync(meta.Owner, meta.Name);
                                if (readmeResult is { } r)
                                {
                                    readme = r.Text;
                                    readmeTruncated = r.Truncated;
                                }
                            }
                            catch (GitHubException ex)
                            {
                                p.Failed += 1;
                                p.PushFailure(meta.FullName, $"readme: {ex.Code}");
                                if (ex.Code == "rate_limited")
                                {
                                    readmeRateLimited = true;
                                    readmeProvided = false;
                                    p.PushFailure(meta.FullName, "readme: remaining README fetches skipped (rate limit)");
                                }
                            }
                        }
                        var result = _repos.UpsertSource(meta, readmeProvided ? readme : null, readmeProvided, readmeTruncated, starred: true);
                        lock (_store.Sync)
                        {
                            using var seen = _store.Conn.CreateCommand();
                            seen.CommandText = "INSERT OR IGNORE INTO job_seen (job_id, github_id) VALUES ($job, $gid)";
                            seen.Parameters.AddWithValue("$job", id);
                            seen.Parameters.AddWithValue("$gid", meta.GithubId);
                            seen.ExecuteNonQuery();
                        }
                        if (result.Created) p.Added += 1; else p.Updated += 1;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        p.Failed += 1;
                        p.PushFailure(meta.FullName, ex is GitHubException ge ? ge.Code : "error");
                    }
                    p.Processed += 1;
                }
                p.NextPage = page + 1;
                Flush(id, p);
                if (pageData.NextPage is null)
                {
                    break;
                }
                page = pageData.NextPage.Value;
            }

            // The listing completed: anything still flagged as starred but
            // absent from this run was unstarred upstream. Flag, never delete.
            lock (_store.Sync)
            {
                using var reconcile = _store.Conn.CreateCommand();
                reconcile.CommandText = """
                    UPDATE repos SET starred_upstream = 0
                    WHERE starred_upstream = 1
                      AND github_id NOT IN (SELECT github_id FROM job_seen WHERE job_id = $job)
                    """;
                reconcile.Parameters.AddWithValue("$job", id);
                reconcile.ExecuteNonQuery();
            }
            Flush(id, p, u =>
            {
                u.Set("status", "done");
                u.Set("finished_at", Now());
            });
        }
        catch (OperationCanceledException)
        {
            Flush(id, p, u =>
            {
                u.Set("status", "cancelled");
                u.Set("finished_at", Now());
            });
        }
    }

    private async Task RunRefreshAllAsync(string id, CancellationToken cancel)
    {
        var job = GetRow(id)!;
        var p = LoadProgress(job);
        List<long> ids;
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM repos ORDER BY id";
            using var reader = cmd.ExecuteReader();
            ids = new List<long>();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }
        try
        {
            foreach (var repoId in ids)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    await _repos.RefreshRepoAsync(repoId);
                    p.Updated += 1;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    p.Failed += 1;
                    p.PushFailure($"#{repoId}", ex is ServiceException se ? se.Code : ex is GitHubException ge ? ge.Code : "error");
                }
                p.Processed += 1;
                if (p.Processed % 10 == 0)
                {
                    Flush(id, p);
                }
            }
            Flush(id, p, u =>
            {
                u.Set("status", "done");
                u.Set("finished_at", Now());
            });
        }
        catch (OperationCanceledException)
        {
            Flush(id, p, u =>
            {
                u.Set("status", "cancelled");
                u.Set("finished_at", Now());
            });
        }
        await Task.CompletedTask;
    }
}
