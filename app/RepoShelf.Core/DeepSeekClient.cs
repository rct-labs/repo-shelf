using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoShelf.Core;

/// <summary>
/// Minimal DeepSeek (OpenAI-compatible) chat client. The API key is stored
/// server-side only (settings table) and never reaches the browser, exports
/// or logs. Base URL and model are overridable for tests.
/// </summary>
public sealed class DeepSeekClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _getKey;
    private readonly string _baseUrl;
    private readonly string _model;

    public DeepSeekClient(HttpClient? http = null, Func<string?>? getKey = null, string? baseUrl = null, string? model = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _getKey = getKey ?? (() => null);
        _baseUrl = (baseUrl ?? "https://api.deepseek.com").TrimEnd('/');
        _model = model ?? "deepseek-chat";
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_getKey());

    /// <summary>Summarize a repository. Throws ServiceException on failure.</summary>
    public async Task<(string Content, string Model)> SummarizeAsync(JsonObject repo, string lang, CancellationToken cancel = default)
    {
        var key = _getKey();
        if (string.IsNullOrEmpty(key))
        {
            throw new ServiceException("ai_not_configured", 400, "DeepSeek API key is not configured. Set it in Settings.");
        }

        var readme = repo["readme"]?.GetValue<string>() ?? "";
        if (readme.Length > 12_000)
        {
            readme = readme[..12_000] + "\n\n[README truncated]";
        }
        var a = repo["annotation"]!;
        var english = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var system = english
            ? "You summarize GitHub repositories for a developer's personal library. Be concrete and factual, use markdown with short sections: what it does, key features, tech stack, when to use it, caveats. Never invent facts not present in the input. Keep it under 250 words. The user's own notes, when present, are their judgment: respect them, never contradict or overwrite them."
            : "你在为开发者的个人仓库库总结 GitHub 项目。使用 Markdown、简短分节：它是做什么的、主要特性、技术栈、适用场景、注意事项。不要编造输入中没有的事实。全文控制在 250 字以内。用户自己的笔记（如有）代表其判断：尊重它们，不要覆盖或反驳。";
        var user = $"""
            Repository: {repo["fullName"]?.GetValue<string>()}
            Description: {repo["description"]?.GetValue<string>()}
            Topics: {string.Join(", ", repo["topics"]!.AsArray().Select(t => t?.GetValue<string>()))}
            Language: {repo["language"]?.GetValue<string>() ?? "-"}
            Stars: {repo["stars"]?.GetValue<long>().ToString() ?? "-"}
            License: {repo["licenseId"]?.GetValue<string>() ?? "-"}
            User's own reason/notes (context only): {a["reason"]?.GetValue<string>()} {a["notes"]?.GetValue<string>()}

            README:
            {readme}
            """;

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = 0.3,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ServiceException("ai_unreachable", 502, $"Could not reach DeepSeek: {ex.Message}", retryable: true);
        }
        using (res)
        {
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new ServiceException("ai_unauthorized", 502, "DeepSeek rejected the API key. Check it in Settings.");
            }
            if ((int)res.StatusCode == 429)
            {
                throw new ServiceException("ai_rate_limited", 502, "DeepSeek rate limit reached; retry shortly.", retryable: true, retryAfterMs: 30_000);
            }
            if (!res.IsSuccessStatusCode)
            {
                throw new ServiceException("ai_error", 502, $"DeepSeek responded with HTTP {(int)res.StatusCode}", retryable: (int)res.StatusCode >= 500);
            }
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cancel), cancellationToken: cancel);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ServiceException("ai_error", 502, "DeepSeek returned an empty summary", retryable: true);
            }
            return (content.Trim(), _model);
        }
    }
}
