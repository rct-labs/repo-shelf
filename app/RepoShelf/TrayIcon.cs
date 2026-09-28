using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace RepoShelf;

/// <summary>WinForms NotifyIcon wrapper: tray menu with open/autostart/quit, new-recommendation balloon.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action onOpen, Func<bool> getAutostart, Action<bool> setAutostart, Action onQuit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Repo Shelf", null, (_, _) => onOpen());

        ToolStripMenuItem autostartItem;
        menu.Items.Add(autostartItem = new ToolStripMenuItem("Launch at login"));
        autostartItem.CheckOnClick = true;
        autostartItem.Checked = getAutostart();
        autostartItem.CheckedChanged += (_, _) =>
        {
            if (autostartItem.Checked != getAutostart())
            {
                setAutostart(autostartItem.Checked);
            }
        };
        menu.Opening += (_, _) => autostartItem.Checked = getAutostart();

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => onQuit());

        Icon icon;
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        icon = resource is not null
            ? new Icon(resource.Stream)
            : SystemIcons.Application;

        _icon = new NotifyIcon
        {
            Text = "Repo Shelf",
            Icon = icon,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => onOpen();
        _icon.BalloonTipClicked += (_, _) => onOpen();
    }

    /// <summary>One balloon per discovery run that inserted candidates; clicking it opens the window.</summary>
    public void ShowNewCandidates(int count, string lang = "en")
    {
        if (count <= 0)
        {
            return;
        }
        var text = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? $"有 {count} 个新推荐"
            : count == 1 ? "1 new recommendation" : $"{count} new recommendations";
        _icon.ShowBalloonTip(10_000, "Repo Shelf", text, ToolTipIcon.Info);
    }

    public void Dispose() => _icon.Dispose();
}
