// WinIdentityProbe — dev-only diagnostic for M5 identity + M6A monitor topology investigation.
// Enumerates top-level windows (M5) and physical display topology (M6A).
// Not production code; not added to the main solution.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

Probe.Run();
MonitorProbe.Run();

// ═══════════════════════════════════════════════════════════════════════════════
// EXISTING: M5 window / identity probe
// ═══════════════════════════════════════════════════════════════════════════════

static class Probe
{
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

        string? processAumid = null;
        string? packageFamily = null;
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

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint appModelIdLength, StringBuilder appModelId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr hProcess, ref uint packageFamilyNameLength, StringBuilder packageFamilyName);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}

// ═══════════════════════════════════════════════════════════════════════════════
// NEW: M6A display topology probe
// ═══════════════════════════════════════════════════════════════════════════════

static class MonitorProbe
{
    // ── DPI awareness context sentinels (opaque handles, defined as negative IntPtr) ──
    static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE             = new IntPtr(-1);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE        = new IntPtr(-2);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE   = new IntPtr(-3);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED   = new IntPtr(-5);

    // MONITORINFOF_PRIMARY flag
    const uint MONITORINFOF_PRIMARY = 0x00000001;

    // QueryDisplayConfig flags
    const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    // DisplayConfig device info type constants
    const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL flag
    const uint DC_OUTPUT_TECH_INTERNAL = 0x80000000;

    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 100));
        Console.WriteLine("=== M6A DISPLAY TOPOLOGY PROBE ===");
        Console.WriteLine(new string('═', 100));
        Console.WriteLine();

        // ── 1. DPI AWARENESS STATE ──────────────────────────────────────────────

        Console.WriteLine("── 1. DPI AWARENESS ──────────────────────────────────────────────────────────");
        Console.WriteLine();

        var initialContext = GetThreadDpiAwarenessContext();
        int initialAwareness = GetAwarenessFromDpiAwarenessContext(initialContext);
        Console.WriteLine($"  Initial thread DPI context handle : {initialContext}");
        Console.WriteLine($"  Initial DPI_AWARENESS value       : {initialAwareness} ({DpiAwarenessName(initialAwareness)})");
        Console.WriteLine($"  (DPI_AWARENESS: -1=Invalid 0=Unaware 1=SystemAware 2=PerMonitorAware)");
        Console.WriteLine();

        // Report process-level awareness via GetProcessDpiAwareness
        int procAwarenessHr = GetProcessDpiAwareness(IntPtr.Zero, out int procAwareness);
        Console.WriteLine($"  GetProcessDpiAwareness (this process): hr=0x{procAwarenessHr:X8}, value={procAwareness} ({ProcDpiAwarenessName(procAwareness)})");
        Console.WriteLine($"  (PROCESS_DPI_AWARENESS: 0=Unaware 1=SystemAware 2=PerMonitorAware)");
        Console.WriteLine();

        // Switch to PerMonitorV2 for accurate physical-pixel measurements
        var prevContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var activeContext = GetThreadDpiAwarenessContext();
        int activeAwareness = GetAwarenessFromDpiAwarenessContext(activeContext);
        Console.WriteLine($"  After SetThreadDpiAwarenessContext(PMv2):");
        Console.WriteLine($"    Thread DPI context handle : {activeContext}");
        Console.WriteLine($"    DPI_AWARENESS value       : {activeAwareness} ({DpiAwarenessName(activeAwareness)})");
        Console.WriteLine($"    Context is PMv2           : {AreDpiAwarenessContextsEqual(activeContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)}");
        Console.WriteLine();
        Console.WriteLine("  NOTE: All geometry measurements below are taken under PerMonitorV2 context.");
        Console.WriteLine("  Under PerMonitorV2, GetWindowRect/GetMonitorInfo/SetWindowPos all use");
        Console.WriteLine("  physical screen pixels with NO DPI virtualization. Coordinates on");
        Console.WriteLine("  different monitors are in the same physical-pixel virtual screen space.");
        Console.WriteLine();

        // ── 2. VIRTUAL SCREEN ───────────────────────────────────────────────────

        Console.WriteLine("── 2. VIRTUAL SCREEN ─────────────────────────────────────────────────────────");
        Console.WriteLine();
        int vsX   = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vsY   = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vsCX  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vsCY  = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int nMon  = GetSystemMetrics(SM_CMONITORS);
        Console.WriteLine($"  SM_CMONITORS     : {nMon}");
        Console.WriteLine($"  SM_XVIRTUALSCREEN: {vsX}  (may be negative if monitors extend left of primary)");
        Console.WriteLine($"  SM_YVIRTUALSCREEN: {vsY}  (may be negative if monitors extend above primary)");
        Console.WriteLine($"  SM_CXVIRTUALSCREEN: {vsCX}");
        Console.WriteLine($"  SM_CYVIRTUALSCREEN: {vsCY}");
        Console.WriteLine($"  Virtual screen rect: ({vsX}, {vsY}) to ({vsX + vsCX}, {vsY + vsCY})");
        Console.WriteLine();

        // ── 3. ENUMDISPLAYMONITORS ───────────────────────────────────────────────

        Console.WriteLine("── 3. ENUMDISPLAYMONITORS + GETMONITORINFO ───────────────────────────────────");
        Console.WriteLine("   (physical-pixel coordinates; enumeration order is UNDEFINED)");
        Console.WriteLine();

        var enumMonitors = new List<EnumMonitorEntry>();
        int enumIdx = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, hdcMon, lprcMon, dwData) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(hMon, ref mi))
            {
                uint dpiX = 0, dpiY = 0;
                int dpiHr = GetDpiForMonitor(hMon, 0 /*MDT_EFFECTIVE_DPI*/, out dpiX, out dpiY);

                enumMonitors.Add(new EnumMonitorEntry(
                    EnumIdx: enumIdx++,
                    HMonitor: hMon,
                    Device: mi.szDevice,
                    Bounds: mi.rcMonitor,
                    WorkArea: mi.rcWork,
                    IsPrimary: (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    EffectiveDpiX: dpiHr == 0 ? dpiX : 0,
                    EffectiveDpiY: dpiHr == 0 ? dpiY : 0,
                    DpiHr: dpiHr));
            }
            return true;
        }, IntPtr.Zero);

        Console.WriteLine($"  Monitors returned by EnumDisplayMonitors: {enumMonitors.Count}");
        Console.WriteLine();

        for (int i = 0; i < enumMonitors.Count; i++)
        {
            var m = enumMonitors[i];
            int bw = m.Bounds.right - m.Bounds.left;
            int bh = m.Bounds.bottom - m.Bounds.top;
            int ww = m.WorkArea.right - m.WorkArea.left;
            int wh = m.WorkArea.bottom - m.WorkArea.top;
            int cx = (m.Bounds.left + m.Bounds.right) / 2;
            int cy = (m.Bounds.top + m.Bounds.bottom) / 2;

            Console.WriteLine($"  Monitor[{i}] (enumeration index — NOT a stable identity)");
            Console.WriteLine($"    HMONITOR        : 0x{m.HMonitor:X}  (runtime handle only; invalidated on topology change)");
            Console.WriteLine($"    GDI device name : {m.Device}  (szDevice from MONITORINFOEX)");
            Console.WriteLine($"    Primary         : {m.IsPrimary}");
            Console.WriteLine($"    Bounds          : ({m.Bounds.left}, {m.Bounds.top}) to ({m.Bounds.right}, {m.Bounds.bottom})  [{bw}×{bh} px]");
            Console.WriteLine($"    Work area       : ({m.WorkArea.left}, {m.WorkArea.top}) to ({m.WorkArea.right}, {m.WorkArea.bottom})  [{ww}×{wh} px]");
            Console.WriteLine($"    Center          : ({cx}, {cy})");
            if (m.DpiHr == 0)
                Console.WriteLine($"    EffectiveDPI    : {m.EffectiveDpiX}×{m.EffectiveDpiY}  (GetDpiForMonitor MDT_EFFECTIVE_DPI)");
            else
                Console.WriteLine($"    EffectiveDPI    : GetDpiForMonitor failed hr=0x{m.DpiHr:X8}");
            Console.WriteLine();
        }

        // ── 4. DISPLAYCONFIG ────────────────────────────────────────────────────

        Console.WriteLine("── 4. DISPLAYCONFIG (QueryDisplayConfig + DisplayConfigGetDeviceInfo) ─────────");
        Console.WriteLine();

        var dcEntries = QueryDisplayConfigEntries();

        if (dcEntries == null)
        {
            Console.WriteLine("  QueryDisplayConfig failed. Cannot proceed with DisplayConfig section.");
        }
        else
        {
            Console.WriteLine($"  Active paths returned: {dcEntries.Count}");
            Console.WriteLine();

            for (int i = 0; i < dcEntries.Count; i++)
            {
                var e = dcEntries[i];
                bool isInternal = (e.OutputTechnology & DC_OUTPUT_TECH_INTERNAL) != 0;
                uint baseTech = e.OutputTechnology & ~DC_OUTPUT_TECH_INTERNAL;

                Console.WriteLine($"  Path[{i}]");
                Console.WriteLine($"    Source adapterId        : {e.SourceAdapterId.HighPart:X8}:{e.SourceAdapterId.LowPart:X8}");
                Console.WriteLine($"    Source id               : {e.SourceId}");
                Console.WriteLine($"    Source GDI device name  : {e.SourceGdiDeviceName ?? "(unavailable)"}");
                Console.WriteLine($"    Source mode position    : ({e.SourcePositionX}, {e.SourcePositionY})  (virtual-screen origin of this source)");
                Console.WriteLine($"    Source mode resolution  : {e.SourceWidth}×{e.SourceHeight}");
                Console.WriteLine($"    Target adapterId        : {e.TargetAdapterId.HighPart:X8}:{e.TargetAdapterId.LowPart:X8}");
                Console.WriteLine($"    Target id               : {e.TargetId}");
                Console.WriteLine($"    Output technology       : 0x{e.OutputTechnology:X8}  ({OutputTechName(e.OutputTechnology)})");
                Console.WriteLine($"    Internal display        : {isInternal}  (DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL bit)");
                Console.WriteLine($"    ConnectorInstance       : {e.ConnectorInstance}");
                Console.WriteLine($"    Friendly name           : {e.FriendlyName ?? "(unavailable)"}");
                Console.WriteLine($"    Device path             : {e.DevicePath ?? "(unavailable)"}");
                Console.WriteLine();
            }
        }

        // ── 5. CROSS-REFERENCE ──────────────────────────────────────────────────

        Console.WriteLine("── 5. CROSS-REFERENCE: EnumDisplayMonitors ↔ DisplayConfig ──────────────────");
        Console.WriteLine("   Join key: GDI device name (szDevice = viewGdiDeviceName)");
        Console.WriteLine();

        if (dcEntries != null)
        {
            bool anyMismatch = false;
            foreach (var m in enumMonitors)
            {
                var dc = dcEntries.FirstOrDefault(e =>
                    string.Equals(e.SourceGdiDeviceName, m.Device, StringComparison.OrdinalIgnoreCase));

                Console.WriteLine($"  GDI device: {m.Device}");
                Console.WriteLine($"    EnumDisplayMonitors: HMONITOR=0x{m.HMonitor:X}  Primary={m.IsPrimary}  Bounds=({m.Bounds.left},{m.Bounds.top})-({m.Bounds.right},{m.Bounds.bottom})  DPI={m.EffectiveDpiX}");
                if (dc != null)
                {
                    Console.WriteLine($"    DisplayConfig path : sourceId={dc.SourceId}  targetId={dc.TargetId}  adapterId.Low={dc.SourceAdapterId.LowPart:X8}");
                    Console.WriteLine($"                         friendlyName=\"{dc.FriendlyName}\"  internal={((dc.OutputTechnology & DC_OUTPUT_TECH_INTERNAL) != 0)}");
                    Console.WriteLine($"                         devicePath={dc.DevicePath}");
                }
                else
                {
                    Console.WriteLine($"    DisplayConfig path : *** NO MATCH FOUND ***");
                    anyMismatch = true;
                }
                Console.WriteLine();
            }
            if (anyMismatch)
                Console.WriteLine("  WARNING: Some EnumDisplayMonitors entries had no DisplayConfig match.");
        }

        // ── 6. SETTINGS ORDINAL INVESTIGATION ──────────────────────────────────

        Console.WriteLine("── 6. SETTINGS DISPLAY ORDINAL INVESTIGATION ────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("  Windows Settings → System → Display assigns display numbers (1, 2, ...)");
        Console.WriteLine("  using an internal algorithm with NO documented public API surface.");
        Console.WriteLine();
        Console.WriteLine("  Candidate ordinal sources available to VoiceOS (compare with Settings manually):");
        Console.WriteLine();
        Console.WriteLine("  [A] EnumDisplayMonitors enumeration order (index 0, 1, ...):");
        for (int i = 0; i < enumMonitors.Count; i++)
            Console.WriteLine($"      EnumIdx {i} → {enumMonitors[i].Device}  ({(enumMonitors[i].IsPrimary ? "PRIMARY" : "secondary")})");
        Console.WriteLine();

        if (dcEntries != null)
        {
            // Sort by adapterId.Low then sourceId as one candidate ordering
            var dcSorted = dcEntries
                .OrderBy(e => e.SourceAdapterId.LowPart)
                .ThenBy(e => e.SourceId)
                .ToList();

            Console.WriteLine("  [B] DisplayConfig paths sorted by adapterId.Low then sourceId:");
            for (int i = 0; i < dcSorted.Count; i++)
            {
                var e = dcSorted[i];
                Console.WriteLine($"      Ordinal {i + 1} → sourceId={e.SourceId}  {e.SourceGdiDeviceName}  \"{e.FriendlyName}\"");
            }
            Console.WriteLine();

            // Sort by sourceId only
            var dcBySourceId = dcEntries.OrderBy(e => e.SourceId).ToList();
            Console.WriteLine("  [C] DisplayConfig paths sorted by sourceId only:");
            for (int i = 0; i < dcBySourceId.Count; i++)
            {
                var e = dcBySourceId[i];
                Console.WriteLine($"      Ordinal {i + 1} → sourceId={e.SourceId}  {e.SourceGdiDeviceName}  \"{e.FriendlyName}\"");
            }
            Console.WriteLine();

            // Sort by virtual screen X position (left to right)
            var dcByPosition = dcEntries.OrderBy(e => e.SourcePositionX).ToList();
            Console.WriteLine("  [D] DisplayConfig paths sorted by virtual-screen X position (left→right):");
            for (int i = 0; i < dcByPosition.Count; i++)
            {
                var e = dcByPosition[i];
                Console.WriteLine($"      Position {i + 1} → X={e.SourcePositionX}  {e.SourceGdiDeviceName}  \"{e.FriendlyName}\"");
            }
            Console.WriteLine();
        }

        Console.WriteLine("  ACTION REQUIRED: Open Settings → System → Display, click Identify.");
        Console.WriteLine("  Numbers will appear on each screen. Compare with rows [B], [C], [D] above.");
        Console.WriteLine("  If none of the orderings match Settings, MonitorByOrdinal cannot be reliably");
        Console.WriteLine("  implemented and should be excluded from the M6 domain model.");
        Console.WriteLine();

        // ── 7. GEOMETRY/DPI NOTES ───────────────────────────────────────────────

        Console.WriteLine("── 7. DPI / COORDINATE SYSTEM NOTES ─────────────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("  Under PerMonitorV2 thread DPI context:");
        Console.WriteLine("    GetWindowRect    → physical screen pixels (no virtualization)");
        Console.WriteLine("    GetMonitorInfo   → physical screen pixels (no virtualization)");
        Console.WriteLine("    SetWindowPos     → expects physical screen pixels");
        Console.WriteLine("    All monitors share one virtual-screen coordinate space (origin may be negative).");
        Console.WriteLine();
        Console.WriteLine("  This means SetWindowPos with coordinates from GetMonitorInfo.rcWork");
        Console.WriteLine("  requires NO DPI scaling — coordinates are directly comparable across monitors.");
        Console.WriteLine();
        Console.WriteLine("  GetDpiForMonitor (MDT_EFFECTIVE_DPI) reports the monitor's effective DPI,");
        Console.WriteLine("  but it is NOT needed for window placement under PerMonitorV2. It is only");
        Console.WriteLine("  needed if VoiceOS wants to preserve the window's logical (user-perceived)");
        Console.WriteLine("  size across a DPI boundary, which is a design choice, not a correctness");
        Console.WriteLine("  requirement. Preserving physical pixel dimensions (what Win+Shift+Arrow does)");
        Console.WriteLine("  requires no DPI math.");
        Console.WriteLine();

        if (dcEntries != null && enumMonitors.Count > 1)
        {
            // Show the actual DPI values so we can see whether there's a difference
            Console.WriteLine("  DPI values on this machine:");
            foreach (var m in enumMonitors)
            {
                var dc = dcEntries.FirstOrDefault(e =>
                    string.Equals(e.SourceGdiDeviceName, m.Device, StringComparison.OrdinalIgnoreCase));
                string label = dc?.FriendlyName is { Length: > 0 } fn ? fn : m.Device;
                Console.WriteLine($"    {label}: EffectiveDPI={m.EffectiveDpiX}  Bounds={m.Bounds.right - m.Bounds.left}×{m.Bounds.bottom - m.Bounds.top}");
            }
            Console.WriteLine();

            bool uniformDpi = enumMonitors.Select(m => m.EffectiveDpiX).Distinct().Count() == 1;
            Console.WriteLine($"  Uniform DPI across all monitors: {uniformDpi}");
            if (!uniformDpi)
                Console.WriteLine("  WARNING: Mixed DPI detected. Window size will change visually when moved");
                Console.WriteLine("  between monitors if preserving physical pixel dimensions. This is the");
                Console.WriteLine("  same behavior as Win+Shift+Arrow (OS native).");
            Console.WriteLine();
        }

        // ── 8. RELATIVE MONITOR GEOMETRY ANALYSIS ─────────────────────────────

        Console.WriteLine("── 8. RELATIVE MONITOR GEOMETRY (left/right/vertical analysis) ─────────────");
        Console.WriteLine();

        if (enumMonitors.Count < 2)
        {
            Console.WriteLine("  Only one monitor detected. RelativeMonitor analysis not applicable.");
        }
        else
        {
            Console.WriteLine("  Monitor center points and spatial relationships:");
            foreach (var m in enumMonitors)
            {
                int cx = (m.Bounds.left + m.Bounds.right) / 2;
                int cy = (m.Bounds.top + m.Bounds.bottom) / 2;
                Console.WriteLine($"    {m.Device}: center=({cx}, {cy})  bounds=({m.Bounds.left},{m.Bounds.top})-({m.Bounds.right},{m.Bounds.bottom})");
            }
            Console.WriteLine();

            Console.WriteLine("  Left/right relationships (by center X comparison):");
            for (int i = 0; i < enumMonitors.Count; i++)
            {
                var a = enumMonitors[i];
                int ax = (a.Bounds.left + a.Bounds.right) / 2;
                int ay = (a.Bounds.top + a.Bounds.bottom) / 2;

                for (int j = 0; j < enumMonitors.Count; j++)
                {
                    if (i == j) continue;
                    var b = enumMonitors[j];
                    int bx = (b.Bounds.left + b.Bounds.right) / 2;
                    int by = (b.Bounds.top + b.Bounds.bottom) / 2;

                    // Vertical overlap: do the monitor bounds share any vertical range?
                    int overlapTop    = Math.Max(a.Bounds.top, b.Bounds.top);
                    int overlapBottom = Math.Min(a.Bounds.bottom, b.Bounds.bottom);
                    int vertOverlapPx = Math.Max(0, overlapBottom - overlapTop);
                    int aHeight       = a.Bounds.bottom - a.Bounds.top;
                    int bHeight       = b.Bounds.bottom - b.Bounds.top;
                    int minH          = Math.Min(aHeight, bHeight);
                    double overlapPct = minH > 0 ? (double)vertOverlapPx / minH * 100 : 0;

                    string dir = bx < ax ? "LEFT-of" : bx > ax ? "RIGHT-of" : "SAME-X-as";
                    Console.WriteLine($"    {b.Device} is {dir} {a.Device}");
                    Console.WriteLine($"      center-X diff: {bx - ax:+0;-0}  center-Y diff: {by - ay:+0;-0}");
                    Console.WriteLine($"      vertical overlap: {vertOverlapPx}px ({overlapPct:F0}% of shorter monitor)");
                    Console.WriteLine($"      Qualifies as left/right neighbor (>0 overlap): {vertOverlapPx > 0}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("  NOTE for RelativeMonitor design:");
            Console.WriteLine("  A 'monitor to the left' should require meaningful vertical overlap, not just");
            Console.WriteLine("  center-X comparison. Without overlap, the monitor is diagonally placed and");
            Console.WriteLine("  'left monitor' is ambiguous. Recommended threshold: >0 px vertical overlap");
            Console.WriteLine("  of the monitor bounds (not centers) to qualify as a left/right neighbor.");
        }

        Console.WriteLine();
        Console.WriteLine(new string('═', 100));
        Console.WriteLine("=== END DISPLAY TOPOLOGY PROBE ===");
        Console.WriteLine(new string('═', 100));

        // Restore original DPI context
        SetThreadDpiAwarenessContext(prevContext);
    }

    // ── DisplayConfig query ──────────────────────────────────────────────────

    static List<DisplayConfigEntry>? QueryDisplayConfigEntries()
    {
        uint numPaths = 0, numModes = 0;
        int hr = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, ref numPaths, ref numModes);
        if (hr != 0)
        {
            Console.WriteLine($"  GetDisplayConfigBufferSizes failed: 0x{hr:X8}");
            return null;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
        var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
        hr = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
        if (hr != 0)
        {
            Console.WriteLine($"  QueryDisplayConfig failed: 0x{hr:X8}");
            return null;
        }

        var results = new List<DisplayConfigEntry>();

        // Build a lookup by (adapterId.HighPart, adapterId.LowPart, sourceId) so source modes are
        // found by identity rather than by modeInfoIdx, which is unreliable across Windows versions.
        var sourceModesByAdapterAndId = new Dictionary<(int high, uint low, uint id), DISPLAYCONFIG_MODE_INFO>();
        for (int i = 0; i < numModes; i++)
        {
            if (modes[i].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.Source)
            {
                var key = (modes[i].adapterId.HighPart, modes[i].adapterId.LowPart, modes[i].id);
                sourceModesByAdapterAndId.TryAdd(key, modes[i]);
            }
        }

        for (int i = 0; i < numPaths; i++)
        {
            var path = paths[i];

            // Query source name
            string? sourceGdiName = null;
            var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            srcName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            srcName.header.adapterId = path.sourceInfo.adapterId;
            srcName.header.id = path.sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref srcName) == 0)
                sourceGdiName = srcName.viewGdiDeviceName;

            // Query target name
            string? friendlyName = null;
            string? devicePath = null;
            uint outputTech = path.targetInfo.outputTechnology;
            uint connectorInstance = 0;
            var tgtName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            tgtName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            tgtName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            tgtName.header.adapterId = path.targetInfo.adapterId;
            tgtName.header.id = path.targetInfo.id;
            if (DisplayConfigGetDeviceInfo(ref tgtName) == 0)
            {
                friendlyName = tgtName.monitorFriendlyDeviceName;
                devicePath   = tgtName.monitorDevicePath;
                outputTech   = tgtName.outputTechnology;
                connectorInstance = tgtName.connectorInstance;
            }

            // Resolve source mode position/resolution by matching adapterId + sourceId.
            int sourceX = 0, sourceY = 0;
            uint sourceW = 0, sourceH = 0;
            var srcKey = (path.sourceInfo.adapterId.HighPart, path.sourceInfo.adapterId.LowPart, path.sourceInfo.id);
            if (sourceModesByAdapterAndId.TryGetValue(srcKey, out var srcMode))
            {
                sourceX = srcMode.sourcePositionX;
                sourceY = srcMode.sourcePositionY;
                sourceW = srcMode.sourceWidth;
                sourceH = srcMode.sourceHeight;
            }

            results.Add(new DisplayConfigEntry(
                SourceAdapterId: path.sourceInfo.adapterId,
                SourceId: path.sourceInfo.id,
                TargetAdapterId: path.targetInfo.adapterId,
                TargetId: path.targetInfo.id,
                OutputTechnology: outputTech,
                ConnectorInstance: connectorInstance,
                SourceGdiDeviceName: sourceGdiName,
                FriendlyName: friendlyName,
                DevicePath: devicePath,
                SourcePositionX: sourceX,
                SourcePositionY: sourceY,
                SourceWidth: sourceW,
                SourceHeight: sourceH));
        }

        return results;
    }

    // ── Name helpers ─────────────────────────────────────────────────────────

    static string DpiAwarenessName(int v) => v switch
    {
        -1 => "Invalid",
        0  => "Unaware",
        1  => "SystemAware",
        2  => "PerMonitorAware",
        _  => $"Unknown({v})"
    };

    static string ProcDpiAwarenessName(int v) => v switch
    {
        0 => "Unaware",
        1 => "SystemAware",
        2 => "PerMonitorAware",
        _ => $"Unknown({v})"
    };

    static string OutputTechName(uint t)
    {
        if (t == 0xFFFFFFFF) return "Other/Unknown";
        bool isInternal = (t & 0x80000000) != 0;
        uint base_ = t & ~0x80000000u;
        // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL (0x80000000) has no underlying connector type.
        if (isInternal && base_ == 0) return "Internal";
        string baseName = base_ switch
        {
            0  => "HD15",
            1  => "S-Video",
            2  => "Composite",
            3  => "Component",
            4  => "DVI",
            5  => "HDMI",
            6  => "LVDS",
            8  => "D-JPN",
            9  => "SDI",
            10 => "DisplayPort-External",
            11 => "DisplayPort-Embedded",
            12 => "UDI-External",
            13 => "UDI-Embedded",
            14 => "SDTVDongle",
            15 => "Miracast",
            16 => "IndirectWired",
            17 => "IndirectVirtual",
            18 => "DisplayPort-USB-Tunnel",
            _ => $"0x{base_:X}"
        };
        return isInternal ? $"{baseName}|INTERNAL" : baseName;
    }

    // ── Win32 P/Invokes ───────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);
    const int SM_CMONITORS      = 80;
    const int SM_XVIRTUALSCREEN = 76;
    const int SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78;
    const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hMonitor, uint dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    static extern bool AreDpiAwarenessContextsEqual(IntPtr dpiContextA, IntPtr dpiContextB);

    [DllImport("shcore.dll")]
    static extern int GetProcessDpiAwareness(IntPtr hProcess, out int value);

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, ref uint numPathArrayElements, ref uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}

// ═══════════════════════════════════════════════════════════════════════════════
// COM: IPropertyStore
// ═══════════════════════════════════════════════════════════════════════════════

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
struct PROPERTYKEY { public Guid fmtid; public uint pid; }

[StructLayout(LayoutKind.Explicit, Size = 16)]
class PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr ptrVal;
    public string? GetString() =>
        vt == 31 && ptrVal != IntPtr.Zero ? Marshal.PtrToStringUni(ptrVal) : null;
    public void Clear()
    {
        if (vt == 31 && ptrVal != IntPtr.Zero) Marshal.FreeCoTaskMem(ptrVal);
        vt = 0; ptrVal = IntPtr.Zero;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Win32 structs for display topology
// ═══════════════════════════════════════════════════════════════════════════════

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int left, top, right, bottom; }

// MONITORINFOEX: cbSize must be set to sizeof(MONITORINFOEX) before calling GetMonitorInfo
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct MONITORINFOEX
{
    public uint cbSize;    // 4
    public RECT rcMonitor; // 16
    public RECT rcWork;    // 16
    public uint dwFlags;   // 4
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice; // CCHDEVICENAME = 32 WCHARs = 64 bytes
    // Total: 4 + 16 + 16 + 4 + 64 = 104 bytes
}

// LUID (Locally Unique Identifier used by DisplayConfig as adapter identifier)
[StructLayout(LayoutKind.Sequential)]
struct LUID { public uint LowPart; public int HighPart; }

// ── DisplayConfig path structs ───────────────────────────────────────────────

// DISPLAYCONFIG_PATH_SOURCE_INFO: 20 bytes
[StructLayout(LayoutKind.Sequential)]
struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID   adapterId;    // 8
    public uint   id;           // 4 — source ID within adapter
    public uint   modeInfoIdx;  // 4 — index into modeInfoArray (0xFFFFFFFF = not in use)
    public uint   statusFlags;  // 4
}

