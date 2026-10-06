using VoiceOS.Core.Candidates;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Native referents come only from the exact window a direct step resolved and acted on (its
/// <see cref="VoiceStepResult.ResultWindow"/>), never from a title lookup afterwards.
/// </summary>
public static class NativeReferents
{
    public sealed record FromProgramResult(IReadOnlyList<Referent> Observed, IReadOnlyList<nint> Closed);

    public static FromProgramResult FromProgram(VoiceProgram? program, ProgramResult result, DateTimeOffset now)
    {
        var observed = new List<Referent>();
        var closed = new List<nint>();
        foreach (var step in result.StepResults)
        {
            if (!step.Succeeded || step.ResultWindow is not { Hwnd: not 0 } window) continue;
            // A window VoiceOS just closed is gone: it must not remain referable.
            if (program?.Steps.FirstOrDefault(s => s.StepId == step.StepId) is CloseWindowStep)
            { closed.Add(window.Hwnd); continue; }
            observed.Add(new(ReferentKind.AppWindow, window.Title, ReferentProvenance.DirectStepResult, now,
                Hwnd: window.Hwnd, ProcessName: window.ProcessName));
        }
        return new(observed, closed);
    }

    /// <summary>
    /// Candidate ids of validated windows an implicit "it" may denote, most recent first. Only windows
    /// established after the latest browser referent qualify: the most recently used surface owns an
    /// implicit reference, so an older native window never captures it once VoiceOS has moved on.
    /// </summary>
    public static IReadOnlyList<string> ImplicitWindowIds(IReadOnlyList<Referent> valid, IReadOnlyList<WindowCandidate> windows)
    {
        var newestBrowser = valid.Where(r => r.IsBrowser).Select(r => r.Seq).DefaultIfEmpty(0).Max();
        return valid.Where(r => r.Kind == ReferentKind.AppWindow && r.Seq > newestBrowser)
            .OrderByDescending(r => r.Seq)
            .Select(r => windows.FirstOrDefault(w => w.Hwnd == r.Hwnd)?.Id).OfType<string>().ToArray();
    }
}
