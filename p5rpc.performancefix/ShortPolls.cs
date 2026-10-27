using System.Diagnostics;
using System.Runtime.InteropServices;

namespace p5rpc.performancefix;

/// <summary>
/// Makes CRI FS's request wait poll every 0.1 ms instead of every 2 ms.
/// <para>
/// When an area loads, the game checks a few hundred files with CRI's file-size query. Files inside a CPK are answered
/// at once, but each optional file that does not exist (about ten per area change: FIELD/SHT/ENVSE..., OBJECT_HIT,
/// CULLING...) makes CRI try to open it, and the game waits for that open in CRI FS's request wait:
/// <c>criFs_ExecuteMain(); Sleep(1)</c> until the request is done. The open itself is quick, but each wait costs at least
/// one <c>Sleep(1)</c>, which lasts about 2 ms even with a 1 ms Windows timer: together about 20 ms per area change (task
/// "Resource MNG Update").
/// </para>
/// <para>
/// The mod points that wait's <c>Sleep</c> call at a routine that, for the first millisecond of a wait, waits 0.1 ms by
/// yielding the processor (a missing file needs one such poll), then sleeps 0.5 ms at a time on a high-resolution
/// waitable timer, so a long wait does not keep a core busy. Other sleeps are left as they are.
/// </para>
/// </summary>
public static unsafe class ShortPolls
{
    /// <summary>
    /// Inside CRI FS's request wait, from its 4th instruction: <c>mov rdi, rdx; mov rbx, rcx; jmp check; call
    /// criFs_ExecuteMain; mov ecx, 1; call Sleep; mov rcx, rbx; lea rdx, [rsp+38h]; call GetStatus; ... cmp eax, 1; je</c>.
    /// It starts past the function's first bytes, so it still matches when another mod has hooked the function.
    /// </summary>
    public const string WaitSig =
        "48 8B FA 48 8B D9 EB 12 E8 ?? ?? ?? ?? B9 01 00 00 00 E8 ?? ?? ?? ?? 48 8B CB 48 8D 54 24 38 E8 ?? ?? ?? ?? 8B 44 24 38 83 F8 01 74";

    /// <summary>Offset of <c>call Sleep</c> (CRI's sleep wrapper, with ecx = 1) in <see cref="WaitSig"/>.</summary>
    public const int SleepCallOffset = 0x12;

    /// <summary>Poll interval while yielding, and how long a wait yields before it sleeps on the timer (ms).</summary>
    public const double YieldPollMs = 0.1, YieldForMs = 1.0;

    /// <summary>A gap of more than this between two polls starts a new wait (ms).</summary>
    public const double NewWaitGapMs = 0.2;

    /// <summary>Timer sleep after the yielding part, in 100 ns units (0.5 ms, the finest this timer gives on common PCs).</summary>
    public const long TimerSleep = 5000;

    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    public static volatile bool Enabled = true;

    /// <summary>Number of short polls made (yielding and timer ones).</summary>
    public static int Polls;

    [ThreadStatic] private static nint _timer;
    [ThreadStatic] private static long _waitStart, _lastEnd;

    public static nint SleepPointer => (nint)(delegate* unmanaged<int, void>)&PollSleep;

    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    /// <summary>Stands in for <c>Sleep(ms)</c> in CRI FS's request wait.</summary>
    [UnmanagedCallersOnly]
    private static void PollSleep(int ms)
    {
        if (!Enabled || ms != 1)
        {
            Sleep((uint)ms);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _lastEnd > Ticks(NewWaitGapMs)) _waitStart = now;
        if (now - _waitStart < Ticks(YieldForMs))
        {
            var end = now + Ticks(YieldPollMs);
            while (Stopwatch.GetTimestamp() < end) SwitchToThread();
        }
        else
        {
            TimerWait();
        }

        Interlocked.Increment(ref Polls);
        _lastEnd = Stopwatch.GetTimestamp();
    }

    /// <summary>Sleeps <see cref="TimerSleep"/> on this thread's high-resolution timer (Sleep(1) where there is none).</summary>
    private static void TimerWait()
    {
        if (_timer == 0)
        {
            var created = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
            _timer = created != 0 ? created : -1; // -1: no high-resolution timers (before Windows 10 1803)
        }

        var due = -TimerSleep; // relative
        if (_timer == -1 || !SetWaitableTimer(_timer, &due, 0, 0, 0, false))
        {
            Sleep(1);
            return;
        }

        WaitForSingleObject(_timer, 0xFFFFFFFF);
    }

    /// <summary>A jump to <see cref="PollSleep"/> (placed near the game code, so the call's rel32 reaches it).</summary>
    public static byte[] BuildStub()
    {
        var c = new byte[12];
        c[0] = 0x48; c[1] = 0xB8;                                    // mov rax, PollSleep
        BitConverter.TryWriteBytes(c.AsSpan(2), (long)SleepPointer);
        c[10] = 0xFF; c[11] = 0xE0;                                  // jmp rax
        return c;
    }

    [DllImport("kernel32.dll")] private static extern void Sleep(uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool SwitchToThread();
    [DllImport("kernel32.dll")] private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(nint timer, long* due, int period, nint routine, nint argument, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
