using VoiceOS.Core.Activation;
using VoiceOS.UI;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class ActivationUiStateAdapterTests
{
    [Theory]
    [InlineData(ActivationState.Idle, ProductUiState.Idle)]
    [InlineData(ActivationState.Recording, ProductUiState.Listening)]
    [InlineData(ActivationState.Stopping, ProductUiState.Listening)]
    [InlineData(ActivationState.Transcribing, ProductUiState.Processing)]
    [InlineData(ActivationState.Understanding, ProductUiState.Processing)]
    public void MapsActivationWithoutExposingPipelineDetails(ActivationState activation, ProductUiState expected)
        => Assert.Equal(expected, ActivationUiStateAdapter.Map(activation));
}
