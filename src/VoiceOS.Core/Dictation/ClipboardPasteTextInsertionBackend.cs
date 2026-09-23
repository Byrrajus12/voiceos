namespace VoiceOS.Core.Dictation;

public sealed class ClipboardPasteTextInsertionBackend : ITextInsertionBackend
{
    private readonly IClipboardService _clipboard;
    private readonly IKeyboardInputService _keyboard;
    private readonly IForegroundWindowService _foreground;
    private readonly TimeSpan _restoreDelay;

    public ClipboardPasteTextInsertionBackend(
        IClipboardService clipboard,
        IKeyboardInputService keyboard,
        IForegroundWindowService foreground,
        TimeSpan? restoreDelay = null)
    {
        _clipboard = clipboard;
        _keyboard = keyboard;
        _foreground = foreground;
        _restoreDelay = restoreDelay ?? TimeSpan.FromMilliseconds(150);
    }

    public string Name => "clipboard-paste";

    public async ValueTask<TextInsertionResult> InsertAsync(
        TextInsertionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Cancelled();
        if (!_foreground.IsCurrent(request.Target))
            return FocusChanged("The foreground window no longer matches the captured insertion target.");

        var acquired = await _clipboard.TryAcquireTextLeaseAsync(request.Text, cancellationToken).ConfigureAwait(false);
        if (acquired.Status != ClipboardLeaseStatus.Acquired || acquired.Lease is null)
            return new(TextInsertionStatus.ClipboardUnavailable, Name,
                acquired.Detail ?? $"Clipboard lease failed: {acquired.Status}.",
                CanTryFallback: acquired.CanTryFallback);

        await using var lease = acquired.Lease;
        if (cancellationToken.IsCancellationRequested)
        {
            var restore = await lease.RestoreIfUnchangedAsync().ConfigureAwait(false);
            return restore == ClipboardRestoreStatus.Restored
                ? Cancelled()
                : new(TextInsertionStatus.Failed, Name, "Cancellation occurred, but clipboard restoration was not confirmed.");
        }

        if (!_foreground.IsCurrent(request.Target))
        {
            var restore = await lease.RestoreIfUnchangedAsync().ConfigureAwait(false);
            return restore == ClipboardRestoreStatus.Restored
                ? FocusChanged("Focus changed after the clipboard write; no paste was dispatched.")
                : new(TextInsertionStatus.Failed, Name, "Focus changed and clipboard restoration was not confirmed.");
        }

        if (_keyboard.HasActiveModifiers())
        {
            var restore = await lease.RestoreIfUnchangedAsync().ConfigureAwait(false);
            return restore == ClipboardRestoreStatus.Restored
                ? new(TextInsertionStatus.ModifierKeysActive, Name,
                    "A physical modifier key is pressed; no paste was dispatched.")
                : new(TextInsertionStatus.Failed, Name,
                    "A modifier key is pressed and clipboard restoration was not confirmed.");
        }

        var dispatch = _keyboard.SendPasteShortcut();
        if (!dispatch.Succeeded)
        {
            var restore = await lease.RestoreIfUnchangedAsync().ConfigureAwait(false);
            var safeToFallback = !dispatch.SubmittedAny && restore == ClipboardRestoreStatus.Restored;
            return new(
                dispatch.SubmittedAny ? TextInsertionStatus.PartialInsertion : TextInsertionStatus.InputRejected,
                Name,
                $"SendInput submitted {dispatch.SubmittedEvents} of {dispatch.RequestedEvents} paste events (error {dispatch.NativeError}).",
                CanTryFallback: safeToFallback);
        }

        if (_restoreDelay > TimeSpan.Zero)
            await Task.Delay(_restoreDelay, CancellationToken.None).ConfigureAwait(false);

        var restored = await lease.RestoreIfUnchangedAsync().ConfigureAwait(false);
        return restored switch
        {
            ClipboardRestoreStatus.Restored => new(TextInsertionStatus.Succeeded, Name,
                SubmittedCodeUnits: request.Text.Length),
            ClipboardRestoreStatus.ClipboardChanged => new(TextInsertionStatus.SucceededClipboardChanged, Name,
                "A newer clipboard update was preserved.", request.Text.Length),
            _ => new(TextInsertionStatus.SucceededClipboardRestoreFailed, Name,
                "Paste was dispatched, but prior clipboard restoration failed.", request.Text.Length)
        };
    }

    private TextInsertionResult Cancelled()
        => new(TextInsertionStatus.Cancelled, Name, "Insertion was cancelled before dispatch.");
    private TextInsertionResult FocusChanged(string detail)
        => new(TextInsertionStatus.FocusChanged, Name, detail);
}
