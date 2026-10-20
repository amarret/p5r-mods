using System.Diagnostics;
using System.Runtime.InteropServices;
using p5rpc.confidanttoggles.Configuration;
using p5rpc.confidanttoggles.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.confidanttoggles;

/// <summary>
/// Disables confidant abilities and Showtimes by filtering the two game functions that answer
/// "is this ability active?" and "do these two characters have a Showtime?", and can stop Futaba from
/// taking over All-Out Attacks.
/// Save data (ranks, unlock flags) is never written, so switching a toggle back on restores vanilla behaviour.
/// </summary>
public unsafe class Mod : ModBase
{
    /// <summary>
    /// int CmmCheckEnableFunc(int functionId): returns &gt; 0 when the confidant ability is active.
    /// Every battle/field check goes through it, as do the CMM_CHECK_ENABLE_FUNC(_DETAIL) flowscript functions.
    /// </summary>
    private const string CmmCheckEnableFuncSig =
        "40 53 55 56 41 54 41 56 48 83 EC 20 4C 8B 35 ?? ?? ?? ?? 41 BC 01 00 00 00 48 63 E9 45 0F B7 CC 45 8B 5E 10 47 8B 54 33 08";

    /// <summary>
    /// int GetShowtimeIndex(short unitA, short unitB): index into the game's 8-entry Showtime table when the pair
    /// has an unlocked Showtime, -1 otherwise. All battle-side Showtime checks call it.
    /// </summary>
    private const string GetShowtimeIndexSig =
        "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 4C 89 64 24 20 55 41 56 41 57 48 8B EC 48 83 EC 40 44 8B 35 ?? ?? ?? ?? 4C 8D 55 C2 41 BC 01 00 00 00 C7 45 C0 03 00 04 00 33 DB C7 45 C4 05 01 00 30";

    /// <summary>
    /// The All-Out Attack sequence rolls rand() % 100 against a 50/70% chance and, on success, runs
    /// <c>mov bl, 1</c> to switch to Futaba's bomb takeover (otherwise <c>xor bl, bl</c>).
    /// </summary>
    private const string FutabaTakeoverRollSig =
        "44 8B C0 B8 1F 85 EB 51 41 F7 E0 C1 EA 05 6B CA 64 44 2B C1 44 3B C3 73 ?? B3 01 EB ?? 32 DB";

    /// <summary>Offset of the <c>mov bl, 1</c> immediate inside <see cref="FutabaTakeoverRollSig"/>.</summary>
    private const int FutabaTakeoverRollImmOffset = 26;

    [Function(CallingConventions.Microsoft)]
    private delegate int CmmCheckEnableFuncFn(int functionId);

    [Function(CallingConventions.Microsoft)]
    private delegate int GetShowtimeIndexFn(int unitA, int unitB);

    private const int AbilityIdCount = 512;
    private const int ShowtimeCount = 16;

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private IHook<CmmCheckEnableFuncFn>? _cmmCheckEnableFuncHook;
    private IHook<GetShowtimeIndexFn>? _getShowtimeIndexHook;

    // Address of the "takeover succeeded" byte; 0 until found. Writing 0 there makes a successful roll
    // behave exactly like a failed one, so the normal All-Out Attack plays.
    private nint _futabaTakeoverImm;
    private bool _suspended;
    private readonly object _patchLock = new();

    // Replaced as a whole when the config changes, read lock-free from game threads.
    private volatile bool[] _disabledAbilities = new bool[AbilityIdCount];
    private volatile bool[] _disabledShowtimes = new bool[ShowtimeCount];

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        ApplyConfig(_configuration);

        if (_hooks == null)
        {
            Log("Reloaded.SharedLib.Hooks is unavailable, nothing will be toggled.");
            return;
        }

        var scannerController = _modLoader.GetController<IStartupScanner>();
        if (scannerController == null || !scannerController.TryGetTarget(out var scanner))
        {
            Log("Reloaded.Memory.SigScan.ReloadedII is unavailable, nothing will be toggled.");
            return;
        }

        var baseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress.ToInt64();

        scanner.AddMainModuleScan(CmmCheckEnableFuncSig, result =>
        {
            if (!result.Found)
            {
                Log("Could not find the confidant ability check (unsupported game version?). Abilities will not be toggled.");
                return;
            }

            var address = baseAddress + result.Offset;
            _cmmCheckEnableFuncHook = _hooks.CreateHook<CmmCheckEnableFuncFn>(CmmCheckEnableFunc, address).Activate();
            Log($"Hooked confidant ability check at 0x{address:X}.");
        });

