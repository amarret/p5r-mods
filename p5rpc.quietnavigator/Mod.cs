using System.Diagnostics;
using p5rpc.quietnavigator.Configuration;
using p5rpc.quietnavigator.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.quietnavigator;

/// <summary>
/// Silences navigator battle lines by the battle event that triggered them, before they are voiced or shown.
///
/// The navigator class gets one virtual call per battle event (see <see cref="EventSlots"/>). For critical hits and
/// knockdowns the mod lets the original handler run, notes which lines it queued, and drops those lines when the
/// battle's per-frame line routine is about to start them. The analysis comment is played immediately by its handler,
/// so that handler is skipped instead. Nothing on disk or in the save is changed.
/// </summary>
public unsafe class Mod : ModBase
{
    /// <summary>
    /// int PlayNavigatorLine(entry, battle, int navigatorId, float frameTime), called from the battle's per-frame loop.
    /// Returns 0 = call again, 1 = waiting, 2 = finished (entry is freed), 3 = busy.
    /// </summary>
    private const string PlayNavigatorLineSig =
        "48 89 5C 24 10 48 89 74 24 18 48 89 7C 24 20 55 41 54 41 55 41 56 41 57 48 8D 6C 24 90 48 81 EC 70 01 00 00 45 33 E4 45 8B F0 44 8B 41 24 48 8B F9";

    /// <summary>
    /// bool MakotoGivesAnalysis(?, battle): true when Makoto, not the navigator, voices the analysis comment.
    /// </summary>
    private const string MakotoGivesAnalysisSig =
        "48 89 5C 24 10 48 89 74 24 18 48 89 7C 24 20 55 41 54 41 55 41 56 41 57 48 8B EC 48 81 EC 80 00 00 00 45 33 FF B9 34 00 00 40";

    private const int LineFinished = 2;

    // The navigator object keeps its queued lines in a linked list: head at +0x10, tail at +0x18.
    // Each node is { next, prev, payload... } with the line entry 0x10 bytes in.
    private const int ListHeadOffset = 0x10;
    private const int ListTailOffset = 0x18;
    private const int NodePayloadOffset = 0x10;
    private const int MaxNewLinesPerEvent = 32;

    [Function(CallingConventions.Microsoft)]
    private delegate int PlayNavigatorLineFn(nint entry, nint battle, int navigatorId, float frameTime);

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private readonly NavigatorClass[] _classes =
    [
        new() { Name = "MORGANA", Speaker = Speakers.Morgana },
        new() { Name = "FUTABA", Speaker = Speakers.Futaba },
        new() { Name = "AKECHI", Speaker = Speakers.Akechi },
    ];

    private readonly (int Slot, nint Replacement)[] _slots =
    [
        (EventSlots.PartyStatus, EventHandlers.PartyStatusPointer),
        (EventSlots.Knockdown, EventHandlers.KnockdownPointer),
        (EventSlots.CriticalHit, EventHandlers.CriticalHitPointer),
        (EventSlots.Analysis, EventHandlers.AnalysisPointer),
        (EventSlots.BatonPass, EventHandlers.BatonPassPointer),
    ];

