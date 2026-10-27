namespace p5rpc.performancefix;

/// <summary>
/// Stops the game from waiting for a field's sound banks to go quiet when it leaves the field.
/// <para>
/// When an area unloads, the game releases its sound banks (CRI ADX2 ACBs) one by one from the game thread, through
/// <c>criAtomExAcb_Release</c>. While any voice still plays from a bank (the ambient sounds fading out), that function
/// stops the voices and then loops: run the CRI server, <c>Sleep(10)</c>, check again. On a train ride or any area change
/// this blocks the game for 20 to 60 ms, a visible hitch in the loading animation (task "bg env [RESRC MNG]").
/// </para>
/// <para>
/// CRI's own <c>criAtomExAcb_ReleaseAsync</c> does the same without waiting: it marks the bank, and CRI's server thread
/// stops its voices and releases it as soon as they are quiet (CRI's "release all" handles marked banks too). The game
/// never uses it. This class points the game's one release call (in its sound-slot release, at the bank of a slot) at a
/// small routine:
/// <code>
///   if (enabled and slot type == 0)       ; a bank loaded from its .acb/.awb files: CRI owns all of its data
///       criAtomExAcb_ReleaseAsync(acb, NULL, NULL)
///   else
///       criAtomExAcb_Release(acb)          ; exactly what the game did
/// </code>
/// A bank loaded from game memory (slot type 1) keeps the waiting release, since the game may reuse that memory as soon as
/// the call returns.
/// </para>
/// </summary>
public static unsafe class SoundBankRelease
{
    /// <summary>
    /// In the game's sound-slot release: <c>cmp dword [rsi+r14+8], 2; jne; mov rcx, [rsi+r14+228h]; call ...; jmp;
    /// mov rcx, [rsi+r14+220h]; call criAtomExAcb_Release; mov rcx, [rsi+r14+230h]</c> (rsi + r14 = the slot).
    /// </summary>
    public const string SlotReleaseSig =
        "42 83 7C 36 08 02 75 0F 4A 8B 8C 36 28 02 00 00 E8 ?? ?? ?? ?? EB 0D 4A 8B 8C 36 20 02 00 00 E8 ?? ?? ?? ?? 4A 8B 8C 36 30 02 00 00";

    /// <summary>Offset of <c>call criAtomExAcb_Release</c> in <see cref="SlotReleaseSig"/>.</summary>
    public const int ReleaseCallOffset = 0x1F;

    /// <summary>Start of <c>criAtomExAcb_ReleaseAsync(acb, callback, object)</c> (CRI ADX2 2.22): checks, then marks the bank at +40h.</summary>
    public const string ReleaseAsyncSig =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 20 49 8B F0 48 8B EA 48 8B D9 48 85 C9 75 12 44 8D 41 FE " +
        "48 8D 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 33 48 83 C1 40 E8";

    /// <summary>Start of <c>criAtomExAcb_Release(acb)</c>: the waiting loop (stop, check, server, Sleep(10)).</summary>
    public const string ReleaseSig =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B D9 48 85 C9 75 15 44 8D 41 FE 48 8D 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? " +
        "E9 ?? ?? ?? ?? 48 8B 71 20 33 FF EB 35 E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? B9 0A 00 00 00 E8";

    /// <summary>The slot type at +8: 0 = bank loaded from its files, 1 = from game memory, 2 = another kind (released elsewhere).</summary>
    public const int SlotTypeOffset = 8;

    /// <summary>Data at the end of the routine: enabled flag, async release count, then the two targets.</summary>
    public const int EnabledOffset = 0x40, CountOffset = 0x44, AsyncTargetOffset = 0x48, SyncTargetOffset = 0x50;

    /// <summary>
    /// Emits the routine (see the class summary). It is reached by <c>call</c> from the game's slot release, with
    /// rcx = the bank and rsi + r14 = the slot, and tail-jumps to one of the two CRI functions.
    /// </summary>
    public static byte[] Build(nint releaseAsync, nint release)
    {
        var c = new byte[0x60];
        var at = 0;
        void E(params byte[] b) { foreach (var x in b) c[at++] = x; }
        void RipTo(int target) { BitConverter.TryWriteBytes(c.AsSpan(at), target - (at + 4)); at += 4; }

        E(0x80, 0x3D); RipTo(EnabledOffset - 1); E(0x00);          // cmp byte [rip+enabled], 0 (the imm8 follows the disp32)
        E(0x74, 0x00); var toSync1 = at - 1;                         // je sync
        E(0x42, 0x83, 0x7C, 0x36, SlotTypeOffset, 0x00);             // cmp dword [rsi+r14+8], 0
        E(0x75, 0x00); var toSync2 = at - 1;                         // jne sync
        E(0x33, 0xD2, 0x45, 0x33, 0xC0);                             // xor edx, edx; xor r8d, r8d (no callback)
        E(0xFF, 0x05); RipTo(CountOffset);                           // inc dword [rip+count]
        E(0xFF, 0x25); RipTo(AsyncTargetOffset);                     // jmp [rip+criAtomExAcb_ReleaseAsync]
        c[toSync1] = (byte)(at - (toSync1 + 1));
        c[toSync2] = (byte)(at - (toSync2 + 1));
        E(0xFF, 0x25); RipTo(SyncTargetOffset);                      // sync: jmp [rip+criAtomExAcb_Release]

        c[EnabledOffset] = 1;
        BitConverter.TryWriteBytes(c.AsSpan(AsyncTargetOffset), (long)releaseAsync);
        BitConverter.TryWriteBytes(c.AsSpan(SyncTargetOffset), (long)release);
        return c;
    }
}
