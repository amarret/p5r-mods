using System.Diagnostics;
using System.Runtime.InteropServices;
using p5rpc.performancefix.Configuration;
using p5rpc.performancefix.Template;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.performancefix;

/// <summary>
/// Makes the renderer upload only the part of each shader constant buffer it actually filled, instead of the whole
/// buffer (usually 7 KB) on every draw, into small companion buffers sized to it. In busy places this cuts tens of MB
/// of copying per frame on the "render" thread, and on area changes it spares the driver 40-50 MB of new upload memory.
/// See <see cref="SmallBuffers"/> and <see cref="UploadStub"/>. On area changes it also stops the game from waiting for the old area's sound
/// banks to go quiet (<see cref="SoundBankRelease"/>) and lets its file waits advance at once instead of once per CRI
/// server tick (<see cref="FileWaits"/>). After a fast-travel destination is picked, the loading animation starts over
/// the map instead of after the map's closing fade (<see cref="MapTravel"/>). Nothing on disk or in the save is changed.
/// </summary>
public unsafe class Mod : ModBase
{
    /// <summary>The engine's three constant-buffer uploaders, up to and including <c>call [r10+180h]</c>.</summary>
    public static readonly (string Stage, string Signature)[] Uploaders =
    [
        ("vertex shader",
         "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 48 83 EC 40 41 8B E9 48 8B F2 49 8B F8 45 33 C9 45 33 C0 4C 8B 16 " +
         "48 8D 85 82 03 00 00 48 03 C0 48 8B 14 C1 48 81 C1 00 39 00 00 48 69 C5 00 1C 00 00 48 89 54 24 50 48 03 C8 33 C0 " +
         "89 44 24 30 89 44 24 28 48 89 4C 24 20 48 8B CE 41 FF 92 80 01 00 00"),
        ("pixel shader",
         "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 48 83 EC 40 48 8B F2 41 8B C1 48 03 C0 41 8B E9 49 8B F8 45 33 C9 " +
         "45 33 C0 4C 8B 16 48 8B 94 C1 28 C1 01 00 48 81 C1 08 C2 01 00 48 69 C5 00 1C 00 00 48 89 54 24 50 48 03 C8 33 C0 " +
         "89 44 24 30 89 44 24 28 48 89 4C 24 20 48 8B CE 41 FF 92 80 01 00 00"),
        ("third shader stage",
         "48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 48 83 EC 40 41 8B E9 48 8B F2 49 8B F8 45 33 C9 45 33 C0 4C 8B 16 " +
         "48 8D 85 A3 34 00 00 48 03 C0 48 8B 14 C1 48 81 C1 10 4B 03 00 48 69 C5 00 1C 00 00 48 89 54 24 50 48 03 C8 33 C0 " +
         "89 44 24 30 89 44 24 28 48 89 4C 24 20 48 8B CE 41 FF 92 80 01 00 00"),
    ];

    /// <summary>Offset of <c>call [r10+180h]</c> (41 FF 92 80 01 00 00) inside each signature.</summary>
    public const int CallOffset = 0x5C;

    /// <summary>The per-shader loops that upload and bind each of a shader's constant buffers, from <c>mov r10, [r14]</c>
    /// up to and including <c>call [r10+180h]</c>, with that call's offset.</summary>
    public static readonly (string Stage, string Signature, int CallOffset)[] LoopUploaders =
    [
        ("vertex shader",
         "4D 8B 16 48 8D 87 82 03 00 00 44 89 44 24 30 48 8D 8D 00 39 00 00 48 03 C0 44 89 44 24 28 45 33 C9 45 33 C0 " +
         "48 8B 54 C5 00 48 69 C7 00 1C 00 00 48 89 54 24 70 48 03 C8 48 89 4C 24 20 49 8B CE 41 FF 92 80 01 00 00", 0x40),
        ("pixel shader",
         "4D 8B 16 48 8D 8D 08 C2 01 00 44 89 44 24 30 48 8B C7 48 03 C0 44 89 44 24 28 45 33 C9 45 33 C0 " +
         "48 8B 94 C5 28 C1 01 00 48 69 C7 00 1C 00 00 48 89 54 24 70 48 03 C8 48 89 4C 24 20 49 8B CE 41 FF 92 80 01 00 00", 0x3F),
        ("third shader stage",
         "4D 8B 16 48 8D 87 A3 34 00 00 44 89 44 24 30 48 8D 8D 10 4B 03 00 48 03 C0 44 89 44 24 28 45 33 C9 45 33 C0 " +
         "48 8B 54 C5 00 48 69 C7 00 1C 00 00 48 89 54 24 70 48 03 C8 48 89 4C 24 20 49 8B CE 41 FF 92 80 01 00 00", 0x40),
    ];

