using System.Net.Http;
using System.Windows;
using RepoShelf.Core;
using Application = System.Windows.Application;

namespace RepoShelf;

public partial class App : Application
{
    private const string MutexName = @"Local\RepoShelf.Singleton";
    private const string ShowEventName = @"Local\RepoShelf.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private ServiceHost? _service;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _ownsService;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch just asks the running one to show.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    _showEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Dispatcher.Invoke(ShowMainWindow);
            }
        });

        // Tray app keeps running with no window open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var serviceOnly = e.Args.Contains("--service");
        var background = e.Args.Contains("--background");
        _ = InitAsync(serviceOnly, background);
    }

    private async Task InitAsync(bool serviceOnly, bool background)
    {
        try
        {
            _service = ServiceHost.Create();
            try
            {
                await _service.StartAsync();
                _ownsService = true;
            }
            catch (InvalidOperationException) // port already bound
            {
                if (await IsHealthyAsync(_service.BaseUrl))
                {
                    _ownsService = false; // our service is already running elsewhere
                }
                else
                {
                    throw;
                }
            }

            if (serviceOnly)
            {
                Console.WriteLine($"Repo Shelf service on {_service.BaseUrl}");
                Console.WriteLine($"Data: {_service.Config.DataDir}");
                return;
            }

            // First run: default to launch-at-login (reversible from the tray).
            if (_service.Store.GetSetting("desktop.autostart_initialized") is null)
            {
                _service.Store.SetSetting("desktop.autostart_initialized", "1");
                if (!ShellIntegration.IsAutostartEnabled())
                {
                    ShellIntegration.SetAutostart(true);
                }
            }

            _tray = new TrayIcon(ShowMainWindow, ShellIntegration.IsAutostartEnabled, ShellIntegration.SetAutostart, Quit);
            if (!background)
            {
                ShowMainWindow();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Repo Shelf failed to start:\n{ex.Message}", "Repo Shelf", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static async Task<bool> IsHealthyAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var res = await http.GetAsync($"{baseUrl}/api/health");
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void ShowMainWindow()
    {
        if (_service is null)
        {
            return;
        }
        if (_window is null)
        {
            _window = new MainWindow();
            _ = _window.LoadAsync(_service.BaseUrl);
        }
        if (!_window.IsVisible)
        {
            _window.Show();
        }
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _quitting = true;
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _tray?.Dispose();
        if (_ownsService)
        {
            _service?.Dispose();
        }
        _showEvent?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_quitting)
        {
            _tray?.Dispose();
            if (_ownsService)
            {
                _service?.Dispose();
            }
        }
        base.OnExit(e);
    }
}
