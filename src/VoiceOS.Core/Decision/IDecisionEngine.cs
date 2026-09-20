namespace VoiceOS.Core.Decision;

public interface IDecisionEngine
{
    Task<DecisionResult> DecideAsync(DecisionState state, CancellationToken ct = default);
}
