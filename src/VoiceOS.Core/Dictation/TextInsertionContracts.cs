namespace VoiceOS.Core.Dictation;

/// <summary>Exact foreground identity captured when dictation begins.</summary>
public readonly record struct TextInsertionTarget(nint WindowHandle, uint ProcessId)
{
    public bool IsValid => WindowHandle != 0 && ProcessId != 0;
}

public sealed record TextInsertionRequest(string Text, TextInsertionTarget Target);

public enum TextInsertionStatus
{
    Succeeded,
    SucceededClipboardChanged,
    SucceededClipboardRestoreFailed,
    NoText,
    Cancelled,
    InvalidTarget,
    FocusChanged,
    ClipboardUnavailable,
    ModifierKeysActive,
    InputRejected,
    PartialInsertion,
    BackendUnavailable,
    Failed
}

public sealed record TextInsertionResult(
    TextInsertionStatus Status,
    string? Backend = null,
    string? Detail = null,
    int SubmittedCodeUnits = 0,
    bool CanTryFallback = false)
{
    public bool Succeeded => Status is TextInsertionStatus.Succeeded
        or TextInsertionStatus.SucceededClipboardChanged
        or TextInsertionStatus.SucceededClipboardRestoreFailed;
}

public interface ITextInsertionService
{
    ValueTask<TextInsertionResult> InsertAsync(TextInsertionRequest request, CancellationToken cancellationToken = default);
}

public interface ITextInsertionBackend
{
    string Name { get; }
    ValueTask<TextInsertionResult> InsertAsync(TextInsertionRequest request, CancellationToken cancellationToken = default);
}

public interface IForegroundWindowService
{
    TextInsertionTarget Capture();
    bool IsCurrent(TextInsertionTarget target);
}

public readonly record struct KeyboardDispatchResult(int RequestedEvents, int SubmittedEvents, int NativeError)
{
    public bool Succeeded => RequestedEvents > 0 && SubmittedEvents == RequestedEvents;
    public bool SubmittedAny => SubmittedEvents > 0;
}

/// <summary>The only global keyboard primitive used by foreground text insertion.</summary>
public interface IKeyboardInputService
{
    bool HasActiveModifiers();
    KeyboardDispatchResult SendPasteShortcut();
    KeyboardDispatchResult SendUnicodeText(string text);
}

public enum ClipboardLeaseStatus { Acquired, Busy, Failed }

public sealed record ClipboardLeaseResult(
    ClipboardLeaseStatus Status,
    IClipboardTextLease? Lease = null,
    string? Detail = null,
    bool CanTryFallback = false);

public enum ClipboardRestoreStatus { Restored, ClipboardChanged, Failed }

public interface IClipboardTextLease : IAsyncDisposable
{
    ValueTask<ClipboardRestoreStatus> RestoreIfUnchangedAsync(CancellationToken cancellationToken = default);
}

public interface IClipboardService
{
    ValueTask<ClipboardLeaseResult> TryAcquireTextLeaseAsync(string text, CancellationToken cancellationToken = default);
}
