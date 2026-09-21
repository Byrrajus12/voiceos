using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Windows;

namespace VoiceOS.Core.Apps;

/// <summary>
/// Discovers installed applications from Start Menu shortcuts.
/// Shortcuts are resolved via IShellLink COM. Launch target, arguments, and AUMID are
/// preserved from each shortcut so VoiceOS can launch apps with their correct parameters
/// and distinguish apps that share a process executable (e.g. Chrome vs Chrome PWAs).
/// </summary>
public sealed class WindowsAppCatalog : IAppCatalog
{
    private readonly Lazy<IReadOnlyList<AppEntry>> _entries;
    private readonly ILogger<WindowsAppCatalog>? _logger;

    public WindowsAppCatalog(ILogger<WindowsAppCatalog>? logger = null)
    {
        _logger = logger;
        _entries = new Lazy<IReadOnlyList<AppEntry>>(Discover, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<AppEntry> GetAll() => _entries.Value;

    public AppEntry? FindById(string id) =>
        _entries.Value.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<AppEntry> Discover()
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AppEntry>();

        var startMenuDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs")
        };

        foreach (var dir in startMenuDirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var lnkPath in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                var entry = TryBuildFromShortcut(lnkPath, seenIds, seenAumids);
                if (entry != null) entries.Add(entry);
            }
        }

        // Seed well-known packaged (MSIX) apps that have no Start Menu .lnk shortcut.
        foreach (var seeded in SeededPackagedApps)
        {
            if (seenIds.Add(seeded.Id))
                entries.Add(seeded);
        }

        _logger?.LogInformation("App catalog: {Count} entries discovered", entries.Count);
        return entries;
    }

    // Packaged apps with no Start Menu .lnk; launched via shell execute on the AUMID.
    private static readonly AppEntry[] SeededPackagedApps =
    [
        new("windows-terminal", "Windows Terminal", "WindowsTerminal",
            AppLaunchKind.PackagedApp, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
    ];

    private AppEntry? TryBuildFromShortcut(string lnkPath, HashSet<string> seenIds, HashSet<string> seenAumids)
    {
        try
        {
            var displayName = Path.GetFileNameWithoutExtension(lnkPath);
            var id = MakeId(displayName);
            if (string.IsNullOrEmpty(id)) return null;
            if (!seenIds.Add(id)) return null;

            var (targetPath, launchArguments) = ResolveShortcut(lnkPath);
            var aumid = WindowPropertyStore.GetShortcutAumid(lnkPath);

            // Skip if a different shortcut with the same AUMID was already added.
            // This collapses shortcuts that represent the same logical app under a different display name.
            if (aumid != null && !seenAumids.Add(aumid))
                return null;

            string? processName = null;
            var launchTarget = lnkPath;

            if (!string.IsNullOrEmpty(targetPath) &&
                targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(targetPath))
            {
                processName = Path.GetFileNameWithoutExtension(targetPath);
                launchTarget = targetPath;
            }

            return new AppEntry(id, displayName, processName, AppLaunchKind.Win32, launchTarget,
                LaunchArguments: string.IsNullOrWhiteSpace(launchArguments) ? null : launchArguments,
                AppUserModelId: aumid);
        }
        catch
        {
            return null;
        }
    }

    public static string MakeId(string displayName)
    {
        var sb = new StringBuilder(displayName.Length);
        bool prevSep = true;
        foreach (char c in displayName)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                prevSep = false;
            }
            else if (!prevSep)
            {
                sb.Append('-');
                prevSep = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }

    private static (string? target, string? arguments) ResolveShortcut(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            var file = (IPersistFile)link;
            file.Load(lnkPath, 0 /* STGM_READ */);

            var buf = new StringBuilder(260);
            link.GetPath(buf, buf.Capacity, IntPtr.Zero, 4 /* SLGP_RAWPATH */);
            var target = string.IsNullOrWhiteSpace(buf.ToString()) ? null : buf.ToString();

            buf.Clear();
            buf.EnsureCapacity(32768);
            link.GetArguments(buf, buf.Capacity);
            var args = string.IsNullOrWhiteSpace(buf.ToString()) ? null : buf.ToString();

            return (target, args);
        }
        catch
        {
            return (null, null);
        }
    }

    // Shell Link CLSID: {00021401-0000-0000-C000-000000000046}
    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    private class ShellLinkCoClass { }

    // IShellLinkW {000214F9-0000-0000-C000-000000000046}
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    // IPersistFile {0000010b-0000-0000-C000-000000000046}
    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
