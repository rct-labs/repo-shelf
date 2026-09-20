using Microsoft.Data.Sqlite;

namespace RepoShelf.Core;

/// <summary>
/// SQLite store: schema, settings and a process-wide lock. The app is
/// single-user and local; a single connection guarded by a lock keeps the
/// threading model simple and mirrors the synchronous better-sqlite3
/// reference implementation.
/// </summary>
public sealed class Store : IDisposable
{
    public static readonly string[] Statuses = ["inbox", "to_investigate", "tried", "adopted", "dismissed"];

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS repos (
          id INTEGER PRIMARY KEY,
          github_id INTEGER NOT NULL UNIQUE,
          owner TEXT NOT NULL,
          name TEXT NOT NULL,
          full_name TEXT NOT NULL,
          html_url TEXT NOT NULL,
          description TEXT NOT NULL DEFAULT '',
          topics TEXT NOT NULL DEFAULT '[]',
          language TEXT,
          license_id TEXT,
          archived INTEGER NOT NULL DEFAULT 0,
          pushed_at TEXT,
          stars INTEGER,
          default_branch TEXT,
          readme TEXT,
          readme_truncated INTEGER NOT NULL DEFAULT 0,
          fetched_at TEXT,
          refresh_status TEXT NOT NULL DEFAULT 'ok',
          starred_upstream INTEGER NOT NULL DEFAULT 0,
          seen_in_import INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_repos_full_name ON repos (full_name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS idx_repos_fetched_at ON repos (fetched_at);

        CREATE TABLE IF NOT EXISTS annotations (
          repo_id INTEGER PRIMARY KEY REFERENCES repos(id) ON DELETE CASCADE,
          reason TEXT NOT NULL DEFAULT '',
          notes TEXT NOT NULL DEFAULT '',
          tags TEXT NOT NULL DEFAULT '[]',
          projects TEXT NOT NULL DEFAULT '[]',
          status TEXT NOT NULL DEFAULT 'inbox',
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
          name, owner, description, topics, reason, notes, readme
        );

        CREATE TABLE IF NOT EXISTS settings (
          key TEXT PRIMARY KEY,
          value TEXT
        );

        CREATE TABLE IF NOT EXISTS import_jobs (
          id TEXT PRIMARY KEY,
          type TEXT NOT NULL,
          username TEXT,
          status TEXT NOT NULL,
          options TEXT NOT NULL DEFAULT '{}',
          next_page INTEGER NOT NULL DEFAULT 1,
          processed INTEGER NOT NULL DEFAULT 0,
          added INTEGER NOT NULL DEFAULT 0,
          updated INTEGER NOT NULL DEFAULT 0,
          failed INTEGER NOT NULL DEFAULT 0,
          failures TEXT NOT NULL DEFAULT '[]',
          error TEXT,
          created_at TEXT NOT NULL,
          finished_at TEXT
        );

        CREATE TABLE IF NOT EXISTS job_seen (
          job_id TEXT NOT NULL,
          github_id INTEGER NOT NULL,
          PRIMARY KEY (job_id, github_id)
        );
        """;

    private readonly SqliteConnection _conn;

    /// <summary>All database access must hold this lock.</summary>
    public object Sync { get; } = new();

    public SqliteConnection Conn => _conn;

    private Store(SqliteConnection conn)
    {
        _conn = conn;
    }

    public static Store Open(string file)
    {
        SqliteConnection conn;
        if (file == ":memory:")
        {
            conn = new SqliteConnection("Data Source=:memory:");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            conn = new SqliteConnection($"Data Source={file}");
        }
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        return new Store(conn);
    }

    public string? GetSetting(string key)
    {
        lock (Sync)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string? value)
    {
        lock (Sync)
        {
            using var cmd = Conn.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM settings WHERE key = $k";
            }
            else
            {
                cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                cmd.Parameters.AddWithValue("$v", value);
            }
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
