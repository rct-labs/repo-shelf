using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RepoShelf;

/// <summary>Global hotkey (Ctrl+Alt+K) via a message-only HWND.</summary>
public sealed class HotKeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id = 1;

    /// <summary>Returns false when the hotkey is already taken by another app.</summary>
    public bool Registered { get; }

    public HotKeys(Action onHotkey)
    {
        var p = new HwndSourceParameters("RepoShelfHotkeys")
        {
            ParentWindow = (IntPtr)(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr _, ref bool handled) =>
        {
            if (msg == WmHotkey && wParam.ToInt32() == _id)
            {
                handled = true;
                onHotkey();
            }
            return IntPtr.Zero;
        });
        // 'K' = 0x4B
        Registered = RegisterHotKey(_source.Handle, _id, ModControl | ModAlt | ModNoRepeat, 0x4B);
    }

    public void Dispose()
    {
        if (Registered)
        {
            UnregisterHotKey(_source.Handle, _id);
        }
        _source.Dispose();
    }
}
