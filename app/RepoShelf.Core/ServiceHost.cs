using System.Security.Cryptography;

namespace RepoShelf.Core;

/// <summary>
/// Service bootstrap shared by the CLI entry and the desktop shell.
/// Owns the store, clients, jobs and the Kestrel host.
/// </summary>
public sealed class ServiceHost : IDisposable
{
    public AppConfig Config { get; }
    public Store Store { get; }
    public string PairingToken { get; }
    public GitHubClient GitHub { get; }
    public RepoService Repos { get; }
    public SearchService Search { get; }
    public JobManager Jobs { get; }
    public BackupService Backup { get; }
    public DeepSeekClient DeepSeek { get; }
    public AiService Ai { get; }

    private Microsoft.AspNetCore.Builder.WebApplication? _app;

    private ServiceHost(AppConfig config, Store store, string pairingToken)
    {
        Config = config;
        Store = store;
        PairingToken = pairingToken;
        GitHub = new GitHubClient(getToken: () => store.GetSetting("github_token"), baseUrl: config.GitHubApiBase);
        Repos = new RepoService(store, GitHub);
        Search = new SearchService(store);
        Jobs = new JobManager(store, GitHub, Repos, config.MaxRateLimitWaitMs);
        Backup = new BackupService(store, Repos);
        DeepSeek = new DeepSeekClient(getKey: () => store.GetSetting("deepseek_api_key"),
            baseUrl: config.DeepSeekApiBase, model: config.DeepSeekModel);
        Ai = new AiService(store, Repos, DeepSeek);
    }

    public static ServiceHost Create(IReadOnlyDictionary<string, string?>? env = null)
    {
        var config = AppConfig.Load(env);
        Directory.CreateDirectory(config.DataDir);
        var store = Store.Open(Path.Combine(config.DataDir, "repo-shelf.db"));
        var token = config.PairingTokenOverride ?? store.GetSetting("pairing_token");
        if (string.IsNullOrEmpty(token))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            store.SetSetting("pairing_token", token);
        }
        return new ServiceHost(config, store, token);
    }

    public string BaseUrl => $"http://{Config.Host}:{Config.Port}";

    /// <summary>Throws InvalidOperationException with Code EADDRINUSE when the port is taken.</summary>
    public async Task StartAsync(CancellationToken cancel = default)
    {
        var api = new HttpApi(this);
        _app = api.Build();
        try
        {
            await _app.StartAsync(cancel);
        }
        catch (IOException ex) when (ex.Message.Contains("address", StringComparison.OrdinalIgnoreCase)
            || ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
        {
            throw new InvalidOperationException($"Port {Config.Port} is already in use.", ex) { Data = { ["Code"] = "EADDRINUSE" } };
        }
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        Store.Dispose();
    }
}
