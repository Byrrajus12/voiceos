using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using VoiceOS.Core.Dictation;
using Xunit;

namespace VoiceOS.Core.Tests.Dictation;

public sealed class ClipboardRestorationTests
{
    [Fact]
    public void Materializer_DetachesCommonFormats_AndSkipsDelayedFormatIndividually()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var source = new SelectiveDataObject(new Dictionary<string, object>
        {
            [DataFormats.UnicodeText] = "hello",
            ["bytes"] = bytes
        }, "delayed");

        var result = ClipboardSnapshotMaterializer.Capture(source);
        bytes[0] = 9;

        Assert.True(result.HadContents);
        Assert.Equal(2, result.Formats.Count);
        Assert.Contains("delayed", result.SkippedFormats);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(result.Formats.Single(x => x.Format == "bytes").Data));
    }

    [Fact]
    public async Task Restore_DoesNotOverwriteANewerClipboardUpdate()
    {
        var initial = new DataObject();
        initial.SetData(DataFormats.UnicodeText, false, "prior");
        var platform = new FakeWindowsClipboard(initial, 10);
        using var service = new WindowsClipboardService(
            NullLogger<WindowsClipboardService>.Instance, platform);

        var acquired = await service.TryAcquireTextLeaseAsync("dictated");
        Assert.Equal(ClipboardLeaseStatus.Acquired, acquired.Status);
        platform.Sequence = 12; // External clipboard owner updated it after our write.
        platform.Current = new DataObject(DataFormats.UnicodeText, "newer");

        var restored = await acquired.Lease!.RestoreIfUnchangedAsync();

        Assert.Equal(ClipboardRestoreStatus.ClipboardChanged, restored);
        Assert.Equal("newer", platform.Current.GetData(DataFormats.UnicodeText));
        Assert.Equal(1, platform.SetCount);
    }

    [Fact]
    public async Task Restore_UsesMaterializedData_NotTheOriginalLiveObject()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var initial = new DataObject();
        initial.SetData("bytes", false, bytes);
        var platform = new FakeWindowsClipboard(initial, 20);
        using var service = new WindowsClipboardService(
            NullLogger<WindowsClipboardService>.Instance, platform);

        var acquired = await service.TryAcquireTextLeaseAsync("dictated");
        bytes[0] = 9;
        var restored = await acquired.Lease!.RestoreIfUnchangedAsync();

        Assert.Equal(ClipboardRestoreStatus.Restored, restored);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(platform.Current.GetData("bytes", false)));
    }

    private sealed class FakeWindowsClipboard(IDataObject current, uint sequence) : IWindowsClipboard
    {
        public IDataObject Current { get; set; } = current;
        public uint Sequence { get; set; } = sequence;
        public int SetCount { get; private set; }

        public IDataObject? GetDataObject() => Current;
        public void SetDataObject(object data)
        {
            SetCount++;
            Current = data as IDataObject ?? new DataObject(DataFormats.UnicodeText, data);
            Sequence++;
        }
        public void Clear() { Current = new DataObject(); Sequence++; }
        public uint GetSequenceNumber() => Sequence;
    }

    private sealed class SelectiveDataObject(
        IReadOnlyDictionary<string, object> values,
        string throwingFormat) : IDataObject
    {
        public object? GetData(string format, bool autoConvert)
            => format == throwingFormat ? throw new ExternalException("delayed") : values.GetValueOrDefault(format);
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => null;
        public bool GetDataPresent(string format, bool autoConvert) => values.ContainsKey(format) || format == throwingFormat;
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => false;
        public string[] GetFormats(bool autoConvert) => [.. values.Keys, throwingFormat];
        public string[] GetFormats() => GetFormats(true);
        public void SetData(string format, bool autoConvert, object? data) => throw new NotSupportedException();
        public void SetData(string format, object? data) => throw new NotSupportedException();
        public void SetData(Type format, object? data) => throw new NotSupportedException();
        public void SetData(object? data) => throw new NotSupportedException();
    }
}
