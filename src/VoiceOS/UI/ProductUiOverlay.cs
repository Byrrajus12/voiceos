namespace VoiceOS.UI;

/// <summary>The small UI-facing contract shared by the live shell and preview.</summary>
internal sealed class ProductUiOverlay : IProductUiSurface
{
    private readonly EdgeGlowOverlay _glow = new();
    private readonly StatePillOverlay _pill = new();
    private readonly System.Windows.Forms.Timer _successTimer = new() { Interval = 1300 };

    public ProductUiOverlay() => _successTimer.Tick += (_, _) =>
    {
        _successTimer.Stop();
        SetState(ProductUiState.Idle);
    };

    public void SetState(ProductUiState state)
    {
        _successTimer.Stop();
        _glow.SetState(state);
        _pill.SetState(state);
        if (state == ProductUiState.Success) _successTimer.Start();
    }

    public void SetActingMessage(string? message) => _pill.SetActingMessage(message);
    public void SetClarificationMessage(string message) => _pill.SetClarificationMessage(message);
    public void SetErrorMessage(string message) => _pill.SetErrorMessage(message);

#if DEBUG
    public void SavePillFrame(string path) => _pill.SaveFrame(path);
    public string DescribePillFrame() => _pill.DescribeFrame();
#endif

    public void Dispose()
    {
        _successTimer.Dispose();
        _pill.Dispose();
        _glow.Dispose();
    }
}
