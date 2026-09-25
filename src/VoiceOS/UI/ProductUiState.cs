namespace VoiceOS.UI;

// The product state published to visible surfaces. Rendering details stay in the surface.
public enum ProductUiState
{
    Idle,
    Listening,
    Understanding,
    Acting,
    Clarify,
    Success,
    Error
}

internal interface IProductUiSurface : IDisposable
{
    void SetState(ProductUiState state);
}
