using VoiceOS.Core.Candidates;
using Xunit;

namespace VoiceOS.Core.Tests.Candidates;

public class ProcessMetadataCacheTests
{
    // ── 1. Same PID across multiple calls resolves only once ────────────────────

    [Fact]
    public void GetProcessName_SamePid_SecondCallIsHit()
    {
        var accessor = new FakeProcessAccessor();
        accessor.AddProcess(42, "chrome");
        var cache = new ProcessMetadataCache(accessor);

        cache.GetProcessName(42, out var hit1);
        cache.GetProcessName(42, out var hit2);

        Assert.False(hit1);
        Assert.True(hit2);
        Assert.Equal(1, accessor.OpenCount); // handle opened once
    }

    // ── 2. Repeated snapshots reuse valid process metadata ──────────────────────

    [Fact]
    public void GetProcessName_RepeatedCalls_ReturnConsistentName()
    {
        var accessor = new FakeProcessAccessor();
        accessor.AddProcess(100, "notepad");
        var cache = new ProcessMetadataCache(accessor);

        var name1 = cache.GetProcessName(100, out _);
        var name2 = cache.GetProcessName(100, out _);
        var name3 = cache.GetProcessName(100, out _);

        Assert.Equal("notepad", name1);
        Assert.Equal("notepad", name2);
        Assert.Equal("notepad", name3);
    }

    // ── 3. A newly observed PID is resolved on first call ───────────────────────

    [Fact]
    public void GetProcessName_NewPid_IsResolvedAndReturned()
    {
        var accessor = new FakeProcessAccessor();
        accessor.AddProcess(200, "explorer");
        var cache = new ProcessMetadataCache(accessor);

        var name = cache.GetProcessName(200, out var hit);

        Assert.Equal("explorer", name);
        Assert.False(hit);
        Assert.Equal(1, cache.Count);
    }

    // ── 4. Dead process entries cannot corrupt identity ─────────────────────────

    [Fact]
    public void GetProcessName_AfterProcessExits_ReturnsEmptyAndEvicts()
    {
        var accessor = new FakeProcessAccessor();
        accessor.AddProcess(300, "old-process");
        var cache = new ProcessMetadataCache(accessor);

        var name1 = cache.GetProcessName(300, out _);
        Assert.Equal("old-process", name1);
        Assert.Equal(1, cache.Count);

        accessor.KillProcess(300);

        var name2 = cache.GetProcessName(300, out var hit2);
        Assert.Equal(string.Empty, name2);
        Assert.False(hit2);
        Assert.Equal(0, cache.Count);
    }

    // ── 5. PID reuse returns metadata for the new process, not the old one ──────

    [Fact]
    public void GetProcessName_PidReuse_ResolvesNewProcess()
    {
        var accessor = new FakeProcessAccessor();
        accessor.AddProcess(400, "old-app");
        var cache = new ProcessMetadataCache(accessor);

        var name1 = cache.GetProcessName(400, out _);
        Assert.Equal("old-app", name1);

        accessor.KillProcess(400);
        accessor.AddProcess(400, "new-app");

        var name2 = cache.GetProcessName(400, out var hit2);
        Assert.Equal("new-app", name2);
        Assert.False(hit2);
    }

    // ── 6. Multiple distinct PIDs are each resolved independently ───────────────

    [Fact]
    public void GetProcessName_MultiplePids_AllCachedAfterFirstPass()
    {
        var accessor = new FakeProcessAccessor();
        accessor.AddProcess(1, "chrome");
        accessor.AddProcess(2, "vscode");
        accessor.AddProcess(3, "discord");
        var cache = new ProcessMetadataCache(accessor);

        Assert.Equal("chrome",  cache.GetProcessName(1, out _));
        Assert.Equal("vscode",  cache.GetProcessName(2, out _));
        Assert.Equal("discord", cache.GetProcessName(3, out _));
        Assert.Equal(3, cache.Count);

        cache.GetProcessName(1, out var h1);
        cache.GetProcessName(2, out var h2);
        cache.GetProcessName(3, out var h3);

        Assert.True(h1);
        Assert.True(h2);
        Assert.True(h3);
        Assert.Equal(3, accessor.OpenCount); // no extra opens on second pass
    }

    // ── 7. Unknown PID returns empty without poisoning the cache ────────────────

    [Fact]
    public void GetProcessName_UnknownPid_ReturnsEmpty_NothingCached()
    {
        var accessor = new FakeProcessAccessor(); // no processes registered
        var cache = new ProcessMetadataCache(accessor);

        var name = cache.GetProcessName(999, out var hit);

        Assert.Equal(string.Empty, name);
        Assert.False(hit);
        Assert.Equal(0, cache.Count);
    }

    // ── Fake accessor ────────────────────────────────────────────────────────────

    private sealed class FakeProcessAccessor : IProcessAccessor
    {
        private readonly Dictionary<int, FakeProcess> _byPid = new();
        private readonly Dictionary<nint, FakeProcess> _byHandle = new();
        private nint _nextHandle = 1;

        public int OpenCount { get; private set; }

        public void AddProcess(int pid, string name)
        {
            nint handle = _nextHandle++;
            var p = new FakeProcess(name, handle, alive: true);
            _byPid[pid] = p;
            _byHandle[handle] = p;
        }

        public void KillProcess(int pid)
        {
            if (_byPid.TryGetValue(pid, out var p))
            {
                p.Alive = false;
                _byPid.Remove(pid);
                // Keep handle in _byHandle so HasExited returns true for it
            }
        }

        public nint OpenProcess(int pid)
        {
            OpenCount++;
            return _byPid.TryGetValue(pid, out var p) && p.Alive ? p.Handle : 0;
        }

        public bool TryGetProcessName(nint handle, out string name)
        {
            if (_byHandle.TryGetValue(handle, out var p) && p.Alive)
            {
                name = p.Name;
                return true;
            }
            name = string.Empty;
            return false;
        }

        public bool HasExited(nint handle)
            => !(_byHandle.TryGetValue(handle, out var p) && p.Alive);

        public void CloseHandle(nint handle) { /* no-op */ }

        private sealed class FakeProcess(string name, nint handle, bool alive)
        {
            public string Name { get; } = name;
            public nint Handle { get; } = handle;
            public bool Alive { get; set; } = alive;
        }
    }
}
