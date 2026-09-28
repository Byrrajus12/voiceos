using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VoiceOS.Core.Apps;

public enum ShortcutKind
{
    /// <summary>Not launchable as an app: a folder, document, missing target, or stale proxy.</summary>
    Rejected,
    Executable,
    /// <summary>A console-subsystem executable; its window is owned by the terminal host.</summary>
    ConsoleExecutable,
    /// <summary>A browser proxy shortcut carrying a valid installed web-app identity.</summary>
    WebApp,
    /// <summary>A shell-namespace item (no file-system target; its ID list parses to a shell
    /// folder CLSID such as File Explorer's), hosted by the shell.</summary>
    ShellNamespace,
    /// <summary>No executable target, but an AUMID the shell can launch.</summary>
    Aumid
}

/// <summary>Resolved facts about one Start Menu shortcut; gathered by the catalog, classified purely.</summary>
/// <remarks>For an MSI advertised shortcut, TargetPath is the executable the installer resolves,
/// not the raw icon path the link stores.</remarks>
public sealed record ShortcutFacts(
    string? TargetPath, string? Arguments, string? Aumid, string? ShellParsingName,
    bool TargetIsFile, bool TargetIsDirectory, bool IsConsoleSubsystem = false);

/// <summary>
/// Pure catalog classification. A shortcut is an app only when it names something launchable:
/// an existing executable, an AUMID, or a shell-namespace item. Browser proxy shortcuts are
/// installed web apps only when they carry a valid app identity.
/// </summary>
public static class AppCatalogClassifier
{
    private static readonly HashSet<string> BrowserProxyExecutables = new(StringComparer.OrdinalIgnoreCase)
        { "chrome_proxy", "msedge_proxy" };

    // Chromium app/extension IDs are 32 characters in a–p.
    private static readonly Regex AppIdArgument = new(@"(?:^|\s)--app-id=(?<id>[a-p]{32})(?:\s|$)",
        RegexOptions.CultureInvariant);

    public static bool HasValidWebAppId(string? arguments)
        => arguments is not null && AppIdArgument.IsMatch(arguments);

    public static ShortcutKind Classify(ShortcutFacts facts)
    {
        if (!string.IsNullOrWhiteSpace(facts.TargetPath))
        {
            if (facts.TargetIsDirectory) return ShortcutKind.Rejected;
            if (facts.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (!facts.TargetIsFile) return ShortcutKind.Rejected;
                if (BrowserProxyExecutables.Contains(Path.GetFileNameWithoutExtension(facts.TargetPath)))
                    return HasValidWebAppId(facts.Arguments) ? ShortcutKind.WebApp : ShortcutKind.Rejected;
                return facts.IsConsoleSubsystem ? ShortcutKind.ConsoleExecutable : ShortcutKind.Executable;
            }
            // A document, URL, or help file is not an app unless the shell registered an app identity.
            return facts.Aumid is not null && facts.TargetIsFile ? ShortcutKind.Aumid : ShortcutKind.Rejected;
        }
        // A shell folder item is hosted by the shell whatever AUMID its link carries.
        if (IsShellFolderItem(facts.ShellParsingName)) return ShortcutKind.ShellNamespace;
        return facts.Aumid is not null ? ShortcutKind.Aumid : ShortcutKind.Rejected;
    }

    /// <summary>A desktop-absolute parsing name of a shell folder item: "::{CLSID}" (optionally
    /// followed by a child path). URLs and file paths are not shell-namespace items.</summary>
    public static bool IsShellFolderItem(string? parsingName)
        => parsingName is not null && parsingName.StartsWith("::{", StringComparison.Ordinal)
            && parsingName.IndexOf('}') > 3;

    /// <summary>Reads the PE optional-header subsystem; true for IMAGE_SUBSYSTEM_WINDOWS_CUI.</summary>
    public static bool IsConsoleSubsystem(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z') return false;
        var peOffset = BitConverter.ToInt32(image[0x3C..0x40]);
        // Signature (4) + COFF header (20) + optional-header Subsystem offset (68).
        var subsystemOffset = peOffset + 4 + 20 + 68;
        if (peOffset < 0 || subsystemOffset + 2 > image.Length) return false;
        if (image[peOffset] != 'P' || image[peOffset + 1] != 'E' || image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
            return false;
        return BitConverter.ToUInt16(image[subsystemOffset..(subsystemOffset + 2)]) == 3;
    }

    /// <summary>The process name declared for one application in a package manifest
    /// (Application/@Executable), or null when it is not declared.</summary>
    public static string? ManifestExecutableProcess(string manifestXml, string applicationId)
    {
        try
        {
            var doc = XDocument.Parse(manifestXml);
            var app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application"
                && string.Equals((string?)e.Attribute("Id"), applicationId, StringComparison.OrdinalIgnoreCase));
            var executable = (string?)app?.Attribute("Executable");
            return string.IsNullOrWhiteSpace(executable)
                ? null : Path.GetFileNameWithoutExtension(executable.Replace('\\', '/').Split('/')[^1]);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Adds entries in precedence order, collapsing duplicates by stable identity:
    /// the catalog ID (from the display name) and the AUMID.</summary>
    public static IReadOnlyList<AppEntry> Deduplicate(IEnumerable<AppEntry> ordered)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AppEntry>();
        foreach (var entry in ordered)
        {
            var aumid = entry.AppUserModelId
                ?? (entry.LaunchKind == AppLaunchKind.PackagedApp ? entry.LaunchTarget : null);
            if (string.IsNullOrEmpty(entry.Id) || seenIds.Contains(entry.Id)) continue;
            if (aumid is not null && seenAumids.Contains(aumid)) continue;
            seenIds.Add(entry.Id);
            if (aumid is not null) seenAumids.Add(aumid);
            result.Add(entry);
        }
        return result;
    }
}
