using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RepoShelf.Core;

/// <summary>
/// Loopback HTTP API + embedded static UI, mirroring the Node reference.
///
/// Security model:
/// - Every /api/* request (except /api/health) must send the pairing token in
///   the X-RepoShelf-Token header. The web UI receives the token via a meta
///   tag in the served HTML (same-origin only, per SOP).
/// - Requests carrying an Origin header are honored only from the app's own
///   loopback origin or a browser-extension scheme.
/// - The Host header must be the loopback listener (DNS-rebinding protection).
/// </summary>
public sealed class HttpApi
{
    public const string AppVersion = "0.1.0";

    private readonly ServiceHost _host;
    private readonly HashSet<string> _ownOrigins;
    private readonly HashSet<string> _validHosts;

    public HttpApi(ServiceHost host)
    {
        _host = host;
        var port = host.Config.Port;
        _ownOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"http://127.0.0.1:{port}", $"http://localhost:{port}", $"http://[::1]:{port}",
        };
        _validHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"127.0.0.1:{port}", $"localhost:{port}", $"[::1]:{port}",
        };
    }

    public WebApplication Build()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls($"http://{_host.Config.Host}:{_host.Config.Port}");
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
            o => o.Limits.MaxRequestBodySize = 64 * 1024 * 1024);

        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    private bool Authorize(HttpContext ctx, out int status, out string code, out string message, out string? extensionOrigin)
    {
        status = 200;
        code = "";
        message = "";
        extensionOrigin = null;

        var host = ctx.Request.Host.ToString();
        if (!_validHosts.Contains(host))
        {
            status = 403;
            code = "invalid_host";
            message = "Requests must address the loopback listener directly";
            return false;
        }
        var origin = ctx.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrEmpty(origin))
        {
            if (_ownOrigins.Contains(origin))
            {
                // The app's own web UI.
            }
            else if (origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
                || origin.StartsWith("moz-extension://", StringComparison.Ordinal))
            {
                extensionOrigin = origin;
            }
            else
            {
                status = 403;
                code = "origin_not_allowed";
                message = $"Origin \"{origin}\" is not allowed";
                return false;
            }
        }
        if (ctx.Request.Path == "/api/health")
        {
            return true;
        }
        if (ctx.Request.Headers["x-reposhelf-token"].FirstOrDefault() != _host.PairingToken)
        {
            status = 401;
            code = "unauthorized";
            message = "Missing or invalid pairing token";
            return false;
        }
        return true;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        try
        {
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                HandlePreflight(ctx);
                return;
            }
            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (!Authorize(ctx, out var status, out var code, out var message, out var extensionOrigin))
                {
                    await SendError(ctx, status, code, message);
                    return;
                }
                if (extensionOrigin is not null)
                {
                    ctx.Response.Headers["Access-Control-Allow-Origin"] = extensionOrigin;
                }
                await RouteApiAsync(ctx, path);
                return;
            }
            await ServeStaticAsync(ctx, path);
        }
        catch (ServiceException ex)
        {
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                await SendError(ctx, ex.HttpStatus, ex.Code, ex.Message, ex.Retryable, ex.RetryAfterMs);
            }
        }
        catch (BackupService.BackupException ex)
        {
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                await SendError(ctx, 400, ex.Code, ex.Message);
            }
        }
        catch (Exception ex)
        {
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                await SendError(ctx, 500, "internal_error", "Internal server error");
            }
            Console.Error.WriteLine($"Unhandled error: {ex}");
        }
    }

    private void HandlePreflight(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.FirstOrDefault() ?? "";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "content-type, x-reposhelf-token";
        ctx.Response.Headers["Access-Control-Max-Age"] = "600";
        if (origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
            || origin.StartsWith("moz-extension://", StringComparison.Ordinal))
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
        }
        ctx.Response.StatusCode = 204;
    }

    private static async Task SendJson(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsJsonAsync(body, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private static Task SendError(HttpContext ctx, int status, string code, string message, bool retryable = false, long? retryAfterMs = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (retryable)
        {
            error["retryable"] = true;
        }
        if (retryAfterMs is not null)
        {
            error["retryAfterMs"] = retryAfterMs.Value;
        }
        return SendJson(ctx, status, new JsonObject { ["ok"] = false, ["error"] = error });
    }

    private static async Task<JsonObject?> ReadJsonBody(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new ServiceException("invalid_json", 400, "Request body must be a JSON object");
        }
        catch (JsonException)
        {
            throw new ServiceException("invalid_json", 400, "Request body is not valid JSON");
        }
    }

    private async Task RouteApiAsync(HttpContext ctx, string path)
    {
        var method = ctx.Request.Method;
        var store = _host.Store;
        var repos = _host.Repos;
        var jobs = _host.Jobs;

        if (method == "GET" && path == "/api/health")
        {
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["app"] = "repo-shelf", ["version"] = AppVersion } });
            return;
        }

        if (method == "POST" && path == "/api/repos")
        {
            var body = await ReadJsonBody(ctx);
            var tags = body?["tags"] is JsonArray ta
                ? ta.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
            var result = await repos.SaveRepoAsync(body?["url"]?.GetValue<string>(), body?["reason"]?.GetValue<string>() ?? "", tags);
            var outcome = result["outcome"]?.GetValue<string>();
            await SendJson(ctx, outcome == "created" ? 201 : 200, new JsonObject { ["ok"] = true, ["data"] = result });
            return;
        }

        if (method == "GET" && path == "/api/repos")
        {
            var q = ctx.Request.Query;
            var archivedParam = q["archived"].FirstOrDefault();
            var result = _host.Search.Search(new SearchService.SearchParams(
                Query: q["q"].FirstOrDefault() ?? "",
                Status: string.IsNullOrEmpty(q["status"]) ? null : q["status"].First(),
                Tag: string.IsNullOrEmpty(q["tag"]) ? null : q["tag"].First(),
                Project: string.IsNullOrEmpty(q["project"]) ? null : q["project"].First(),
                Language: string.IsNullOrEmpty(q["language"]) ? null : q["language"].First(),
                Archived: archivedParam == "true" ? true : archivedParam == "false" ? false : null,
                Limit: int.TryParse(q["limit"], out var l) ? l : 50,
                Offset: int.TryParse(q["offset"], out var o) ? o : 0));
            var items = new JsonArray();
            foreach (var item in result.Items)
            {
                var record = item.Repo; // readme text already omitted from list payloads
                items.Add(new JsonObject
                {
                    ["repo"] = record,
                    ["matchedFields"] = new JsonArray(item.MatchedFields.Select(f => JsonValue.Create(f)).ToArray()),
                    ["snippet"] = item.Snippet,
                    ["snippetField"] = item.SnippetField,
                    ["rank"] = item.Rank,
                });
            }
            await SendJson(ctx, 200, new JsonObject
            {
                ["ok"] = true,
                ["data"] = new JsonObject { ["total"] = result.Total, ["items"] = items },
            });
            return;
        }

        var repoMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/api/repos/(\d+)$");
        if (repoMatch.Success && method == "GET")
        {
            var repo = repos.GetRepo(long.Parse(repoMatch.Groups[1].Value))
                ?? throw new ServiceException("not_found", 404, "Repository not found");
            repo["hasReadme"] = repo["readme"]?.GetValue<string>() is { Length: > 0 };
            repo["generated"] = new JsonObject { ["summary"] = _host.Ai.GetSummary(repo["id"]!.GetValue<long>()) };
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["repo"] = repo } });
            return;
        }
        if (repoMatch.Success && method == "DELETE")
        {
            repos.DeleteRepo(long.Parse(repoMatch.Groups[1].Value));
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["deleted"] = true } });
            return;
        }

        var annotationMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/api/repos/(\d+)/annotation$");
        if (annotationMatch.Success && method == "PATCH")
        {
            var body = await ReadJsonBody(ctx) ?? new JsonObject();
            var annotation = repos.UpdateAnnotation(long.Parse(annotationMatch.Groups[1].Value), body);
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["annotation"] = annotation.DeepClone() } });
            return;
        }

        var refreshMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/api/repos/(\d+)/refresh$");
        if (refreshMatch.Success && method == "POST")
        {
            var (repo, renamed) = await repos.RefreshRepoAsync(long.Parse(refreshMatch.Groups[1].Value));
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["repo"] = repo, ["renamed"] = renamed } });
            return;
        }

        var summarizeMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/api/repos/(\d+)/summarize$");
        if (summarizeMatch.Success && method == "POST")
        {
            var body = await ReadJsonBody(ctx);
            var lang = body?["lang"]?.GetValue<string>() ?? "zh";
            var summary = await _host.Ai.SummarizeAsync(long.Parse(summarizeMatch.Groups[1].Value), lang, ctx.RequestAborted);
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["summary"] = summary } });
            return;
        }

        if (method == "GET" && path == "/api/repos/lookup")
        {
            var urlParam = ctx.Request.Query["url"].FirstOrDefault() ?? "";
            try
            {
                var parsed = RepoUrlParser.Parse(urlParam);
                var repo = repos.FindByFullName(parsed.FullName);
                if (repo is not null)
                {
                    repo["readme"] = null; // lookup stays light
                }
                await SendJson(ctx, 200, new JsonObject
                {
                    ["ok"] = true,
                    ["data"] = new JsonObject
                    {
                        ["valid"] = true,
                        ["fullName"] = parsed.FullName,
                        ["found"] = repo is not null,
                        ["repo"] = repo,
                    },
                });
            }
            catch (RepoUrlParser.InvalidUrlException)
            {
                await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["valid"] = false } });
            }
            return;
        }

        if (method == "POST" && path == "/api/import/chrome-bookmarks")
        {
            var body = await ReadJsonBody(ctx);
            var (job, error) = jobs.StartBookmarksImport(body?["path"]?.GetValue<string>());
            if (job is null)
            {
                throw new ServiceException("bookmarks_unavailable", 400, error ?? "Bookmarks file not found");
            }
            await SendJson(ctx, 202, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["job"] = job } });
            return;
        }

        if (method == "GET" && path == "/api/filters")
        {
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = repos.ListFilterOptions() });
            return;
        }

        if (method == "POST" && path == "/api/jobs/import-stars")
        {
            var body = await ReadJsonBody(ctx);
            var username = body?["username"]?.GetValue<string>()?.Trim() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,38})$"))
            {
                throw new ServiceException("invalid_field", 400, "A valid GitHub username is required");
            }
            var includeReadme = body?["includeReadme"]?.GetValue<bool>() ?? true;
            var job = jobs.StartStarsImport(username, includeReadme);
            await SendJson(ctx, 202, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["job"] = job } });
            return;
        }

        if (method == "POST" && path == "/api/jobs/refresh-all")
        {
            var job = jobs.StartRefreshAll();
            await SendJson(ctx, 202, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["job"] = job } });
            return;
        }

        if (method == "GET" && path == "/api/jobs")
        {
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["jobs"] = jobs.ListJobs() } });
            return;
        }

        var jobMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/api/jobs/([0-9a-fA-F-]+)$");
        if (jobMatch.Success && method == "GET")
        {
            var job = jobs.GetJob(jobMatch.Groups[1].Value)
                ?? throw new ServiceException("not_found", 404, "Job not found");
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["job"] = job } });
            return;
        }

        var jobAction = System.Text.RegularExpressions.Regex.Match(path, @"^/api/jobs/([0-9a-fA-F-]+)/(cancel|resume)$");
        if (jobAction.Success && method == "POST")
        {
            if (jobAction.Groups[2].Value == "cancel")
            {
                var stopped = jobs.CancelJob(jobAction.Groups[1].Value);
                await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["cancelled"] = stopped } });
                return;
            }
            var job = jobs.ResumeJob(jobAction.Groups[1].Value)
                ?? throw new ServiceException("not_found", 404, "Job not found");
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["job"] = job } });
            return;
        }

        if (method == "GET" && path == "/api/export")
        {
            var data = _host.Backup.Export();
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd");
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"repo-shelf-export-{stamp}.json\"";
            await SendJson(ctx, 200, data);
            return;
        }

        if (method == "POST" && path == "/api/restore")
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync();
            JsonNode? payload;
            try
            {
                payload = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                throw new ServiceException("invalid_json", 400, "Request body is not valid JSON");
            }
            var mode = ctx.Request.Query["mode"].FirstOrDefault() == "overwrite" ? "overwrite" : "merge";
            var counts = _host.Backup.Import(payload, mode);
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = counts });
            return;
        }

        if (method == "GET" && path == "/api/settings")
        {
            await SendJson(ctx, 200, new JsonObject
            {
                ["ok"] = true,
                ["data"] = new JsonObject
                {
                    ["version"] = AppVersion,
                    ["port"] = _host.Config.Port,
                    ["dataDir"] = _host.Config.DataDir,
                    ["pairingToken"] = _host.PairingToken,
                    ["githubTokenSet"] = store.GetSetting("github_token") is not null,
                    ["deepseekKeySet"] = store.GetSetting("deepseek_api_key") is not null,
                    ["statuses"] = new JsonArray(Store.Statuses.Select(s => JsonValue.Create(s)).ToArray()),
                },
            });
            return;
        }

        if (method == "PUT" && path == "/api/settings/github-token")
        {
            var body = await ReadJsonBody(ctx);
            var token = body?["token"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                throw new ServiceException("invalid_field", 400, "token must be a non-empty string");
            }
            store.SetSetting("github_token", token);
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["githubTokenSet"] = true } });
            return;
        }
        if (method == "DELETE" && path == "/api/settings/github-token")
        {
            store.SetSetting("github_token", null);
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["githubTokenSet"] = false } });
            return;
        }

        if (method == "PUT" && path == "/api/settings/deepseek-key")
        {
            var body = await ReadJsonBody(ctx);
            var key = body?["token"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                throw new ServiceException("invalid_field", 400, "token must be a non-empty string");
            }
            store.SetSetting("deepseek_api_key", key);
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["deepseekKeySet"] = true } });
            return;
        }
        if (method == "DELETE" && path == "/api/settings/deepseek-key")
        {
            store.SetSetting("deepseek_api_key", null);
            await SendJson(ctx, 200, new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["deepseekKeySet"] = false } });
            return;
        }

        await SendError(ctx, 404, "not_found", "Unknown API endpoint");
    }

    private static readonly Dictionary<string, string> Mime = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".md"] = "text/markdown; charset=utf-8",
    };

    private async Task ServeStaticAsync(HttpContext ctx, string path)
    {
        if (ctx.Request.Method is not ("GET" or "HEAD"))
        {
            await SendError(ctx, 405, "method_not_allowed", "Method not allowed");
            return;
        }
        if (path is "/" or "/index.html")
        {
            var html = UiAssets.ReadText("index.html")
                ?? throw new ServiceException("internal_error", 500, "UI assets missing");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; connect-src 'self'; object-src 'none'; base-uri 'none'";
            await ctx.Response.WriteAsync(html.Replace("__RS_TOKEN__", _host.PairingToken));
            return;
        }
        var assetPath = path.TrimStart('/');
        if (assetPath.Contains("..", StringComparison.Ordinal))
        {
            await SendError(ctx, 404, "not_found", "Not found");
            return;
        }
        if (!UiAssets.TryRead(assetPath, out var content, out var contentType))
        {
            await SendError(ctx, 404, "not_found", "Not found");
            return;
        }
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.CacheControl = "no-cache";
        if (ctx.Request.Method != "HEAD")
        {
            await ctx.Response.Body.WriteAsync(content);
        }
    }
}