// DISPLAYCONFIG_PATH_TARGET_INFO: 48 bytes
[StructLayout(LayoutKind.Sequential)]
struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID   adapterId;            // 8
    public uint   id;                   // 4 — target ID within adapter
    public uint   modeInfoIdx;          // 4 — index into modeInfoArray
    public uint   outputTechnology;     // 4 — DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY
    public uint   rotation;             // 4 — DISPLAYCONFIG_ROTATION
    public uint   scaling;              // 4 — DISPLAYCONFIG_SCALING
    public uint   refreshRateNumerator; // 4 \
    public uint   refreshRateDenominator; // 4 / DISPLAYCONFIG_RATIONAL
    public uint   scanLineOrdering;     // 4 — DISPLAYCONFIG_SCANLINE_ORDERING
    public int    targetAvailable;      // 4 — BOOL
    public uint   statusFlags;          // 4
    // Total: 8+4+4+4+4+4+4+4+4+4+4 = 48
}

// DISPLAYCONFIG_PATH_INFO: 76 bytes
[StructLayout(LayoutKind.Sequential)]
struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; // 20
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; // 48
    public uint flags;                                // 4
    // Padding to 76 bytes total: 20 + 48 + 4 = 72 — need +4 for alignment? Let's check:
    // Actually 20+48+4 = 72. The actual struct size per SDK is 72 bytes. That's correct.
}

