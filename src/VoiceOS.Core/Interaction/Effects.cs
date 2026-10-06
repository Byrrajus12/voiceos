namespace VoiceOS.Core.Interaction;

/// <summary>
/// Structured facts about what an interaction did or observed. Observational only:
/// nothing in the engine branches on effects.
/// </summary>
public enum EffectKind
{
    /// <summary>A task surface (tab) was opened or adopted for this run.</summary>
    SurfaceAcquired,
    /// <summary>The surface reported that the exact element in <see cref="Effect.Subject"/> was activated.</summary>
    Activated,
    /// <summary>A field was set or appended to; data carries the value and, when re-identifiable, the readback.</summary>
    TextSet,
    /// <summary>The surface's URL changed as a result of an action.</summary>
    Navigated,
    /// <summary>A history traversal was confirmed by the surface.</summary>
    HistoryMoved,
    /// <summary>Observable state changed while the URL did not.</summary>
    ContentChanged,
    /// <summary>The action was accepted but produced no observable effect (data: reason).</summary>
    NoEffect
}

/// <summary>Who established the fact.</summary>
public enum EffectSource { CompanionResponse, SnapshotDelta, EngineRule }

/// <summary>Observed: reported directly by the surface. Derived: computed from before/after state or an engine rule.</summary>
public enum EffectStrength { Observed, Derived }

/// <summary>
/// A within-run reference to something a surface exposed. <see cref="ElementRef"/> is
/// snapshot-local and valid only for <see cref="ObservationRevision"/>; use
/// <see cref="Fingerprint"/> to re-identify across observations. A null ElementRef is a page-level ref.
/// </summary>
public sealed record TypedRef(
    int TabId,
    string SessionId,
    long ObservationRevision,
    string? ElementRef,
    string Fingerprint,
    string Label,
    string? Role = null,
    string? Href = null,
    string? Value = null);

/// <summary>
/// One recorded fact. Emitters leave <see cref="Id"/>, <see cref="StepId"/> and (when the emitter
/// does not know it) <see cref="ActionId"/> empty; <see cref="EffectLedger"/> stamps them.
/// </summary>
public sealed record Effect(
    EffectKind Kind,
    EffectSource Source,
    EffectStrength Strength,
    TypedRef? Subject = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public string Id { get; init; } = "";
    public string StepId { get; init; } = "";
    public string? ActionId { get; init; }

    public string? Get(string key) => Data is not null && Data.TryGetValue(key, out var value) ? value : null;

    /// <summary>Log-safe one-line form: never includes typed values, labels, or full URLs.</summary>
    public string Summarize()
    {
        var parts = new List<string> { $"kind={Kind}" };
        if (ActionId is not null) parts.Add($"action={ActionId}");
        if (Subject is { } subject)
            parts.Add($"subject={subject.ElementRef ?? "page"}@r{subject.ObservationRevision}({subject.Role ?? "?"}) tab={subject.TabId} session={subject.SessionId}");
        foreach (var key in SafeDataKeys)
            if (Get(key) is { } value) parts.Add($"{key}={value}");
        return string.Join(' ', parts);
    }

    private static readonly string[] SafeDataKeys =
        ["mode", "signal", "direction", "reason", "matched", "subjectPresent", "toOrigin", "fromTabId"];
}

/// <summary>Append-only record of the effects of one interaction run.</summary>
public sealed class EffectLedger(string stepId = "s1")
{
    private readonly List<Effect> _effects = [];

    public string StepId { get; } = stepId;
    public IReadOnlyList<Effect> All => _effects;

    /// <summary>Appends in order, stamping id and step, and the action id when the effect has none.</summary>
    public void Append(IEnumerable<Effect>? effects, string? actionId = null)
    {
        if (effects is null) return;
        foreach (var effect in effects)
            _effects.Add(effect with
            {
                Id = $"{StepId}.{_effects.Count + 1}",
                StepId = StepId,
                ActionId = effect.ActionId ?? actionId
            });
    }
}
