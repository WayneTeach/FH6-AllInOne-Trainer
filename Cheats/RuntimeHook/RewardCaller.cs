using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FH6Mod.Cheats.RuntimeHook;

/// <summary>
/// Grants rewards by hijacking a GAME THREAD (which has TLS) to call the game's grant
/// function. Thread hijacking is needed because get_target uses TLS (_Init_thread static
/// init) that a bare CreateRemoteThread thread doesn't have. The SQL cheats don't need
/// this (ExecuteQuery is TLS-free), but the reward system does.
///
/// Flow: AOB-find functions → suspend a game thread → redirect its RIP to the shellcode
/// → shellcode runs on the game thread (TLS valid) → grants reward → sets done flag →
/// loops → trainer detects flag, restores original context, resumes thread.
/// </summary>
internal sealed class RewardCaller
{
    private readonly RuntimeHookEngine _engine;

    private const string SIG_GETWHEELSPINS = "48 89 5C 24 08 57 48 83 EC 30 E8 ? ? ? ? F3 48 0F 2C D8 48 8D 54 24 20 48 8B 0D";
    private const string SIG_GET_TARGET = "48 89 5C 24 08 57 48 83 EC 30 48 8B DA 48 8B 79 08 8B 0D ? ? ? ? 65 48 8B 04 25 58 00 00 00";
    private const string SIG_GRANT = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 30 48 8B F1 41 8B";

    public RewardCaller(RuntimeHookEngine engine) => _engine = engine;

