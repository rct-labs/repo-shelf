using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace RepoShelf.Core;

/// <summary>
/// Local keyword search over FTS5 with mixed Chinese/English tokenization,
/// field weighting, filters and highlighted snippets. No network or model
/// involved; everything runs against the local SQLite index.
/// </summary>
public sealed class SearchService
{
    // Column order matches search_fts: name, owner, description, topics, reason, notes, readme.
    private const string RankExpr = "bm25(search_fts, 10.0, 4.0, 3.0, 5.0, 8.0, 8.0, 1.0)";
    private const double ExactNameBoost = -10.0;
    private static readonly string[] FieldPriority = ["name", "reason", "notes", "description", "topics", "owner", "readme"];
    private const int SnippetWidth = 180;
    private const int ReadmeSnippetScanLimit = 400_000;

    // Explicit column list: the large readme text is NOT selected here; it is
    // batch-loaded afterwards for the page of rows that actually needs it.
    private const string Columns = """
        r.id, r.github_id, r.owner, r.name, r.full_name, r.html_url, r.description, r.topics,
        r.language, r.license_id, r.archived, r.pushed_at, r.stars, r.default_branch,
        r.readme_truncated, r.fetched_at, r.refresh_status, r.starred_upstream, r.seen_in_import,
        r.created_at, r.updated_at,
        (r.readme IS NOT NULL AND length(r.readme) > 0) AS has_readme,
        a.reason, a.notes, a.tags AS a_tags, a.projects AS a_projects, a.status,
        a.created_at AS a_created_at, a.updated_at AS a_updated_at
        """;

    private readonly Store _store;

    public SearchService(Store store)
    {
        _store = store;
    }

    public sealed record SearchParams(
        string Query = "",
        string? Status = null,
        string? Tag = null,
        string? Project = null,
        string? Language = null,
        bool? Archived = null,
        int Limit = 50,
        int Offset = 0);

    public sealed record SearchItem(JsonObject Repo, List<string> MatchedFields, string Snippet, string? SnippetField, double? Rank);

    public sealed record SearchResult(long Total, List<SearchItem> Items, List<string> Tokens);