        scanner.AddMainModuleScan(GetShowtimeIndexSig, result =>
        {
            if (!result.Found)
            {
                Log("Could not find the Showtime check (unsupported game version?). Showtimes will not be toggled.");
                return;
            }

            var address = baseAddress + result.Offset;
            _getShowtimeIndexHook = _hooks.CreateHook<GetShowtimeIndexFn>(GetShowtimeIndex, address).Activate();
            Log($"Hooked Showtime check at 0x{address:X}.");
        });

        scanner.AddMainModuleScan(FutabaTakeoverRollSig, result =>
        {
            var address = (nint)(baseAddress + result.Offset + FutabaTakeoverRollImmOffset);
            if (!result.Found || *(byte*)(address - 1) != 0xB3 || *(byte*)address != 1)
            {
                Log("Could not find Futaba's All-Out Attack takeover roll (unsupported game version?). It will not be toggled.");
                return;
            }

            lock (_patchLock)
                _futabaTakeoverImm = address;
            Log($"Found Futaba's All-Out Attack takeover roll at 0x{address - 1:X}.");
            ApplyFutabaTakeover();
        });
    }

    private int CmmCheckEnableFunc(int functionId)
    {
        var disabled = _disabledAbilities;
        if ((uint)functionId < (uint)disabled.Length && disabled[functionId])
            return 0; // "Not unlocked yet"; the rank and flags in the save stay untouched.

        return _cmmCheckEnableFuncHook!.OriginalFunction(functionId);
    }

    private int GetShowtimeIndex(int unitA, int unitB)
    {
        var index = _getShowtimeIndexHook!.OriginalFunction(unitA, unitB);
        var disabled = _disabledShowtimes;
        if ((uint)index < (uint)disabled.Length && disabled[index])
            return -1; // Same answer as for a pair whose Showtime is still locked.

        return index;
    }

    private void ApplyConfig(Config config)
    {
        var abilities = new bool[AbilityIdCount];
        foreach (var id in config.GetDisabledAbilityIds())
        {
            if ((uint)id < AbilityIdCount)
                abilities[id] = true;
        }

        var showtimes = new bool[ShowtimeCount];
        foreach (var index in config.GetDisabledShowtimeIndices())
        {
            if ((uint)index < ShowtimeCount)
                showtimes[index] = true;
        }

        _disabledAbilities = abilities;
        _disabledShowtimes = showtimes;
        Log($"{abilities.Count(x => x)} ability IDs and {showtimes.Count(x => x)} Showtimes disabled, " +
            $"Futaba's All-Out Attack takeover {(config.Futaba_AllOutAttackTakeover ? "on" : "off")}.");
        ApplyFutabaTakeover();
    }

    private void ApplyFutabaTakeover()
    {
        lock (_patchLock)
        {
            if (_futabaTakeoverImm == 0)
                return;

            var allowed = _suspended || _configuration.Futaba_AllOutAttackTakeover;
            WriteCodeByte(_futabaTakeoverImm, allowed ? (byte)1 : (byte)0);
        }
    }

    private static void WriteCodeByte(nint address, byte value)
    {
        if (*(byte*)address == value)
            return;

        VirtualProtect(address, 1, PageExecuteReadWrite, out var oldProtect);
        *(byte*)address = value;
        VirtualProtect(address, 1, oldProtect, out _);
        FlushInstructionCache(GetCurrentProcess(), address, 1);
    }

    private const uint PageExecuteReadWrite = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    private void Log(string message) => _logger.WriteLine($"[{_modConfig.ModId}] {message}");

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Log("Config updated, applying.");
        ApplyConfig(configuration);
    }

    public override bool CanSuspend() => true;

    public override void Suspend()
    {
        _cmmCheckEnableFuncHook?.Disable();
        _getShowtimeIndexHook?.Disable();
        lock (_patchLock)
            _suspended = true;
        ApplyFutabaTakeover();
    }

    public override void Resume()
    {
        _cmmCheckEnableFuncHook?.Enable();
        _getShowtimeIndexHook?.Enable();
        lock (_patchLock)
            _suspended = false;
        ApplyFutabaTakeover();
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
