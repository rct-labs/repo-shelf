using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace RepoShelf;

/// <summary>WinForms NotifyIcon wrapper: tray menu with open/autostart/quit.</summary>
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
    }

    public void Dispose() => _icon.Dispose();
}
