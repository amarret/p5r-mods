using System.Diagnostics;
using p5rpc.quietregen.Configuration;
using p5rpc.quietregen.Template;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.quietregen;

/// <summary>
/// Silences the sound played when HP/SP is restored at the start of a turn by Regenerate/Invigorate-type passives and
/// accessories. The battle has three copies of that turn-start step; each works out the amounts, rolls the passive
/// that boosts them, then plays the SYSTEM cue 263 ("heal"). The mod removes only that play, by byte patch.
/// Nothing on disk or in the save is changed.
/// </summary>
public unsafe class Mod : ModBase
{
    /// <summary>Cue 263 "heal" in SOUND/SYSTEM.ACB.</summary>
    internal const int HealCue = 0x107;

    /// <summary>How a site is silenced.</summary>
    internal enum PatchKind
    {
        /// <summary><c>mov ecx, 263; call PlaySystemSe</c>: the 5-byte call becomes a 5-byte NOP.</summary>
        NopCall,
        /// <summary><c>mov rdi, [sound]; test rdi, rdi; je skip; ... play 263</c>: the <c>je</c> becomes <c>jmp</c>.</summary>
        JumpOver,
    }

    internal sealed record Site(string Name, string Signature, int Offset, PatchKind Kind);

    /// <summary>
    /// The three turn-start recovery steps. Each signature covers the end of the recovery-boost roll
    /// (amount = amount x boost), the play of cue 263, and the start of what follows.
    /// </summary>
    internal static readonly Site[] Sites =
    [
        new("turn-start recovery #1",
            "66 0F 6E 45 00 0F 5B C0 F3 0F 59 43 0C F3 0F 2C C0 89 45 00 B9 07 01 00 00 E8 ?? ?? ?? ?? 48 8D 95 60 01 00 00 48 8B CE",
            25, PatchKind.NopCall),
        new("turn-start recovery #2",
            "66 0F 6E 85 50 06 00 00 0F 5B C0 F3 0F 59 43 0C F3 0F 2C C0 89 85 50 06 00 00 B9 07 01 00 00 E8 ?? ?? ?? ?? 48 8D 55 28 48 8B CE",
            31, PatchKind.NopCall),
        new("turn-start recovery #3",
            "48 8B 3D ?? ?? ?? ?? 48 85 FF 74 2C 8B 57 08 45 33 C9 8B 4F 04 41 B8 07 01 00 00 C6 44 24 28 01",
            10, PatchKind.JumpOver),
    ];

    private static readonly byte[] Nop5 = [0x0F, 0x1F, 0x44, 0x00, 0x00];

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private readonly object _patchLock = new();
    private readonly List<CodePatch> _patches = new();
    private bool _suspended;

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
            Log("Reloaded.Memory.SigScan.ReloadedII is unavailable, the recovery sound stays.");
            return;
        }

        using var process = Process.GetCurrentProcess();
        var baseAddress = process.MainModule!.BaseAddress;

        foreach (var site in Sites)
        {
            startupScanner.AddMainModuleScan(site.Signature, result =>
            {
                if (!result.Found)
                {
                    Log($"Could not find the {site.Name} (unsupported game version, or another mod has changed it); its sound stays.");
                    return;
                }

                var patch = CreatePatch(site, baseAddress + result.Offset + site.Offset);
                if (patch == null)
                {
                    Log($"The {site.Name} does not look as expected; its sound stays.");
                    return;
                }

                lock (_patchLock)
                {
                    _patches.Add(patch);
                    Apply(patch);
                }

                Log($"Found the {site.Name} at 0x{patch.Address:X}.");
            });
        }
    }

    private T? GetController<T>() where T : class =>
        _modLoader.GetController<T>() is { } weak && weak.TryGetTarget(out var target) ? target : null;

    /// <summary>Builds the patch for a site from the bytes currently at <paramref name="address"/>, or null if they are not the expected instruction.</summary>
    internal static CodePatch? CreatePatch(Site site, nint address)
    {
        var code = (byte*)address;
        switch (site.Kind)
        {
            case PatchKind.NopCall when code[0] == 0xE8:
                return new CodePatch(site.Name, address, new ReadOnlySpan<byte>(code, 5).ToArray(), Nop5);
            case PatchKind.JumpOver when code[0] == 0x74:
                return new CodePatch(site.Name, address, [0x74], [0xEB]);
            default:
                return null;
        }
    }

    private void Apply(CodePatch patch)
    {
        var silence = !_suspended && _configuration.SilenceTurnStartRecovery;
        if (!patch.Set(silence))
            Log($"The {patch.Name} at 0x{patch.Address:X} was changed by something else; left alone.");
    }

    private void ApplyAll()
    {
        lock (_patchLock)
        {
            foreach (var patch in _patches)
                Apply(patch);

            Log(_configuration.SilenceTurnStartRecovery && !_suspended
                ? $"Turn-start recovery sound: silenced ({_patches.Count(p => p.IsApplied)} of {Sites.Length} places)."
                : "Turn-start recovery sound: vanilla.");
        }
    }

    private void Log(string message) => _logger.WriteLine($"[{_modConfig.ModId}] {message}");

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Log("Config updated, applying.");
        ApplyAll();
    }

    public override bool CanSuspend() => true;

    public override void Suspend()
    {
        _suspended = true;
        ApplyAll();
    }

    public override void Resume()
    {
        _suspended = false;
        ApplyAll();
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
