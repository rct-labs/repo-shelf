using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace RepoShelf.Core;

/// <summary>Explicit discovery decisions; only accept writes to the library.</summary>
public sealed class DiscoveryTriage : IDiscoveryRoutes
{
    private static readonly string[] DismissReasons = { "not_relevant", "too_heavy", "already_have", "low_quality" };
    private readonly Store _store;
    private readonly RepoService _repos;
    // Serialize decisions across projects as one GitHub repo may appear in several feeds.
    // Never hold the database lock while awaiting GitHub.
    private readonly SemaphoreSlim _decisions = new(1, 1);

    public DiscoveryTriage(ServiceHost host)
    {
        _store = host.Store;
        _repos = host.Repos;
    }

#pragma warning disable CA2255 // Spec extension point: register without editing the shared dispatcher.
    [ModuleInitializer]
    internal static void Register() => DiscoveryRoutes.Register(host => host.Feature(h => new DiscoveryTriage(h)));
#pragma warning restore CA2255

    private JsonObject GetCandidate(long projectId, long githubId)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.*, p.name AS project_name FROM discovery_candidates c
                JOIN projects p ON p.id = c.project_id
                WHERE c.project_id = $project AND c.github_id = $github
                """;
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$github", githubId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? DiscoveryRunner.ToCandidate(reader)
                : throw new ServiceException("not_found", 404, "Discovery candidate not found");
        }
    }

    private JsonObject? FindSaved(long githubId)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM repos WHERE github_id = $github";
            cmd.Parameters.AddWithValue("$github", githubId);
            return cmd.ExecuteScalar() is long id ? _repos.GetRepo(id) : null;
        }
    }

    private static JsonArray ProjectLabels(JsonObject? repo, JsonObject candidate)
    {
        var name = candidate["projectName"]!.GetValue<string>();
        var labels = repo?["annotation"]?["projects"]?.DeepClone().AsArray() ?? new JsonArray();
        if (!labels.Any(p => p!.GetValue<string>() == name)) labels.Add(name);
        // Use the same limits as RepoService.UpdateAnnotation, before any network/save work.
        if (name.Length > 100 || labels.Count > 50)
            throw new ServiceException("invalid_field", 400, "projects must be an array of up to 50 strings of at most 100 characters");
        return labels;
    }

    private async Task<JsonObject> AcceptAsync(long projectId, long githubId)
    {
        var candidate = GetCandidate(projectId, githubId);
        var existing = FindSaved(githubId);
        // A retry must not reset a user's subsequent status/annotation edits.
        if (candidate["state"]!.GetValue<string>() == "accepted" && existing is not null) return existing;
        ProjectLabels(existing, candidate);

        // Resolve a saved repository by durable identity, so a stale candidate URL cannot
        // refresh or duplicate an already-saved repository after a rename.
        var url = (existing ?? candidate)["htmlUrl"]?.GetValue<string>();
        var saved = await _repos.SaveRepoAsync(url);
        var repoId = saved["repo"]!["id"]!.GetValue<long>();
        lock (_store.Sync)
        {
            // Re-read after network I/O to preserve edits made while the save was pending.
            candidate = GetCandidate(projectId, githubId);
            var repo = _repos.GetRepo(repoId)!;
            if (repo["githubId"]!.GetValue<long>() != githubId)
                throw new ServiceException("invalid_field", 400, "Candidate URL no longer identifies the recommended repository");
            var labels = ProjectLabels(repo, candidate);
            using var tx = _store.Conn.BeginTransaction();
            // Only a fresh (Inbox) record moves to To investigate; a status the owner
            // already chose for a repository saved earlier is personal and kept.
            var patch = new JsonObject { ["projects"] = labels };
            if (repo["annotation"]?["status"]?.GetValue<string>() is null or "inbox") patch["status"] = "to_investigate";
            _repos.UpdateAnnotation(repoId, patch);
            SetState(projectId, githubId, "accepted");
            tx.Commit();
            return _repos.GetRepo(repoId)!;
        }
    }

    private JsonObject SetState(long projectId, long githubId, string state, string? reason = null)
    {
        lock (_store.Sync)
        {
            GetCandidate(projectId, githubId);
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                UPDATE discovery_candidates SET state = $state, dismiss_reason = $reason, decided_at = $now
                WHERE project_id = $project AND github_id = $github
                  AND (state != $state OR dismiss_reason IS NOT $reason)
                """;
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$github", githubId);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            return GetCandidate(projectId, githubId);
        }
    }

    private JsonArray Recommendations(long repoId)
    {
        lock (_store.Sync)
        {
            var repo = _repos.GetRepo(repoId) ?? throw new ServiceException("not_found", 404, "Repository not found");
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.*, p.name AS project_name FROM discovery_candidates c
                JOIN projects p ON p.id = c.project_id WHERE c.github_id = $github
                ORDER BY c.score DESC, c.project_id
                """;
            cmd.Parameters.AddWithValue("$github", repo["githubId"]!.GetValue<long>());
            using var reader = cmd.ExecuteReader();
            var rows = new JsonArray();
            while (reader.Read()) rows.Add(DiscoveryRunner.ToCandidate(reader));
            return rows;
        }
    }

    private static string? DismissReason(JsonObject body)
    {
        if (body["reason"] is null) return null;
        if (body["reason"] is JsonValue value && value.TryGetValue<string>(out var text) && DismissReasons.Contains(text))
            return text;
        throw new ServiceException("invalid_field", 400, $"reason must be one of: {string.Join(", ", DismissReasons)}");
    }

    public async Task<bool> TryHandleAsync(HttpContext ctx, string method, string path)
    {
        var recommendations = Regex.Match(path, @"^/api/repos/([0-9]+)/recommendations$");
        if (method == "GET" && recommendations.Success
            && long.TryParse(recommendations.Groups[1].Value, out var repoId) && repoId > 0)
        {
            await DiscoveryApi.SendDataAsync(ctx, new JsonObject { ["recommendations"] = Recommendations(repoId) });
            return true;
        }
        var decision = Regex.Match(path, @"^/api/discovery/candidates/([0-9]+)/([0-9]+)/(accept|later|dismiss)$");
        if (method != "POST" || !decision.Success
            || !long.TryParse(decision.Groups[1].Value, out var projectId) || projectId <= 0
            || !long.TryParse(decision.Groups[2].Value, out var githubId) || githubId <= 0) return false;

        JsonObject data;
        await _decisions.WaitAsync(ctx.RequestAborted);
        try
        {
            if (decision.Groups[3].Value == "accept")
                data = new JsonObject { ["repo"] = await AcceptAsync(projectId, githubId) };
            else
            {
                GetCandidate(projectId, githubId);
                var dismiss = decision.Groups[3].Value == "dismiss";
                string? reason = null;
                if (dismiss && (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.TransferEncoding.Count > 0))
                    reason = DismissReason(await DiscoveryApi.ReadBodyAsync(ctx));
                data = new JsonObject { ["candidate"] = SetState(projectId, githubId, dismiss ? "dismissed" : "later", reason) };
            }
        }
        finally { _decisions.Release(); }
        await DiscoveryApi.SendDataAsync(ctx, data);
        return true;
    }
}
