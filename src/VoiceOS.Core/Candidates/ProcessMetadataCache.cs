using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VoiceOS.Core.Candidates;

/// <summary>
/// Thread-safe PID → process identity cache: image name plus, for packaged processes, the
/// application identity (AUMID) Windows assigned to the process.
///
/// Cache miss: opens the process handle via QueryFullProcessImageName — O(1), no full
/// process-list scan, no module enumeration.
/// Cache hit: checks HasExited via the stored handle (WaitForSingleObject 0ms timeout) —
/// no new OS call needed for the common case where the process is still running.
///
/// PID reuse safety: when the stored handle signals (process exited), the entry is evicted.
/// The recycled PID then falls through to a fresh resolve, so old metadata can never
/// silently attach to a new process.
/// </summary>
internal sealed class ProcessMetadataCache : IDisposable
{
    private readonly record struct CacheEntry(string ProcessName, string? PackageAumid, nint Handle);

    private readonly ConcurrentDictionary<int, CacheEntry> _entries = new();
    private readonly IProcessAccessor _accessor;

    internal ProcessMetadataCache() : this(Win32ProcessAccessor.Instance) { }
    internal ProcessMetadataCache(IProcessAccessor accessor) => _accessor = accessor;

    /// <summary>
    /// Returns the ProcessName for <paramref name="pid"/>, using the cache when valid.
    /// Sets <paramref name="cacheHit"/> to indicate whether the cached value was used.
    /// </summary>
    public string GetProcessName(int pid, out bool cacheHit)
        => GetProcessIdentity(pid, out cacheHit).ProcessName;

    /// <summary>
    /// Returns the image name and package AUMID for <paramref name="pid"/>; an unpackaged process
    /// or a failed lookup has a null AUMID. Same caching and PID-reuse rules as the name.
    /// </summary>
    public (string ProcessName, string? PackageAumid) GetProcessIdentity(int pid, out bool cacheHit)
    {
        if (_entries.TryGetValue(pid, out var entry))
        {
            if (!_accessor.HasExited(entry.Handle))
            {
                cacheHit = true;
                return (entry.ProcessName, entry.PackageAumid);
            }

            // Process exited — evict and release handle. Next call will re-resolve.
            // This also handles PID reuse: the old handle signals when the old process
            // exits, so any new process that inherits the PID starts fresh.
            if (_entries.TryRemove(pid, out var stale))
                _accessor.CloseHandle(stale.Handle);
        }

        cacheHit = false;
        nint handle = _accessor.OpenProcess(pid);
        if (handle == 0)
            return (string.Empty, null);

        if (!_accessor.TryGetProcessName(handle, out var name))
        {
            _accessor.CloseHandle(handle);
            return (string.Empty, null);
        }

        var newEntry = new CacheEntry(name, _accessor.GetPackageAumid(handle), handle);
        if (_entries.TryAdd(pid, newEntry))
            return (name, newEntry.PackageAumid);

        // Another thread resolved concurrently — close our handle and use theirs.
        _accessor.CloseHandle(handle);
        return _entries.TryGetValue(pid, out var winner)
            ? (winner.ProcessName, winner.PackageAumid)
            : (name, newEntry.PackageAumid);
    }

    /// <summary>Current entry count — for tests.</summary>
    internal int Count => _entries.Count;

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            _accessor.CloseHandle(entry.Handle);
        _entries.Clear();
    }
}

// ── Process accessor abstraction ─────────────────────────────────────────────────────

internal interface IProcessAccessor
{
    /// <summary>Opens a process handle sufficient for name and liveness queries, or 0 on failure.</summary>
    nint OpenProcess(int pid);

    /// <summary>Fills <paramref name="name"/> with the process image name (no extension). Returns false on failure.</summary>
    bool TryGetProcessName(nint handle, out string name);

    /// <summary>The application identity (AUMID) of a packaged process, or null when unpackaged.</summary>
    string? GetPackageAumid(nint handle);

    /// <summary>Returns true if the process behind <paramref name="handle"/> has exited.</summary>
    bool HasExited(nint handle);

    void CloseHandle(nint handle);
}

// ── Win32 production implementation ──────────────────────────────────────────────────

internal sealed class Win32ProcessAccessor : IProcessAccessor
{
    public static readonly Win32ProcessAccessor Instance = new();

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint WAIT_OBJECT_0 = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public nint OpenProcess(int pid)
    {
        nint h = NativeOpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        return h;
    }

    public bool TryGetProcessName(nint handle, out string name)
    {
        name = string.Empty;
        var buf = new char[1024];
        uint size = (uint)buf.Length;
        if (!QueryFullProcessImageName(handle, 0, buf, ref size) || size == 0)
            return false;
        name = Path.GetFileNameWithoutExtension(new string(buf, 0, (int)size));
        return name.Length > 0;
    }

    public string? GetPackageAumid(nint handle)
    {
        try
        {
            uint length = 0;
            // Unpackaged processes fail with APPMODEL_ERROR_NO_APPLICATION before sizing.
            if (GetApplicationUserModelId(handle, ref length, null) != ERROR_INSUFFICIENT_BUFFER || length == 0)
                return null;
            var buffer = new char[length];
            return GetApplicationUserModelId(handle, ref length, buffer) == 0
                ? new string(buffer, 0, (int)length - 1)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool HasExited(nint handle)
    {
        try
        {
            return WaitForSingleObject(handle, 0) == WAIT_OBJECT_0;
        }
        catch
        {
            return true;
        }
    }

    public void CloseHandle(nint handle)
    {
        if (handle != 0)
            NativeCloseHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern nint NativeOpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char[] lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint hProcess, ref uint applicationUserModelIdLength,
        char[]? applicationUserModelId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool NativeCloseHandle(nint hObject);
}
