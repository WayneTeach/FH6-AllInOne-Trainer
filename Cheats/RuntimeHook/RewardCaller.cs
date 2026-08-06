using System;
using System.Collections.Generic;

namespace FH6Mod.Cheats.RuntimeHook;

/// <summary>
/// Grants profile rewards (wheelspins / super-wheelspins / credits) by calling the game's
/// own reward-grant function via injected shellcode (CreateRemoteThread), exactly like the
/// SQL cheats and Phorza's approach. Crash-free: it never touches the game's .text section,
/// and the grant function writes through the wallet's REAL guards (no fake-descriptor crash).
///
/// Flow (from the v403 decompile):
///   target = GetRewardTarget(*DAT_7ff78ee02778, &out)   // FUN_7ff784c62a20, global-sourced
///   Grant(target, type, amount)                          // FUN_7ff7856ab3c0 — ADDS amount
/// type 0 = wheelspins, 1 = super-wheelspins.
///
/// RVAs are v403.798-specific; re-derive via AOB when the game updates.
/// </summary>
internal sealed class RewardCaller
{
    private readonly RuntimeHookEngine _engine;

    // v403.798 RVAs
    private const ulong RVA_DAT_GLOBAL  = 0xA7E2778; // DAT_7ff78ee02778 (rewards-system global)
    private const ulong RVA_GET_TARGET  = 0x642A20;  // FUN_7ff784c62a20 (target getter)
    private const ulong RVA_GRANT       = 0x10AB3C0; // FUN_7ff7856ab3c0 (additive grant)

    public RewardCaller(RuntimeHookEngine engine) => _engine = engine;

    /// <summary>Grant <paramref name="amount"/> of reward <paramref name="type"/> (0=wheelspins,1=super).</summary>
    public bool Grant(int type, int amount, out string? error)
    {
        error = null;
        var handle = _engine.HandlePublic;
        var mb = _engine.MainBase;
        if (handle == IntPtr.Zero || mb == 0) { error = "Not attached."; return false; }

        var code = BuildGrantShellcode(mb + RVA_DAT_GLOBAL, mb + RVA_GET_TARGET, mb + RVA_GRANT, type, amount);

        var codeMem = Native.VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)4096,
            Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
        if (codeMem == IntPtr.Zero) { error = "VirtualAllocEx failed."; return false; }

        try
        {
            _engine.WriteBytesPublic((ulong)codeMem.ToInt64(), code);
            var thread = Native.CreateRemoteThread(handle, IntPtr.Zero, 0, codeMem, IntPtr.Zero, 0, out _);
            if (thread == IntPtr.Zero) { error = "CreateRemoteThread failed."; return false; }
            Native.WaitForSingleObject(thread, 5000);
            Native.CloseHandle(thread);
            _engine.LogPublic($"RewardCaller: granted type={type} amount={amount}");
            return true;
        }
        finally
        {
            Native.VirtualFreeEx(handle, codeMem, UIntPtr.Zero, Native.MEM_RELEASE);
        }
    }

    /// <summary>
    /// x64 shellcode:
    ///   sub rsp,0x38                       (shadow + 16-byte out slot, 16-aligned)
    ///   rcx = *DAT_GLOBAL                  (rewards system)
    ///   rdx = &out                         (rsp+0x28)
    ///   call GetRewardTarget               -> out = target
    ///   rcx = out                          (target); if null skip
    ///   edx = type, r8d = amount
    ///   call Grant                         (adds amount)
    ///   add rsp,0x38; ret
    /// </summary>
    private static byte[] BuildGrantShellcode(ulong dat, ulong getTarget, ulong grant, int type, int amount)
    {
        var c = new List<byte>(80);
        void MovabsRax(ulong v) { c.Add(0x48); c.Add(0xB8); c.AddRange(BitConverter.GetBytes(v)); }

        c.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x38 });          // sub rsp, 0x38
        MovabsRax(dat);                                              // movabs rax, dat
        c.AddRange(new byte[] { 0x48, 0x8B, 0x08 });                 // mov rcx, [rax]
        c.AddRange(new byte[] { 0x48, 0x8D, 0x54, 0x24, 0x28 });     // lea rdx, [rsp+0x28]
        MovabsRax(getTarget);                                        // movabs rax, getTarget
        c.AddRange(new byte[] { 0xFF, 0xD0 });                       // call rax
        c.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x28 });     // mov rcx, [rsp+0x28]  (target)
        c.AddRange(new byte[] { 0x48, 0x85, 0xC9 });                 // test rcx, rcx
        c.AddRange(new byte[] { 0x74, 0x16 });                       // jz +0x16 (skip grant -> epilogue)
        c.Add(0xBA); c.AddRange(BitConverter.GetBytes(type));        // mov edx, type
        c.AddRange(new byte[] { 0x41, 0xB8 }); c.AddRange(BitConverter.GetBytes(amount)); // mov r8d, amount
        MovabsRax(grant);                                            // movabs rax, grant
        c.AddRange(new byte[] { 0xFF, 0xD0 });                       // call rax
        c.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x38 });           // add rsp, 0x38
        c.Add(0xC3);                                                 // ret
        return c.ToArray();
    }
}
