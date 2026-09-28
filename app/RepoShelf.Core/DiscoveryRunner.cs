using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace RepoShelf.Core;

/// <summary>
/// Background discovery run (recall → score → insert pending candidates), the weekly scheduler
/// and the run/candidates API. One run at a time; progress is persisted after each scored item
/// and each project; cancellation is honoured between items.
/// </summary>
public sealed class DiscoveryRunner : IDiscoveryRoutes, IDisposable
{
    public const int MinScore = 60;
    public const int MaxPerProject = 5;
    public static readonly TimeSpan ScheduleInterval = TimeSpan.FromDays(7);
    // After a failed, cancelled or rate-limited run the hourly tick must not retry every hour (quota).
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    public const string LangSetting = "discovery_lang";

    /// <summary>The owner's language for AI text: last language the UI sent (manual
    /// run or profile), else the OS UI language — never a hard-coded default.</summary>
    public static string ResolveLang(Store store) =>
        store.GetSetting(LangSetting) is { Length: > 0 } saved ? saved
        : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";
    private static readonly string[] States = { "pending", "later", "accepted", "dismissed" };

    private readonly Store _store;
    private readonly DeepSeekClient _ai;
    private readonly DiscoveryStore _projects;
    private readonly DiscoveryRecall _recall;
    private readonly DiscoveryScorer _scorer;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _sync = new();
    private string? _runningId;
    private CancellationTokenSource? _runCancel;
    private Task? _runTask;
    private CancellationTokenSource? _schedulerCancel;
    private Task? _schedulerTask;
    private bool _disposed;

    /// <summary>Raised once at the end of a run that inserted at least one candidate (any final status).</summary>
    public event Action<int>? NewCandidates;

