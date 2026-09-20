using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoShelf.Core;

/// <summary>
/// Reads a Chromium "Bookmarks" JSON file and extracts GitHub repository URLs.
/// Read-only: the browser profile is never modified.
/// </summary>
public static class ChromeBookmarks
{
    public static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var chrome = Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Bookmarks");
        if (File.Exists(chrome))
        {
            return chrome;
        }
        // Edge uses the same format; handy fallback.
        return Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Bookmarks");
    }

    /// <summary>
    /// Returns repository URLs (deduplicated, order preserved) or null with
    /// <paramref name="error"/> set when the file cannot be read.
    /// </summary>
    public static List<string>? ExtractRepoUrls(string? path, out string sourcePath, out string? error)
    {
        sourcePath = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path!;
        error = null;
        if (!File.Exists(sourcePath))
        {
            error = $"Bookmarks file not found: {sourcePath}";
            return null;
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(sourcePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"Could not read bookmarks: {ex.Message}";
            return null;
        }
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(root, urls, seen);
        return urls;
    }

    private static void Walk(JsonNode? node, List<string> urls, HashSet<string> seen)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "url" && obj["url"]?.GetValue<string>() is { } url)
            {
                TryAdd(url, urls, seen);
            }
            foreach (var child in obj)
            {
                Walk(child.Value, urls, seen);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr)
            {
                Walk(child, urls, seen);
            }
        }
    }

    private static void TryAdd(string url, List<string> urls, HashSet<string> seen)
    {
        try
        {
            var parsed = RepoUrlParser.Parse(url);
            if (seen.Add(parsed.FullName))
            {
                urls.Add(parsed.Url);
            }
        }
        catch (RepoUrlParser.InvalidUrlException)
        {
            // Not a GitHub repository URL: skip silently.
        }
    }
}
