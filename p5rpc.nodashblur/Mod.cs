using System.Diagnostics;
using p5rpc.nodashblur.Configuration;
using p5rpc.nodashblur.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.nodashblur;

/// <summary>
/// Removes the radial blur (and the washed-out colours it causes) from the sprint effect, keeping its speed lines and
/// the camera's FOV change; the blur the same camera effect adds while the camera swings round (recentring); and
/// optionally the target-point leap's blur. See <see cref="BlurRemover"/>. Nothing on disk or in the save is changed.
/// </summary>
public unsafe class Mod : ModBase
{
    /// <summary>Start of <see cref="CameraSpeedEffectFn"/>.</summary>
    internal const string CameraSpeedEffectSig =
        "48 8B C4 48 89 58 08 48 89 78 10 55 48 8D 68 A1 48 81 EC D0 00 00 00 F6 41 0C 01 48 8B D9 F3 0F 10 81 30 01 00 00 F3 0F 58 81 08 03 00 00 0F 29 78 D8";

    /// <summary>
    /// Inside the camera speed effect: the blur colour (white, alpha = intensity x 192) is stored, then
    /// <see cref="SubmitRadialBlurFn"/> is called at <see cref="CameraBlurCallOffset"/>.
    /// </summary>
    internal const string CameraBlurCallSig =
        "F3 0F 59 05 ?? ?? ?? ?? C7 45 B7 00 00 00 00 F3 0F 11 75 BF F3 0F 11 4D C3 F3 0F 2C C0 C7 45 C7 01 00 00 00 66 C7 45 CB FF FF C6 45 CD FF 88 45 CE E8 ?? ?? ?? ??";

    internal const int CameraBlurCallOffset = 49;
    internal const int CameraSpeedEffectLength = 0x642;

    /// <summary>
    /// Further down the camera speed effect, while the camera swings round: the turn blur's parameters (1.0, mode 1, white
    /// with the opacity worked out from the turning speed) are stored, then <see cref="SubmitTurnBlurFn"/> is called at
    /// <see cref="CameraTurnCallOffset"/>.
    /// </summary>
    internal const string CameraTurnCallSig =
        "C7 45 B7 00 00 80 3F C7 45 C3 01 00 00 00 66 C7 45 C7 FF FF F3 0F 2C C0 C6 45 C9 FF 88 45 CA E8 ?? ?? ?? ??";

    internal const int CameraTurnCallOffset = 31;

    /// <summary>Start of <see cref="DashFilterUpdateFn"/>.</summary>
    internal const string DashFilterUpdateSig =
        "40 53 48 83 EC 60 48 8B 59 48 33 C9 0F 29 74 24 50 0F 28 F1 0F 29 7C 24 40 E8 ?? ?? ?? ?? 48 8B C8 E8 ?? ?? ?? ?? 0F B7 53 20";

    /// <summary>
    /// Inside the dash filter update: the last blur parameters (colour 0xC0FFFFFF, radius 900, strength, centre) are
    /// stored, then <see cref="SubmitRadialBlurFn"/> is called at <see cref="DashBlurCallOffset"/>.
    /// </summary>
    internal const string DashBlurCallSig =
        "C7 44 24 34 FF FF FF C0 F3 0F 10 05 ?? ?? ?? ?? F3 0F 11 4C 24 24 F3 0F 11 7C 24 28 F3 0F 11 44 24 2C E8 ?? ?? ?? ??";

    internal const int DashBlurCallOffset = 34;
    internal const int DashFilterUpdateLength = 0x1E2;

    /// <summary>One effect whose blur can be removed: its update function and the blur call inside it.</summary>
    private sealed class Source(BlurSource kind, string name, string functionSig, string callSig, int callOffset, int length)
    {
        public BlurSource Kind { get; } = kind;
        public string Name { get; } = name;
        public string FunctionSig { get; } = functionSig;
        public string CallSig { get; } = callSig;
        public int CallOffset { get; } = callOffset;
        public int Length { get; } = length;
        public nint Function { get; set; }
        public nint Call { get; set; }
        public IHook? Hook { get; set; }
    }

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private readonly BlurRemover _remover = new();
    private readonly object _installLock = new();
    private readonly Source[] _sources =
    [
        new(BlurSource.Sprint, "sprint camera effect", CameraSpeedEffectSig, CameraBlurCallSig, CameraBlurCallOffset, CameraSpeedEffectLength),
        new(BlurSource.TargetLeap, "target-point leap effect", DashFilterUpdateSig, DashBlurCallSig, DashBlurCallOffset, DashFilterUpdateLength),
    ];
    private int _scansDone;
    private nint _turnCall;
    private IHook<SubmitRadialBlurFn>? _submitRadialBlurHook;
    private IHook<SubmitTurnBlurFn>? _submitTurnBlurHook;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        _remover.FirstDrop = source => Log(source switch
        {
            BlurSource.Sprint => "Removed the sprint blur (shown once per session).",
            BlurSource.CameraTurn => "Removed the camera-turn blur (shown once per session).",
            _ => "Removed the target-point leap blur (shown once per session).",
        });
        ApplyConfig(_configuration);

        if (_hooks == null)
        {
            Log("Reloaded.SharedLib.Hooks is unavailable, the blur stays.");
            return;
        }

        var startupScanner = GetController<IStartupScanner>();
        if (startupScanner == null)
        {
            Log("Reloaded.Memory.SigScan.ReloadedII is unavailable, the blur stays.");
            return;
        }

