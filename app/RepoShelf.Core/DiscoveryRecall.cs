using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace RepoShelf.Core;

/// <summary>Per-project limits for one run. Tests can supply smaller budgets.</summary>
public sealed record DiscoveryBudget(int QueriesPerProject = 4, int ResultsPerQuery = 30, int ShortlistPerProject = 10);
public sealed record RecalledRepo(RepoMeta Repo, double StarsPerMonth, string? Readme = null);

/// <summary>Deterministic recall from a stored project profile; no AI calls or library writes.</summary>
public sealed class DiscoveryRecall
{
    private const int MaxPushAgeDays = 180;
    private readonly Store _store;
    private readonly GitHubClient _github;
    private readonly DiscoveryBudget _budget;
    private readonly Func<DateTimeOffset> _clock;

    public DiscoveryRecall(Store store, GitHubClient github, DiscoveryBudget? budget = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _github = github;
        _budget = budget ?? new DiscoveryBudget();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Elapsed 30-day months, with a one-month floor. ID breaks growth ties reproducibly.</summary>
    public static IReadOnlyList<RecalledRepo> Rank(IEnumerable<RepoMeta> repos, DateTimeOffset now, int limit)
    {
        var ranked = new List<RecalledRepo>();
        foreach (var repo in repos)
        {
            if (string.IsNullOrWhiteSpace(repo.LicenseId) || repo.Archived || repo.Fork
                || !DateTimeOffset.TryParse(repo.PushedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pushed)
                || pushed < now.AddDays(-MaxPushAgeDays)
                || !DateTimeOffset.TryParse(repo.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created))
                continue;
            var months = Math.Max(1, (now - created).TotalDays / 30);
            ranked.Add(new RecalledRepo(repo, Math.Max(0, repo.Stars ?? 0) / months));
        }
        return ranked.OrderByDescending(x => x.StarsPerMonth).ThenBy(x => x.Repo.GithubId)
            .DistinctBy(x => x.Repo.GithubId).Take(limit).ToList();
    }

    /// <summary>
    /// Searches once per query, then fetches only shortlisted READMEs. The caller can score and
    /// persist each yielded item before requesting the next. GitHubException (including the rate
    /// limit reset time) propagates immediately, leaving previously persisted candidates intact.
    /// </summary>
    public async IAsyncEnumerable<RecalledRepo> RecallAsync(JsonObject project, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        if (_budget.QueriesPerProject <= 0 || _budget.ShortlistPerProject <= 0) yield break;
        var projectId = project["id"]!.GetValue<long>();
        var now = _clock().ToUniversalTime();
        var language = project["languages"]?.AsArray().FirstOrDefault()?.GetValue<string>().Trim();
        if (language?.Any(char.IsWhiteSpace) == true)
            language = "\"" + language.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var qualifier = string.IsNullOrWhiteSpace(language) ? "" : $" language:{language}";
        var cutoff = now.AddDays(-MaxPushAgeDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var queries = (project["queries"]?.AsArray() ?? new JsonArray())
            .Select(q => q?.GetValue<string>()).Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q!.Trim()).Distinct(StringComparer.Ordinal).Take(_budget.QueriesPerProject);
        var excluded = ExcludedIds(projectId);
        var repos = new List<RepoMeta>();
        foreach (var query in queries)
        {
            cancel.ThrowIfCancellationRequested();
            var results = await _github.SearchReposAsync(
                $"{query}{qualifier} pushed:>={cutoff} archived:false fork:false", _budget.ResultsPerQuery, cancel);
            repos.AddRange(results.Where(repo => !excluded.Contains(repo.GithubId)));
        }

        foreach (var candidate in Rank(repos, now, _budget.ShortlistPerProject))
        {
            cancel.ThrowIfCancellationRequested();
            if (IsExcluded(projectId, candidate.Repo.GithubId)) continue;
            var readme = await _github.FetchReadmeAsync(candidate.Repo.Owner, candidate.Repo.Name, cancel, retryTransient: false);
            yield return candidate with { Readme = readme?.Text };
        }
    }

    private bool IsExcluded(long projectId, long githubId)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS(SELECT 1 FROM repos WHERE github_id = $github)
                    OR EXISTS(SELECT 1 FROM discovery_candidates WHERE project_id = $project AND github_id = $github)
                """;
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$github", githubId);
            return (long)cmd.ExecuteScalar()! != 0;
        }
    }

    private HashSet<long> ExcludedIds(long projectId)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                SELECT github_id FROM repos
                UNION SELECT github_id FROM discovery_candidates WHERE project_id = $project
                """;
            cmd.Parameters.AddWithValue("$project", projectId);
            using var reader = cmd.ExecuteReader();
            var ids = new HashSet<long>();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return ids;
        }
    }
}
