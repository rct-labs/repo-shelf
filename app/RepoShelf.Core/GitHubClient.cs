using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RepoShelf.Core;

/// <summary>Classified GitHub failure with rate-limit metadata.</summary>
public sealed class GitHubException : Exception
{
    public string Code { get; }
    public int? Status { get; init; }
    public bool Retryable { get; init; }
    public long? RetryAfterMs { get; init; }
    public DateTimeOffset? RateLimitResetAt { get; init; }

    public GitHubException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public sealed record RepoMeta(
    long GithubId,
    string Owner,
    string Name,
    string FullName,
    string HtmlUrl,
    string Description,
    List<string> Topics,
    string? Language,
    string? LicenseId,
    bool Archived,
    string? PushedAt,
    long? Stars,
    string? DefaultBranch)
{
    public static RepoMeta FromJson(JsonElement j)
    {
        var license = j.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object
            && lic.TryGetProperty("spdx_id", out var spdx) ? spdx.GetString() : null;
        var topics = new List<string>();
        if (j.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            topics.AddRange(t.EnumerateArray().Select(x => x.GetString()).Where(s => s is not null)!);
        }
        long? stars = j.TryGetProperty("stargazers_count", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt64() : null;
        return new RepoMeta(
            GithubId: j.GetProperty("id").GetInt64(),
            Owner: j.TryGetProperty("owner", out var o) ? o.GetProperty("login").GetString() ?? "" : "",
            Name: j.GetProperty("name").GetString() ?? "",
            FullName: j.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? "" : "",
            HtmlUrl: j.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "",
            Description: j.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            Topics: topics,
            Language: j.TryGetProperty("language", out var l) ? l.GetString() : null,
            LicenseId: license,
            Archived: j.TryGetProperty("archived", out var a) && a.GetBoolean(),
            PushedAt: j.TryGetProperty("pushed_at", out var pa) ? pa.GetString() : null,
            Stars: stars,
            DefaultBranch: j.TryGetProperty("default_branch", out var db) ? db.GetString() : null);
    }
}

/// <summary>
/// Read-only GitHub REST client with bounded retries and rate-limit
/// awareness. Never stars, forks or mutates GitHub.
/// </summary>
public sealed class GitHubClient
{
    private const string ApiVersion = "2022-11-28";
    private const long MaxReadmeChars = 1_000_000;

    private readonly HttpClient _http;
    private readonly Func<string?> _getToken;
    private readonly string _baseUrl;
    private readonly int _maxTransientRetries;

    public GitHubClient(HttpClient? http = null, Func<string?>? getToken = null, string baseUrl = "https://api.github.com", int maxTransientRetries = 3)
    {
        _http = http ?? new HttpClient();
        _getToken = getToken ?? (() => null);
        _baseUrl = baseUrl.TrimEnd('/');
        _maxTransientRetries = maxTransientRetries;
    }

    private HttpRequestMessage NewRequest(string path, string accept = "application/vnd.github+json")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.Add("x-github-api-version", ApiVersion);
        req.Headers.Add("user-agent", "repo-shelf/0.1 (+https://github.com/rct-labs/repo-shelf)");
        var token = _getToken();
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private static void RaiseForStatus(HttpResponseMessage res, string context)
    {
        var status = (int)res.StatusCode;
        if (status < 400)
        {
            return;
        }
        var remaining = Header(res, "x-ratelimit-remaining");
        var reset = Header(res, "x-ratelimit-reset");
        var retryAfter = Header(res, "retry-after");
        var resetAt = long.TryParse(reset, out var rs) ? DateTimeOffset.FromUnixTimeSeconds(rs) : (DateTimeOffset?)null;

        if (status == 404)
        {
            throw new GitHubException("not_found", $"{context} not found (or not accessible without authentication)") { Status = 404 };
        }
        if ((status == 403 || status == 429) && (remaining == "0" || retryAfter is not null || status == 429))
        {
            long? waitMs = long.TryParse(retryAfter, out var ra) ? ra * 1000
                : resetAt is { } r ? (long?)Math.Max(0, (long)(r - DateTimeOffset.UtcNow).TotalMilliseconds)
                : null;
            throw new GitHubException("rate_limited",
                $"GitHub rate limit reached{(resetAt is { } rr ? $"; quota resets at {rr:O}" : "")}")
            {
                Status = status,
                Retryable = true,
                RetryAfterMs = waitMs,
                RateLimitResetAt = resetAt,
            };
        }
        if (status == 401)
        {
            throw new GitHubException("unauthorized", "GitHub rejected the configured access token") { Status = 401 };
        }
        if (status == 403)
        {
            throw new GitHubException("forbidden", $"GitHub refused access to {context}") { Status = 403 };
        }
        if (status == 451)
        {
            throw new GitHubException("unavailable_451", $"{context} is unavailable (HTTP 451)") { Status = 451 };
        }
        throw new GitHubException("github_error", $"GitHub responded with HTTP {status} for {context}")
        {
            Status = status,
            Retryable = status >= 500,
        };
    }

    /// <summary>GET with bounded retries for network errors and 5xx.</summary>
    private async Task<HttpResponseMessage> SendAsync(string path, string accept = "application/vnd.github+json")
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= _maxTransientRetries; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(NewRequest(path, accept));
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                await Task.Delay(400 * (1 << attempt));
                continue;
            }
            catch (TaskCanceledException ex)
            {
                lastError = ex;
                await Task.Delay(400 * (1 << attempt));
                continue;
            }
            if ((int)res.StatusCode >= 500)
            {
                lastError = new GitHubException("upstream_error", $"GitHub responded with HTTP {(int)res.StatusCode}")
                {
                    Status = (int)res.StatusCode,
                    Retryable = true,
                };
                res.Dispose();
                await Task.Delay(400 * (1 << attempt));
                continue;
            }
            return res;
        }
        throw new GitHubException("upstream_unavailable",
            $"GitHub is unreachable after {_maxTransientRetries + 1} attempts: {lastError?.Message ?? "network error"}")
        { Retryable = true };
    }

    private async Task<JsonElement> GetJsonAsync(string path, string context)
    {
        using var res = await SendAsync(path);
        RaiseForStatus(res, context);
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }

    public async Task<RepoMeta> FetchRepoAsync(string owner, string repo)
    {
        var json = await GetJsonAsync($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}", $"repository {owner}/{repo}");
        return RepoMeta.FromJson(json);
    }

    // GET /repositories/{id} resolves the current location of a repo id,
    // which lets refresh follow renames and ownership transfers.
    public async Task<RepoMeta> FetchRepoByIdAsync(long githubId)
    {
        var json = await GetJsonAsync($"/repositories/{githubId}", $"repository id {githubId}");
        return RepoMeta.FromJson(json);
    }

    /// <summary>Returns null when the repo has no README.</summary>
    public async Task<(string Text, bool Truncated)?> FetchReadmeAsync(string owner, string repo)
    {
        using var res = await SendAsync($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/readme",
            accept: "application/vnd.github.raw+json");
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        RaiseForStatus(res, $"README of {owner}/{repo}");
        var text = await res.Content.ReadAsStringAsync();
        var truncated = false;
        if (text.Length > MaxReadmeChars)
        {
            text = text[..(int)MaxReadmeChars];
            truncated = true;
        }
        return (text, truncated);
    }

    public async Task<(List<RepoMeta> Items, int? NextPage)> FetchStarredPageAsync(string username, int page, int perPage = 100)
    {
        using var res = await SendAsync($"/users/{Uri.EscapeDataString(username)}/starred?per_page={perPage}&page={page}");
        RaiseForStatus(res, $"starred list of user \"{username}\"");
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(RepoMeta.FromJson).ToList()
            : new List<RepoMeta>();
        int? nextPage = null;
        if (Header(res, "link") is { } link)
        {
            var match = System.Text.RegularExpressions.Regex.Match(link, @"<[^>]*[?&]page=(\d+)[^>]*>;\s*rel=""next""");
            if (match.Success)
            {
                nextPage = int.Parse(match.Groups[1].Value);
            }
        }
        return (items, nextPage);
    }
}
