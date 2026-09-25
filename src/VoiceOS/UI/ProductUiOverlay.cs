namespace VoiceOS.UI;

/// <summary>The small UI-facing contract shared by the live shell and preview.</summary>
internal sealed class ProductUiOverlay : IProductUiSurface
{
    private readonly EdgeGlowOverlay _glow = new();
    private readonly StatePillOverlay _pill = new();

    public void SetState(ProductUiState state)
    {
        _glow.SetState(state);
        _pill.SetState(state);
    }

    public void SetClarificationMessage(string message) => _pill.SetClarificationMessage(message);
    public void SetErrorMessage(string message) => _pill.SetErrorMessage(message);

#if DEBUG
    public void SavePillFrame(string path) => _pill.SaveFrame(path);
    public string DescribePillFrame() => _pill.DescribeFrame();
#endif

    public void Dispose()
    {
        _pill.Dispose();
        _glow.Dispose();
    }
}
