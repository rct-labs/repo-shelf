using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RepoShelf.Core;

/// <summary>Local project registration and profile metadata; never calls an upstream service.</summary>
public sealed class DiscoveryStore
{
    private readonly Store _store;

    public DiscoveryStore(Store store) => _store = store;

    private const string SelectProjects = """
        SELECT p.*,
          (SELECT COUNT(*) FROM discovery_candidates c WHERE c.project_id = p.id AND c.state = 'pending') AS pending_count,
          (SELECT COUNT(*) FROM discovery_candidates c WHERE c.project_id = p.id AND c.state = 'later') AS later_count,
          (SELECT COUNT(*) FROM discovery_candidates c WHERE c.project_id = p.id AND c.state = 'accepted') AS accepted_count,
          (SELECT COUNT(*) FROM discovery_candidates c WHERE c.project_id = p.id AND c.state = 'dismissed') AS dismissed_count
        FROM projects p
        """;

    private static JsonObject ToProject(SqliteDataReader reader)
    {
        string? Text(string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));
        long Number(string column) => reader.GetInt64(reader.GetOrdinal(column));
        return new JsonObject
        {
            ["id"] = Number("id"), ["name"] = Text("name"), ["path"] = Text("path"),
            ["paused"] = Number("paused") != 0,
            ["needs"] = JsonNode.Parse(Text("needs")!),
            ["queries"] = JsonNode.Parse(Text("queries")!),
            ["languages"] = JsonNode.Parse(Text("languages")!),
            ["dependencies"] = JsonNode.Parse(Text("dependencies")!),
            ["profileFiles"] = JsonNode.Parse(Text("profile_files")!),
            ["profiledAt"] = Text("profiled_at"),
            ["createdAt"] = Text("created_at"), ["updatedAt"] = Text("updated_at"),
            ["counts"] = new JsonObject
            {
                ["pending"] = Number("pending_count"), ["later"] = Number("later_count"),
                ["accepted"] = Number("accepted_count"), ["dismissed"] = Number("dismissed_count"),
            },
        };
    }

    public JsonArray ListProjects()
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = SelectProjects + " ORDER BY p.id";
            using var reader = cmd.ExecuteReader();
            var projects = new JsonArray();
            while (reader.Read()) projects.Add(ToProject(reader));
            return projects;
        }
    }

    public JsonObject? GetProject(long id)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = SelectProjects + " WHERE p.id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ToProject(reader) : null;
        }
    }

    public JsonObject AddProject(JsonObject body)
    {
        var name = RequiredText(body["name"], "name");
        var path = ExistingDirectory(body["path"]);
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO projects (name, path, created_at, updated_at)
                VALUES ($name, $path, $now, $now) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            long id;
            try { id = (long)cmd.ExecuteScalar()!; }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
            {
                throw Invalid("name", "must be unique");
            }
            return GetProject(id)!;
        }
    }

    public JsonObject UpdateProject(long id, JsonObject changes)
    {
        lock (_store.Sync)
        {
            var project = GetProject(id) ?? throw NotFound();
            var name = changes.ContainsKey("name") ? RequiredText(changes["name"], "name") : project["name"]!.GetValue<string>();
            // An offline/unmounted project can still be paused or renamed.
            var path = changes.ContainsKey("path") ? ExistingDirectory(changes["path"]) : project["path"]!.GetValue<string>();
            var paused = project["paused"]!.GetValue<bool>();
            if (changes.ContainsKey("paused") && !(changes["paused"] is JsonValue value && value.TryGetValue(out paused)))
                throw Invalid("paused", "must be a boolean");
            var needs = changes.ContainsKey("needs") ? StringArray(changes["needs"], "needs") : project["needs"]!.ToJsonString();
            var queries = changes.ContainsKey("queries") ? StringArray(changes["queries"], "queries", 4) : project["queries"]!.ToJsonString();

            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = """
                UPDATE projects SET name = $name, path = $path, paused = $paused,
                  needs = $needs, queries = $queries, updated_at = $now WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$paused", paused ? 1 : 0);
            cmd.Parameters.AddWithValue("$needs", needs);
            cmd.Parameters.AddWithValue("$queries", queries);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            try { cmd.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
            {
                throw Invalid("name", "must be unique");
            }
            return GetProject(id)!;
        }
    }

    public void DeleteProject(long id)
    {
        lock (_store.Sync)
        {
            using var cmd = _store.Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM projects WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) throw NotFound();
        }
    }

    private static string RequiredText(JsonNode? node, string field)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw Invalid(field, "must be a non-empty string");
        return text.Trim();
    }

    private static string ExistingDirectory(JsonNode? node)
    {
        var path = RequiredText(node, "path");
        if (!Directory.Exists(path)) throw Invalid("path", "must be an existing directory");
        return Path.GetFullPath(path);
    }

    private static string StringArray(JsonNode? node, string field, int max = int.MaxValue)
    {
        if (node is not JsonArray values || values.Count > max)
            throw Invalid(field, max == int.MaxValue ? "must be an array of strings" : $"must be an array of at most {max} strings");
        var result = new JsonArray();
        foreach (var value in values) result.Add(RequiredText(value, field));
        return result.ToJsonString();
    }

    private static ServiceException Invalid(string field, string requirement) => new("invalid_field", 400, $"{field} {requirement}");
    private static ServiceException NotFound() => new("not_found", 404, "Project not found");
}
