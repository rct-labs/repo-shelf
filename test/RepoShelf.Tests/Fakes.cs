using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RepoShelf.Core;

namespace RepoShelf.Tests;

public static class TestRepos
{
    public static JsonObject MakeRepo(
        long id, string owner = "octocat", string name = "repo", string description = "A sample repository",
        string[]? topics = null, string? language = "JavaScript", string? license = "MIT",
        bool archived = false, string pushedAt = "2026-09-01T00:00:00Z", long stars = 42, string defaultBranch = "main")
    {
        return new JsonObject
        {
            ["id"] = id,
            ["owner"] = new JsonObject { ["login"] = owner },
            ["name"] = name,
            ["full_name"] = $"{owner}/{name}",
            ["html_url"] = $"https://github.com/{owner}/{name}",
            ["description"] = description,
            ["topics"] = new JsonArray((topics ?? Array.Empty<string>()).Select(t => JsonValue.Create(t)).ToArray()),
            ["language"] = language is null ? null : JsonValue.Create(language),
            ["license"] = license is null ? null : new JsonObject { ["spdx_id"] = license },
            ["archived"] = archived,
            ["pushed_at"] = pushedAt,
            ["stargazers_count"] = stars,
            ["default_branch"] = defaultBranch,
        };
    }
}

/// <summary>In-memory fake of the GitHub REST API over real HTTP (Kestrel).</summary>
public sealed class FakeGitHubServer : IDisposable
{
    private readonly WebApplication _app;
    private readonly Dictionary<string, JsonObject> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _readmes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<JsonObject>> _starred = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Func<string, bool> Match, Func<HttpResponseMessage> Respond)> _failures = new();
    private readonly List<Func<string, bool>> _throwOnce = new();

    public List<string> Requests { get; } = new();
    public string BaseUrl { get; }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public FakeGitHubServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseUrls(BaseUrl);
        _app = builder.Build();
        _app.Run(HandleAsync);
        _app.StartAsync().GetAwaiter().GetResult();
    }

    private static string Key(string owner, string repo) => $"{owner}/{repo}";

    public JsonObject AddRepo(JsonObject raw, string? readme = null)
    {
        var key = Key(raw["owner"]!["login"]!.GetValue<string>(), raw["name"]!.GetValue<string>());
        _repos[key] = raw;
        if (readme is not null)
        {
            _readmes[key] = readme;
        }
        return raw;
    }

    public JsonObject MoveRepo(JsonObject raw, string newOwner, string newName)
    {
        _repos.Remove(Key(raw["owner"]!["login"]!.GetValue<string>(), raw["name"]!.GetValue<string>()));
        var moved = (JsonObject)raw.DeepClone();
        moved["owner"] = new JsonObject { ["login"] = newOwner };
        moved["name"] = newName;
        moved["full_name"] = $"{newOwner}/{newName}";
        moved["html_url"] = $"https://github.com/{newOwner}/{newName}";
        _repos[Key(newOwner, newName)] = moved;
        return moved;
    }

    public void DeleteRepo(JsonObject raw) =>
        _repos.Remove(Key(raw["owner"]!["login"]!.GetValue<string>(), raw["name"]!.GetValue<string>()));

    public void SetStarred(string username, List<JsonObject> repos) => _starred[username] = repos;

    /// <summary>Fail the next matching request N times with the given status.</summary>
    public void FailNext(Func<string, bool> match, int times, HttpStatusCode status, Dictionary<string, string>? headers = null)
    {
        for (var i = 0; i < times; i++)
        {
            _failures.Add((match, () =>
            {
                var res = new HttpResponseMessage(status);
                if (headers is not null)
                {
                    foreach (var (k, v) in headers)
                    {
                        res.Headers.TryAddWithoutValidation(k, v);
                    }
                }
                return res;
            }));
        }
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        Requests.Add(path + ctx.Request.QueryString);

        var failureIndex = _failures.FindIndex(f => f.Match(path + ctx.Request.QueryString));
        if (failureIndex >= 0)
        {
            var failure = _failures[failureIndex];
            _failures.RemoveAt(failureIndex);
            using var res = failure.Respond();
            ctx.Response.StatusCode = (int)res.StatusCode;
            foreach (var h in res.Headers)
            {
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
            }
            return;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["repos", var owner, var repo, "readme"])
        {
            if (_readmes.TryGetValue(Key(owner, repo), out var text))
            {
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync(text);
                return;
            }
            ctx.Response.StatusCode = 404;
            return;
        }
        if (parts is ["repos", var o, var r])
        {
            if (_repos.TryGetValue(Key(o, r), out var raw))
            {
                await ctx.Response.WriteAsJsonAsync(raw);
                return;
            }
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new JsonObject { ["message"] = "Not Found" });
            return;
        }
        if (parts is ["repositories", var idStr] && long.TryParse(idStr, out var id))
        {
            var found = _repos.Values.FirstOrDefault(x => x["id"]!.GetValue<long>() == id);
            if (found is not null)
            {
                await ctx.Response.WriteAsJsonAsync(found);
                return;
            }
            ctx.Response.StatusCode = 404;
            return;
        }
        if (parts is ["users", var username, "starred"])
        {
            if (!_starred.TryGetValue(username, out var list))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new JsonObject { ["message"] = "Not Found" });
                return;
            }
            var perPage = int.TryParse(ctx.Request.Query["per_page"], out var pp) ? pp : 100;
            var page = int.TryParse(ctx.Request.Query["page"], out var pg) ? pg : 1;
            var slice = list.Skip((page - 1) * perPage).Take(perPage).ToList();
            if (page * perPage < list.Count)
            {
                ctx.Response.Headers.Link = $"<{BaseUrl}/users/{username}/starred?per_page={perPage}&page={page + 1}>; rel=\"next\"";
            }
            await ctx.Response.WriteAsJsonAsync(new JsonArray(slice.Select(x => x.DeepClone()).ToArray()));
            return;
        }
        ctx.Response.StatusCode = 404;
    }

    public void Dispose() => _app.StopAsync().GetAwaiter().GetResult();
}

public static class JobWaiter
{
    public static async Task<JsonObject> WaitForJobAsync(JobManager jobs, string id, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var job = jobs.GetJob(id);
            if (job is not null && job["status"]!.GetValue<string>() != "running")
            {
                return job;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Job {id} did not finish within {timeoutMs} ms");
    }
}
