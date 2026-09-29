using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Interaction;

/// <summary>Concrete things VoiceOS has actually interacted with. Deliberately small.</summary>
public enum ReferentKind
{
    /// <summary>A native window identified by HWND + process.</summary>
    AppWindow,
    /// <summary>A browser tab identified by tab id + session. Identity only; its content is a <see cref="Page"/>.</summary>
    BrowserTab,
    /// <summary>What a tab was showing when VoiceOS last observed it.</summary>
    Page,
    /// <summary>A concrete clickable target whose activation was confirmed by its landing.</summary>
    Item
}

/// <summary>How a referent was established. Never an inference by a model.</summary>
public enum ReferentProvenance
{
    ObservedActivePage,
    ActivatedTarget,
    NavigationResult,
    SurfaceAcquired,
    DirectStepResult,
    UserSelected,
    DerivedFromEffect
}

/// <summary>
/// One observed entity. Browser referents carry tab/session identity, native ones HWND/process identity.
/// <see cref="Key"/> is the dedup identity: observing the same key again refreshes the entry.
/// </summary>
public sealed record Referent(
    ReferentKind Kind,
    string Label,
    ReferentProvenance Provenance,
    DateTimeOffset LastSeen,
    int? TabId = null,
    string? SessionId = null,
    string? Url = null,
    string? Href = null,
    string? Fingerprint = null,
    string? SourceUrl = null,
    nint Hwnd = 0,
    string? ProcessName = null,
    bool OwnedByVoiceOs = false)
{
    /// <summary>Monotonic recency stamp assigned by the store; larger is more recent.</summary>
    public long Seq { get; init; }

    public string Key => Kind switch
    {
        ReferentKind.AppWindow => $"win:{Hwnd}",
        ReferentKind.BrowserTab => $"tab:{TabId}:{SessionId}",
        ReferentKind.Page => $"page:{TabId}:{SessionId}",
        _ => $"item:{Href ?? Fingerprint ?? Label}"
    };

    public bool IsBrowser => Kind != ReferentKind.AppWindow;
}

/// <summary>Live inventory a referent is checked against before it can be reused.</summary>
public sealed record ReferentInventory(bool BrowserKnown, IReadOnlyList<BrowserTabInfo> Tabs,
    IReadOnlyList<WindowCandidate> Windows);

/// <summary>
/// Bounded, in-memory, code-owned record of recently established entities. Entries come only from
/// observed product state (effects, direct step results); nothing here is model-written, and nothing
/// is persisted. Every entry is validated against the live inventory before reuse.
/// </summary>
public sealed class ReferentStore(int maxEntries = 32, TimeSpan? ttl = null)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, Referent> _entries = [];
    private long _seq;

    public TimeSpan Ttl { get; } = ttl ?? DefaultTtl;
    public int Count { get { lock (_gate) return _entries.Count; } }

    public void Observe(Referent referent)
    {
        lock (_gate)
        {
            var stamped = referent with { Seq = ++_seq };
            // A page's identity is its tab; when a tab navigates, the page it showed before is replaced.
            _entries[stamped.Key] = stamped;
            while (_entries.Count > maxEntries)
            {
                var oldest = _entries.Values.MinBy(static r => r.Seq)!;
                _entries.Remove(oldest.Key);
            }
        }
    }

    public void ObserveMany(IEnumerable<Referent>? referents)
    {
        if (referents is null) return;
        foreach (var referent in referents) Observe(referent);
    }

    /// <summary>Removes the window referent for a handle (e.g. VoiceOS just closed it).</summary>
    public void InvalidateWindow(nint hwnd)
    {
        lock (_gate) _entries.Remove($"win:{hwnd}");
    }

    /// <summary>Most recent first, without validating. Test/diagnostic use.</summary>
    public IReadOnlyList<Referent> All()
    {
        lock (_gate) return _entries.Values.OrderByDescending(static r => r.Seq).ToArray();
    }

    /// <summary>
    /// Drops entries that are expired or provably stale and returns the survivors, most recent first.
    /// Browser entries whose browser inventory is unavailable are kept but not returned: unknown is not valid.
    /// </summary>
    public IReadOnlyList<Referent> Validate(ReferentInventory inventory, DateTimeOffset now)
    {
        lock (_gate)
        {
            var usable = new List<Referent>();
            foreach (var entry in _entries.Values.ToArray())
            {
                if (now - entry.LastSeen > Ttl) { _entries.Remove(entry.Key); continue; }
                switch (Check(entry, inventory))
                {
                    case Validity.Invalid: _entries.Remove(entry.Key); break;
                    case Validity.Unknown: break;
                    default:
                        var refreshed = Refresh(entry, inventory);
                        if (!ReferenceEquals(refreshed, entry)) _entries[entry.Key] = refreshed;
                        usable.Add(refreshed);
                        break;
                }
            }
            return usable.OrderByDescending(static r => r.Seq).ToArray();
        }
    }

    private enum Validity { Valid, Invalid, Unknown }

    private static Validity Check(Referent entry, ReferentInventory inventory)
    {
        switch (entry.Kind)
        {
            case ReferentKind.AppWindow:
                return inventory.Windows.Any(w => w.Hwnd == entry.Hwnd && entry.Hwnd != 0
                    && string.Equals(w.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase))
                    ? Validity.Valid : Validity.Invalid;
            case ReferentKind.Item:
                // An item is a concrete address, reusable even after the tab it came from is gone.
                return Uri.TryCreate(entry.Href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    ? Validity.Valid : Validity.Invalid;
            default:
                if (!inventory.BrowserKnown) return Validity.Unknown;
                var tab = TabFor(entry, inventory);
                if (tab is null) return Validity.Invalid;
                // A page is only valid while its tab still shows it.
                return entry.Kind == ReferentKind.Page
                    && !StringComparer.Ordinal.Equals(tab.Url, entry.Url) ? Validity.Invalid : Validity.Valid;
        }
    }

    /// <summary>The session must match for a tab VoiceOS owns; a user's tab carries no session.</summary>
    private static BrowserTabInfo? TabFor(Referent entry, ReferentInventory inventory)
        => inventory.Tabs.FirstOrDefault(t => t.TabId == entry.TabId
            && (t.SessionId is null ? !entry.OwnedByVoiceOs : t.SessionId == entry.SessionId));

    /// <summary>Inventory is the truth about ownership and the current title.</summary>
    private static Referent Refresh(Referent entry, ReferentInventory inventory)
    {
        if (entry.Kind is not (ReferentKind.Page or ReferentKind.BrowserTab)) return entry;
        var tab = TabFor(entry, inventory)!;
        var owned = tab.Provenance == BrowserTabProvenance.VoiceOs;
        var label = entry.Kind == ReferentKind.Page && !string.IsNullOrWhiteSpace(tab.Title) ? tab.Title! : entry.Label;
        return owned == entry.OwnedByVoiceOs && label == entry.Label ? entry
            : entry with { OwnedByVoiceOs = owned, Label = label };
    }
}
