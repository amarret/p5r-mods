using System.Diagnostics;
using p5rpc.setpalacesecurity.Configuration;
using p5rpc.setpalacesecurity.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.setpalacesecurity;

/// <summary>
/// Keeps the Palace security level at a chosen value (see <see cref="SecurityLevel"/>). Nothing on disk is changed;
/// the level itself is saved with the game as usual.
/// </summary>
public unsafe class Mod : ModBase
{
    /// <summary>Start of the security setter (<see cref="SetAlertFn"/>): clamps to 0-100, then the calling-card minimum.</summary>
    internal const string SetAlertSig =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 33 DB 48 63 F1 8B FA 85 D2 79 04 8B FB EB 0A B8 64 00 00 00 3B F8 0F 4F F8 B9 50 00 00 20 E8";

    /// <summary>
    /// In the setter: <c>call BIT_CHK</c> (for flag 0x20000050), <c>mov edx, [r8+rsi*4+counts]</c> (r8 = image base), then
    /// <c>mov rax, [rip+owner]</c> and <c>mov rcx, [rax+0x48]</c>, the security meter.
    /// </summary>
    internal const int FlagCheckCallOffset = 0x2D, CountsLoadOffset = 0x66, MeterOwnerLoadOffset = 0x6E, MeterLoadOffset = 0x75;

    /// <summary>Start of the generic COUNT setter (<see cref="SetCountFn"/>), up to its store into the counts.</summary>
    internal const string SetCountSig =
        "4C 8D 0D ?? ?? ?? ?? 33 C0 4D 8B C1 0F 1F 40 00 41 39 08 74 ?? FF C0 49 83 C0 08 83 F8 02 72 ?? 48 63 C1 48 8D 0D ?? ?? ?? ?? 89 94 81";

    /// <summary>Start of the security meter's refresh (<see cref="RefreshAlertHudFn"/>).</summary>
    internal const string RefreshAlertHudSig =
        "40 57 48 83 EC 30 48 8D 0D ?? ?? ?? ?? 33 FF E8 ?? ?? ?? ?? 48 85 C0 75 06 48 83 C4 30 5F C3";

    /// <summary>Start of the function that turns the security meter on or off (<see cref="ActivateMeterFn"/>).</summary>
    internal const string ActivateMeterSig =
        "48 89 5C 24 08 57 48 83 EC 20 48 8B 05 ?? ?? ?? ?? 0F B6 D9 BA 00 04 00 00 48 8B 78 48 48 8D 8F E0 00 00 00 E8";

    /// <summary>Start of the function that fades the security meter in or out (<see cref="ShowMeterFn"/>, FLD_ALERT_VISIBLE).</summary>
    internal const string ShowMeterSig =
        "40 53 48 83 EC 40 48 8B 05 ?? ?? ?? ?? 48 8B 58 48 48 63 83 E0 04 00 00 48 83 C0 0E 48 03 C0 8B 14 C3 83 FA 03 74";

    /// <summary>
    /// FLD_CHECK_DUNGEON's handler: <c>call GetField</c>, <c>mov rcx, [rax+0x48]</c>, <c>movzx eax, word [rcx+major]</c>,
    /// then true for majors 100-199.
    /// </summary>
    internal const string CheckDungeonSig =
        "48 83 EC 28 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B 48 48 48 85 C9 74 ?? 0F B7 81 ?? ?? ?? ?? 33 D2 48 8B 0D ?? ?? ?? ?? 66 83 E8 64 66 83 F8 63";

    internal const int FieldGetterCallOffset = 4, MajorLoadOffset = 0x17;

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private readonly SecurityLevel _security = new();
    private readonly object _installLock = new();
    private nint _setAlert, _setCount, _refresh, _activate, _show, _checkDungeon;
    private EventLog? _eventLog;
    private int _scansDone;
    private readonly List<IHook> _installed = new();

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        _security.FirstHeld = value => Log($"Holding the security level at {value}% (shown once per session).");
        ApplyConfig(_configuration);

        if (_hooks == null)
        {
            Log("Reloaded.SharedLib.Hooks is unavailable; the security level stays vanilla.");
            return;
        }

        var startupScanner = GetController<IStartupScanner>();
        if (startupScanner == null)
        {
            Log("Reloaded.Memory.SigScan.ReloadedII is unavailable; the security level stays vanilla.");
            return;
        }

        using var process = Process.GetCurrentProcess();
        var baseAddress = process.MainModule!.BaseAddress;
        void Scan(string signature, int offset, Action<nint> found) => startupScanner.AddMainModuleScan(signature, result =>
        {
            lock (_installLock)
            {
                if (result.Found) found(baseAddress + result.Offset + offset);
                ScanDone(baseAddress);
            }
        });

