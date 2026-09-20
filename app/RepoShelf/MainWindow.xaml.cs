using System.IO;
using Application = System.Windows.Application;
using System.Windows;

namespace RepoShelf;

public partial class MainWindow : Window
{
    /// <summary>When false, closing the window hides it to the tray instead.</summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream);
        }
        catch { /* icon is cosmetic */ }
    }

    public async Task LoadAsync(string url)
    {
        // WebView2 user data must be writable: keep it in the local app-data dir.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "repo-shelf", "webview2");
        Directory.CreateDirectory(userData);
        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userData);
        await WebView.EnsureCoreWebView2Async(env);
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF6, 0xF7, 0xF9);
        WebView.CoreWebView2.Navigate(url);
        WebView.NavigationCompleted += (_, _) => LoadingText.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
