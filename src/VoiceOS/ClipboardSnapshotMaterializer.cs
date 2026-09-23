using System.Collections.Specialized;
using System.Drawing;

namespace VoiceOS;

/// <summary>
/// A detached clipboard snapshot. It never retains the source IDataObject, so
/// delayed OLE providers cannot disappear before restoration.
/// </summary>
internal sealed record MaterializedClipboardSnapshot(
    IReadOnlyList<MaterializedClipboardFormat> Formats,
    bool HadContents,
    uint TemporarySequence)
{
    public IDataObject CreateDataObject()
    {
        var dataObject = new DataObject();
        foreach (var item in Formats)
            dataObject.SetData(item.Format, autoConvert: false, CloneValue(item.Data));
        return dataObject;
    }

    private static object CloneValue(object value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        char[] chars => chars.ToArray(),
        string[] strings => strings.ToArray(),
        StringCollection strings => CloneStrings(strings),
        MemoryStream stream => CloneStream(stream),
        Image image => new Bitmap(image),
        _ => value
    };

    private static StringCollection CloneStrings(StringCollection source)
    {
        var copy = new StringCollection();
        copy.AddRange(source.Cast<string>().ToArray());
        return copy;
    }

    private static MemoryStream CloneStream(Stream source)
    {
        var originalPosition = source.CanSeek ? source.Position : (long?)null;
        if (source.CanSeek)
            source.Position = 0;
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        if (originalPosition.HasValue)
            source.Position = originalPosition.Value;
        return copy;
    }
}

internal sealed record MaterializedClipboardFormat(string Format, object Data);

internal sealed record ClipboardMaterializationResult(
    IReadOnlyList<MaterializedClipboardFormat> Formats,
    bool HadContents,
    IReadOnlyList<string> SkippedFormats);

internal static class ClipboardSnapshotMaterializer
{
    public static ClipboardMaterializationResult Capture(IDataObject? source)
    {
        if (source is null)
            return new([], false, []);

        string[] formats;
        try
        {
            formats = source.GetFormats(autoConvert: false);
        }
        catch
        {
            return new([], true, ["<format enumeration failed>"]);
        }

        var materialized = new List<MaterializedClipboardFormat>();
        var skipped = new List<string>();
        foreach (var format in formats.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var value = source.GetData(format, autoConvert: false);
                var detached = Detach(value);
                if (detached is null)
                    skipped.Add(format);
                else
                    materialized.Add(new(format, detached));
            }
            catch
            {
                // Delayed, unavailable, or unsupported formats are isolated.
                skipped.Add(format);
            }
        }

        return new(materialized, formats.Length > 0, skipped);
    }

    private static object? Detach(object? value) => value switch
    {
        null => null,
        string text => text,
        byte[] bytes => bytes.ToArray(),
        char[] chars => chars.ToArray(),
        string[] strings => strings.ToArray(),
        StringCollection strings => CloneStrings(strings),
        MemoryStream stream => CloneStream(stream),
        Stream stream when stream.CanRead => CloneStream(stream),
        Image image => new Bitmap(image),
        _ => null
    };

    private static StringCollection CloneStrings(StringCollection source)
    {
        var copy = new StringCollection();
        copy.AddRange(source.Cast<string>().ToArray());
        return copy;
    }

    private static MemoryStream CloneStream(Stream source)
    {
        var originalPosition = source.CanSeek ? source.Position : (long?)null;
        if (source.CanSeek)
            source.Position = 0;
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        if (originalPosition.HasValue)
            source.Position = originalPosition.Value;
        return copy;
    }
}