    public SearchResult Search(SearchParams p)
    {
        var tokens = Tokenizer.TokenizeQuery(p.Query);
        var limit = Math.Clamp(p.Limit <= 0 ? 50 : p.Limit, 1, 200);
        var offset = Math.Max(p.Offset, 0);

        var clauses = new List<string>();
        if (p.Status is not null)
        {
            if (!Store.Statuses.Contains(p.Status))
            {
                throw new ServiceException("invalid_filter", 400, "invalid status filter");
            }
            clauses.Add("COALESCE(a.status, 'inbox') = $status");
        }
        if (p.Tag is not null)
        {
            clauses.Add("EXISTS (SELECT 1 FROM json_each(COALESCE(a.tags, '[]')) ft WHERE ft.value = $tag)");
        }
        if (p.Project is not null)
        {
            clauses.Add("EXISTS (SELECT 1 FROM json_each(COALESCE(a.projects, '[]')) fp WHERE fp.value = $project)");
        }
        if (p.Language is not null)
        {
            clauses.Add("r.language = $language COLLATE NOCASE");
        }
        if (p.Archived is not null)
        {
            clauses.Add("r.archived = $archived");
        }
        var whereExtra = clauses.Count > 0 ? " AND " + string.Join(" AND ", clauses) : "";

        lock (_store.Sync)
        {
            void AddFilterParams(Microsoft.Data.Sqlite.SqliteCommand cmd)
            {
                if (p.Status is not null) cmd.Parameters.AddWithValue("$status", p.Status);
                if (p.Tag is not null) cmd.Parameters.AddWithValue("$tag", p.Tag);
                if (p.Project is not null) cmd.Parameters.AddWithValue("$project", p.Project);
                if (p.Language is not null) cmd.Parameters.AddWithValue("$language", p.Language);
                if (p.Archived is not null) cmd.Parameters.AddWithValue("$archived", p.Archived.Value ? 1 : 0);
            }

            long total;
            var rows = new List<(JsonObject Repo, double? Rank)>();
            if (tokens.Count > 0)
            {
                // Quoted phrases per token avoid FTS5 query-syntax injection;
                // AND requires every token (e.g. each Chinese bigram) to match.
                var match = string.Join(" AND ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
                var baseSql = """
                    FROM search_fts
                    JOIN repos r ON r.id = search_fts.rowid
                    LEFT JOIN annotations a ON a.repo_id = r.id
                    WHERE search_fts MATCH $match
                    """ + whereExtra;

                using (var countCmd = _store.Conn.CreateCommand())
                {
                    countCmd.CommandText = $"SELECT COUNT(*) {baseSql}";
                    countCmd.Parameters.AddWithValue("$match", match);
                    AddFilterParams(countCmd);
                    total = (long)(countCmd.ExecuteScalar() ?? 0L);
                }
                using (var cmd = _store.Conn.CreateCommand())
                {
                    cmd.CommandText = $"""
                        SELECT {Columns},
                               ({RankExpr}
                                  + CASE WHEN lower(r.name) = lower($rawq) OR lower(r.full_name) = lower($rawq)
                                         THEN $boost ELSE 0 END) AS rank
                        {baseSql}
                        ORDER BY rank ASC
                        LIMIT $limit OFFSET $offset
                        """;
                    cmd.Parameters.AddWithValue("$match", match);
                    cmd.Parameters.AddWithValue("$rawq", p.Query.Trim());
                    cmd.Parameters.AddWithValue("$boost", ExactNameBoost);
                    cmd.Parameters.AddWithValue("$limit", limit);
                    cmd.Parameters.AddWithValue("$offset", offset);
                    AddFilterParams(cmd);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((ReadRecord(reader), reader.IsDBNull(29) ? null : reader.GetDouble(29)));
                    }
                }
            }
            else
            {
                var baseSql = "FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id WHERE 1 = 1" + whereExtra;
                using (var countCmd = _store.Conn.CreateCommand())
                {
                    countCmd.CommandText = $"SELECT COUNT(*) {baseSql}";
                    AddFilterParams(countCmd);
                    total = (long)(countCmd.ExecuteScalar() ?? 0L);
                }
                using (var cmd = _store.Conn.CreateCommand())
                {
                    cmd.CommandText = $"""
                        SELECT {Columns}
                        {baseSql}
                        ORDER BY r.created_at DESC, r.id DESC
                        LIMIT $limit OFFSET $offset
                        """;
                    cmd.Parameters.AddWithValue("$limit", limit);
                    cmd.Parameters.AddWithValue("$offset", offset);
                    AddFilterParams(cmd);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((ReadRecord(reader), null));
                    }
                }
            }

            // Batch-load README text only for the current page of results, and
            // only when a keyword search needs it for matched-field detection
            // and snippet generation.
            var readmes = new Dictionary<long, string>();
            if (tokens.Count > 0 && rows.Count > 0)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.CommandText = $"SELECT id, readme FROM repos WHERE readme IS NOT NULL AND id IN ({string.Join(',', rows.Select(r => r.Repo["id"]!.GetValue<long>()))})";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(1))
                    {
                        readmes[reader.GetInt64(0)] = reader.GetString(1);
                    }
                }
            }

            // Matchers are built once per search, not per row.
            var matchers = tokens.Select(t => new Matcher(t)).ToList();
            var items = rows.Select(row =>
            {
                var id = row.Repo["id"]!.GetValue<long>();
                var highlight = tokens.Count > 0
                    ? Highlight(row.Repo, readmes.GetValueOrDefault(id), matchers)
                    : (new List<string>(), "", (string?)null);
                return new SearchItem(row.Repo, highlight.Item1, highlight.Item2, highlight.Item3, row.Rank);
            }).ToList();
            return new SearchResult(total, items, tokens);
        }
    }

    // Positional row reader matching the Columns list above (search results
    // never carry the readme column; it is batch-loaded per page instead).
    private static JsonObject ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        static List<string> Arr(string? t)
        {
            try { return JsonSerializer.Deserialize<List<string>>(t ?? "[]") ?? new(); }
            catch { return new(); }
        }
        var fetchedAt = S(15);
        var stale = fetchedAt is null || DateTime.UtcNow - DateTime.Parse(fetchedAt).ToUniversalTime() > RepoService.StaleAfter;
        return new JsonObject
        {
            ["id"] = r.GetInt64(0),
            ["githubId"] = r.GetInt64(1),
            ["owner"] = S(2),
            ["name"] = S(3),
            ["fullName"] = S(4),
            ["htmlUrl"] = S(5),
            ["description"] = S(6) ?? "",
            ["topics"] = new JsonArray(Arr(S(7)).Select(t => JsonValue.Create(t)).ToArray()),
            ["language"] = (JsonValue?)S(8) ?? JsonValue.Create((string?)null),
            ["licenseId"] = (JsonValue?)S(9) ?? JsonValue.Create((string?)null),
            ["archived"] = r.GetInt64(10) != 0,
            ["pushedAt"] = S(11),
            ["stars"] = L(12),
            ["defaultBranch"] = S(13),
            ["readme"] = null,
            ["hasReadme"] = r.GetInt64(21) != 0,
            ["readmeTruncated"] = r.GetInt64(14) != 0,
            ["fetchedAt"] = fetchedAt,
            ["refreshStatus"] = S(16),
            ["stale"] = stale,
            ["starredUpstream"] = r.GetInt64(17) != 0,
            ["seenInImport"] = r.GetInt64(18) != 0,
            ["createdAt"] = S(19),
            ["updatedAt"] = S(20),
            ["annotation"] = new JsonObject
            {
                ["reason"] = S(22) ?? "",
                ["notes"] = S(23) ?? "",
                ["tags"] = new JsonArray(Arr(S(24)).Select(t => JsonValue.Create(t)).ToArray()),
                ["projects"] = new JsonArray(Arr(S(25)).Select(t => JsonValue.Create(t)).ToArray()),
                ["status"] = S(26) ?? "inbox",
                ["createdAt"] = S(27),
                ["updatedAt"] = S(28),
            },
        };
    }

    private static bool IsCjk(string token) => token.Any(c => c is >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '豈' and <= '﫿');

    private sealed class Matcher(string token)
    {
        // No RegexOptions.Compiled: matchers are built once per search and
        // patterns are tiny; compiling per-query would dominate the cost.
        private readonly Regex? _wordRe = IsCjk(token) ? null
            : new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);

        public int IndexIn(string text)
        {
            if (_wordRe is null)
            {
                return text.IndexOf(token, StringComparison.Ordinal);
            }
            var m = _wordRe.Match(text);
            return m.Success ? m.Index : -1;
        }

        public string Mark(string escaped) => _wordRe is null
            ? escaped.Replace(token, $"<mark>{token}</mark>", StringComparison.Ordinal)
            : _wordRe.Replace(escaped, m => $"<mark>{m.Value}</mark>");
    }

    /// <summary>Escape HTML, then wrap token matches in &lt;mark&gt;.</summary>
    private static (List<string> MatchedFields, string Snippet, string? SnippetField) Highlight(JsonObject repo, string? readmeText, List<Matcher> matchers)
    {
        var readmeRaw = readmeText ?? "";
        var fields = new Dictionary<string, string>
        {
            ["name"] = repo["name"]?.GetValue<string>() ?? "",
            ["owner"] = repo["owner"]?.GetValue<string>() ?? "",
            ["description"] = repo["description"]?.GetValue<string>() ?? "",
            ["topics"] = string.Join(' ', repo["topics"]?.AsArray().Select(x => x?.GetValue<string>() ?? "") ?? Enumerable.Empty<string>()),
            ["reason"] = repo["annotation"]?["reason"]?.GetValue<string>() ?? "",
            ["notes"] = repo["annotation"]?["notes"]?.GetValue<string>() ?? "",
            ["readme"] = readmeRaw.Length > ReadmeSnippetScanLimit ? readmeRaw[..ReadmeSnippetScanLimit] : readmeRaw,
        };

        var matchedFields = FieldPriority
            .Where(f => fields[f].Length > 0 && matchers.Any(m => m.IndexIn(fields[f]) >= 0))
            .ToList();

        foreach (var field in FieldPriority)
        {
            if (!matchedFields.Contains(field))
            {
                continue;
            }
            var text = fields[field];
            var firstIndex = matchers.Select(m => m.IndexIn(text)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (firstIndex < 0)
            {
                continue;
            }
            var start = Math.Max(0, firstIndex - SnippetWidth / 3);
            var end = Math.Min(text.Length, start + SnippetWidth);
            var escaped = (start > 0 ? "…" : "") + HtmlEncode(text[start..end]) + (end < text.Length ? "…" : "");
            foreach (var m in matchers)
            {
                escaped = m.Mark(escaped);
            }
            return (matchedFields, escaped, field);
        }
        return (matchedFields, "", null);
    }

    private static string HtmlEncode(string text) => HttpUtility.HtmlEncode(text);
}
