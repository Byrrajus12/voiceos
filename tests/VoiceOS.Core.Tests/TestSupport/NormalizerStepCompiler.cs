using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Tests;

/// <summary>Test-only single-shot goal source, presented as a compiler that yields a one-step plan.</summary>
public abstract class NormalizingCompiler : IBrowserStepCompiler
{
    public abstract ValueTask<BrowserGoalNormalization?> NormalizeAsync(string utterance, CancellationToken cancellationToken = default);

    public async ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken cancellationToken = default)
    {
        var normalized = await NormalizeAsync(utterance, cancellationToken).ConfigureAwait(false);
        return normalized is null ? null
            : new(new InteractionPlan(utterance, normalized.Objective,
                [new(PlanStepKind.Act, normalized.Objective)]), normalized);
    }
}
