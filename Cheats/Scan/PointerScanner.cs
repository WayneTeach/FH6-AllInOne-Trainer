using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FH6Mod.Cheats.RuntimeHook;

namespace FH6Mod.Cheats.Scan;

/// <summary>
/// A pointer chain from a static (module-relative) root to a live address.
/// Resolve: p = MainBase + RootOffset; for each o in Offsets: p = read(p) + o; return p.
/// Stored as module-relative offsets, so it survives ASLR relaunches as long as the
/// chain layout is unchanged.
/// </summary>
public sealed class PointerChain
{
    public long RootOffset = 0;
    public int[] Offsets = Array.Empty<int>();
    public string Label = "";
    public DateTime SavedUtc;

    public ulong? Resolve(RuntimeHookEngine e)
    {
        if (e.MainBase == 0) return null;
        ulong p = e.MainBase + (ulong)RootOffset;
        for (int i = 0; i < Offsets.Length; i++)
        {
            var v = e.ReadUInt64Public(p);
            if (v == 0) return null;
            p = v + (ulong)(long)Offsets[i];
            if (p == 0) return null;
        }
        return p;
    }

    public override string ToString()
    {
        var s = $"[FH6+0x{RootOffset:X}]";
        foreach (var o in Offsets) s += $"+0x{o:X}";
        return s;
    }
}

/// <summary>
/// Discovers a static-rooted pointer chain to a known live address (the address found
/// by the value scanner). This is what turns a one-time found address into a permanent,
/// ASLR-safe, one-click address: find the value once, run this, save the chain, and the
/// trainer resolves it on every future launch without re-scanning.
///
/// Search runs bottom-up (from the value address toward a static root) in breadth-first
/// waves, one full memory scan per depth level. At each level we look for memory
/// locations whose stored 8-byte value points at (or just below) a frontier address,
/// allowing a small struct-field offset window.
/// </summary>
public sealed class PointerScanner
{
    // Allowed offset between a pointer and the address it must reach. Covers pointing
    // directly (0) and to an object whose field sits at +8 ... +0x48 (wheelspins is +8,
    // skill points +0x40, etc.).
    private static readonly int[] OffWindow = { 0, 8, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48 };
    private const ulong ChunkSize = 0x100000; // 1 MiB read granularity
    private const int MaxFrontier = 512;
    private const int MaxMatchesPerDepth = 200_000;

    private readonly RuntimeHookEngine _engine;
    private readonly byte[] _chunk = new byte[ChunkSize];

    public PointerScanner(RuntimeHookEngine e) => _engine = e;

    public List<PointerChain> FindChains(ulong target, int maxDepth, int maxResults, Action<string>? progress)
    {
        var results = new List<PointerChain>();
        if (!_engine.IsAttached || _engine.MainBase == 0 || target == 0) return results;

        // frontier node: (searchAddr, offsets) where a static pointer to searchAddr,
        // followed by offsets (resolution order), reaches target.
        var frontier = new List<(ulong Addr, int[] Offs)> { (target, Array.Empty<int>()) };

        for (int depth = 0; depth < maxDepth && results.Count < maxResults; depth++)
        {
            progress?.Invoke($"Pointer scan depth {depth + 1}/{maxDepth}: {frontier.Count} candidate(s), {results.Count} chain(s) found");

            var searchMap = new Dictionary<ulong, List<int[]>>();
            foreach (var (addr, offs) in frontier)
            {
                if (!searchMap.TryGetValue(addr, out var list)) { list = new List<int[]>(); searchMap[addr] = list; }
                list.Add(offs);
            }

            var nextFrontier = new List<(ulong, int[])>();
            int matches = 0;
            foreach (var (loc, matchedAddr, off) in ScanPointersInto(searchMap))
            {
                if (matches++ >= MaxMatchesPerDepth) break;
                foreach (var offs in searchMap[matchedAddr])
                {
                    var newOffs = Prepend(off, offs);
                    if (IsStatic(loc))
                    {
                        var chain = new PointerChain
                        {
                            RootOffset = (long)loc - (long)_engine.MainBase,
                            Offsets = newOffs,
                            Label = $"auto depth {depth + 1}",
                        };
                        if (chain.Resolve(_engine) == target)
                        {
                            results.Add(chain);
                            if (results.Count >= maxResults) return results;
                        }
                    }
                    else if (nextFrontier.Count < MaxFrontier * 4)
                    {
                        nextFrontier.Add((loc, newOffs));
                    }
                }
            }

            // cap frontier breadth to keep the next scan tractable
            frontier = nextFrontier.Count > MaxFrontier ? nextFrontier.GetRange(0, MaxFrontier) : nextFrontier;
            if (frontier.Count == 0) break;
        }
        return results;
    }

    // Scan all committed readable memory once. Yield (location, matchedAddr, off) for each
    // 8-byte value V where (V + off) is a frontier search address for some off in the window.
    private IEnumerable<(ulong Loc, ulong MatchedAddr, int Off)> ScanPointersInto(Dictionary<ulong, List<int[]>> searchMap)
    {
        var handle = _engine.HandlePublic;
        var mbiSize = (UIntPtr)Marshal.SizeOf<Native.MemoryBasicInformation64>();
        ulong addr = 0;
        while (Native.VirtualQueryEx(handle, (UIntPtr)addr, out var mbi, mbiSize) != UIntPtr.Zero)
        {
            ulong next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= mbi.BaseAddress) break;
            if (mbi.State == Native.MEM_COMMIT && Native.IsReadable(mbi.Protect))
            {
                foreach (var hit in ScanRegion(handle, mbi.BaseAddress, mbi.RegionSize, searchMap))
                    yield return hit;
            }
            addr = next;
        }
    }

    private IEnumerable<(ulong, ulong, int)> ScanRegion(IntPtr handle, ulong regionBase, ulong regionSize, Dictionary<ulong, List<int[]>> searchMap)
    {
        for (ulong off = 0; off < regionSize; off += ChunkSize)
        {
            ulong a = regionBase + off;
            int toRead = (int)Math.Min(ChunkSize, regionSize - off);
            if (!Native.ReadProcessMemory(handle, new IntPtr((long)a), _chunk, (UIntPtr)toRead, out var read))
                continue;
            int got = (int)(ulong)read;
            // 8-byte aligned sweep
            for (int i = 0; i + 8 <= got; i += 8)
            {
                ulong v = BitConverter.ToUInt64(_chunk, i);
                if (v == 0) continue;
                foreach (var k in OffWindow)
                {
                    var key = v + (ulong)k;
                    if (searchMap.ContainsKey(key))
                        yield return (a + (ulong)i, key, k);
                }
            }
        }
    }

    private bool IsStatic(ulong addr)
        => _engine.MainBase != 0 && addr >= _engine.MainBase && addr < _engine.MainBase + (ulong)_engine.MainSize;

    private static int[] Prepend(int v, int[] rest)
    {
        var r = new int[rest.Length + 1];
        r[0] = v;
        Array.Copy(rest, 0, r, 1, rest.Length);
        return r;
    }
}
