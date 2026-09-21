using System.Runtime.InteropServices;
using System.Text;

namespace VoiceOS.Core.Windows;

/// <summary>
/// Encapsulates Windows property-store COM interop for AUMID retrieval.
/// All methods swallow failures and return null — callers must tolerate absent identity data.
/// </summary>
public static class WindowPropertyStore
{
    // PKEY_AppUserModel_ID: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid=5
    private static readonly PROPERTYKEY PkeyAumid =
        new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    /// <summary>
    /// Returns the per-window AppUserModelID for <paramref name="hwnd"/>, or null if unavailable.
    /// Uses SHGetPropertyStoreForWindow — reads the window-level property (not the process AUMID).
    /// </summary>
    public static string? GetPerWindowAumid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var storeObj);
            if (hr != 0 || storeObj is not IPropertyStore ps) return null;
            try { return GetStringProp(ps, PkeyAumid); }
            finally { Marshal.ReleaseComObject(ps); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the AppUserModelID stored in the .lnk shortcut file's property store, or null if absent.
    /// Uses SHGetPropertyStoreFromParsingName with GPS_DEFAULT (reads the file's own property store).
    /// </summary>
    public static string? GetShortcutAumid(string lnkPath)
    {
        if (string.IsNullOrEmpty(lnkPath)) return null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0, ref iid, out var storeObj);
            if (hr != 0 || storeObj is not IPropertyStore ps) return null;
            try { return GetStringProp(ps, PkeyAumid); }
            finally { Marshal.ReleaseComObject(ps); }
        }
        catch { return null; }
    }

    private static string? GetStringProp(IPropertyStore store, PROPERTYKEY key)
    {
        try
        {
            var pv = new PropVariant();
            if (store.GetValue(ref key, pv) != 0) return null;
            var result = pv.GetString();
            pv.Clear();
            return result;
        }
        catch { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath,
        IntPtr pbc,
        int flags,
        ref Guid riid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    // IPropertyStore {886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, [In, Out] PropVariant pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, PropVariant propvar);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // Minimal PROPVARIANT — only VT_LPWSTR (31) strings needed for AUMID.
    // Reference type (class) so COM can pass it by pointer.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private class PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptrVal;

        public string? GetString() =>
            vt == 31 && ptrVal != IntPtr.Zero ? Marshal.PtrToStringUni(ptrVal) : null;

        public void Clear()
        {
            if (vt == 31 && ptrVal != IntPtr.Zero)
                Marshal.FreeCoTaskMem(ptrVal);
            vt = 0;
            ptrVal = IntPtr.Zero;
        }
    }
}
