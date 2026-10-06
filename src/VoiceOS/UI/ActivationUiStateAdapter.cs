using VoiceOS.Core.Activation;

namespace VoiceOS.UI;

internal static class ActivationUiStateAdapter
{
    public static ProductUiState Map(ActivationState state) => state switch
    {
        ActivationState.Recording or ActivationState.Stopping => ProductUiState.Listening,
        ActivationState.Transcribing or ActivationState.Understanding => ProductUiState.Understanding,
        _ => ProductUiState.Idle
    };
}