    public static readonly byte[] OriginalCall = [0x41, 0xFF, 0x92, 0x80, 0x01, 0x00, 0x00];

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private readonly object _gate = new();
    private bool _suspended;

    private nint _uploadRoutine, _smallRoutine;
    private byte* _uploadsEnabled;
    private int _patched, _smallSites;
    private int _loggedCreated = -1;

    private nint _slotRelease, _releaseAsync, _release, _fsExecuteMain, _fsRegister, _fsWait;
    private bool _shortPolls;
    private int _loggedPolls;
    private int _bankScans;
    private byte* _banksEnabled, _fileWaitsEnabled;
    private int* _asyncReleases, _fileWaitSteps;
    private int _loggedReleases, _loggedSteps, _loggedSkips = -1;
    private bool _mapTravel;
    private Timer? _releaseLog;
    private bool _timerRaised;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        var startupScanner = GetController<IStartupScanner>();
        if (startupScanner == null)
        {
            Log("Reloaded.Memory.SigScan.ReloadedII is unavailable; uploads stay as they are.");
            return;
        }

        using var process = Process.GetCurrentProcess();
        var baseAddress = process.MainModule!.BaseAddress;

        Log($"Windows timer resolution at start-up: {TimerResolutionMs():0.0##} ms.");
        void BankScan(string signature, int offset, Action<nint> found) => startupScanner.AddMainModuleScan(signature, result =>
        {
            lock (_gate)
            {
                if (result.Found) found(baseAddress + result.Offset + offset);
                if (++_bankScans < 6) return;
                PatchSoundBankRelease();
                PatchFileWaits();
                PatchPolls();
                StartLog();
            }
        });
        BankScan(SoundBankRelease.SlotReleaseSig, SoundBankRelease.ReleaseCallOffset, a => _slotRelease = a);
        BankScan(SoundBankRelease.ReleaseAsyncSig, 0, a => _releaseAsync = a);
        BankScan(SoundBankRelease.ReleaseSig, 0, a => _release = a);
        BankScan(FileWaits.ExecuteMainSig, 0, a => _fsExecuteMain = a);
        BankScan(FileWaits.RegisterSig, 0, a => _fsRegister = a);
        BankScan(ShortPolls.WaitSig, ShortPolls.SleepCallOffset, a => _fsWait = a);
        ApplyTimer();

        var (ok, message) = D3DCheck.Run();
        Log($"Driver check: {message}.");
        if (!ok)
        {
            Log("Uploads stay as they are.");
            return;
        }

        foreach (var (stage, signature) in Uploaders)
        {
            startupScanner.AddMainModuleScan(signature, result =>
            {
                if (!result.Found)
                {
                    Log($"Could not find the {stage} constant-buffer uploader (unsupported game version, or another mod changed it).");
                    return;
                }

                lock (_gate)
                    PatchUploader(stage, baseAddress + result.Offset + CallOffset);
            });
        }

