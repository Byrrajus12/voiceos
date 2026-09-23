namespace VoiceOS.Core.Dictation;

/// <summary>Bounded UTF-16 fallback with exact focus validation before every batch.</summary>
public sealed class UnicodeKeyboardTextInsertionBackend : ITextInsertionBackend
{
    private const int MaxBatchCodeUnits = 128;
    private readonly IKeyboardInputService _keyboard;
    private readonly IForegroundWindowService _foreground;

    public UnicodeKeyboardTextInsertionBackend(IKeyboardInputService keyboard, IForegroundWindowService foreground)
    {
        _keyboard = keyboard;
        _foreground = foreground;
    }

    public string Name => "unicode-sendinput";

    public ValueTask<TextInsertionResult> InsertAsync(
        TextInsertionRequest request,
        CancellationToken cancellationToken = default)
    {
        var submitted = 0;
        while (submitted < request.Text.Length)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result(submitted == 0
                    ? new(TextInsertionStatus.Cancelled, Name, "Insertion was cancelled before dispatch.")
                    : Partial("Insertion was cancelled after a partial dispatch.", submitted));
            if (!_foreground.IsCurrent(request.Target))
                return Result(submitted == 0
                    ? new(TextInsertionStatus.FocusChanged, Name, "The foreground window no longer matches the captured target.")
                    : Partial("Focus changed after a partial dispatch.", submitted));
            if (_keyboard.HasActiveModifiers())
                return Result(submitted == 0
                    ? new(TextInsertionStatus.ModifierKeysActive, Name, "A physical modifier key is pressed; no text was dispatched.")
                    : Partial("A modifier key became active after a partial dispatch.", submitted));

            var length = Math.Min(MaxBatchCodeUnits, request.Text.Length - submitted);
            if (length < request.Text.Length - submitted && char.IsHighSurrogate(request.Text[submitted + length - 1]))
                length--;
            if (length < request.Text.Length - submitted
                && request.Text[submitted + length - 1] == '\r'
                && request.Text[submitted + length] == '\n')
                length--;

            var batch = request.Text.Substring(submitted, length);
            var dispatch = _keyboard.SendUnicodeText(batch);
            if (!dispatch.Succeeded)
            {
                var mayHaveInserted = submitted > 0 || dispatch.SubmittedAny;
                return Result(new(
                    mayHaveInserted ? TextInsertionStatus.PartialInsertion : TextInsertionStatus.InputRejected,
                    Name,
                    $"SendInput submitted {dispatch.SubmittedEvents} of {dispatch.RequestedEvents} events (error {dispatch.NativeError}).",
                    submitted,
                    CanTryFallback: !mayHaveInserted));
            }
            submitted += length;
        }

        return Result(new(TextInsertionStatus.Succeeded, Name, SubmittedCodeUnits: submitted));
    }

    private static ValueTask<TextInsertionResult> Result(TextInsertionResult result) => ValueTask.FromResult(result);
    private TextInsertionResult Partial(string detail, int submitted)
        => new(TextInsertionStatus.PartialInsertion, Name, detail, submitted);
}
