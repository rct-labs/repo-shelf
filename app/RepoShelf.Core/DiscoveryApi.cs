using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace RepoShelf.Core;

public interface IDiscoveryRoutes
{
    Task<bool> TryHandleAsync(HttpContext ctx, string method, string path);
}

/// <summary>Modules register at assembly initialization, before hosts are started.</summary>
public static class DiscoveryRoutes
{
    private static readonly object Sync = new();
    private static readonly List<Func<ServiceHost, IDiscoveryRoutes>> Factories = new();
    private static readonly List<Action<ServiceHost>> StartHooks = new();

    public static void Register(Func<ServiceHost, IDiscoveryRoutes> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Sync) Factories.Add(factory);
    }

    public static void RegisterStartHook(Action<ServiceHost> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (Sync) StartHooks.Add(hook);
    }

    internal static IDiscoveryRoutes[] CreateHandlers(ServiceHost host)
    {
        Func<ServiceHost, IDiscoveryRoutes>[] factories;
        lock (Sync) factories = Factories.ToArray();
        return factories.Select(factory => factory(host)).ToArray();
    }

    internal static void Start(ServiceHost host)
    {
        Action<ServiceHost>[] hooks;
        lock (Sync) hooks = StartHooks.ToArray();
        foreach (var hook in hooks) hook(host);
    }
}

/// <summary>Project CRUD and dispatch to the discovery modules, behind HttpApi authorization.</summary>
public sealed class DiscoveryApi : IDiscoveryRoutes
{
    private readonly DiscoveryStore _store;
    private readonly IDiscoveryRoutes[] _handlers;

    public DiscoveryApi(ServiceHost host)
    {
        _store = host.Feature(h => new DiscoveryStore(h.Store));
        _handlers = DiscoveryRoutes.CreateHandlers(host);
    }

    public static bool HandlesPath(string path) =>
        path == "/api/projects" || path.StartsWith("/api/projects/", StringComparison.Ordinal)
        || path == "/api/discovery" || path.StartsWith("/api/discovery/", StringComparison.Ordinal)
        || Regex.IsMatch(path, @"^/api/repos/[0-9]+/recommendations$");

    public async Task<bool> TryHandleAsync(HttpContext ctx, string method, string path)
    {
        if (path == "/api/projects" && method == "GET")
        {
            await SendDataAsync(ctx, new JsonObject { ["projects"] = _store.ListProjects() });
            return true;
        }
        if (path == "/api/projects" && method == "POST")
        {
            var project = _store.AddProject(await ReadBodyAsync(ctx));
            await SendDataAsync(ctx, new JsonObject { ["project"] = project }, 201);
            return true;
        }
        var match = Regex.Match(path, @"^/api/projects/([0-9]+)$");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var id))
        {
            if (method == "PUT")
            {
                var project = _store.UpdateProject(id, await ReadBodyAsync(ctx));
                await SendDataAsync(ctx, new JsonObject { ["project"] = project });
                return true;
            }
            if (method == "DELETE")
            {
                _store.DeleteProject(id);
                await SendDataAsync(ctx, new JsonObject { ["deleted"] = true });
                return true;
            }
        }
        foreach (var handler in _handlers)
            if (await handler.TryHandleAsync(ctx, method, path)) return true;
        return false;
    }

    // Shared by extension modules so their envelope and validation stay consistent.
    public static async Task<JsonObject> ReadBodyAsync(HttpContext ctx)
    {
        try
        {
            return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject
                ?? throw new ServiceException("invalid_json", 400, "Request body must be a JSON object");
        }
        catch (JsonException)
        {
            throw new ServiceException("invalid_json", 400, "Request body is not valid JSON");
        }
    }

    public static async Task SendDataAsync(HttpContext ctx, JsonObject data, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["ok"] = true, ["data"] = data }, ctx.RequestAborted);
    }
}
