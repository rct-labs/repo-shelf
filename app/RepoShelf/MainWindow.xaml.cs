using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Application = System.Windows.Application;

namespace RepoShelf;

public partial class MainWindow : Window
{
    private static readonly (double W, double H) BarSize = (640, 96);
    private static readonly (double W, double H) CompactSize = (640, 500);
    private static readonly (double W, double H) ExpandedSize = (1120, 720);

    /// <summary>When false, closing the window hides it to the tray instead.</summary>
    public bool AllowClose { get; set; }
    private bool _expanded;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var ico = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(ico);
            TitleIcon.Source = Icon;
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
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await WebView.EnsureCoreWebView2Async(env);
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x12, 0x14);
        WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
        WebView.CoreWebView2.Navigate(url);
        WebView.NavigationCompleted += (_, _) => LoadingText.Visibility = Visibility.Collapsed;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "resize")
            {
                var mode = doc.RootElement.GetProperty("mode").GetString();
                AnimateTo(mode switch
                {
                    "bar" => BarSize,
                    "expanded" => ExpandedSize,
                    _ => CompactSize,
                });
                _expanded = mode == "expanded";
            }
            else if (type == "theme")
            {
                ApplyShellTheme(doc.RootElement.GetProperty("mode").GetString() == "light");
            }
        }
        catch { /* malformed host message: ignore */ }
    }

    private void ApplyShellTheme(bool light)
    {
        // Keep the custom title bar in step with the web UI theme.
        var bg = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#f6f7f9" : "#101214")!;
        var fg = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#59636e" : "#8b938f")!;
        Background = new System.Windows.Media.SolidColorBrush(bg);
        TitleBar.Background = Background;
        var fgBrush = new System.Windows.Media.SolidColorBrush(fg);
        BtnMin.Foreground = fgBrush;
        BtnClose.Foreground = fgBrush;
        ((System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)TitleBar.Child).Children[1]).Foreground = fgBrush;
    }

    private void AnimateTo((double W, double H) size)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        BeginAnimation(WidthProperty, new DoubleAnimation(size.W, duration) { EasingFunction = new QuadraticEase() });
        BeginAnimation(HeightProperty, new DoubleAnimation(size.H, duration) { EasingFunction = new QuadraticEase() });
    }

    public void ResetToCompact()
    {
        _expanded = false;
        Width = CompactSize.W;
        Height = CompactSize.H;
    }

    private static bool IsOnButton(object source)
    {
        // OriginalSource may be the TextBlock/ButtonChrome inside the button —
        // walk up the visual tree.
        for (var d = source as DependencyObject; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }
        return false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Clicks on the window buttons must not start a drag (they would
        // swallow the click and the buttons would appear dead).
        if (IsOnButton(e.OriginalSource))
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            AnimateTo(_expanded ? CompactSize : ExpandedSize);
            _expanded = !_expanded;
            return;
        }
        DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

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
