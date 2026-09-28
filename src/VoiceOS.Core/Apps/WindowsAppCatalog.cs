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
        var shortcuts = new List<AppEntry>();
        var rejected = 0;

        var startMenuDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs")
        };

        // Shell link and MSI shortcut resolution require a single-threaded apartment.
        var scan = new Thread(() =>
        {
            foreach (var dir in startMenuDirs)
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var lnkPath in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    var entry = TryBuildFromShortcut(lnkPath);
                    if (entry != null) shortcuts.Add(entry);
                    else rejected++;
                }
            }
        }) { IsBackground = true, Name = "AppCatalogShortcutScan" };
        scan.SetApartmentState(ApartmentState.STA);
        scan.Start();
        scan.Join();

        // Precedence: Start Menu shortcuts, then seeded packaged apps, then the packaged apps the
        // shell lists in AppsFolder. Duplicates collapse by catalog ID and AUMID.
        var packaged = EnumeratePackagedApps();
        var entries = AppCatalogClassifier.Deduplicate(shortcuts.Concat(SeededPackagedApps).Concat(packaged));

        _logger?.LogInformation("App catalog: {Count} entries discovered ({Shortcuts} shortcuts, {Packaged} packaged candidates, {Rejected} non-launchable shortcuts rejected)",
            entries.Count, shortcuts.Count, packaged.Count, rejected);
        return entries;
    }


    // Packaged apps with no Start Menu .lnk; launched via shell execute on the AUMID.
    private static readonly AppEntry[] SeededPackagedApps =
    [
        new("windows-terminal", "Windows Terminal", "WindowsTerminal",
            AppLaunchKind.PackagedApp, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
    ];

    /// <summary>
    /// Packaged (MSIX/UWP) apps the shell lists in AppsFolder for the current user, e.g.
    /// Calculator and Paint. Each is launched by AUMID through shell:AppsFolder; its process
    /// name comes from the package manifest when declared.
    /// </summary>
    private List<AppEntry> EnumeratePackagedApps()
    {
        var result = new List<AppEntry>();
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    string? manifest = null;
                    foreach (var app in package.GetAppListEntries())
                    {
                        var aumid = app.AppUserModelId;
                        var name = app.DisplayInfo?.DisplayName;
                        if (string.IsNullOrWhiteSpace(aumid) || string.IsNullOrWhiteSpace(name)
                            || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                        var id = MakeId(name);
                        if (string.IsNullOrEmpty(id)) continue;
                        manifest ??= ReadManifest(package.InstalledLocation?.Path);
                        var appId = aumid[(aumid.IndexOf('!') + 1)..];
                        var process = manifest is null ? null : AppCatalogClassifier.ManifestExecutableProcess(manifest, appId);
                        result.Add(new AppEntry(id, name, process, AppLaunchKind.PackagedApp, aumid,
                            AppUserModelId: aumid));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "App catalog: skipped a packaged app");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "App catalog: packaged app enumeration failed");
        }
        return result;
    }

    private static string? ReadManifest(string? installedLocation)
    {
        try
        {
            var path = installedLocation is null ? null : Path.Combine(installedLocation, "AppxManifest.xml");
            return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private AppEntry? TryBuildFromShortcut(string lnkPath)
    {
        try
        {
            var displayName = Path.GetFileNameWithoutExtension(lnkPath);
            var id = MakeId(displayName);
            if (string.IsNullOrEmpty(id)) return null;

            var (targetPath, launchArguments, parsingName) = ResolveShortcut(lnkPath);
            var aumid = WindowPropertyStore.GetShortcutAumid(lnkPath);
            // An MSI advertised shortcut stores an icon path; the installer resolves its executable.
            var target = ResolveAdvertisedTarget(lnkPath)
                ?? (string.IsNullOrWhiteSpace(targetPath) ? null : Environment.ExpandEnvironmentVariables(targetPath));
            var isFile = target is not null && File.Exists(target);
            var facts = new ShortcutFacts(target, launchArguments, aumid, parsingName, isFile,
                target is not null && Directory.Exists(target),
                isFile && target!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsConsoleImage(target));
            var kind = AppCatalogClassifier.Classify(facts);
            if (kind == ShortcutKind.Rejected)
            {
                _logger?.LogDebug("App catalog: rejected non-launchable shortcut {Name} target={Target}",
                    displayName, target ?? "-");
                return null;
            }

            var args = string.IsNullOrWhiteSpace(launchArguments) ? null : launchArguments;
            return kind switch
            {
                ShortcutKind.Executable or ShortcutKind.WebApp => new AppEntry(id, displayName,
                    Path.GetFileNameWithoutExtension(target), AppLaunchKind.Win32, target!, args, aumid),
                ShortcutKind.ConsoleExecutable => new AppEntry(id, displayName,
                    Path.GetFileNameWithoutExtension(target), AppLaunchKind.Win32, target!, args, aumid,
                    Host: AppHostKind.ConsoleHosted),
                // Shell-namespace items have no own process; the shortcut itself is the launch target.
                ShortcutKind.ShellNamespace => new AppEntry(id, displayName, null, AppLaunchKind.Win32,
                    lnkPath, Host: AppHostKind.ShellNamespace),
                _ => new AppEntry(id, displayName, null, AppLaunchKind.Win32, lnkPath, AppUserModelId: aumid)
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsConsoleImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            return AppCatalogClassifier.IsConsoleSubsystem(buffer.AsSpan(0, read));
        }
        catch
        {
            return false;
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

    private static (string? target, string? arguments, string? parsingName) ResolveShortcut(string lnkPath)
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

            string? parsingName = null;
            if (target is null)
            {
                try
                {
                    link.GetIDList(out var pidl);
                    if (pidl != IntPtr.Zero)
                    {
                        try
                        {
                            if (SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, out var name) == 0)
                            {
                                parsingName = Marshal.PtrToStringUni(name);
                                Marshal.FreeCoTaskMem(name);
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(pidl);
                        }
                    }
                }
                catch
                {
                    // No resolvable ID list.
                }
            }

            return (target, args, parsingName);
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>The executable behind an MSI advertised (Darwin) shortcut, or null when the
    /// shortcut is not advertised or its component is not installed locally.</summary>
    private static string? ResolveAdvertisedTarget(string lnkPath)
    {
        try
        {
            var product = new StringBuilder(39);
            var feature = new StringBuilder(39);
            var component = new StringBuilder(39);
            if (MsiGetShortcutTarget(lnkPath, product, feature, component) != 0) return null;
            var size = 1024;
            var path = new StringBuilder(size);
            MsiGetComponentPath(product.ToString(), component.ToString(), path, ref size);
            // The install state can report absent for per-user/per-machine mismatches while the
            // key path exists; the classifier requires the resolved executable to exist.
            return path.Length > 0 && File.Exists(path.ToString()) ? path.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetShortcutTarget(string szShortcutTarget, StringBuilder szProductCode,
        StringBuilder szFeatureId, StringBuilder szComponentCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPath(string szProduct, string szComponent,
        StringBuilder lpPathBuf, ref int pcchBuf);

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
