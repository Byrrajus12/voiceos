namespace VoiceOS.UI;

// The product state published to visible surfaces. Rendering details stay in the surface.
public enum ProductUiState
{
    Idle,
    Listening,
    Processing,
    Executing,
    Clarifying,
    Success,
    Error
}

internal interface IProductUiSurface : IDisposable
{
    void SetState(ProductUiState state);
}