        Scan(SetAlertSig, 0, a => _setAlert = a);
        Scan(SetCountSig, 0, a => _setCount = a);
        Scan(RefreshAlertHudSig, 0, a => _refresh = a);
        Scan(ActivateMeterSig, 0, a => _activate = a);
        Scan(ShowMeterSig, 0, a => _show = a);
        Scan(CheckDungeonSig, 0, a => _checkDungeon = a);
    }

    private T? GetController<T>() where T : class =>
        _modLoader.GetController<T>() is { } weak && weak.TryGetTarget(out var target) ? target : null;

    private void ScanDone(nint baseAddress)
    {
        if (++_scansDone < 6) return;

        // The setter is needed: it holds the addresses of the counts and the meter, and the flag check, and changes the level
        // most often.
        var setter = (byte*)_setAlert;
        if (setter == null || setter[FlagCheckCallOffset] != 0xE8 || !Matches(setter + CountsLoadOffset, 0x41, 0x8B, 0x94, 0xB0) ||
            !Matches(setter + MeterOwnerLoadOffset, 0x48, 0x8B, 0x05) || !Matches(setter + MeterLoadOffset, 0x48, 0x8B, 0x48, SecurityLevel.MeterOffset))
        {
            Log("Could not find the game's security-level setter (unsupported game version, or another mod has changed it); the security level stays vanilla.");
            return;
        }

        _security.Counts = (int*)(baseAddress + *(int*)(setter + CountsLoadOffset + 4));
        _security.MeterOwner = (nint*)(_setAlert + MeterOwnerLoadOffset + 7 + *(int*)(setter + MeterOwnerLoadOffset + 3));
        _security.FlagCheck = (delegate* unmanaged<uint, byte>)CallTarget(_setAlert + FlagCheckCallOffset);

        // Where the player is: the game's own Palace test. Without it, the meter being in use stands in for it.
        if (_checkDungeon != 0)
        {
            _security.GetField = (delegate* unmanaged<nint>)CallTarget(_checkDungeon + FieldGetterCallOffset);
            _security.MajorOffset = *(int*)(_checkDungeon + MajorLoadOffset + 3);
        }
        else Log("Could not find the game's Palace check (FLD_CHECK_DUNGEON); the mod acts while the security meter is in use instead.");

        // Set the originals before activating, so the replacements never run without them.
        var hooks = _hooks!;
        var setAlert = hooks.CreateHook<SetAlertFn>(_security.SetAlert, _setAlert);
        _security.OriginalSetAlert = setAlert.OriginalFunction;
        _installed.Add(setAlert);
        var found = new List<string> { $"setter at 0x{_setAlert:X}" };

        if (_setCount != 0)
        {
            var setCount = hooks.CreateHook<SetCountFn>(_security.SetCount, _setCount);
            _security.OriginalSetCount = setCount.OriginalFunction;
            _installed.Add(setCount);
            found.Add($"count setter at 0x{_setCount:X}");
        }
        else Log("Could not find the game's generic count setter; a script that sets the level directly is corrected at the next change.");

        if (_refresh != 0)
        {
            var refresh = hooks.CreateHook<RefreshAlertHudFn>(_security.RefreshAlertHud, _refresh);
            _security.OriginalRefreshAlertHud = refresh.OriginalFunction;
            _installed.Add(refresh);
            found.Add($"meter refresh at 0x{_refresh:X}");
        }
        else Log("Could not find the security meter's refresh; the meter may show other values until it next updates.");

        if (_activate != 0)
        {
            var activate = hooks.CreateHook<ActivateMeterFn>(_security.ActivateMeter, _activate);
            _security.OriginalActivateMeter = activate.OriginalFunction;
            _installed.Add(activate);
            found.Add($"meter switch at 0x{_activate:X}");
        }
        else Log("Could not find the security meter's switch; on entering a Palace the level is set at its first change.");

        if (_show != 0)
        {
            var show = hooks.CreateHook<ShowMeterFn>(_security.ShowMeter, _show);
            _security.OriginalShowMeter = show.OriginalFunction;
            _installed.Add(show);
            found.Add($"meter fade at 0x{_show:X}");
        }
        else Log("Could not find the security meter's fade; after an event the level is set at its next change.");

        foreach (var hook in _installed) hook.Activate();
        Log($"Hooked the security level ({string.Join(", ", found)}; counts at 0x{(nint)_security.Counts:X}, meter at 0x{(nint)_security.MeterOwner:X}, " +
            (_checkDungeon != 0 ? $"Palace check at 0x{_checkDungeon:X}, field major at +0x{_security.MajorOffset:X})." : "no Palace check)."));
    }

    private static bool Matches(byte* code, params byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            if (code[i] != bytes[i]) return false;
        return true;
    }

    /// <summary>Target of the <c>call rel32</c> (E8) instruction at <paramref name="call"/>.</summary>
    internal static nint CallTarget(nint call) => call + 5 + *(int*)(call + 1);

    private void ApplyConfig(Config config)
    {
        _security.Level = config.Level;
        _security.Enabled = config.FixLevel;
        Log(config.FixLevel ? $"Security level fixed at {_security.Level}%." : "Security level: vanilla.");

        if (config.LogEvents && _eventLog == null)
        {
            try
            {
                var directory = _modLoader.GetDirectoryForModId(_modConfig.ModId);
                if (!string.IsNullOrEmpty(directory)) _eventLog = new EventLog(Path.Combine(directory, "events.log"));
            }
            catch (Exception e)
            {
                Log($"Could not open events.log ({e.Message}); events go to the console only.");
            }
        }

        _security.Trace = config.LogEvents
            ? line =>
            {
                Log(line);
                _eventLog?.Write(line);
            }
            : null;
    }

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
        foreach (var hook in _installed) hook.Disable();
    }

    public override void Resume()
    {
        foreach (var hook in _installed) hook.Enable();
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
