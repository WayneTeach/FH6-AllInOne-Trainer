using System;
using System.Collections.Generic;

namespace FH6Mod.Cheats.RuntimeHook;

/// <summary>
/// Grants profile rewards by calling the game's own reward-grant function via injected
/// shellcode (CreateRemoteThread). Crash-free: never touches .text; the grant writes through
/// the wallet's real guards.
///
/// All three game functions are found by AOB at runtime against the live module — no
/// hardcoded addresses (the decompile's fix_pe-shifted addresses crashed on the live build).
///
/// Flow: AOB-find getWheelspins (for the DAT global), get_target, grant → build 2-call
/// shellcode (get_target(*DAT, &out) → grant(out, type, amount)) → CreateRemoteThread.
/// </summary>
internal sealed class RewardCaller
{
    private readonly RuntimeHookEngine _engine;

    // AOB signatures (from the v403 raw dump; call/rip disps wildcarded for version robustness).
    // getWheelspins: prologue + call(amountReader) + cvttss2si + lea rdx + mov rcx,[rip=DAT]
    private const string SIG_GETWHEELSPINS = "48 89 5C 24 08 57 48 83 EC 30 E8 ? ? ? ? F3 48 0F 2C D8 48 8D 54 24 20 48 8B 0D";
    // get_target: prologue + mov rsi,rcx + mov rdi,[rcx+8] + mov ecx,[rip=TLS] + mov rax,gs:[0x58]
    private const string SIG_GET_TARGET = "48 89 5C 24 08 57 48 83 EC 30 48 8B DA 48 8B 79 08 8B 0D ? ? ? ? 65 48 8B 04 25 58 00 00 00";
    // grant: prologue + mov rsi,rcx + mov ebx,r8d (the additive grant)
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
            error = $"Reward functions not found via AOB (gw=0x{gw:X} gt=0x{gt:X} gr=0x{gr:X}). Wrong game build?";
            _engine.LogPublic($"RewardCaller: AOB MISS gw=0x{gw:X} gt=0x{gt:X} gr=0x{gr:X}");
            return false;
        }

        // DAT global address: getWheelspins loads it at offset 0x19 via mov rcx,[rip+disp32].
        // disp32 at gw+0x1C; DAT_addr = (gw + 0x19 + 7) + disp = gw + 0x20 + disp.
        var gwOff = (int)(gw - mb);
        var disp = BitConverter.ToInt32(moduleBytes, gwOff + 0x1C);
        var datAddr = (ulong)((long)(gw + 0x20) + disp);

        var code = BuildGrantShellcode(datAddr, gt, gr, type, amount);

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
            _engine.LogPublic($"RewardCaller: granted type={type} amount={amount} (gt=0x{gt:X} gr=0x{gr:X} dat=0x{datAddr:X})");
            return true;
        }
        finally { Native.VirtualFreeEx(handle, codeMem, UIntPtr.Zero, Native.MEM_RELEASE); }
    }

    private static ulong FindFirst(byte[] data, string sig, ulong baseAddr)
    {
        var pat = Pattern.Parse(sig);
        foreach (var off in Pattern.FindAll(data, pat, 4))
            return baseAddr + (ulong)off;
        return 0;
    }

    /// <summary>
    /// x64 shellcode (identical layout to before, but addresses are now AOB-resolved):
    ///   sub rsp,0x38; rcx=*DAT; rdx=&out; call get_target;
    ///   rcx=out; if null skip; edx=type; r8d=amount; call grant;
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
        c.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x28 });     // mov rcx, [rsp+0x28]
        c.AddRange(new byte[] { 0x48, 0x85, 0xC9 });                 // test rcx, rcx
        c.AddRange(new byte[] { 0x74, 0x16 });                       // jz +0x16
        c.Add(0xBA); c.AddRange(BitConverter.GetBytes(type));        // mov edx, type
        c.AddRange(new byte[] { 0x41, 0xB8 }); c.AddRange(BitConverter.GetBytes(amount)); // mov r8d, amount
        MovabsRax(grant);                                            // movabs rax, grant
        c.AddRange(new byte[] { 0xFF, 0xD0 });                       // call rax
        c.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x38 });           // add rsp, 0x38
        c.Add(0xC3);                                                 // ret
        return c.ToArray();
    }
}
