namespace RepoShelf.Core;

/// <summary>Runtime configuration, resolved from environment variables.</summary>
public sealed record AppConfig(
    string Host,
    int Port,
    string DataDir,
    string GitHubApiBase,
    string? PairingTokenOverride,
    int MaxRateLimitWaitMs)
{
    public const int DefaultPort = 4790;

    public static string DefaultDataDir()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(roaming))
        {
            roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.Combine(roaming, "repo-shelf");
    }

    public static AppConfig Load(IReadOnlyDictionary<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        string? Get(string key) => env.TryGetValue(key, out var v) ? v : null;

        var port = int.TryParse(Get("REPO_SHELF_PORT"), out var p) && p is > 0 and < 65536 ? p : DefaultPort;
        var maxWait = int.TryParse(Get("REPO_SHELF_MAX_RATE_WAIT_MS"), out var w) && w >= 0 ? w : 90_000;
        return new AppConfig(
            Host: "127.0.0.1",
            Port: port,
            DataDir: Get("REPO_SHELF_DATA_DIR") is { Length: > 0 } d ? Path.GetFullPath(d) : DefaultDataDir(),
            GitHubApiBase: (Get("REPO_SHELF_GITHUB_API") ?? "https://api.github.com").TrimEnd('/'),
            PairingTokenOverride: Get("REPO_SHELF_TOKEN"),
            MaxRateLimitWaitMs: maxWait);
    }
}
