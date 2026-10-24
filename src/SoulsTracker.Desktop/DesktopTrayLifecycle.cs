using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SoulsTracker.Desktop;

/// <summary>Separates presentation-only hiding from explicit application shutdown.</summary>
internal sealed class DesktopTrayLifecycle(Action hide, Action restore, Action exit, Action<string> guidance, Func<bool>? minimizeOnClose = null)
{
    private bool guided;
    private bool exiting;
    public bool TrayAvailable { get; set; }
    public bool Exiting => exiting;

    public void Minimized()
    {
        if (TrayAvailable && !exiting) hide();
        else restore();
    }

    public void Closing(CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (minimizeOnClose?.Invoke() != true)
        {
            Exit();
            return;
        }
        if (!TrayAvailable)
        {
            restore();
            guidance("The notification area is unavailable. SoulsTracker will stay open; turn off Minimize to tray on close in Settings to quit.");
            return;
        }
        hide();
        if (!guided)
        {
            guided = true;
            guidance("SoulsTracker is still running in the notification area. Use its icon to open or exit.");
        }
    }

    public void Restore()
    {
        if (!exiting) restore();
    }

    public void Exit()
    {
        if (exiting) return;
        exiting = true;
        exit();
    }
}

/// <summary>Owns the native notification icon and its standard keyboard-accessible menu.</summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon productIcon;
    private readonly HwndSource source;
    private readonly int taskbarCreated;
    private readonly Action onFailure;
    private readonly Action onRecovery;
    private bool disposed;

    internal WindowsTrayIcon(Window window, Action restore, Action exit, Action onRecovery, Action onFailure)
    {
        this.onFailure = onFailure;
        this.onRecovery = onRecovery;
        source = (HwndSource)PresentationSource.FromVisual(window)!;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var open = new Forms.ToolStripMenuItem("Open SoulsTracker");
        var quit = new Forms.ToolStripMenuItem("Exit");
        open.Click += (_, _) => restore();
        quit.Click += (_, _) => exit();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(open);
        menu.Items.Add(quit);
        using var stream = System.Windows.Application.GetResourceStream(
            new Uri("/SoulsTracker.Desktop;component/assets/branding/souls-tracker-skull-compact.ico", UriKind.Relative))?.Stream
            ?? throw new InvalidOperationException("The product icon is unavailable.");
        using var loaded = new Icon(stream);
        productIcon = (Icon)loaded.Clone();
        icon = new Forms.NotifyIcon { Icon = productIcon, Text = "SoulsTracker - tracking deaths", ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) restore(); };
        source.AddHook(WndProc);
        try { icon.Visible = true; onRecovery(); }
        catch { onFailure(); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == taskbarCreated && !disposed)
        {
            try { icon.Visible = false; icon.Visible = true; onRecovery(); }
            catch { onFailure(); }
        }
        return IntPtr.Zero;
    }

    internal void ShowGuidance(string message)
    {
        if (!disposed && icon.Visible) icon.ShowBalloonTip(4000, "SoulsTracker", message, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.RemoveHook(WndProc);
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        productIcon.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);
}
