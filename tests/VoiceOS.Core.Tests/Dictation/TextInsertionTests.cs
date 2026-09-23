using VoiceOS.Core.Dictation;
using Xunit;

namespace VoiceOS.Core.Tests.Dictation;

public sealed class TextInsertionTests
{
    private static readonly TextInsertionTarget Target = new((nint)123, 456);

    [Fact]
    public async Task Service_FallsBackOnlyAfterProvenSafeFailure()
    {
        var first = new StubBackend("clipboard", new(TextInsertionStatus.ClipboardUnavailable, CanTryFallback: true));
        var second = new StubBackend("unicode", new(TextInsertionStatus.Succeeded));
        var result = await new TextInsertionService([first, second]).InsertAsync(new("hello", Target));

        Assert.True(result.Succeeded);
        Assert.Equal(1, second.CallCount);
    }

    [Theory]
    [InlineData(TextInsertionStatus.PartialInsertion)]
    [InlineData(TextInsertionStatus.Failed)]
    [InlineData(TextInsertionStatus.InputRejected)]
    public async Task Service_DoesNotFallbackWithoutExplicitPermission(TextInsertionStatus status)
    {
        var second = new StubBackend("unicode", new(TextInsertionStatus.Succeeded));
        var result = await new TextInsertionService([
            new StubBackend("clipboard", new(status)), second
        ]).InsertAsync(new("hello", Target));

        Assert.Equal(status, result.Status);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task Service_RejectsEmptyTextAndInvalidTargetBeforeBackend()
    {
        var backend = new StubBackend("unused", new(TextInsertionStatus.Succeeded));
        var service = new TextInsertionService([backend]);

        Assert.Equal(TextInsertionStatus.NoText, (await service.InsertAsync(new("", Target))).Status);
        Assert.Equal(TextInsertionStatus.InvalidTarget, (await service.InsertAsync(new("hello", default))).Status);
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public async Task ClipboardBackend_FocusChangeAfterWriteRestoresWithoutDispatch()
    {
        var lease = new StubLease(ClipboardRestoreStatus.Restored);
        var keyboard = new StubKeyboard();
        var backend = new ClipboardPasteTextInsertionBackend(
            new StubClipboard(lease), keyboard, new SequenceForeground(true, false), TimeSpan.Zero);

        var result = await backend.InsertAsync(new("hello", Target));

        Assert.Equal(TextInsertionStatus.FocusChanged, result.Status);
        Assert.Equal(1, lease.RestoreCount);
        Assert.Equal(0, keyboard.PasteCount);
    }

    [Fact]
    public async Task ClipboardBackend_RefusesPhysicalModifiersAndRestores()
    {
        var lease = new StubLease(ClipboardRestoreStatus.Restored);
        var keyboard = new StubKeyboard { ModifiersActive = true };
        var backend = new ClipboardPasteTextInsertionBackend(
            new StubClipboard(lease), keyboard, new SequenceForeground(true, true), TimeSpan.Zero);

        var result = await backend.InsertAsync(new("hello", Target));

        Assert.Equal(TextInsertionStatus.ModifierKeysActive, result.Status);
        Assert.Equal(1, lease.RestoreCount);
        Assert.Equal(0, keyboard.PasteCount);
    }

    [Theory]
    [InlineData(ClipboardRestoreStatus.Restored, TextInsertionStatus.Succeeded)]
    [InlineData(ClipboardRestoreStatus.ClipboardChanged, TextInsertionStatus.SucceededClipboardChanged)]
    [InlineData(ClipboardRestoreStatus.Failed, TextInsertionStatus.SucceededClipboardRestoreFailed)]
    public async Task ClipboardBackend_ReportsRestoreOutcome(
        ClipboardRestoreStatus restore,
        TextInsertionStatus expected)
    {
        var backend = new ClipboardPasteTextInsertionBackend(
            new StubClipboard(new StubLease(restore)), new StubKeyboard(),
            new SequenceForeground(true, true), TimeSpan.Zero);

        var result = await backend.InsertAsync(new("héllo\r\n世界😀", Target));

        Assert.Equal(expected, result.Status);
        Assert.Equal(11, result.SubmittedCodeUnits);
    }

    [Fact]
    public async Task UnicodeBackend_PreservesEmojiAndCrLfAcrossBatchBoundaries()
    {
        var keyboard = new StubKeyboard();
        var backend = new UnicodeKeyboardTextInsertionBackend(
            keyboard, new SequenceForeground(true, true, true));
        var text = new string('a', 127) + "😀" + new string('b', 125) + "\r\nend";

        var result = await backend.InsertAsync(new(text, Target));

        Assert.True(result.Succeeded);
        Assert.Equal(text, string.Concat(keyboard.UnicodeBatches));
        Assert.DoesNotContain(keyboard.UnicodeBatches, b => char.IsHighSurrogate(b[^1]) || b[^1] == '\r');
    }

    [Fact]
    public async Task UnicodeBackend_FailsPartialWhenFocusChangesBetweenBatches()
    {
        var keyboard = new StubKeyboard();
        var backend = new UnicodeKeyboardTextInsertionBackend(
            keyboard, new SequenceForeground(true, false));

        var result = await backend.InsertAsync(new(new string('x', 200), Target));

        Assert.Equal(TextInsertionStatus.PartialInsertion, result.Status);
        Assert.Equal(128, result.SubmittedCodeUnits);
        Assert.False(result.CanTryFallback);
    }

    private sealed class StubBackend(string name, TextInsertionResult result) : ITextInsertionBackend
    {
        public string Name => name;
        public int CallCount { get; private set; }
        public ValueTask<TextInsertionResult> InsertAsync(TextInsertionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SequenceForeground(params bool[] values) : IForegroundWindowService
    {
        private readonly Queue<bool> _values = new(values);
        public TextInsertionTarget Capture() => Target;
        public bool IsCurrent(TextInsertionTarget target) => _values.Count == 0 || _values.Dequeue();
    }

    private sealed class StubKeyboard : IKeyboardInputService
    {
        public bool ModifiersActive { get; init; }
        public KeyboardDispatchResult PasteResult { get; init; } = new(4, 4, 0);
        public int PasteCount { get; private set; }
        public List<string> UnicodeBatches { get; } = [];
        public bool HasActiveModifiers() => ModifiersActive;
        public KeyboardDispatchResult SendPasteShortcut() { PasteCount++; return PasteResult; }
        public KeyboardDispatchResult SendUnicodeText(string text)
        {
            UnicodeBatches.Add(text);
            return new(text.Length * 2, text.Length * 2, 0);
        }
    }

    private sealed class StubClipboard(IClipboardTextLease lease) : IClipboardService
    {
        public ValueTask<ClipboardLeaseResult> TryAcquireTextLeaseAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ClipboardLeaseResult(ClipboardLeaseStatus.Acquired, lease));
    }

    private sealed class StubLease(ClipboardRestoreStatus status) : IClipboardTextLease
    {
        public int RestoreCount { get; private set; }
        public ValueTask<ClipboardRestoreStatus> RestoreIfUnchangedAsync(CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            return ValueTask.FromResult(status);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