    public DiscoveryRunner(Store store, GitHubClient github, DeepSeekClient ai, DiscoveryBudget? budget = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _ai = ai;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _projects = new DiscoveryStore(store);
        _recall = new DiscoveryRecall(store, github, budget, _clock);
        _scorer = new DiscoveryScorer(store, ai);
        // A row still "running" belongs to a process that died; it would block runs and the schedule forever.
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "UPDATE discovery_runs SET status = 'failed', error = 'interrupted', finished_at = $now WHERE status = 'running'";
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public DiscoveryRunner(ServiceHost host) : this(host.Store, host.GitHub, host.DeepSeek)
    {
    }

#pragma warning disable CA2255 // Spec extension point: modules self-register without editing DiscoveryApi.
    [ModuleInitializer]
    internal static void Register()
    {
        DiscoveryRoutes.Register(host => host.Feature(h => new DiscoveryRunner(h)));
        DiscoveryRoutes.RegisterStartHook(host => host.Feature(h => new DiscoveryRunner(h)).StartScheduler());
    }
#pragma warning restore CA2255

    private string Now() => _clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // ---- runs ---------------------------------------------------------------------------------

    public bool IsRunning
    {
        get { lock (_sync) return _runningId is not null; }
    }

    /// <summary>Starts a run in the background. 409 <c>run_in_progress</c> when one is running.</summary>
    public JsonObject Start(string trigger = "manual", string? lang = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runningId is not null) throw new ServiceException("run_in_progress", 409, "A discovery run is already in progress");
            if (!_ai.IsConfigured)
                throw new ServiceException("ai_not_configured", 400, "DeepSeek API key is not configured. Set it in Settings.");
            var projects = ActiveProjects();
            if (projects.Count == 0)
                throw new ServiceException("no_active_projects", 400, "Add a project, generate its profile and keep it unpaused first");

            // Scheduled runs reuse the language of the last manual run.
            if (!string.IsNullOrWhiteSpace(lang)) _store.SetSetting(LangSetting, lang.Trim());
            var reasonLang = string.IsNullOrWhiteSpace(lang) ? ResolveLang(_store) : lang.Trim();

            var id = Guid.NewGuid().ToString();
            var progress = new JsonObject
            {
                ["projectsTotal"] = projects.Count, ["projectsDone"] = 0, ["currentProject"] = null,
                ["scored"] = 0, ["rejected"] = 0, ["inserted"] = 0, ["errors"] = new JsonArray(), ["rateLimitResetAt"] = null,
            };
            lock (_store.Sync)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO discovery_runs (id, status, trigger, started_at, progress, new_candidates)
                    VALUES ($id, 'running', $trigger, $now, $progress, 0)
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$trigger", trigger);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.Parameters.AddWithValue("$progress", progress.ToJsonString());
                cmd.ExecuteNonQuery();
            }
            var cancel = new CancellationTokenSource();
            _runningId = id;
            _runCancel = cancel;
            _runTask = Task.Run(() => RunAsync(id, projects, reasonLang, progress, cancel.Token));
            return GetRun(id)!;
        }
    }

    /// <summary>True when a running run was signalled; false when it already finished. 404 for unknown ids.</summary>
    public bool Cancel(string id)
    {
        lock (_sync)
        {
            if (_runningId == id)
            {
                _runCancel!.Cancel();
                return true;
            }
        }
        return GetRun(id) is null ? throw new ServiceException("not_found", 404, "Run not found") : false;
    }

    /// <summary>Test hook: waits for the current run, if any, to reach a final status.</summary>
    public Task WaitAsync()
    {
        lock (_sync) return _runTask ?? Task.CompletedTask;
    }

    private List<JsonObject> ActiveProjects() =>
        _projects.ListProjects().OfType<JsonObject>()
            .Where(p => !p["paused"]!.GetValue<bool>() && p["profiledAt"] is not null)
            .ToList();

    private async Task RunAsync(string id, List<JsonObject> projects, string lang, JsonObject progress, CancellationToken cancel)
    {
        var status = "done";
        string? error = null;
        var inserted = 0;
        try
        {
            foreach (var project in projects)
            {
                cancel.ThrowIfCancellationRequested();
                progress["currentProject"] = project["name"]!.GetValue<string>();
                SaveProgress(id, progress, inserted);
                var scored = new List<(RecalledRepo Item, ScoreResult Score)>();
                try
                {
                    await foreach (var item in _recall.RecallAsync(project, cancel))
                    {
                        var result = await _scorer.ScoreAsync(project, item, lang, cancel);
                        if (result is null) Increment(progress, "rejected");
                        else
                        {
                            Increment(progress, "scored");
                            scored.Add((item, result));
                        }
                        SaveProgress(id, progress, inserted);
                    }
                }
                catch (GitHubException ex) when (ex.Code != "rate_limited")
                {
                    // One project's failed search must not cost the other projects their week.
                    progress["errors"]!.AsArray().Add(new JsonObject { ["project"] = project["name"]!.GetValue<string>(), ["code"] = ex.Code });
                }
                finally
                {
                    // Rate limit, cancellation and AI failures keep what was already scored.
                    inserted += Insert(id, project["id"]!.GetValue<long>(), scored, lang);
                    progress["inserted"] = inserted;
                }
                Increment(progress, "projectsDone");
                SaveProgress(id, progress, inserted);
            }
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // Clients report an aborted request as their own error codes (e.g. ai_unreachable).
            status = "cancelled";
        }
        catch (GitHubException ex) when (ex.Code == "rate_limited")
        {
            status = "failed";
            error = ex.Message;
            progress["rateLimitResetAt"] = ex.RateLimitResetAt?.ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ServiceException ex)
        {
            status = "failed";
            error = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            status = "failed";
            error = ex is GitHubException ge ? $"{ge.Code}: {ge.Message}" : ex.Message;
        }

        progress["currentProject"] = null;
        progress["inserted"] = inserted;
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                UPDATE discovery_runs SET status = $status, error = $error, finished_at = $now,
                  progress = $progress, new_candidates = $inserted WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$progress", progress.ToJsonString());
            cmd.Parameters.AddWithValue("$inserted", inserted);
            cmd.ExecuteNonQuery();
        }
        CancellationTokenSource? finished;
        lock (_sync)
        {
            finished = _runCancel;
            _runningId = null;
            _runCancel = null;
        }
        finished?.Dispose();
        if (inserted > 0)
        {
            try { NewCandidates?.Invoke(inserted); }
            catch (Exception) { /* A notification failure must not affect the stored run. */ }
        }
    }

    private static void Increment(JsonObject progress, string key) => progress[key] = progress[key]!.GetValue<int>() + 1;

    private void SaveProgress(string id, JsonObject progress, int inserted)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "UPDATE discovery_runs SET progress = $progress, new_candidates = $inserted WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$progress", progress.ToJsonString());
            cmd.Parameters.AddWithValue("$inserted", inserted);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Inserts the top <see cref="MaxPerProject"/> items scoring at least <see cref="MinScore"/> as pending.</summary>
    private int Insert(string runId, long projectId, List<(RecalledRepo Item, ScoreResult Score)> scored, string lang)
    {
        var keep = scored.Where(x => x.Score.Score >= MinScore)
            .OrderByDescending(x => x.Score.Score).ThenByDescending(x => x.Item.StarsPerMonth).ThenBy(x => x.Item.Repo.GithubId)
            .Take(MaxPerProject).ToList();
        if (keep.Count == 0) return 0;
        var count = 0;
        lock (_store.Sync)
        {
            using var tx = _store.Conn.BeginTransaction();
            foreach (var (item, score) in keep)
            {
                var repo = item.Repo;
                using var cmd = _store.Conn.CreateCommand();
                cmd.Transaction = tx;
                // The project may have been deleted while the run was scoring it.
                cmd.CommandText = """
                    INSERT OR IGNORE INTO discovery_candidates (project_id, github_id, full_name, html_url, description,
                      language, license_id, stars, stars_per_month, pushed_at, created_at_upstream, score, matched_need,
                      cost, reason, reason_lang, model, state, run_id, proposed_at)
                    SELECT $project, $github, $fullName, $htmlUrl, $description, $language, $license, $stars, $spm,
                      $pushed, $created, $score, $need, $cost, $reason, $lang, $model, 'pending', $run, $now
                    WHERE EXISTS (SELECT 1 FROM projects WHERE id = $project)
                    """;
                cmd.Parameters.AddWithValue("$project", projectId);
                cmd.Parameters.AddWithValue("$github", repo.GithubId);
                cmd.Parameters.AddWithValue("$fullName", repo.FullName);
                cmd.Parameters.AddWithValue("$htmlUrl", repo.HtmlUrl);
                cmd.Parameters.AddWithValue("$description", repo.Description);
                cmd.Parameters.AddWithValue("$language", (object?)repo.Language ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$license", (object?)repo.LicenseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$stars", (object?)repo.Stars ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$spm", item.StarsPerMonth);
                cmd.Parameters.AddWithValue("$pushed", (object?)repo.PushedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$created", (object?)repo.CreatedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$score", score.Score);
                cmd.Parameters.AddWithValue("$need", score.MatchedNeed);
                cmd.Parameters.AddWithValue("$cost", score.Cost);
                cmd.Parameters.AddWithValue("$reason", score.Reason);
                cmd.Parameters.AddWithValue("$lang", lang);
                cmd.Parameters.AddWithValue("$model", _ai.Model);
                cmd.Parameters.AddWithValue("$run", runId);
                cmd.Parameters.AddWithValue("$now", Now());
                count += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return count;
    }

    public JsonObject? GetRun(string id) => QueryRun("SELECT * FROM discovery_runs WHERE id = $id", id);

    public JsonObject? LatestRun() => QueryRun("SELECT * FROM discovery_runs ORDER BY started_at DESC, rowid DESC LIMIT 1", null);

    private JsonObject? QueryRun(string sql, string? id)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = sql;
            if (id is not null) cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            string? Text(string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));
            return new JsonObject
            {
                ["id"] = Text("id"), ["status"] = Text("status"), ["trigger"] = Text("trigger"),
                ["startedAt"] = Text("started_at"), ["finishedAt"] = Text("finished_at"),
                ["progress"] = JsonNode.Parse(Text("progress") ?? "{}"), ["error"] = Text("error"),
                ["newCandidates"] = reader.GetInt64(reader.GetOrdinal("new_candidates")),
            };
        }
    }

    // ---- scheduler ----------------------------------------------------------------------------

    /// <summary>
    /// Due when DeepSeek is configured, at least one project is profiled and unpaused, nothing is
    /// running, the last done run finished over 7 days ago, and no run of any status started in the
    /// last 24 hours. Reads only the local database.
    /// </summary>
    public bool IsScheduledRunDue()
    {
        if (IsRunning || !_ai.IsConfigured || ActiveProjects().Count == 0) return false;
        var now = _clock();
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                SELECT (SELECT MAX(finished_at) FROM discovery_runs WHERE status = 'done'),
                       (SELECT MAX(started_at) FROM discovery_runs)
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            if (!reader.IsDBNull(0) && Parse(reader.GetString(0)) > now - ScheduleInterval) return false;
            if (!reader.IsDBNull(1) && Parse(reader.GetString(1)) > now - RetryInterval) return false;
        }
        return true;
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>One scheduler tick; returns the started run, or null when not due.</summary>
    public JsonObject? TryStartScheduled()
    {
        if (!IsScheduledRunDue()) return null;
        try
        {
            return Start("schedule");
        }
        catch (ServiceException)
        {
            return null; // Lost a race with a manual run or a project change.
        }
    }

    /// <summary>Checks once now (database only, no network) and then every hour until disposed.</summary>
    public void StartScheduler()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed || _schedulerCancel is not null) return;
            _schedulerCancel = new CancellationTokenSource();
            token = _schedulerCancel.Token;
        }
        Tick();
        var task = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TickInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token)) Tick();
            }
            catch (OperationCanceledException)
            {
            }
        });
        lock (_sync) _schedulerTask = task;
    }

    private void Tick()
    {
        try { TryStartScheduled(); }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        Task? run, scheduler;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _schedulerCancel?.Cancel();
            _runCancel?.Cancel();
            run = _runTask;
            scheduler = _schedulerTask;
        }
        // The host closes the database right after disposing features: let the run record its final status first.
        try { Task.WhenAll(new[] { run, scheduler }.OfType<Task>()).Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { }
        _schedulerCancel?.Dispose();
    }

    // ---- candidates ---------------------------------------------------------------------------

    /// <summary>Candidates in the given states (default pending), best score first.</summary>
    public JsonArray ListCandidates(IReadOnlyCollection<string>? states = null, long? projectId = null)
    {
        states = states is { Count: > 0 } ? states : new[] { "pending" };
        foreach (var state in states)
            if (!States.Contains(state)) throw new ServiceException("invalid_field", 400, $"state must be one of {string.Join(", ", States)}");
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            var names = states.Select((_, i) => $"$s{i}").ToList();
            cmd.CommandText = $"""
                SELECT c.*, p.name AS project_name FROM discovery_candidates c JOIN projects p ON p.id = c.project_id
                WHERE c.state IN ({string.Join(", ", names)}) {(projectId is null ? "" : "AND c.project_id = $project")}
                ORDER BY c.score DESC, c.stars_per_month DESC, c.github_id
                """;
            var i = 0;
            foreach (var state in states) cmd.Parameters.AddWithValue($"$s{i++}", state);
            if (projectId is not null) cmd.Parameters.AddWithValue("$project", projectId.Value);
            using var reader = cmd.ExecuteReader();
            var result = new JsonArray();
            while (reader.Read()) result.Add(ToCandidate(reader));
            return result;
        }
    }

    /// <summary>API shape of one discovery_candidates row (optionally joined with project_name).</summary>
    public static JsonObject ToCandidate(SqliteDataReader reader)
    {
        JsonNode? Value(string column)
        {
            int ordinal;
            try { ordinal = reader.GetOrdinal(column); }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { return null; }
            if (reader.IsDBNull(ordinal)) return null;
            return reader.GetValue(ordinal) switch
            {
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
            };
        }
        return new JsonObject
        {
            ["projectId"] = Value("project_id"), ["projectName"] = Value("project_name"),
            ["githubId"] = Value("github_id"), ["fullName"] = Value("full_name"), ["htmlUrl"] = Value("html_url"),
            ["description"] = Value("description"), ["language"] = Value("language"), ["licenseId"] = Value("license_id"),
            ["stars"] = Value("stars"), ["starsPerMonth"] = Value("stars_per_month"), ["pushedAt"] = Value("pushed_at"),
            ["createdAtUpstream"] = Value("created_at_upstream"), ["score"] = Value("score"),
            ["matchedNeed"] = Value("matched_need"), ["cost"] = Value("cost"), ["reason"] = Value("reason"),
            ["reasonLang"] = Value("reason_lang"), ["model"] = Value("model"), ["state"] = Value("state"),
            ["dismissReason"] = Value("dismiss_reason"), ["runId"] = Value("run_id"),
            ["proposedAt"] = Value("proposed_at"), ["decidedAt"] = Value("decided_at"),
        };
    }

    // ---- HTTP ---------------------------------------------------------------------------------

    public async Task<bool> TryHandleAsync(HttpContext ctx, string method, string path)
    {
        if (path == "/api/discovery/runs" && method == "POST")
        {
            string? lang = null;
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
            {
                var body = await DiscoveryApi.ReadBodyAsync(ctx);
                if (body["lang"] is JsonValue value && value.TryGetValue<string>(out var text)) lang = text;
            }
            await DiscoveryApi.SendDataAsync(ctx, new JsonObject { ["run"] = Start("manual", lang) }, 202);
            return true;
        }
        if (path == "/api/discovery/runs/latest" && method == "GET")
        {
            await DiscoveryApi.SendDataAsync(ctx, new JsonObject { ["run"] = LatestRun() });
            return true;
        }
        var cancel = Regex.Match(path, @"^/api/discovery/runs/([0-9a-fA-F-]{1,64})/cancel$");
        if (cancel.Success && method == "POST")
        {
            var id = cancel.Groups[1].Value;
            var cancelled = Cancel(id);
            await DiscoveryApi.SendDataAsync(ctx, new JsonObject { ["cancelled"] = cancelled, ["run"] = GetRun(id) });
            return true;
        }
        if (path == "/api/discovery/candidates" && method == "GET")
        {
            var states = ctx.Request.Query["state"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            long? project = null;
            var projectText = ctx.Request.Query["project"].ToString();
            if (projectText.Length > 0)
            {
                if (!long.TryParse(projectText, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    throw new ServiceException("invalid_field", 400, "project must be a project id");
                project = id;
            }
            await DiscoveryApi.SendDataAsync(ctx, new JsonObject { ["candidates"] = ListCandidates(states, project) });
            return true;
        }
        return false;
    }
}