    private readonly EventTags _tags = new();
    private readonly object _patchLock = new();
    private IHook<PlayNavigatorLineFn>? _playNavigatorLineHook;
    private PlayNavigatorLineFn? _originalPlayNavigatorLine;
    private nint _makotoGivesAnalysis;
    private bool _slotsPatched;
    private volatile LineFilter _filter = LineFilter.None;

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
            Log("Reloaded.SharedLib.Hooks is unavailable, nothing will be silenced.");
            return;
        }

        var startupScanner = GetController<IStartupScanner>();
        if (startupScanner == null)
        {
            Log("Reloaded.Memory.SigScan.ReloadedII is unavailable, nothing will be silenced.");
            return;
        }

        using var process = Process.GetCurrentProcess();
        var module = process.MainModule!;
        var baseAddress = module.BaseAddress;

        // Navigator classes, found from their RTTI names.
        foreach (var navigator in _classes)
        {
            navigator.VTable = Rtti.FindVTable(baseAddress, $".?AV{navigator.Name}@support@btl@@");
            if (navigator.VTable == 0)
                Log($"Could not find the {navigator.Name} navigator class; its lines will not be silenced.");
        }

        EventHandlers.Owner = this;
        PatchSlots(true);

        startupScanner.AddMainModuleScan(PlayNavigatorLineSig, result =>
        {
            if (!result.Found)
            {
                Log("Could not find the navigator line routine (unsupported game version, or another mod has replaced it). " +
                    "Critical-hit and knockdown lines will not be silenced.");
                return;
            }

            var address = baseAddress + result.Offset;
            _playNavigatorLineHook = _hooks.CreateHook<PlayNavigatorLineFn>(PlayNavigatorLine, address).Activate();
            _originalPlayNavigatorLine = _playNavigatorLineHook.OriginalFunction;
            Log($"Hooked the navigator line routine at 0x{address:X}.");
        });

        startupScanner.AddMainModuleScan(MakotoGivesAnalysisSig, result =>
        {
            if (!result.Found)
            {
                Log("Could not find the Makoto analysis check; analysis comments are attributed to the navigator.");
                return;
            }

            _makotoGivesAnalysis = baseAddress + result.Offset;
        });
    }

    private T? GetController<T>() where T : class =>
        _modLoader.GetController<T>() is { } weak && weak.TryGetTarget(out var target) ? target : null;

    #region Battle events

    /// <summary>
    /// Events that queue their lines: run the original, then tag the lines it queued (only those whose message
    /// passes <paramref name="onlyMessages"/>, when given).
    /// </summary>
    internal nint OnQueuedEvent(LineKind kind, int slot, nint self, nint battle, nint a3, nint a4, nint a5, nint a6,
        Func<int, bool>? onlyMessages = null)
    {
        var navigator = ClassOf(self);
        if (navigator == null || !navigator.Originals.TryGetValue(slot, out var original))
            return 0;

        var oldTail = *(nint*)(self + ListTailOffset);
        var result = EventHandlers.Call(original, self, battle, a3, a4, a5, a6);

        try
        {
            var node = oldTail != 0 ? *(nint*)oldTail : *(nint*)(self + ListHeadOffset);
            for (var i = 0; node != 0 && i < MaxNewLinesPerEvent; i++, node = *(nint*)node)
            {
                var entry = node + NodePayloadOffset;
                var message = LineEntry.MessageId(entry);
                if (onlyMessages == null || onlyMessages(message))
                    _tags.Add(entry, kind, message);
            }
        }
        catch (Exception exception)
        {
            Log($"Could not read the queued lines: {exception.Message}");
        }

        return result;
    }

    /// <summary>Enemy analysed: the comment is played straight away, so skip the handler when it is silenced.</summary>
    internal nint OnAnalysisEvent(nint self, nint battle, nint a3, nint a4, nint a5, nint a6)
    {
        var navigator = ClassOf(self);
        if (navigator == null || !navigator.Originals.TryGetValue(EventSlots.Analysis, out var original))
            return 0;

        var filter = _filter;
        if (filter.IsOn(LineKind.Analysis))
        {
            var speaker = MakotoGivesAnalysis(battle) ? Speakers.Makoto : navigator.Speaker;
            if (filter.Silences(LineKind.Analysis, speaker))
            {
                Log($"Silenced an analysis comment by {Speakers.Name(speaker)}.");
                return 0;
            }
        }

        var result = EventHandlers.Call(original, self, battle, a3, a4, a5, a6);
        _tags.Clear(); // A played analysis comment clears the navigator's queue.
        return result;
    }

    private bool MakotoGivesAnalysis(nint battle)
    {
        if (_makotoGivesAnalysis == 0)
            return false;

        return ((delegate* unmanaged<nint, nint, byte>)_makotoGivesAnalysis)(0, battle) != 0;
    }

    private NavigatorClass? ClassOf(nint self)
    {
        var vtable = *(nint*)self;
        foreach (var navigator in _classes)
        {
            if (navigator.VTable == vtable)
                return navigator;
        }

        return null;
    }

    private int PlayNavigatorLine(nint entry, nint battle, int navigatorId, float frameTime)
    {
        if (entry != 0 && LineEntry.NotStarted(entry))
        {
            var message = LineEntry.MessageId(entry);
            if (_tags.TryGet(entry, message, out var kind))
            {
                var speaker = LineEntry.Speaker(entry, navigatorId);
                if (_filter.Silences(kind, speaker))
                {
                    _tags.Remove(entry);
                    Log($"Silenced {kind} line btl_support_{speaker:00}_{message + 1:000} by {Speakers.Name(speaker)}.");
                    return LineFinished;
                }
            }
        }

        var result = _originalPlayNavigatorLine!(entry, battle, navigatorId, frameTime);
        if (result == LineFinished)
            _tags.Remove(entry);

        return result;
    }

    #endregion

    private void PatchSlots(bool install)
    {
        lock (_patchLock)
        {
            if (_slotsPatched == install)
                return;

            foreach (var navigator in _classes)
            {
                if (navigator.VTable == 0)
                    continue;

                foreach (var (slot, replacement) in _slots)
                {
                    if (install)
                        navigator.Originals[slot] = VTablePatcher.Exchange(navigator.VTable, slot, replacement);
                    else if (navigator.Originals.TryGetValue(slot, out var original))
                        VTablePatcher.Exchange(navigator.VTable, slot, original);
                }

                if (install)
                    Log($"Listening to {navigator.Name}'s critical-hit, knockdown, analysis and Baton Pass events (vtable 0x{navigator.VTable:X}).");
            }

            _slotsPatched = install;
        }
    }

    private void ApplyConfig(Config config)
    {
        _filter = LineFilter.FromConfig(config);
        Log($"Silencing {_filter.Describe()}.");
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
        _playNavigatorLineHook?.Disable();
        PatchSlots(false);
        _tags.Clear();
    }

    public override void Resume()
    {
        PatchSlots(true);
        _playNavigatorLineHook?.Enable();
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