// ── DisplayConfig mode structs ───────────────────────────────────────────────

enum DISPLAYCONFIG_MODE_INFO_TYPE : uint { Target = 1, Source = 2, DesktopImage = 3 }

// DISPLAYCONFIG_MODE_INFO: 64 bytes total
// Contains a union: DISPLAYCONFIG_TARGET_MODE (48 bytes) | DISPLAYCONFIG_SOURCE_MODE (20 bytes) | DISPLAYCONFIG_DESKTOP_IMAGE_INFO (40 bytes)
// We expose the source mode fields (what we actually need) plus padding to fill the 48-byte union.
[StructLayout(LayoutKind.Explicit)]
struct DISPLAYCONFIG_MODE_INFO
{
    [FieldOffset(0)]  public DISPLAYCONFIG_MODE_INFO_TYPE infoType; // 4
    [FieldOffset(4)]  public uint   id;           // 4 — source or target ID
    [FieldOffset(8)]  public LUID   adapterId;    // 8
    // ── source mode fields (union, starts at offset 16) ──
    [FieldOffset(16)] public uint   sourceWidth;
    [FieldOffset(20)] public uint   sourceHeight;
    [FieldOffset(24)] public int    pixelFormat;
    [FieldOffset(28)] public int    sourcePositionX; // POINTL.x (virtual-screen origin)
    [FieldOffset(32)] public int    sourcePositionY; // POINTL.y
    // Padding to reach the full union size of 48 bytes (total struct = 16 + 48 = 64)
    [FieldOffset(60)] public uint   _pad; // ensures struct is at least 64 bytes
}

