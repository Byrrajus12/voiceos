using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Windows;

/// <summary>
/// Captures the active display topology via Win32 (EnumDisplayMonitors + QueryDisplayConfig)
/// and resolves HWND → current monitor via MonitorFromWindow.
/// </summary>
public sealed class DisplayTopologyService : IDisplayTopologyService
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint MONITOR_DEFAULTTONULL = 0;

    // Output technology constants (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY)
    private const uint OT_INTERNAL_FLAG     = 0x80000000; // set on any internal/embedded connector
    private const uint OT_LVDS              = 6;
    private const uint OT_DISPLAYPORT_EMB   = 11;
    private const uint OT_UDI_EMB           = 13;

    private readonly ILogger<DisplayTopologyService> _logger;

    public DisplayTopologyService(ILogger<DisplayTopologyService> logger) => _logger = logger;

    public DisplayTopology CaptureTopology()
    {
        var baseEntries = EnumerateBaseMonitors();
        var dcMap = BuildDisplayConfigMap();

        var monitors = new List<MonitorInfo>(baseEntries.Count);
        foreach (var (hMonitor, device, bounds, workArea, isPrimary) in baseEntries)
        {
            string? devicePath = null;
            string? friendlyName = null;
            var connectionKind = DisplayConnectionKind.Unknown;

            if (dcMap.TryGetValue(device, out var dc))
            {
                devicePath     = dc.DevicePath;
                friendlyName   = string.IsNullOrEmpty(dc.FriendlyName) ? null : dc.FriendlyName;
                connectionKind = dc.OutputTechnology.HasValue
                    ? ClassifyConnectionKind(dc.OutputTechnology.Value)
                    : DisplayConnectionKind.Unknown;
            }
            else
            {
                _logger.LogDebug("No DisplayConfig entry for {Device}; connection classification unavailable", device);
            }

            monitors.Add(new MonitorInfo(
                HMonitor: hMonitor,
                Bounds: bounds,
                WorkArea: workArea,
                IsPrimary: isPrimary,
                GdiDeviceName: device,
                DevicePath: devicePath,
                FriendlyName: friendlyName,
                ConnectionKind: connectionKind));
        }

        return new DisplayTopology(monitors);
    }

    public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topology)
    {
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL);
        if (hMonitor == IntPtr.Zero) return null;

        return topology.Monitors.FirstOrDefault(m => m.HMonitor == hMonitor);
    }

    public MonitorInfo? GetForegroundMonitor(DisplayTopology topology)
    {
        var hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : GetCurrentMonitor(hwnd, topology);
    }

    // ── Base monitor enumeration ─────────────────────────────────────────────

    private List<(nint HMonitor, string Device, MonitorBounds Bounds, MonitorBounds WorkArea, bool IsPrimary)>
        EnumerateBaseMonitors()
    {
        var result = new List<(nint, string, MonitorBounds, MonitorBounds, bool)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                result.Add((
                    hMon,
                    mi.szDevice,
                    ToMonitorBounds(mi.rcMonitor),
                    ToMonitorBounds(mi.rcWork),
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // ── DisplayConfig enrichment ─────────────────────────────────────────────

    // ── Connection classification ────────────────────────────────────────────

    /// <summary>
    /// Classifies a DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY value.
    /// Internal: technologies documented by Windows as integrated/embedded.
    /// External: technologies that are explicitly discrete/external connectors.
    /// Unknown: unrecognized values (no safe assumption).
    /// </summary>
    private static DisplayConnectionKind ClassifyConnectionKind(uint outputTechnology)
    {
        // INTERNAL flag bit or embedded connector types → Internal
        if ((outputTechnology & OT_INTERNAL_FLAG) != 0) return DisplayConnectionKind.Internal;
        uint base_ = outputTechnology & ~OT_INTERNAL_FLAG;
        return base_ switch
        {
            OT_LVDS            => DisplayConnectionKind.Internal,  // Low-voltage differential signaling (internal panel)
            OT_DISPLAYPORT_EMB => DisplayConnectionKind.Internal,  // DisplayPort embedded
            OT_UDI_EMB         => DisplayConnectionKind.Internal,  // UDI embedded
            0  => DisplayConnectionKind.External,  // HD15/VGA
            1  => DisplayConnectionKind.External,  // S-Video
            2  => DisplayConnectionKind.External,  // Composite
            3  => DisplayConnectionKind.External,  // Component
            4  => DisplayConnectionKind.External,  // DVI
            5  => DisplayConnectionKind.External,  // HDMI
            8  => DisplayConnectionKind.External,  // D-JPN
            9  => DisplayConnectionKind.External,  // SDI
            10 => DisplayConnectionKind.External,  // DisplayPort external
            12 => DisplayConnectionKind.External,  // UDI external
            14 => DisplayConnectionKind.External,  // SDTVDongle
            15 => DisplayConnectionKind.External,  // Miracast
            18 => DisplayConnectionKind.External,  // DisplayPort USB tunnel
            _  => DisplayConnectionKind.Unknown
        };
    }

    private Dictionary<string, (string? DevicePath, string? FriendlyName, uint? OutputTechnology)>
        BuildDisplayConfigMap()
    {
        var result = new Dictionary<string, (string?, string?, uint?)>(StringComparer.OrdinalIgnoreCase);

        uint numPaths = 0, numModes = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, ref numPaths, ref numModes) != 0)
        {
            _logger.LogWarning("GetDisplayConfigBufferSizes failed; DisplayConfig enrichment skipped");
            return result;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
        var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0)
        {
            _logger.LogWarning("QueryDisplayConfig failed; DisplayConfig enrichment skipped");
            return result;
        }

        for (int i = 0; i < numPaths; i++)
        {
            var path = paths[i];

            var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type       = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size       = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId  = path.sourceInfo.adapterId,
                    id         = path.sourceInfo.id
                }
            };
            if (DisplayConfigGetDeviceInfo(ref srcName) != 0) continue;

            string gdiName = srcName.viewGdiDeviceName;
            if (string.IsNullOrEmpty(gdiName)) continue;

            var tgtName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type      = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id        = path.targetInfo.id
                }
            };

            string? friendlyName = null;
            string? devicePath   = null;
            uint? outputTech     = null;

            if (DisplayConfigGetDeviceInfo(ref tgtName) == 0)
            {
                outputTech   = tgtName.outputTechnology;
                friendlyName = tgtName.monitorFriendlyDeviceName;
                devicePath   = tgtName.monitorDevicePath;
            }

            result[gdiName] = (devicePath, friendlyName, outputTech);
        }

        return result;
    }

    private static MonitorBounds ToMonitorBounds(RECT r) => new(r.left, r.top, r.right, r.bottom);

    // ── Win32 P/Invokes ──────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, ref uint numPathArrayElements, ref uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    // ── Win32 structs ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID   adapterId;
        public uint   id;
        public uint   modeInfoIdx;
        public uint   outputTechnology;
        public uint   rotation;
        public uint   scaling;
        public uint   refreshRateNumerator;
        public uint   refreshRateDenominator;
        public uint   scanLineOrdering;
        public int    targetAvailable;
        public uint   statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    private enum DISPLAYCONFIG_MODE_INFO_TYPE : uint { Target = 1, Source = 2, DesktopImage = 3 }

    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)]  public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
        [FieldOffset(4)]  public uint id;
        [FieldOffset(8)]  public LUID adapterId;
        [FieldOffset(16)] public uint sourceWidth;
        [FieldOffset(20)] public uint sourceHeight;
        [FieldOffset(24)] public int  pixelFormat;
        [FieldOffset(28)] public int  sourcePositionX;
        [FieldOffset(32)] public int  sourcePositionY;
        [FieldOffset(60)] public uint _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS { public uint value; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER       header;
        public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS flags;
        public uint   outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint   connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }
}