        using var process = Process.GetCurrentProcess();
        var baseAddress = process.MainModule!.BaseAddress;

        foreach (var source in _sources)
        {
            startupScanner.AddMainModuleScan(source.FunctionSig, result =>
            {
                lock (_installLock)
                {
                    if (result.Found)
                        source.Function = baseAddress + result.Offset;
                    ScanDone();
                }
            });

            startupScanner.AddMainModuleScan(source.CallSig, result =>
            {
                lock (_installLock)
                {
                    if (result.Found)
                        source.Call = baseAddress + result.Offset + source.CallOffset;
                    ScanDone();
                }
            });
        }

        startupScanner.AddMainModuleScan(CameraTurnCallSig, result =>
        {
            lock (_installLock)
            {
                if (result.Found)
                    _turnCall = baseAddress + result.Offset + CameraTurnCallOffset;
                ScanDone();
            }
        });
    }

    private T? GetController<T>() where T : class =>
        _modLoader.GetController<T>() is { } weak && weak.TryGetTarget(out var target) ? target : null;

    private void ScanDone()
    {
        if (++_scansDone < _sources.Length * 2 + 1)
            return;

        // Keep the sources whose function and blur call were both found, with the call inside the function.
        nint submitRadialBlur = 0;
        var usable = new List<Source>();
        foreach (var source in _sources)
        {
            if (source.Function == 0 || source.Call == 0)
            {
                Log($"Could not find the {source.Name} (unsupported game version, or another mod has changed it); its blur stays.");
                continue;
            }

            var callOffset = source.Call - source.Function;
            var target = CallTarget(source.Call);
            if (callOffset < 0 || callOffset >= source.Length || (submitRadialBlur != 0 && target != submitRadialBlur))
            {
                Log($"The {source.Name} at 0x{source.Function:X} does not look as expected (unsupported game version); its blur stays.");
                continue;
            }

            submitRadialBlur = target;
            usable.Add(source);
        }

        if (usable.Count == 0)
            return;

        // The camera-turn blur is told apart by the camera speed effect running, so it needs that hook too.
        var speedEffect = usable.FirstOrDefault(s => s.Kind == BlurSource.Sprint);
        nint submitTurnBlur = 0;
        if (speedEffect == null || _turnCall == 0)
            Log("Could not find the camera-turn blur (unsupported game version, or another mod has changed it); it stays.");
        else if (_turnCall - speedEffect.Function is var turnOffset && (turnOffset < 0 || turnOffset >= speedEffect.Length))
            Log($"The camera-turn blur call at 0x{_turnCall:X} is not inside the camera speed effect (unsupported game version); it stays.");
        else
            submitTurnBlur = CallTarget(_turnCall);

        // Set the originals before activating, so the replacements never run without them.
        _submitRadialBlurHook = _hooks!.CreateHook<SubmitRadialBlurFn>(_remover.SubmitRadialBlur, submitRadialBlur);
        _remover.OriginalSubmitRadialBlur = _submitRadialBlurHook.OriginalFunction;
        if (submitTurnBlur != 0)
        {
            _submitTurnBlurHook = _hooks.CreateHook<SubmitTurnBlurFn>(_remover.SubmitTurnBlur, submitTurnBlur);
            _remover.OriginalSubmitTurnBlur = _submitTurnBlurHook.OriginalFunction;
        }
        foreach (var source in usable)
        {
            if (source.Kind == BlurSource.Sprint)
            {
                var hook = _hooks.CreateHook<CameraSpeedEffectFn>(_remover.CameraSpeedEffect, source.Function);
                _remover.OriginalCameraSpeedEffect = hook.OriginalFunction;
                source.Hook = hook;
            }
            else
            {
                var hook = _hooks.CreateHook<DashFilterUpdateFn>(_remover.DashFilterUpdate, source.Function);
                _remover.OriginalDashFilterUpdate = hook.OriginalFunction;
                source.Hook = hook;
            }
        }

        _submitRadialBlurHook.Activate();
        _submitTurnBlurHook?.Activate();
        foreach (var source in usable)
        {
            source.Hook!.Activate();
            Log($"Hooked the {source.Name} at 0x{source.Function:X}.");
        }

        Log($"Hooked the radial blur at 0x{submitRadialBlur:X}.");
        if (submitTurnBlur != 0)
            Log($"Hooked the camera-turn blur at 0x{submitTurnBlur:X}.");
    }

    /// <summary>Target of the <c>call rel32</c> (E8) instruction at <paramref name="call"/>.</summary>
    internal static nint CallTarget(nint call) => call + 5 + *(int*)(call + 1);

    private void ApplyConfig(Config config)
    {
        _remover.RemoveSprint = config.RemoveSprintBlur;
        _remover.RemoveTurn = config.RemoveTurnBlur;
        _remover.RemoveTargetLeap = config.RemoveTargetLeapBlur;
        Log($"Sprint blur: {(config.RemoveSprintBlur ? "removed" : "vanilla")}. " +
            $"Camera-turn blur: {(config.RemoveTurnBlur ? "removed" : "vanilla")}. " +
            $"Target-point leap blur: {(config.RemoveTargetLeapBlur ? "removed" : "vanilla")}.");
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
        foreach (var source in _sources)
            source.Hook?.Disable();
        _submitRadialBlurHook?.Disable();
        _submitTurnBlurHook?.Disable();
    }

    public override void Resume()
    {
        _submitRadialBlurHook?.Enable();
        _submitTurnBlurHook?.Enable();
        foreach (var source in _sources)
            source.Hook?.Enable();
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
