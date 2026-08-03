using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FH6Mod.Cheats.RuntimeHook;

namespace FH6Mod.Cheats.Scan;

/// <summary>
/// Crash-free value finder and setter. Locates an in-game number by scanning the
/// target process's private writable memory (Cheat Engine style), then writes a
/// new plaintext value directly to the found address.
///
/// This never touches the game's code section, so it cannot trigger the integrity
/// scan that kills hook-based cheats. Reading is ReadProcessMemory (read-only);
/// writing is WriteProcessMemory to heap data, the same class of operation the SQL
/// cheats have always done safely. See the wheelspins getter decompilation: profile
/// values live as plaintext int32 at object+0x8, so they are findable and writable.
/// </summary>
public sealed class MemoryScanner
{
    private const ulong ChunkSize = 0x10000; // 64 KiB read granularity
    private const int ValueSize = 4;         // int32

    private readonly RuntimeHookEngine _engine;
    private readonly byte[] _chunk = new byte[ChunkSize];
    private readonly byte[] _single = new byte[ValueSize];

    // Current candidate addresses (narrowed across successive scans).
    private readonly List<ulong> _addresses = new();
    // Value at each address at the end of the last scan, for increased/decreased/changed.
    private readonly Dictionary<ulong, int> _snapshot = new();

    // Periodic re-write of the locked value to the snapshot of addresses captured at
    // lock-on. Replaces the old hook-based "force this value" behavior, crash-free.
    private CancellationTokenSource? _lockCts;
    private ulong[]? _lockAddresses;
    private int _lockValue;

    public int MatchCount => _addresses.Count;
    public IReadOnlyList<ulong> Addresses => _addresses;
    public bool HasResults => _addresses.Count > 0;
    public bool IsLockActive => _lockCts != null;

    public MemoryScanner(RuntimeHookEngine engine) => _engine = engine;

    private IntPtr Handle => _engine.HandlePublic;

    // ---------------------------------------------------------------------
    // Scan operations. Each returns the new match count and refreshes the
    // snapshot used by the comparative (increased/decreased/changed) filters.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Brand new scan: find every 4-byte-aligned int32 equal to <paramref name="value"/>
    /// across all private committed writable regions. Clears any prior results.
    /// </summary>
    public int FirstScan(int value, Action<string>? onProgress = null)
    {
        if (!Ready()) { Reset(); return 0; }
        Reset();
        var needle = BitConverter.GetBytes(value);
        ulong regionIndex = 0;
        foreach (var (baseAddr, size) in EnumerateRegions())
        {
            ScanRegionExact(baseAddr, size, needle);
            if ((++regionIndex & 0x3F) == 0) onProgress?.Invoke($"scanning... {MatchCount} matches");
        }
        Snapshot();
        return _addresses.Count;
    }

    /// <summary>Keep only candidates whose current value still equals <paramref name="value"/>.</summary>
    public int NextScanExact(int value)
    {
        if (!Ready()) return 0;
        var needle = BitConverter.GetBytes(value);
        Filter(addr => ReadFour(addr, _single) && EqualsFour(_single, needle));
        Snapshot();
        return _addresses.Count;
    }

    /// <summary>Keep only candidates whose value rose since the last scan.</summary>
    public int NextScanIncreased()
    {
        if (!Ready()) return 0;
        Filter(addr => _snapshot.TryGetValue(addr, out var old) && ReadInt(addr) is var cur && cur > old);
        Snapshot();
        return _addresses.Count;
    }

    /// <summary>Keep only candidates whose value fell since the last scan.</summary>
    public int NextScanDecreased()
    {
        if (!Ready()) return 0;
        Filter(addr => _snapshot.TryGetValue(addr, out var old) && ReadInt(addr) is var cur && cur < old);
        Snapshot();
        return _addresses.Count;
    }

    /// <summary>Keep only candidates whose value differs from the last scan.</summary>
    public int NextScanChanged()
    {
        if (!Ready()) return 0;
        Filter(addr => _snapshot.TryGetValue(addr, out var old) && ReadInt(addr) != old);
        Snapshot();
        return _addresses.Count;
    }

    /// <summary>Keep only candidates whose value is unchanged since the last scan.</summary>
    public int NextScanUnchanged()
    {
        if (!Ready()) return 0;
        Filter(addr => _snapshot.TryGetValue(addr, out var old) && ReadInt(addr) == old);
        Snapshot();
        return _addresses.Count;
    }

    public void Reset()
    {
        StopLock();
        _addresses.Clear();
        _snapshot.Clear();
    }

