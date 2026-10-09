using System.Collections.Concurrent;
using System.Text;
using Trayify.Native;

namespace Trayify.Core;

/// <summary>Fast, cached pid -> executable lookups (used from the low-level hook thread).</summary>
public static class ProcessInfo
{
    private sealed record Entry(string Path, string Name, long StartTime, long Expires);
    private static readonly ConcurrentDictionary<uint, Entry> Cache = new();

    public static string? GetExePath(uint pid) => Get(pid)?.Path;
    public static string? GetExeName(uint pid) => Get(pid)?.Name;
    public static long GetStartTime(uint pid) => Get(pid)?.StartTime ?? 0;

    private static Entry? Get(uint pid)
    {
        if (pid == 0) return null;
        long now = Environment.TickCount64;
        if (Cache.TryGetValue(pid, out var e) && e.Expires > now) return e;
        var path = QueryPath(pid, out long start);
        if (path == null) { Cache.TryRemove(pid, out _); return null; }
        e = new Entry(path, System.IO.Path.GetFileName(path).ToLowerInvariant(), start, now + 15_000);
        Cache[pid] = e;
        if (Cache.Count > 512) Cache.Clear();
        return e;
    }

    public static string? QueryPath(uint pid, out long startTime)
    {
        startTime = 0;
        var h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (N.GetProcessTimes(h, out long c, out _, out _, out _)) startTime = c;
            return N.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { N.CloseHandle(h); }
    }

    /// <summary>Process creation time (FILETIME ticks) without the cache, for validating persisted PIDs.</summary>
    public static long QueryStartTime(uint pid)
    {
        QueryPath(pid, out long start);
        return start;
    }
}