    public bool Grant(int type, int amount, out string? error)
    {
        error = null;
        var handle = _engine.HandlePublic;
        var mb = _engine.MainBase;
        if (handle == IntPtr.Zero || mb == 0) { error = "Not attached."; return false; }

        var moduleBytes = _engine.ReadBytesPublic(mb, _engine.MainSize);
        if (moduleBytes.Length == 0) { error = "Could not read module."; return false; }

        ulong gw = FindFirst(moduleBytes, SIG_GETWHEELSPINS, mb);
        ulong gt = FindFirst(moduleBytes, SIG_GET_TARGET, mb);
        ulong gr = FindFirst(moduleBytes, SIG_GRANT, mb);
        if (gw == 0 || gt == 0 || gr == 0)
        {
            error = $"AOB miss (gw=0x{gw:X} gt=0x{gt:X} gr=0x{gr:X}). Wrong build?";
            return false;
        }

        var gwOff = (int)(gw - mb);
        var disp = BitConverter.ToInt32(moduleBytes, gwOff + 0x1C);
        var datAddr = (ulong)((long)(gw + 0x20) + disp);

        // Allocate shellcode + done-flag.
        var codeMem = Native.VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)4096, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
        var flagMem = Native.VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)8, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
        if (codeMem == IntPtr.Zero || flagMem == IntPtr.Zero) { error = "VirtualAllocEx failed."; return false; }

        try
        {
            var code = BuildHijackShellcode(datAddr, gt, gr, (ulong)flagMem.ToInt64(), type, amount);
            _engine.WriteBytesPublic((ulong)codeMem.ToInt64(), code);

            // Find a game thread to hijack.
            var pid = (uint)_engine.Pid!;
            var threadId = PickGameThread(pid);
            if (threadId == 0) { error = "No game thread found to hijack."; return false; }

            var th = Native.OpenThread(Native.THREAD_GET_CONTEXT | Native.THREAD_SET_CONTEXT | Native.THREAD_SUSPEND_RESUME | Native.THREAD_QUERY_INFORMATION, false, threadId);
            if (th == IntPtr.Zero) { error = "OpenThread failed."; return false; }

            try
            {
                // Suspend → save context → redirect RIP → resume → poll flag → restore → resume.
                Native.SuspendThread(th);

                var ctxRaw = Marshal.AllocHGlobal(Native.CONTEXT_X64_SIZE + 16);
                var ctx = new IntPtr((ctxRaw.ToInt64() + 15) & ~15);
                try
                {
                    // Zero + set flags.
                    for (int i = 0; i < Native.CONTEXT_X64_SIZE + 16; i++) Marshal.WriteByte(ctxRaw, i, 0);
                    Marshal.WriteInt32(ctx, Native.OFF_CTX_FLAGS, (int)Native.CONTEXT_ALL);

                    if (!Native.GetThreadContext(th, ctx)) { error = "GetThreadContext failed."; Native.ResumeThread(th); return false; }

                    // Save original RIP, then set RIP to shellcode.
                    var origRip = (ulong)Marshal.ReadInt64(ctx, Native.OFF_CTX_RIP);
                    Marshal.WriteInt64(ctx, Native.OFF_CTX_RIP, (long)codeMem.ToInt64());

                    if (!Native.SetThreadContext(th, ctx)) { error = "SetThreadContext failed."; Native.ResumeThread(th); return false; }

                    Native.ResumeThread(th);

                    // Poll the done-flag (the shellcode sets it to 1 after the grant).
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool done = false;
                    while (sw.ElapsedMilliseconds < 5000)
                    {
                        if (_engine.ReadInt32Public((ulong)flagMem.ToInt64()) == 1) { done = true; break; }
                        System.Threading.Thread.Sleep(10);
                    }

                    // Restore original context regardless.
                    Native.SuspendThread(th);
                    Marshal.WriteInt64(ctx, Native.OFF_CTX_RIP, (long)origRip);
                    // Re-read the current context (the thread ran the shellcode; we restore orig RIP).
                    Marshal.WriteInt32(ctx, Native.OFF_CTX_FLAGS, (int)Native.CONTEXT_ALL);
                    Native.SetThreadContext(th, ctx);
                    Native.ResumeThread(th);

                    if (!done) { error = "Hijack timeout (flag not set in 5s). Grant may not have executed."; return false; }
                }
                finally { Marshal.FreeHGlobal(ctxRaw); }
            }
            finally { Native.CloseHandle(th); }

            _engine.LogPublic($"RewardCaller: hijack grant OK type={type} amount={amount} (gt=0x{gt:X} gr=0x{gr:X})");
            return true;
        }
        finally
        {
            // Free after a short delay (the thread is past the shellcode now).
            System.Threading.Thread.Sleep(100);
            Native.VirtualFreeEx(handle, codeMem, UIntPtr.Zero, Native.MEM_RELEASE);
            Native.VirtualFreeEx(handle, flagMem, UIntPtr.Zero, Native.MEM_RELEASE);
        }
    }

    private uint PickGameThread(uint pid)
    {
        var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPTHREAD, 0);
        if (snap == IntPtr.Zero) return 0;
        try
        {
            var te = new Native.THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.THREADENTRY32>() };
            var first = Native.Thread32First(snap, ref te);
            uint firstTid = 0;
            while (first)
            {
                if (te.th32OwnerProcessID == pid)
                {
                    if (firstTid == 0) firstTid = te.th32ThreadID; // save main thread
                    else return te.th32ThreadID;                    // return first WORKER thread
                }
                first = Native.Thread32Next(snap, ref te);
            }
            return firstTid; // fallback: main thread
        }
        finally { Native.CloseHandle(snap); }
    }

    private static ulong FindFirst(byte[] data, string sig, ulong baseAddr)
    {
        var pat = Pattern.Parse(sig);
        foreach (var off in Pattern.FindAll(data, pat, 4))
            return baseAddr + (ulong)off;
        return 0;
    }

    private static byte[] BuildHijackShellcode(ulong dat, ulong getTarget, ulong grant, ulong flag, int type, int amount)
    {
        var c = new List<byte>(96);
        void MovabsRax(ulong v) { c.Add(0x48); c.Add(0xB8); c.AddRange(BitConverter.GetBytes(v)); }

        c.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x38 });
        MovabsRax(dat);
        c.AddRange(new byte[] { 0x48, 0x8B, 0x08 });
        c.AddRange(new byte[] { 0x48, 0x8D, 0x54, 0x24, 0x28 });
        MovabsRax(getTarget);
        c.AddRange(new byte[] { 0xFF, 0xD0 });
        c.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x28 });
        c.AddRange(new byte[] { 0x48, 0x85, 0xC9 });
        c.AddRange(new byte[] { 0x74, 0x17 });                       // jz +0x17 (skip grant → done)
        c.Add(0xBA); c.AddRange(BitConverter.GetBytes(type));
        c.AddRange(new byte[] { 0x41, 0xB8 }); c.AddRange(BitConverter.GetBytes(amount));
        MovabsRax(grant);
        c.AddRange(new byte[] { 0xFF, 0xD0 });
        // .done: write flag = 1
        MovabsRax(flag);
        c.AddRange(new byte[] { 0xC7, 0x00, 0x01, 0x00, 0x00, 0x00 }); // mov dword [rax], 1
        c.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x38 });             // add rsp, 0x38
        c.AddRange(new byte[] { 0xEB, 0xFE });                         // jmp -2 (infinite loop)
        return c.ToArray();
    }
}
