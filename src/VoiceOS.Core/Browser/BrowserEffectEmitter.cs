using System.Security.Cryptography;
using System.Text;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Pure translation of what the browser layer knows (pre/post snapshots, the companion's navigation
/// signal, the companion's error code) into typed effects. Emits only facts; it never judges a goal.
/// </summary>
internal static class BrowserEffectEmitter
{
    /// <summary>Cross-observation identity: element refs are renumbered per snapshot, this is not.</summary>
    internal static string Fingerprint(BrowserElement element)
        => Fingerprint(element.Role, element.Name, element.Context, element.Href);

    internal static string Fingerprint(string? role, string? name, string? context, string? href)
        => Hash($"{role}\u001f{name}\u001f{context}\u001f{href}");

    internal static TypedRef ElementRef(BrowserSnapshot snapshot, BrowserElement element, long revision)
        => new(snapshot.TabId, snapshot.SessionId, revision, element.Ref, Fingerprint(element),
            element.Name, element.Role, element.Href, element.Value);

    internal static TypedRef PageRef(BrowserSnapshot snapshot, long revision)
        => new(snapshot.TabId, snapshot.SessionId, revision, null, Hash(snapshot.Url),
            snapshot.Title, "page", snapshot.Url);

    /// <summary>A task tab was opened (mode=opened), continued into a child tab (mode=adopted), or returned to its opener (mode=returned_to_opener).</summary>
    internal static Effect SurfaceAcquired(BrowserSnapshot snapshot, long revision, string mode, int? fromTabId = null)
    {
        var data = new Dictionary<string, string>
        {
            ["mode"] = mode, ["url"] = snapshot.Url, ["toOrigin"] = BrowserSurface.LogOrigin(snapshot.Url)
        };
        if (fromTabId is int from) data["fromTabId"] = from.ToString();
        return new(EffectKind.SurfaceAcquired, EffectSource.CompanionResponse, EffectStrength.Observed,
            PageRef(snapshot, revision), data);
    }

    /// <summary>
    /// Effects of one successfully executed action in the same tab. <paramref name="revision"/> is the
    /// observation revision the action was bound against. Adoption into a child tab is reported by the
    /// next observation as <see cref="SurfaceAcquired"/>, so no navigation/content effects are emitted for it.
    /// </summary>
    internal static IReadOnlyList<Effect> ForSuccess(
        InteractionAction action, string protocolAction, long revision,
        BrowserSnapshot before, BrowserSnapshot after, bool stateChanged, bool adopted)
    {
        var effects = new List<Effect>();
        var signal = after.Signals is { } signals && BrowserActionSignals.IsMeaningfulFor(protocolAction)
            ? signals.Navigation : null;
        var target = action.TargetId is null ? null : before.Elements.FirstOrDefault(e => e.Ref == action.TargetId);
        var subject = target is null ? null : ElementRef(before, target, revision);

        switch (action.Kind)
        {
            case InteractionActionKind.Activate:
            case InteractionActionKind.PressKey:
                effects.Add(new(EffectKind.Activated, EffectSource.CompanionResponse, EffectStrength.Observed,
                    subject, WithSignal(new(), signal)));
                if (adopted) break;
                if (!StringComparer.Ordinal.Equals(before.Url, after.Url))
                {
                    var data = WithSignal(new() { ["from"] = before.Url, ["to"] = after.Url,
                        ["toOrigin"] = BrowserSurface.LogOrigin(after.Url) }, signal);
                    effects.Add(new(EffectKind.Navigated, EffectSource.SnapshotDelta, EffectStrength.Derived, subject, data));
                }
                else if (stateChanged)
                {
                    var present = subject is not null && after.Elements.Any(e => Fingerprint(e) == subject.Fingerprint);
                    effects.Add(new(EffectKind.ContentChanged, EffectSource.SnapshotDelta, EffectStrength.Derived, subject,
                        WithSignal(new() { ["subjectPresent"] = present ? "true" : "false" }, signal)));
                }
                break;

            case InteractionActionKind.SetText or InteractionActionKind.TypeText:
                effects.Add(TextSet(action, subject, after, target is null ? null : BrowserDomFacts.SearchScopeOf(target)));
                break;

            case InteractionActionKind.GoBack:
                // Returning to the opener tab is reported as SurfaceAcquired(mode=returned_to_opener)
                // by the next observation; only a same-tab traversal is HistoryMoved.
                if (!adopted)
                    effects.Add(new(EffectKind.HistoryMoved, EffectSource.CompanionResponse, EffectStrength.Observed, null,
                        WithSignal(new() { ["direction"] = "back", ["from"] = before.Url, ["to"] = after.Url }, signal)));
                break;
        }
        return effects;
    }

    /// <summary>The companion confirmed that nothing happened (NO_HISTORY, NAVIGATION_NOT_OBSERVED).
    /// NO_HISTORY is the "no previous page" outcome: no same-tab history and no opener to return to.</summary>
    internal static IReadOnlyList<Effect> ForNoEffectFailure(string code)
        => [new(EffectKind.NoEffect, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["reason"] = code })];

    private static Effect TextSet(InteractionAction action, TypedRef? subject, BrowserSnapshot after, string? scope = null)
    {
        var replace = action.Kind == InteractionActionKind.SetText;
        var data = new Dictionary<string, string>
        {
            ["mode"] = replace ? "replace" : "insert",
            ["value"] = action.Text ?? ""
        };
        if (scope is not null) data["scope"] = scope;
        // Readback only when the field re-identifies unambiguously in the post-action snapshot.
        if (subject is not null)
        {
            var matches = after.Elements.Where(e => Fingerprint(e) == subject.Fingerprint).Take(2).ToArray();
            if (matches.Length == 1)
            {
                var readback = matches[0].Value ?? "";
                data["readback"] = readback;
                data["matched"] = (replace ? StringComparer.Ordinal.Equals(readback, action.Text)
                    : readback.Contains(action.Text ?? "", StringComparison.Ordinal)) ? "true" : "false";
            }
        }
        return new(EffectKind.TextSet, EffectSource.CompanionResponse, EffectStrength.Observed, subject, data);
    }

    private static Dictionary<string, string> WithSignal(Dictionary<string, string> data, string? signal)
    {
        if (signal is not null) data["signal"] = signal;
        return data;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
