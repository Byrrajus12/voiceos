using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VoiceOS.Core.Activation;

namespace VoiceOS;

/// <summary>
/// WinForms ApplicationContext that owns the tray icon.
/// Listens to ActivationOrchestrator.StateChanged and updates the icon and tooltip accordingly.
/// </summary>
public sealed class TrayApplication : ApplicationContext
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly ActivationOrchestrator _orchestrator;
    private readonly NotifyIcon _trayIcon;
    private SynchronizationContext? _uiContext;
    private Icon? _currentIcon;
    private bool _disposed;

    public TrayApplication(ActivationOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _trayIcon = new NotifyIcon
        {
            Text = "VoiceOS — Idle",
            Visible = true,
            ContextMenuStrip = menu
        };
        SetIcon(ActivationState.Idle);

        _orchestrator.StateChanged += OnStateChanged;

        // Install hook after the message pump is running.
        Application.Idle += OnFirstIdle;
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        // Capture the sync context now that the WinForms message pump has installed it.
        _uiContext = SynchronizationContext.Current;
        _orchestrator.Start();
    }

    private void OnStateChanged(object? sender, ActivationState state)
    {
        if (_disposed) return;

        // Marshal to UI thread since StateChanged fires from hook/timer threads.
        var ctx = _uiContext;
        if (ctx != null)
            ctx.Post(_ => UpdateTray(state), null);
        else
            UpdateTray(state);
    }

    private void UpdateTray(ActivationState state)
    {
        SetIcon(state);
        _trayIcon.Text = state switch
        {
            ActivationState.Recording => "VoiceOS — Recording",
            ActivationState.Stopping => "VoiceOS — Stopping",
            _ => "VoiceOS — Idle"
        };
    }

    private void SetIcon(ActivationState state)
    {
        var color = state switch
        {
            ActivationState.Recording => Color.Crimson,
            ActivationState.Stopping => Color.DarkOrange,
            _ => Color.SlateGray
        };

        var newIcon = CreateColoredIcon(color);
        var oldIcon = _currentIcon;
        _trayIcon.Icon = newIcon;
        _currentIcon = newIcon;
        oldIcon?.Dispose();
    }

    private static Icon CreateColoredIcon(Color fill)
    {
        using var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 1, 1, 13, 13);
        }

        IntPtr hIcon = bmp.GetHicon();
        // Clone creates an independent Icon that owns a copy of the HICON resource.
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon); // Release the original HICON allocated by GetHicon().
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            _orchestrator.StateChanged -= OnStateChanged;
            _orchestrator.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
