namespace RepoShelf.Core;

/// <summary>
/// URL normalization for GitHub repository URLs. Input is treated strictly as
/// data: parsed, never executed or fetched here.
/// </summary>
public static class RepoUrlParser
{
    // GitHub-owned top-level paths that look like an "owner" segment but are not users.
    private static readonly HashSet<string> ReservedOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "apps", "collections", "contact", "customer-stories", "enterprise", "events",
        "explore", "features", "gist", "import", "login", "logout", "marketplace", "new",
        "notifications", "organizations", "pricing", "readme", "search", "security", "settings",
        "signup", "sponsors", "stars", "team", "topics", "trending", "users",
    };

    public sealed record Parsed(string Owner, string Repo)
    {
        public string FullName => $"{Owner}/{Repo}";
        public string Url => $"https://github.com/{Owner}/{Repo}";
    }

    public sealed class InvalidUrlException(string message) : Exception(message)
    {
        public string Code => "invalid_url";
    }

    private static bool ValidOwner(string s) =>
        s.Length is >= 1 and <= 39
        && char.IsAsciiLetterOrDigit(s[0])
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool ValidRepo(string s) =>
        s.Length is >= 1 and <= 100
        && s is not "." and not ".."
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static Parsed Parse(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new InvalidUrlException("A repository URL is required");
        }
        // Allow pasting "github.com/owner/repo" without a scheme.
        var probe = text.Length > 12 ? text[..12] : text;
        if (!probe.Contains("://", StringComparison.Ordinal) && !text.Contains(':'))
        {
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            throw new InvalidUrlException("Not a valid URL");
        }
        if (url.Scheme != "http" && url.Scheme != "https")
        {
            throw new InvalidUrlException("Only http(s) URLs are supported");
        }
        var host = url.Host.ToLowerInvariant();
        if (host is not ("github.com" or "www.github.com"))
        {
            throw new InvalidUrlException("Only github.com repository URLs are supported");
        }
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2)
        {
            throw new InvalidUrlException("URL does not point to a repository (expected /owner/repo)");
        }
        var owner = segments[0];
        var repo = segments[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }
        if (!ValidOwner(owner))
        {
            throw new InvalidUrlException($"\"{owner}\" is not a valid GitHub owner name");
        }
        if (ReservedOwners.Contains(owner))
        {
            throw new InvalidUrlException($"\"{owner}\" is a GitHub system path, not a repository owner");
        }
        if (!ValidRepo(repo))
        {
            throw new InvalidUrlException($"\"{repo}\" is not a valid GitHub repository name");
        }
        return new Parsed(owner, repo);
    }
}