    /// <summary>
    /// Re-write <paramref name="value"/> to the addresses captured at lock-on every
    /// <paramref name="periodSec"/> seconds. Snapshots the current candidates so a later
    /// scan does not disturb an active lock.
    /// </summary>
    public bool StartLock(int value, int periodSec)
    {
        if (!Ready() || _addresses.Count == 0) return false;
        StopLock();
        _lockAddresses = _addresses.ToArray();
        _lockValue = value;
        var data = BitConverter.GetBytes(value);
        var cts = new CancellationTokenSource();
        _lockCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(Math.Max(1, periodSec) * 1000, cts.Token); }
                catch (OperationCanceledException) { return; }
                if (!_engine.IsAttached || _lockAddresses == null) continue;
                foreach (var a in _lockAddresses)
                    Native.WriteProcessMemory(Handle, new IntPtr((long)a), data, (UIntPtr)ValueSize, out _);
            }
        }, cts.Token);
        return true;
    }

    public void StopLock()
    {
        var cts = _lockCts;
        _lockCts = null;
        _lockAddresses = null;
        if (cts != null) { cts.Cancel(); cts.Dispose(); }
    }

    // ---------------------------------------------------------------------
    // Writes
    // ---------------------------------------------------------------------

    /// <summary>
    /// Write <paramref name="value"/> to every current candidate. Narrow first via
    /// NextScan so you are not overwriting unrelated memory. Returns addresses written.
    /// </summary>
    public int WriteAll(int value)
    {
        if (!Ready() || _addresses.Count == 0) return 0;
        var data = BitConverter.GetBytes(value);
        var written = new List<ulong>(_addresses.Count);
        foreach (var addr in _addresses)
        {
            if (Native.WriteProcessMemory(Handle, new IntPtr((long)addr), data, (UIntPtr)ValueSize, out _))
                written.Add(addr);
        }
        Snapshot();
        return written.Count;
    }

    /// <summary>Write <paramref name="value"/> to a single explicit address (no scan needed).</summary>
    public bool Write(ulong address, int value)
    {
        if (!Ready()) return false;
        var data = BitConverter.GetBytes(value);
        return Native.WriteProcessMemory(Handle, new IntPtr((long)address), data, (UIntPtr)ValueSize, out _);
    }

    public int Read(ulong address)
    {
        if (!Ready()) return 0;
        return ReadInt(address);
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private bool Ready() => _engine.IsAttached && Handle != IntPtr.Zero;

    private IEnumerable<(ulong Base, ulong Size)> EnumerateRegions()
    {
        var mbiSize = (UIntPtr)Marshal.SizeOf<Native.MemoryBasicInformation64>();
        ulong addr = 0;
        while (Native.VirtualQueryEx(Handle, (UIntPtr)addr, out var mbi, mbiSize) != UIntPtr.Zero)
        {
            ulong next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= mbi.BaseAddress) break; // overflow / done
            if (mbi.State == Native.MEM_COMMIT &&
                mbi.Type == Native.MEM_PRIVATE &&
                Native.IsWritable(mbi.Protect))
            {
                yield return (mbi.BaseAddress, mbi.RegionSize);
            }
            addr = next;
        }
    }

    private void ScanRegionExact(ulong regionBase, ulong regionSize, byte[] needle)
    {
        for (ulong off = 0; off < regionSize; off += ChunkSize)
        {
            ulong addr = regionBase + off;
            int toRead = (int)Math.Min(ChunkSize, regionSize - off);
            if (!Native.ReadProcessMemory(Handle, new IntPtr((long)addr), _chunk, (UIntPtr)toRead, out var read))
                continue;
            int got = (int)(ulong)read;
            // 4-byte aligned sweep
            for (int i = 0; i + ValueSize <= got; i += ValueSize)
            {
                if (_chunk[i] == needle[0] && _chunk[i + 1] == needle[1] &&
                    _chunk[i + 2] == needle[2] && _chunk[i + 3] == needle[3])
                {
                    _addresses.Add(addr + (ulong)i);
                }
            }
        }
    }

    private void Filter(Func<ulong, bool> predicate)
    {
        var keep = new List<ulong>(_addresses.Count);
        foreach (var addr in _addresses)
            if (predicate(addr)) keep.Add(addr);
        _addresses.Clear();
        _addresses.AddRange(keep);
    }

    private void Snapshot()
    {
        _snapshot.Clear();
        foreach (var addr in _addresses)
            _snapshot[addr] = ReadInt(addr);
    }

    private bool ReadFour(ulong addr, byte[] buf)
        => Native.ReadProcessMemory(Handle, new IntPtr((long)addr), buf, (UIntPtr)ValueSize, out var r) && (ulong)r == ValueSize;

    private static bool EqualsFour(byte[] a, byte[] b)
        => a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];

    private int ReadInt(ulong addr)
        => ReadFour(addr, _single) ? BitConverter.ToInt32(_single, 0) : 0;
}
