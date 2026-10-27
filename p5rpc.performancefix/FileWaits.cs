namespace p5rpc.performancefix;

/// <summary>
/// Lets the game's synchronous file waits advance right away instead of once per CRI server tick.
/// <para>
/// CRI's file system (CRI FS) does its bookkeeping in a server step that CRI's shared server thread runs 60 times a
/// second (the same thread as the sound library, paced by a timer). Every synchronous file operation of the game, such as
/// a file-size lookup when an area loads (task "Resource MNG Update") or a CPK bind, waits in a loop:
/// <c>criFs_ExecuteMain(); Sleep(1)</c>, until the request is done. With a timer-paced server,
/// <c>criFs_ExecuteMain</c> does nothing (it only notifies an event-driven server), so each step of the request waits for
/// the next tick: 17 to 44 ms per lookup, a hitch on every area change.
/// </para>
/// <para>
/// The CRI FS server step guards its work with atomic busy flags (also in the branch the server thread takes), so
/// another thread may run it: whichever thread holds a flag does that part, the other skips it. In the thread models
/// without a server thread, CRI's ExecuteMain runs the step itself, and CRI's own wait loops call the step directly. With a
/// server thread (thread model 0, P5R's case) nothing runs it outside the 60 Hz tick. This class gives
/// <c>criFs_ExecuteMain</c> one more call in that case, through its one call (to the shared server's ExecuteMain):
/// <code>
///   server ExecuteMain()                      ; what the game did
///   if (enabled and thread model == 0)        ; CRI's server runs on its own (timer-paced) thread
///       CRI FS server step()                  ; advance the file requests now
/// </code>
/// It is only called from wait loops, so nothing changes outside them, and the sound server keeps its own cadence.
/// </para>
/// </summary>
public static unsafe class FileWaits
{
    /// <summary>
    /// <c>criFs_ExecuteMain</c> (<c>sub rsp, 28h; call server ExecuteMain; xor eax, eax; add rsp, 28h; ret</c>) followed by
    /// the start of the CRI FS server step.
    /// </summary>
    public const string ExecuteMainSig = "48 83 EC 28 E8 ?? ?? ?? ?? 33 C0 48 83 C4 28 C3 48 83 EC 28 FF 05 ?? ?? ?? ?? 33 C9 E8";

    /// <summary>The <c>call</c> in criFs_ExecuteMain, and the CRI FS server step that follows the function.</summary>
    public const int ServerCallOffset = 4, ServerStepOffset = 0x10;

    /// <summary>In the server step: <c>call</c> of the function returning CRI's server thread model (0 = it has its own thread).</summary>
    public const int ThreadedCallOffset = 0x11;

    /// <summary>CRI FS registering its server step on CRI's server (type 5): <c>mov edx, 5; lea rcx, [rip+step]; call</c>.</summary>
    public const string RegisterSig = "BA 05 00 00 00 48 8D 0D ?? ?? ?? ?? E8";

    /// <summary>Data at the end of the routine: enabled flag, count of extra steps, then the three targets.</summary>
    public const int EnabledOffset = 0x40, CountOffset = 0x44, ServerExecuteOffset = 0x48, ThreadedOffset = 0x50, StepOffset = 0x58;

    /// <summary>
    /// Emits the routine, reached by <c>call</c> from criFs_ExecuteMain in place of the shared server's ExecuteMain.
    /// </summary>
    public static byte[] Build(nint serverExecute, nint threadModel, nint step)
    {
        var c = new byte[0x60];
        var at = 0;
        void E(params byte[] b) { foreach (var x in b) c[at++] = x; }
        void RipTo(int target, int extra = 0) { BitConverter.TryWriteBytes(c.AsSpan(at), target - (at + 4 + extra)); at += 4; }

        E(0x48, 0x83, 0xEC, 0x28);                                   // sub rsp, 28h
        E(0xFF, 0x15); RipTo(ServerExecuteOffset);                   // call [rip+server ExecuteMain]
        E(0x80, 0x3D); RipTo(EnabledOffset, 1); E(0x00);             // cmp byte [rip+enabled], 0
        E(0x74, 0x00); var toDone1 = at - 1;                         // je done
        E(0xFF, 0x15); RipTo(ThreadedOffset);                        // call [rip+thread model]
        E(0x85, 0xC0);                                               // test eax, eax
        E(0x75, 0x00); var toDone2 = at - 1;                         // jnz done (no server thread: ExecuteMain ran the step)
        E(0xFF, 0x05); RipTo(CountOffset);                           // inc dword [rip+count]
        E(0xFF, 0x15); RipTo(StepOffset);                            // call [rip+CRI FS server step]
        c[toDone1] = (byte)(at - (toDone1 + 1));
        c[toDone2] = (byte)(at - (toDone2 + 1));
        E(0x48, 0x83, 0xC4, 0x28, 0xC3);                             // done: add rsp, 28h; ret

        c[EnabledOffset] = 1;
        BitConverter.TryWriteBytes(c.AsSpan(ServerExecuteOffset), (long)serverExecute);
        BitConverter.TryWriteBytes(c.AsSpan(ThreadedOffset), (long)threadModel);
        BitConverter.TryWriteBytes(c.AsSpan(StepOffset), (long)step);
        return c;
    }
}
