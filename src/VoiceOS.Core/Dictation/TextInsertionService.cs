namespace VoiceOS.Core.Dictation;

/// <summary>
/// Tries ordered insertion backends, stopping after an attempt that may have
/// dispatched input or left externally visible state behind.
/// </summary>
public sealed class TextInsertionService : ITextInsertionService
{
    private readonly IReadOnlyList<ITextInsertionBackend> _backends;

    public TextInsertionService(IEnumerable<ITextInsertionBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.ToArray();
    }

    public async ValueTask<TextInsertionResult> InsertAsync(
        TextInsertionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return new(TextInsertionStatus.Cancelled, Detail: "Insertion was cancelled before dispatch.");
        if (string.IsNullOrEmpty(request.Text))
            return new(TextInsertionStatus.NoText, Detail: "The insertion request contains no text.");
        if (!request.Target.IsValid)
            return new(TextInsertionStatus.InvalidTarget, Detail: "A valid foreground HWND and process ID are required.");
        if (_backends.Count == 0)
            return new(TextInsertionStatus.BackendUnavailable, Detail: "No text insertion backend is configured.");

        TextInsertionResult? last = null;
        foreach (var backend in _backends)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(TextInsertionStatus.Cancelled, Detail: "Insertion was cancelled before dispatch.");

            try
            {
                var result = await backend.InsertAsync(request, cancellationToken).ConfigureAwait(false);
                last = result with { Backend = result.Backend ?? backend.Name };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(TextInsertionStatus.Cancelled, backend.Name, "Insertion was cancelled before completion.");
            }
            catch (Exception ex)
            {
                return new(TextInsertionStatus.Failed, backend.Name, ex.Message);
            }

            if (last.Succeeded || !last.CanTryFallback)
                return last;
        }

        return last ?? new(TextInsertionStatus.BackendUnavailable);
    }
}
