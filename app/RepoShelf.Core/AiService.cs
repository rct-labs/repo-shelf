using System.Text.Json.Nodes;

namespace RepoShelf.Core;

/// <summary>
/// AI-generated summaries. Results live in the `generated` table — separate
/// from source metadata and from the user's own annotations, which AI output
/// never overwrites.
/// </summary>
public sealed class AiService
{
    private readonly Store _store;
    private readonly RepoService _repos;
    private readonly DeepSeekClient _deepseek;

    public AiService(Store store, RepoService repos, DeepSeekClient deepseek)
    {
        _store = store;
        _repos = repos;
        _deepseek = deepseek;
    }

    public bool IsConfigured => _deepseek.IsConfigured;

    public async Task<JsonObject> SummarizeAsync(long repoId, string lang, CancellationToken cancel = default)
    {
        var repo = _repos.GetRepo(repoId)
            ?? throw new ServiceException("not_found", 404, $"No repository with id {repoId}");
        var (content, model) = await _deepseek.SummarizeAsync(repo, lang, cancel);
        var ts = DateTime.UtcNow.ToString("O");
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
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$lang", lang);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.ExecuteNonQuery();
        }
        return new JsonObject
        {
            ["content"] = content,
            ["model"] = model,
            ["lang"] = lang,
            ["createdAt"] = ts,
        };
    }

    public JsonObject? GetSummary(long repoId)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "SELECT content, model, lang, created_at FROM generated WHERE repo_id = $id AND kind = 'summary'";
            cmd.Parameters.AddWithValue("$id", repoId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            return new JsonObject
            {
                ["content"] = reader.GetString(0),
                ["model"] = reader.GetString(1),
                ["lang"] = reader.GetString(2),
                ["createdAt"] = reader.GetString(3),
            };
        }
    }
}
