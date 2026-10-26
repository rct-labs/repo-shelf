namespace RepoShelf.Core;

/// <summary>Daily snapshots, oldest first, with a bounded hourly API budget.</summary>
public sealed class AutoRefresh : IAsyncDisposable
{
    private readonly Store _store;
    private readonly RepoService _repos;
    private readonly Func<bool> _busy;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    public AutoRefresh(Store store, RepoService repos, Func<bool>? busy = null)
    {
        _store = store;
        _repos = repos;
        _busy = busy ?? (() => false);
    }

    public void Start() => _loop ??= LoopAsync();

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                try { await RunOnceAsync(DateTimeOffset.UtcNow, _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { _store.SetSetting("auto_refresh_error", ex.Message); }
                await Task.Delay(TimeSpan.FromMinutes(5), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancel = default)
    {
        if (!await _gate.WaitAsync(0, cancel)) return 0;
        try
        {
            if (_busy()) return 0;
            if (DateTimeOffset.TryParse(_store.GetSetting("auto_refresh_next_at"), out var next) && next > now) return 0;
            var ids = new List<long>();
            lock (_store.Sync)
            {
                using var cmd = _store.Conn.CreateCommand();
                cmd.CommandText = """
                    SELECT id FROM repos
                    WHERE fetched_at IS NULL OR julianday(fetched_at) IS NULL OR julianday(fetched_at) <= julianday($cutoff)
                    ORDER BY fetched_at, id LIMIT 20
                    """;
                cmd.Parameters.AddWithValue("$cutoff", now.AddDays(-1).ToString("O"));
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetInt64(0));
            }
            // Persist before network requests so restart cannot bypass the budget.
            _store.SetSetting("auto_refresh_next_at", now.AddHours(1).ToString("O"));
            var updated = 0;
            foreach (var id in ids)
            {
                cancel.ThrowIfCancellationRequested();
                if (_busy()) break;
                try
                {
                    await _repos.RefreshRepoAsync(id, cancel);
                    updated++;
                }
                catch (ServiceException ex)
                {
                    _store.SetSetting("auto_refresh_error", ex.Message);
                    if (ex.Code is "github_rate_limited" or "github_unauthorized" or "github_forbidden" or "upstream_unavailable")
                    {
                        var delay = Math.Max(3_600_000L, ex.RetryAfterMs ?? 0);
                        _store.SetSetting("auto_refresh_next_at", now.AddMilliseconds(delay).ToString("O"));
                        break;
                    }
                }
            }
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
        _stop.Dispose();
    }
}
