// WinIdentityProbe — dev-only diagnostic for M5 identity investigation.
// Enumerates top-level windows and dumps per-window identity signals.
// Not production code; not added to the main solution.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

Probe.Run();

static class Probe
{
    // Property keys under {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3} (AppUserModel shell namespace)
    static readonly PROPERTYKEY PKEY_AppUserModel_ID =
        MakeKey("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
    static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand =
        MakeKey("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 2);
    static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource =
        MakeKey("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 4);

    static PROPERTYKEY MakeKey(string fmtid, uint pid) =>
        new() { fmtid = new Guid(fmtid), pid = pid };

    public static void Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== WinIdentityProbe ===");
        Console.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        var foreground = GetForegroundWindow();
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var titleBuf = new StringBuilder(512);
            if (GetWindowText(hWnd, titleBuf, titleBuf.Capacity) == 0) return true;
            windows.Add(Inspect(hWnd, titleBuf.ToString(), hWnd == foreground));
            return true;
        }, IntPtr.Zero);

        Console.WriteLine($"Visible top-level windows: {windows.Count}");
        Console.WriteLine(new string('=', 100));

        foreach (var w in windows)
        {
            Console.WriteLine();
            Console.WriteLine($"HWND         : 0x{w.Hwnd:X}");
            Console.WriteLine($"Title        : {w.Title}");
            Console.WriteLine($"Foreground   : {w.IsForeground}");
            Console.WriteLine($"WinClass     : {w.ClassName}");
            Console.WriteLine($"PID          : {w.Pid}");
            Console.WriteLine($"ProcessName  : {w.ProcessName}");
            Console.WriteLine($"ExePath      : {w.ExePath ?? "(unavailable)"}");
            Console.WriteLine($"PerWin AUMID : {w.PerWindowAumid ?? "(none)"}");
            Console.WriteLine($"Proc  AUMID  : {w.ProcessAumid ?? "(none)"}");
            Console.WriteLine($"PackageFamily: {w.PackageFamilyName ?? "(none)"}");
            Console.WriteLine($"RelaunchCmd  : {w.RelaunchCommand ?? "(none)"}");
            Console.WriteLine($"RelaunchDN   : {w.RelaunchDisplayName ?? "(none)"}");
            Console.WriteLine(new string('-', 100));
        }

        Console.WriteLine();
        Console.WriteLine("=== Chrome-family windows (exe path contains 'chrome') ===");
        var chromeWindows = windows
            .Where(w => (w.ExePath ?? w.ProcessName ?? "").Contains("chrome", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (chromeWindows.Count == 0)
        {
            Console.WriteLine("(none found)");
        }
        else
        {
            Console.WriteLine($"{"HWND",-14} {"ProcessName",-14} {"PerWin AUMID",-60} {"Title"}");
            Console.WriteLine(new string('-', 140));
            foreach (var w in chromeWindows)
            {
                var title = w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title;
                Console.WriteLine($"0x{w.Hwnd,-12:X} {w.ProcessName,-14} {w.PerWindowAumid ?? "(none)",-60} {title}");
            }
        }
    }

    static WindowInfo Inspect(IntPtr hWnd, string title, bool isForeground)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);

        string processName = string.Empty;
        string? exePath = null;
        string? processAumid = null;
        string? packageFamily = null;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
            try { exePath = proc.MainModule?.FileName; } catch { }
        }
        catch { }

        string? perWindowAumid = null;
        string? relaunchCmd = null;
        string? relaunchDn = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreForWindow(hWnd, ref iid, out var storeObj);
            if (hr == 0 && storeObj is IPropertyStore ps)
            {
                perWindowAumid = GetStringProperty(ps, PKEY_AppUserModel_ID);
                relaunchCmd = GetStringProperty(ps, PKEY_AppUserModel_RelaunchCommand);
                relaunchDn = GetStringProperty(ps, PKEY_AppUserModel_RelaunchDisplayNameResource);
                Marshal.ReleaseComObject(ps);
            }
        }
        catch { }

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != IntPtr.Zero)
        {
            try
            {
                uint len = 512;
                var sb = new StringBuilder((int)len);
                if (GetApplicationUserModelId(hProcess, ref len, sb) == 0 && sb.Length > 0)
                    processAumid = sb.ToString();

                len = 512;
                sb.Clear();
                sb.EnsureCapacity((int)len);
                if (GetPackageFamilyName(hProcess, ref len, sb) == 0 && sb.Length > 0)
                    packageFamily = sb.ToString();
            }
            catch { }
            finally { CloseHandle(hProcess); }
        }

        var classBuf = new StringBuilder(256);
        GetClassName(hWnd, classBuf, classBuf.Capacity);

        return new WindowInfo(
            hWnd, title, isForeground, classBuf.ToString(),
            pid, processName, exePath,
            perWindowAumid, processAumid, packageFamily,
            relaunchCmd, relaunchDn);
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

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint appModelIdLength, StringBuilder appModelId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackageFamilyName(IntPtr hProcess, ref uint packageFamilyNameLength, StringBuilder packageFamilyName);
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
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

// Minimal PROPVARIANT for VT_LPWSTR string extraction.
// Must be a class (reference type) so COM can marshal it via pointer.
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

record WindowInfo(
    IntPtr Hwnd,
    string Title,
    bool IsForeground,
    string ClassName,
    uint Pid,
    string ProcessName,
    string? ExePath,
    string? PerWindowAumid,
    string? ProcessAumid,
    string? PackageFamilyName,
    string? RelaunchCommand,
    string? RelaunchDisplayName);
