namespace VoiceOS.UI;

/// <summary>Manual proof harness; never connects to activation or the backend.</summary>
internal sealed class GlowPreviewApplication : ApplicationContext
{
    private readonly IProductUiSurface _glow = new EdgeGlowOverlay();
    private bool _started;

    public GlowPreviewApplication()
    {
        Application.Idle += OnFirstIdle;
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;
        Application.Idle -= OnFirstIdle;
        SynchronizationContext context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("WinForms UI context unavailable.");

        Console.WriteLine("Glow preview: L=listening, P=processing, I=idle, Q=quit. Press Enter after each letter.");
        _ = Task.Run(() =>
        {
            while (true)
            {
                string? command = Console.ReadLine();
                if (command is null || command.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    context.Post(_ => ExitThread(), null);
                    return;
                }

                ProductUiState? state = command.Trim().ToLowerInvariant() switch
                {
                    "l" => ProductUiState.Listening,
                    "p" => ProductUiState.Processing,
                    "i" => ProductUiState.Idle,
                    _ => null
                };
                if (state is { } next)
                    context.Post(_ => _glow.SetState(next), null);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnFirstIdle;
            _glow.Dispose();
        }
        base.Dispose(disposing);
    }
}
