// ShortcutProbe — dev-only diagnostic for M5 identity investigation.
// Reads .lnk shortcut metadata (target, args, AUMID) from Start Menu and Desktop locations.
// Not production code; not added to the main solution.
using System.Runtime.InteropServices;
using System.Text;

ShortcutProbe.Run();

static class ShortcutProbe
{
    static readonly PROPERTYKEY PKEY_AppUserModel_ID =
        MakeKey("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);

    static PROPERTYKEY MakeKey(string fmtid, uint pid) =>
        new() { fmtid = new Guid(fmtid), pid = pid };

    public static void Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== ShortcutProbe ===");
        Console.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        var searchDirs = new[]
        {
            // Per-user Start Menu (Chrome PWA shortcuts land here by default)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            // System-wide Start Menu
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            // Per-user Desktop
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            // Public Desktop
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        var allLinks = new List<ShortcutInfo>();

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            // Recurse for Start Menu (has subdirectories); Desktop is flat
            var searchOpt = dir.Contains("Start Menu", StringComparison.OrdinalIgnoreCase)
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", searchOpt))
            {
                var info = InspectShortcut(lnk, dir);
                if (info != null) allLinks.Add(info);
            }
        }

        Console.WriteLine($"Total .lnk files found: {allLinks.Count}");
        Console.WriteLine(new string('=', 110));

        foreach (var s in allLinks)
        {
            Console.WriteLine();
            Console.WriteLine($"Shortcut  : {s.LnkPath}");
            Console.WriteLine($"Name      : {s.Name}");
            Console.WriteLine($"Target    : {s.TargetPath ?? "(none)"}");
            Console.WriteLine($"Arguments : {s.Arguments ?? "(none)"}");
            Console.WriteLine($"WorkDir   : {s.WorkingDirectory ?? "(none)"}");
            Console.WriteLine($"AUMID     : {s.Aumid ?? "(none)"}");
            Console.WriteLine(new string('-', 110));
        }

        // ── Chrome / PWA focused summary ──────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== Chrome / PWA shortcuts (target or name contains 'chrome', 'youtube', 'pwa') ===");

        var chromeLinks = allLinks.Where(s =>
            IsChromeLike(s.TargetPath) ||
            IsChromeLike(s.Name) ||
            IsChromeLike(s.Arguments)).ToList();

        if (chromeLinks.Count == 0)
        {
            Console.WriteLine("(none found in scanned locations)");
        }
        else
        {
            Console.WriteLine($"{"Name",-40} {"AUMID",-55} {"Arguments"}");
            Console.WriteLine(new string('-', 160));
            foreach (var s in chromeLinks)
            {
                var name = s.Name.Length > 38 ? s.Name[..38] + "…" : s.Name;
                var aumid = (s.Aumid ?? "(none)").Length > 53
                    ? (s.Aumid ?? "(none)")[..53] + "…"
                    : (s.Aumid ?? "(none)");
                var args = s.Arguments ?? "(none)";
                Console.WriteLine($"{name,-40} {aumid,-55} {args}");
            }
        }

        // ── AUMID match experiment ─────────────────────────────────────────────
        const string knownYtMusicAumid = "Chrome._crx_cinhimbnkkghhklpknlkffjgod";
        Console.WriteLine();
        Console.WriteLine($"=== AUMID match experiment (looking for '{knownYtMusicAumid}') ===");
        var matched = allLinks.Where(s =>
            string.Equals(s.Aumid, knownYtMusicAumid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
            Console.WriteLine("No shortcut found with that AUMID.");
        else
            foreach (var s in matched)
                Console.WriteLine($"  MATCH: {s.LnkPath}  args={s.Arguments ?? "(none)"}");

        Console.WriteLine();
        Console.WriteLine("=== All unique AUMIDs observed ===");
        foreach (var g in allLinks
            .Where(s => s.Aumid != null)
            .GroupBy(s => s.Aumid!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {g.Key,-60} ({g.Count()} shortcut{(g.Count() == 1 ? "" : "s")})");
            foreach (var s in g)
                Console.WriteLine($"    → {s.Name}");
        }
    }

    static bool IsChromeLike(string? s) =>
        s != null && (
            s.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("app-id", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("crx", StringComparison.OrdinalIgnoreCase));

    static ShortcutInfo? InspectShortcut(string lnkPath, string rootDir)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(lnkPath);

            // IShellLink for target/args/workdir
            string? targetPath = null;
            string? arguments = null;
            string? workingDir = null;
            try
            {
                var link = (IShellLinkW)new ShellLinkCoClass();
                var file = (IPersistFile)link;
                file.Load(lnkPath, 0 /* STGM_READ */);

                var buf = new StringBuilder(32768);
                link.GetPath(buf, buf.Capacity, IntPtr.Zero, 4 /* SLGP_RAWPATH */);
                targetPath = buf.Length > 0 ? buf.ToString() : null;

                buf.Clear();
                link.GetArguments(buf, buf.Capacity);
                arguments = buf.Length > 0 ? buf.ToString() : null;

                buf.Clear();
                link.GetWorkingDirectory(buf, buf.Capacity);
                workingDir = buf.Length > 0 ? buf.ToString() : null;
            }
            catch { }

            // IPropertyStore on the .lnk file itself for AUMID
            string? aumid = null;
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                // GPS_DEFAULT = 0; reads the .lnk file's own property store
                int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0, ref iid, out var storeObj);
                if (hr == 0 && storeObj is IPropertyStore ps)
                {
                    aumid = GetStringProperty(ps, PKEY_AppUserModel_ID);
                    Marshal.ReleaseComObject(ps);
                }
            }
            catch { }

            return new ShortcutInfo(lnkPath, name, targetPath, arguments, workingDir, aumid);
        }
        catch
        {
            return null;
        }
    }

    static string? GetStringProperty(IPropertyStore store, PROPERTYKEY key)
    {
        try
        {
            var pv = new PropVariant();
            int hr = store.GetValue(ref key, pv);
            if (hr != 0) return null;
            var result = pv.GetString();
            pv.Clear();
            return result;
        }
        catch { return null; }
    }

    // ─── Win32 ───────────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(
        string pszPath,
        IntPtr pbc,
        int flags,
        ref Guid riid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    // Shell Link CLSID {00021401-0000-0000-C000-000000000046}
    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    class ShellLinkCoClass { }

    // IShellLinkW {000214F9-0000-0000-C000-000000000046}
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
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
    interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}

// ─── COM: IPropertyStore ─────────────────────────────────────────────────────

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint cProps);
    [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, [In, Out] PropVariant pv);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, PropVariant propvar);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
class PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr ptrVal;

    public string? GetString() =>
        vt == 31 /*VT_LPWSTR*/ && ptrVal != IntPtr.Zero
            ? Marshal.PtrToStringUni(ptrVal)
            : null;

    public void Clear()
    {
        if (vt == 31 && ptrVal != IntPtr.Zero)
            Marshal.FreeCoTaskMem(ptrVal);
        vt = 0;
        ptrVal = IntPtr.Zero;
    }
}

// ─── Data record ─────────────────────────────────────────────────────────────

record ShortcutInfo(
    string LnkPath,
    string Name,
    string? TargetPath,
    string? Arguments,
    string? WorkingDirectory,
    string? Aumid);
