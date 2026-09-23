using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Dictation;

namespace VoiceOS;

/// <summary>
/// Owns clipboard work on one stable STA. The prior contents are fully
/// materialized before the temporary payload is installed. Sequence checks
/// prevent restoration from overwriting a newer clipboard update.
/// </summary>
internal sealed class WindowsClipboardService : IClipboardService, IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private readonly Thread _thread;
    private readonly ILogger<WindowsClipboardService> _logger;
    private readonly IWindowsClipboard _clipboard;
    private bool _disposed;

    public WindowsClipboardService(ILogger<WindowsClipboardService> logger)
        : this(logger, new SystemWindowsClipboard())
    {
    }

    internal WindowsClipboardService(ILogger<WindowsClipboardService> logger, IWindowsClipboard clipboard)
    {
        _logger = logger;
        _clipboard = clipboard;
        _thread = new Thread(Run) { IsBackground = true, Name = "VoiceOS-Clipboard-STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async ValueTask<ClipboardLeaseResult> TryAcquireTextLeaseAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            if (!await _leaseGate.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false))
                return new(ClipboardLeaseStatus.Busy, Detail: "Another clipboard lease is active.", CanTryFallback: true);
        }
        catch (OperationCanceledException)
        {
            return new(ClipboardLeaseStatus.Busy, Detail: "Clipboard acquisition was cancelled.", CanTryFallback: true);
        }

        try
        {
            var outcome = await InvokeAsync(() =>
            {
                ClipboardMaterializationResult previous;
                try
                {
                    previous = ClipboardSnapshotMaterializer.Capture(_clipboard.GetDataObject());
                }
                catch (Exception ex)
                {
                    return ClipboardAcquireOutcome.SafeFailure(ex.Message);
                }

                if (previous.HadContents && previous.Formats.Count == 0)
                    return ClipboardAcquireOutcome.SafeFailure(
                        "The existing clipboard had no safely materializable formats.");

                foreach (var format in previous.SkippedFormats)
                    _logger.LogDebug("Skipping clipboard format {Format} during snapshot", format);

                try
                {
                    _clipboard.SetDataObject(text);
                    var snapshot = new MaterializedClipboardSnapshot(
                        previous.Formats, previous.HadContents, _clipboard.GetSequenceNumber());
                    return ClipboardAcquireOutcome.Success(snapshot);
                }
                catch (Exception ex)
                {
                    // SetDataObject may have changed ownership before reporting failure.
                    return ClipboardAcquireOutcome.UnsafeFailure(ex.Message);
                }
            }).ConfigureAwait(false);

            if (outcome.Snapshot is null)
            {
                _leaseGate.Release();
                return new(outcome.SafeToFallback ? ClipboardLeaseStatus.Busy : ClipboardLeaseStatus.Failed,
                    Detail: outcome.Detail,
                    CanTryFallback: outcome.SafeToFallback);
            }

            return new(ClipboardLeaseStatus.Acquired,
                new Lease(this, outcome.Snapshot, _leaseGate, _logger));
        }
        catch (ExternalException ex)
        {
            _leaseGate.Release();
            return new(ClipboardLeaseStatus.Busy, Detail: ex.Message, CanTryFallback: true);
        }
        catch (Exception ex)
        {
            _leaseGate.Release();
            _logger.LogWarning(ex, "Clipboard acquisition failed");
            return new(ClipboardLeaseStatus.Failed, Detail: ex.Message);
        }
    }

    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }

    private void Run()
    {
        foreach (var action in _work.GetConsumingEnumerable())
            action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _work.Dispose();
        _leaseGate.Dispose();
    }

    private sealed record ClipboardAcquireOutcome(
        MaterializedClipboardSnapshot? Snapshot,
        string? Detail,
        bool SafeToFallback)
    {
        public static ClipboardAcquireOutcome Success(MaterializedClipboardSnapshot snapshot) => new(snapshot, null, false);
        public static ClipboardAcquireOutcome SafeFailure(string detail) => new(null, detail, true);
        public static ClipboardAcquireOutcome UnsafeFailure(string detail) => new(null, detail, false);
    }

    private sealed class Lease : IClipboardTextLease
    {
        private readonly WindowsClipboardService _owner;
        private readonly MaterializedClipboardSnapshot _snapshot;
        private readonly SemaphoreSlim _gate;
        private readonly ILogger _logger;
        private int _finished;

        public Lease(WindowsClipboardService owner, MaterializedClipboardSnapshot snapshot, SemaphoreSlim gate, ILogger logger)
        {
            _owner = owner;
            _snapshot = snapshot;
            _gate = gate;
            _logger = logger;
        }

        public async ValueTask<ClipboardRestoreStatus> RestoreIfUnchangedAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
                return ClipboardRestoreStatus.Failed;

            try
            {
                return await _owner.InvokeAsync(() =>
                {
                    if (_owner._clipboard.GetSequenceNumber() != _snapshot.TemporarySequence)
                        return ClipboardRestoreStatus.ClipboardChanged;

                    if (_snapshot.HadContents)
                        _owner._clipboard.SetDataObject(_snapshot.CreateDataObject());
                    else
                        _owner._clipboard.Clear();
                    return ClipboardRestoreStatus.Restored;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Clipboard restoration failed");
                return ClipboardRestoreStatus.Failed;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _finished) == 0)
                await RestoreIfUnchangedAsync().ConfigureAwait(false);
        }
    }
}

internal interface IWindowsClipboard
{
    IDataObject? GetDataObject();
    void SetDataObject(object data);
    void Clear();
    uint GetSequenceNumber();
}

internal sealed class SystemWindowsClipboard : IWindowsClipboard
{
    public IDataObject? GetDataObject() => Clipboard.GetDataObject();
    public void SetDataObject(object data) => Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 20);
    public void Clear() => Clipboard.Clear();
    public uint GetSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();
    }
}