// ── DisplayConfig device info structs ────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint type;       // DISPLAYCONFIG_DEVICE_INFO_TYPE
    public uint size;       // sizeof the outer struct
    public LUID adapterId;
    public uint id;
}

// DISPLAYCONFIG_SOURCE_DEVICE_NAME — returned by DisplayConfigGetDeviceInfo(GET_SOURCE_NAME)
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] // CCHDEVICENAME
    public string viewGdiDeviceName; // e.g. "\\.\DISPLAY1"
}

// DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS (1 UINT32 bitfield)
[StructLayout(LayoutKind.Sequential)]
struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS { public uint value; }

// DISPLAYCONFIG_TARGET_DEVICE_NAME — returned by DisplayConfigGetDeviceInfo(GET_TARGET_NAME)
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER        header;
    public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS  flags;
    public uint                                    outputTechnology;    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY
    public ushort                                  edidManufactureId;
    public ushort                                  edidProductCodeId;
    public uint                                    connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;
}

// ═══════════════════════════════════════════════════════════════════════════════
// Data records
// ═══════════════════════════════════════════════════════════════════════════════

record WindowInfo(
    IntPtr Hwnd, string Title, bool IsForeground, string ClassName,
    uint Pid, string ProcessName, string? ExePath,
    string? PerWindowAumid, string? ProcessAumid, string? PackageFamilyName,
    string? RelaunchCommand, string? RelaunchDisplayName);

record EnumMonitorEntry(
    int EnumIdx, IntPtr HMonitor, string Device,
    RECT Bounds, RECT WorkArea, bool IsPrimary,
    uint EffectiveDpiX, uint EffectiveDpiY, int DpiHr);

record DisplayConfigEntry(
    LUID SourceAdapterId, uint SourceId,
    LUID TargetAdapterId, uint TargetId,
    uint OutputTechnology, uint ConnectorInstance,
    string? SourceGdiDeviceName, string? FriendlyName, string? DevicePath,
    int SourcePositionX, int SourcePositionY,
    uint SourceWidth, uint SourceHeight);
