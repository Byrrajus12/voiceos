using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VoiceOS.Core.Activation;
using VoiceOS.UI;

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
    private readonly ProductUiOverlay _ui;
    private SynchronizationContext? _uiContext;
    private Icon? _currentIcon;
    private bool _disposed;
    private long _lastUiGeneration;

    public TrayApplication(ActivationOrchestrator orchestrator,
        Microsoft.Extensions.Logging.ILogger? productUiLogger = null)
    {
        _orchestrator = orchestrator;
        _ui = new ProductUiOverlay(productUiLogger);

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
        _orchestrator.ProductUiChanged += OnProductUiChanged;

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
            ActivationState.Transcribing => "VoiceOS — Transcribing",
            ActivationState.Understanding => "VoiceOS — Understanding",
            _ => "VoiceOS — Idle"
        };
    }

    private void OnProductUiChanged(object? sender, ProductUiLifecycle update)
    {
        if (_disposed) return;
        var ctx = _uiContext;
        if (ctx != null) ctx.Post(_ => ApplyProductUi(update), null);
    }

    private void ApplyProductUi(ProductUiLifecycle update)
    {
        if (_disposed || update.Generation < _lastUiGeneration) return;
        _lastUiGeneration = update.Generation;
        if (update.Phase == ProductUiPhase.Acting)
            _ui.SetActingMessage(update.Message);
        else if (update.Phase == ProductUiPhase.Clarify)
            _ui.SetClarificationMessage(update.Message ?? "Could you clarify that request?");
        else if (update.Phase == ProductUiPhase.Error)
            _ui.SetErrorMessage(update.Message ?? "Couldn't complete that action.");
        _ui.SetState(update.Phase switch
        {
            ProductUiPhase.Listening => ProductUiState.Listening,
            ProductUiPhase.Understanding => ProductUiState.Understanding,
            ProductUiPhase.Acting => ProductUiState.Acting,
            ProductUiPhase.Success => ProductUiState.Success,
            ProductUiPhase.Clarify => ProductUiState.Clarify,
            ProductUiPhase.Error => ProductUiState.Error,
            _ => ProductUiState.Idle
        });
    }

    private void SetIcon(ActivationState state)
    {
        var color = state switch
        {
            ActivationState.Recording => Color.Crimson,
            ActivationState.Stopping => Color.DarkOrange,
            ActivationState.Transcribing => Color.DodgerBlue,
            ActivationState.Understanding => Color.MediumPurple,
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
            _orchestrator.ProductUiChanged -= OnProductUiChanged;
            _orchestrator.Dispose();
            _ui.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