        startupScanner.AddMainModuleScan(MapTravel.DispatcherSig, result =>
        {
            lock (_gate)
            {
                if (!result.Found)
                {
                    Log("Could not find the flowscript interpreter (unsupported game version); the map closes the vanilla way before travelling.");
                    return;
                }

                var at = baseAddress + result.Offset;
                var failure = MapTravel.Install(at);
                _mapTravel = failure == null;
                ApplyBanks(log: false);
                Log(failure == null
                    ? $"Fast travel: the loading animation starts over the map (flowscript interpreter at 0x{at:X})."
                    : $"Fast travel: {failure}; the map closes the vanilla way before travelling.");
            }
        });

        foreach (var (stage, signature, callOffset) in LoopUploaders)
        {
            startupScanner.AddMainModuleScan(signature, result =>
            {
                if (!result.Found)
                {
                    Log($"Could not find the {stage} per-shader constant-buffer loop (unsupported game version, or another mod changed it).");
                    return;
                }

                lock (_gate)
                    PatchLoopUploader(stage, baseAddress + result.Offset + callOffset);
            });
        }
    }

    private T? GetController<T>() where T : class =>
        _modLoader.GetController<T>() is { } weak && weak.TryGetTarget(out var target) ? target : null;

    /// <summary>Copies a routine into executable memory within rel32 reach of <paramref name="site"/>.</summary>
    private static nint Place(nint site, byte[] code)
    {
        var at = UploadStub.AllocateNear(site, 0x1000);
        if (at == 0) return 0;
        Marshal.Copy(code, 0, at, code.Length);
        FlushInstructionCache(GetCurrentProcess(), at, (nuint)code.Length);
        return at;
    }

    /// <summary>Builds the trimmed-upload routine and the small-buffer routine on first use, near <paramref name="site"/>.</summary>
    private bool EnsureRoutines(nint site)
    {
        if (_uploadRoutine == 0)
        {
            var (code, enabledOffset) = UploadStub.Build();
            _uploadRoutine = Place(site, code);
            if (_uploadRoutine == 0)
            {
                Log("No memory near the game code for the upload routine; uploads stay as they are.");
                return false;
            }

            _uploadsEnabled = (byte*)(_uploadRoutine + enabledOffset);
            ApplyConfig(log: false);
        }

        if (_smallRoutine == 0)
        {
            SmallBuffers.Prepare();
            _smallRoutine = Place(site, SmallBuffers.Build(_uploadRoutine));
            if (_smallRoutine == 0) Log("No memory near the game code for the small-buffer routine; uploads are only trimmed.");
            ApplyConfig(log: false);
        }

        return true;
    }

    /// <summary>Replaces a 7-byte <c>call [r10+180h]</c> with a call to <paramref name="target"/> and a 2-byte nop.</summary>
    private static void PatchCall(nint call, nint target)
    {
        var patch = new byte[OriginalCall.Length];
        patch[0] = 0xE8;
        BitConverter.TryWriteBytes(patch.AsSpan(1), (int)(target - (call + 5)));
        patch[5] = 0x66; patch[6] = 0x90; // 2-byte nop
        VirtualProtect(call, (nuint)patch.Length, 0x40, out var old);
        patch.CopyTo(new Span<byte>((void*)call, patch.Length));
        VirtualProtect(call, (nuint)patch.Length, old, out _);
        FlushInstructionCache(GetCurrentProcess(), call, (nuint)patch.Length);
    }

    private static bool Matches(nint at, byte[] bytes) => new ReadOnlySpan<byte>((void*)at, bytes.Length).SequenceEqual(bytes);

    /// <summary>Points one single-slot uploader's UpdateSubresource call at the small-buffer routine, which hands
    /// buffers it cannot take to the trimmed-upload routine (or straight at that one if the bind code differs).</summary>
    private void PatchUploader(string stage, nint call)
    {
        if (!Matches(call, OriginalCall))
        {
            Log($"The {stage} uploader's call does not look as expected; left alone.");
            return;
        }

        if (!EnsureRoutines(call)) return;
        var small = _smallRoutine != 0 && Matches(call + OriginalCall.Length, SmallBuffers.SinglePostCall);
        var target = small ? _smallRoutine + SmallBuffers.SingleEntry : _uploadRoutine;
        PatchCall(call, target);
        _patched++;
        if (small) _smallSites++;
        Log(small
            ? $"Small constant buffers for {stage} uploads, trimmed above 4 KB (call at 0x{call:X}, routine at 0x{target:X})."
            : $"Trimming {stage} constant-buffer uploads (call at 0x{call:X}, routine at 0x{target:X}); its bind code differs, so no small buffers.");
    }

    /// <summary>Points one per-shader loop's UpdateSubresource call at the small-buffer routine, which does the original
    /// full upload for buffers it cannot take.</summary>
    private void PatchLoopUploader(string stage, nint call)
    {
        if (!Matches(call, OriginalCall) || !Matches(call + OriginalCall.Length, SmallBuffers.LoopPostCall))
        {
            Log($"The {stage} per-shader constant-buffer loop does not look as expected; left alone.");
            return;
        }

        if (!EnsureRoutines(call) || _smallRoutine == 0) return;
        var target = _smallRoutine + SmallBuffers.LoopEntry;
        PatchCall(call, target);
        _smallSites++;
        Log($"Small constant buffers for the {stage} per-shader loop (call at 0x{call:X}, routine at 0x{target:X}).");
    }

    /// <summary>Points the game's sound-slot release call at the routine of <see cref="SoundBankRelease"/>.</summary>
    private void PatchSoundBankRelease()
    {
        var call = _slotRelease;
        if (call == 0 || _releaseAsync == 0)
        {
            Log("Could not find the game's sound-bank release (unsupported game version, or another mod changed it); area changes still wait for sound banks.");
            return;
        }

        var target = call + 5 + *(int*)(call + 1);
        if (*(byte*)call != 0xE8 || (_release != 0 && target != _release))
        {
            Log($"The game's sound-bank release call at 0x{call:X} does not point at CRI's release (another mod?); left alone.");
            return;
        }

        var routine = Place(call, SoundBankRelease.Build(_releaseAsync, target));
        if (routine == 0)
        {
            Log("No memory near the game code for the sound-bank routine; area changes still wait for sound banks.");
            return;
        }

        _banksEnabled = (byte*)(routine + SoundBankRelease.EnabledOffset);
        _asyncReleases = (int*)(routine + SoundBankRelease.CountOffset);
        ApplyBanks(log: false);

        var rel = BitConverter.GetBytes((int)(routine - (call + 5)));
        VirtualProtect(call + 1, 4, 0x40, out var old);
        rel.CopyTo(new Span<byte>((void*)(call + 1), 4));
        VirtualProtect(call + 1, 4, old, out _);
        FlushInstructionCache(GetCurrentProcess(), call, 5);
        Log($"Area changes release sound banks without waiting (call at 0x{call:X}, routine at 0x{routine:X}, " +
            $"criAtomExAcb_ReleaseAsync at 0x{_releaseAsync:X}).");
    }

    /// <summary>Points criFs_ExecuteMain's call at the routine of <see cref="FileWaits"/>.</summary>
    private void PatchFileWaits()
    {
        var function = _fsExecuteMain;
        if (function == 0 || _fsRegister == 0)
        {
            Log("Could not find CRI's file-system ExecuteMain (unsupported game version, or another mod changed it); file waits stay as they are.");
            return;
        }

        var call = function + FileWaits.ServerCallOffset;
        var step = function + FileWaits.ServerStepOffset;
        var lea = _fsRegister + 5;
        var registered = lea + 7 + *(int*)(lea + 3);
        var threadedCall = step + FileWaits.ThreadedCallOffset;
        if (*(byte*)call != 0xE8 || *(byte*)threadedCall != 0xE8 || registered != step)
        {
            Log($"CRI's file-system ExecuteMain at 0x{function:X} does not look as expected; file waits stay as they are.");
            return;
        }

        var serverExecute = call + 5 + *(int*)(call + 1);
        var threadModel = threadedCall + 5 + *(int*)(threadedCall + 1);
        var routine = Place(call, FileWaits.Build(serverExecute, threadModel, step));
        if (routine == 0)
        {
            Log("No memory near the game code for the file-wait routine; file waits stay as they are.");
            return;
        }

        _fileWaitsEnabled = (byte*)(routine + FileWaits.EnabledOffset);
        _fileWaitSteps = (int*)(routine + FileWaits.CountOffset);
        ApplyBanks(log: false);

        var rel = BitConverter.GetBytes((int)(routine - (call + 5)));
        VirtualProtect(call + 1, 4, 0x40, out var old);
        rel.CopyTo(new Span<byte>((void*)(call + 1), 4));
        VirtualProtect(call + 1, 4, old, out _);
        FlushInstructionCache(GetCurrentProcess(), call, 5);
        Log($"File waits run CRI's file-system step right away (criFs_ExecuteMain at 0x{function:X}, routine at 0x{routine:X}, " +
            $"file-system step at 0x{step:X}).");
    }

    /// <summary>Points the <c>Sleep(1)</c> of CRI FS's request wait at <see cref="ShortPolls"/>.</summary>
    private void PatchPolls()
    {
        var call = _fsWait;
        if (call == 0 || *(byte*)call != 0xE8)
        {
            Log("Could not find CRI FS's request wait (unsupported game version, or another mod changed it); its polls stay at 1 ms.");
            return;
        }

        var stub = Place(call, ShortPolls.BuildStub());
        if (stub == 0)
        {
            Log("No memory near the game code for the poll routine; CRI FS's request wait keeps its 1 ms polls.");
            return;
        }

        // Compile the routine now, so the first area change does not pay for it.
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(
            typeof(ShortPolls).GetMethod("PollSleep", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle);
        _shortPolls = true;
        ApplyBanks(log: false);
        var rel = BitConverter.GetBytes((int)(stub - (call + 5)));
        VirtualProtect(call + 1, 4, 0x40, out var old);
        rel.CopyTo(new Span<byte>((void*)(call + 1), 4));
        VirtualProtect(call + 1, 4, old, out _);
        FlushInstructionCache(GetCurrentProcess(), call, 5);
        Log($"CRI FS's request wait polls every 0.1 ms (Sleep call at 0x{call:X}, routine at 0x{stub:X}).");
    }

    /// <summary>Every 30 s with something new, logs how often the area-change routines took over, and how many small
    /// constant buffers there are.</summary>
    private void StartLog()
    {
        _releaseLog = new Timer(_ =>
        {
            var skips = Volatile.Read(ref MapTravel.Skipped) + Volatile.Read(ref MapTravel.Elsewhere);
            if (_mapTravel && skips != _loggedSkips)
            {
                _loggedSkips = skips;
                Log($"Fast travel: {Volatile.Read(ref MapTravel.Skipped)} map closing fades skipped, {Volatile.Read(ref MapTravel.Deferred)} kept for the crowd scene change, " +
                    $"{Volatile.Read(ref MapTravel.Elsewhere)} map fade calls from other scripts (vanilla).");
            }

            var created = Volatile.Read(ref SmallBuffers.Created);
            if (_smallRoutine != 0 && created != _loggedCreated)
            {
                _loggedCreated = created;
                var failed = Volatile.Read(ref SmallBuffers.Failed);
                Log($"Small constant buffers: {created} for {Volatile.Read(ref SmallBuffers.Registered)} of the game's buffers" +
                    (failed > 0 ? $" ({failed} could not be created)" : "") +
                    $"; {SmallBuffers.Uploads} uploads went to them, {SmallBuffers.Others} to the game's own buffers.");
            }

            var releases = _asyncReleases != null ? Volatile.Read(ref *_asyncReleases) : 0;
            var steps = _fileWaitSteps != null ? Volatile.Read(ref *_fileWaitSteps) : 0;
            var polls = Volatile.Read(ref ShortPolls.Polls);
            if (releases == _loggedReleases && steps == _loggedSteps && polls == _loggedPolls) return;
            _loggedReleases = releases;
            _loggedSteps = steps;
            _loggedPolls = polls;
            Log($"So far: {releases} sound banks released without waiting, {steps} file-wait steps run right away, {polls} short file-wait polls.");
        }, null, 30_000, 30_000);
    }

    private void ApplyBanks(bool log = true)
    {
        var on = _configuration.SmoothTransitions && !_suspended;
        if (_banksEnabled != null) Volatile.Write(ref *_banksEnabled, (byte)(on ? 1 : 0));
        if (_fileWaitsEnabled != null) Volatile.Write(ref *_fileWaitsEnabled, (byte)(on ? 1 : 0));
        ShortPolls.Enabled = on;
        MapTravel.Enabled = _configuration.DirectMapTravel && !_suspended;
        if (log && _mapTravel)
            Log(MapTravel.Enabled ? "Fast travel: the loading animation starts over the map." : "Fast travel: the map closes first (vanilla).");
        if (log && (_banksEnabled != null || _fileWaitsEnabled != null || _shortPolls))
            Log(on ? "Area changes: sound banks released without waiting, file waits run right away."
                   : "Area changes: sound banks released and file waits the vanilla way (waiting).");
    }

    /// <summary>A 1 ms Windows timer while smooth transitions are on, so the game's 1 ms load waits wake up on time.</summary>
    private void ApplyTimer()
    {
        var on = _configuration.SmoothTransitions && !_suspended;
        if (on == _timerRaised) return;
        if (on) TimeBeginPeriod(1); else TimeEndPeriod(1);
        _timerRaised = on;
    }

    /// <summary>The current Windows timer resolution of this process, in ms.</summary>
    private static double TimerResolutionMs() =>
        NtQueryTimerResolution(out _, out _, out var current) == 0 ? current / 10000.0 : double.NaN;

    private void ApplyConfig(bool log = true)
    {
        if (_uploadsEnabled != null)
        {
            var on = _configuration.TrimUploads && !_suspended;
            Volatile.Write(ref *_uploadsEnabled, (byte)(on ? 1 : 0));
            if (log)
                Log(on ? $"Constant-buffer uploads to the game's buffers: only the filled part ({_patched} of {Uploaders.Length} uploaders)." : "Constant-buffer uploads to the game's buffers: full (vanilla).");
        }

        if (_smallRoutine != 0)
        {
            var on = _configuration.SmoothTransitions && !_suspended;
            SmallBuffers.Enabled = on;
            if (log)
                Log(on ? $"Small constant buffers: on ({_smallSites} of {Uploaders.Length + LoopUploaders.Length} upload sites)." : "Small constant buffers: off.");
        }
    }

    private string? _logPath;

    /// <summary>Writes to the Reloaded console and to performancefix-log.txt in the mod's folder (cleared at each start).</summary>
    private void Log(string message)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] {message}");
        try
        {
            if (_logPath == null)
            {
                var directory = _modLoader.GetDirectoryForModId(_modConfig.ModId);
                if (string.IsNullOrEmpty(directory)) return;
                _logPath = Path.Combine(directory, "performancefix-log.txt");
                File.WriteAllText(_logPath, $"P5R Performance Fix, session started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            }

            File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // The console still has it.
        }
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Log("Config updated, applying.");
        lock (_gate)
        {
            ApplyConfig();
            ApplyBanks();
            ApplyTimer();
        }
    }

    // The call sites keep pointing at the routine; switching off (or suspending) makes it do the original full upload.
    public override bool CanSuspend() => true;

    public override void Suspend()
    {
        lock (_gate) { _suspended = true; ApplyConfig(); ApplyBanks(); ApplyTimer(); }
    }

    public override void Resume()
    {
        lock (_gate) { _suspended = false; ApplyConfig(); ApplyBanks(); ApplyTimer(); }
    }
    #endregion

    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);
    [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);
    [DllImport("ntdll.dll")] private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
