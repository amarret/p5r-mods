using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions.X64;

namespace p5rpc.setpalacesecurity;

/// <summary>SetAlertValue(count, value): the game's setter for a security-level count, clamped to 0-100.</summary>
[Function(CallingConventions.Microsoft)]
public delegate nint SetAlertFn(int count, int value);

/// <summary>SetCount(index, value): the game's generic COUNT setter (scripts' SET_COUNT, and much native code).</summary>
[Function(CallingConventions.Microsoft)]
public delegate void SetCountFn(int index, int value);

/// <summary>RefreshAlertHud(): brings the security meter up to date with the count (and animates a change).</summary>
[Function(CallingConventions.Microsoft)]
public delegate int RefreshAlertHudFn();

/// <summary>ActivateAlertMeter(on): turns the security meter on or off at once, and has it read the count.</summary>
[Function(CallingConventions.Microsoft)]
public delegate void ActivateMeterFn(byte on);

/// <summary>ShowAlertMeter(show): fades the security meter in or out (FLD_ALERT_VISIBLE).</summary>
[Function(CallingConventions.Microsoft)]
public delegate void ShowMeterFn(byte show);

/// <summary>The game's security meter (its alert HUD object), as far as this mod uses it.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct AlertMeter
{
    /// <summary>Bit 0: on screen. A refresh that finds a change while it is clear brings the meter up.</summary>
    [FieldOffset(0x04)] public uint Flags;

    /// <summary>Bit 0: the meter is on (its Palace texture is wanted).</summary>
    [FieldOffset(0x08)] public uint State;

    [FieldOffset(0x44)] public float Alpha;

    /// <summary>The value drawn; showing the meter or turning it on copies <see cref="Count"/> here.</summary>
    [FieldOffset(0x48)] public int Shown;

    [FieldOffset(0x4C)] public int SecondShown;

    /// <summary>The count the meter last read.</summary>
    [FieldOffset(0x50)] public int Count;

    /// <summary>The value a refresh animates from. A refresh that finds it different from <see cref="Count"/> shows the change.</summary>
    [FieldOffset(0x54)] public int From;

    /// <summary>A second pair the refresh compares the same way.</summary>
    [FieldOffset(0x58)] public int Second;

    [FieldOffset(0x5C)] public int SecondFrom;
}

/// <summary>
/// Keeps the Palace security level at a chosen value, in the Palaces only, and only when something changes it.
/// <para>
/// The level is a game COUNT: 131 (0x83) in the Palaces, 135 (0x87) where flag 0x50000016 is set. It changes through the
/// security setter (being spotted, ambushes, safe rooms, script commands), through the generic COUNT setter, or by a
/// direct write followed by a refresh of the meter (CALL_ALERT_CHANGE, the delayed update after a battle). Each of those
/// is given the chosen level while the player is in a Palace. When the meter is turned on or fades in, the counts and the
/// meter are set to the level, so it always appears at it.
/// </para>
/// <para>
/// "In a Palace" is the game's own FLD_CHECK_DUNGEON test: the current field's major ID is 100-199. It does not depend
/// on the HUD, so battles, events, menus and HUD-hiding mods do not change it. Outside, nothing is touched.
/// </para>
/// <para>
/// The mod never makes the meter come up. A refresh that finds a change while the meter is off screen brings it up, and
/// where no Palace texture applies that loads one that does not exist (<c>calendar/P5_danger_cha09_a.dds</c>): the game
/// hangs. So a meter off screen is given the level directly, as the game's own show and switch code do, and only a meter
/// on screen animates a change.
/// </para>
/// <para>
/// The game's own rules on the value still apply: it is clamped to 0-100, and while flag 0x20000050 is set (after the
/// calling card) it is kept at 80 or more.
/// </para>
/// </summary>
public sealed unsafe class SecurityLevel
{
    /// <summary>The two COUNT indices that hold a Palace's security level.</summary>
    public const int AlertCount = 0x83, OtherAlertCount = 0x87;

    /// <summary>While this flag is set (after the calling card), the game keeps the level at <see cref="CallingCardMinimum"/> or more.</summary>
    public const uint CallingCardFlag = 0x20000050;

    public const int CallingCardMinimum = 80;

    /// <summary>Offset of the meter pointer in the object <see cref="MeterOwner"/> points to.</summary>
    public const int MeterOffset = 0x48;

    /// <summary>Offset of the field's work data in the object <see cref="GetField"/> returns.</summary>
    public const int FieldWorkOffset = 0x48;

    public const uint MeterOnScreen = 1, MeterOn = 1;

    private volatile bool _enabled;
    private volatile int _level;

    /// <summary>The game's COUNT array (32-bit values by index).</summary>
    public int* Counts { get; set; }

    /// <summary>The game's pointer to the field HUD object that holds the security meter (at <see cref="MeterOffset"/>).</summary>
    public nint* MeterOwner { get; set; }

    /// <summary>The game's flag check, BIT_CHK (true when the flag is set).</summary>
    public delegate* unmanaged<uint, byte> FlagCheck { get; set; }

    /// <summary>The game's current-field getter (null outside the field), as FLD_CHECK_DUNGEON calls it.</summary>
    public delegate* unmanaged<nint> GetField { get; set; }

    /// <summary>Offset of the 16-bit major ID in the field's work data, and the range FLD_CHECK_DUNGEON accepts.</summary>
    public int MajorOffset { get; set; }

    public int DungeonFirst { get; set; } = 100;
    public int DungeonLast { get; set; } = 199;

    public SetAlertFn? OriginalSetAlert { get; set; }
    public SetCountFn? OriginalSetCount { get; set; }
    public RefreshAlertHudFn? OriginalRefreshAlertHud { get; set; }
    public ActivateMeterFn? OriginalActivateMeter { get; set; }
    public ShowMeterFn? OriginalShowMeter { get; set; }

    /// <summary>Called the first time the level is held, with the value.</summary>
    public Action<int>? FirstHeld { get; set; }

    /// <summary>Receives one line per game event, when set.</summary>
    public Action<string>? Trace { get; set; }

    private int _held;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>The chosen level, 0-100.</summary>
    public int Level
    {
        get => _level;
        set => _level = Math.Clamp(value, 0, 100);
    }

    /// <summary>The value the counts are kept at: the chosen level, at least 80 after the calling card.</summary>
    public int Target
    {
        get
        {
            var target = _level;
            if (target < CallingCardMinimum && FlagCheck != null && FlagCheck(CallingCardFlag) != 0) target = CallingCardMinimum;
            return target;
        }
    }

    /// <summary>The security meter, or null while the game has none.</summary>
    private AlertMeter* Meter
    {
        get
        {
            var owner = MeterOwner;
            if (owner == null || *owner == 0) return null;
            return *(AlertMeter**)(*owner + MeterOffset);
        }
    }

    /// <summary>The current field's major ID, or -1 outside the field (or without the field getter).</summary>
    public int FieldMajor
    {
        get
        {
            var getField = GetField;
            if (getField == null) return -1;
            var field = getField();
            if (field == 0) return -1;
            var work = *(nint*)(field + FieldWorkOffset);
            return work == 0 ? -1 : *(ushort*)(work + MajorOffset);
        }
    }

    /// <summary>True in a Palace: the game's FLD_CHECK_DUNGEON test, or the meter being in use if that test was not found.</summary>
    public bool InPalace(AlertMeter* meter)
    {
        if (GetField != null)
        {
            var major = FieldMajor;
            return major >= DungeonFirst && major <= DungeonLast;
        }

        return meter != null && ((meter->State & MeterOn) != 0 || meter->Alpha > 0); // FLD_ALERT_GET_VISIBLE
    }

    private static bool IsAlert(int index) => index is AlertCount or OtherAlertCount;

    private static bool OnScreen(AlertMeter* meter) => meter != null && (meter->Flags & MeterOnScreen) != 0;

    public nint SetAlert(int count, int value)
    {
        if (_enabled && IsAlert(count))
        {
            var meter = Meter;
            var palace = InPalace(meter);
            var before = Counts != null ? Counts[count] : -1;
            if (palace)
            {
                // Off screen, the meter gets the level now: the setter then finds no change, and the meter is not brought up.
                if (!OnScreen(meter)) Force(meter);
                Held(_level);
            }

            var result = OriginalSetAlert!(count, palace ? _level : value); // the game applies its own clamp and calling-card minimum
            Log($"setter(0x{count:X}, {value})", meter, palace, before, count);
            return result;
        }

        return OriginalSetAlert!(count, value);
    }

    public void SetCount(int index, int value)
    {
        if (_enabled && IsAlert(index))
        {
            var meter = Meter;
            var palace = InPalace(meter);
            var before = Counts != null ? Counts[index] : -1;
            if (palace)
            {
                Force(meter);
                Held(Target);
            }

            OriginalSetCount!(index, palace ? Target : value);
            Log($"SET_COUNT(0x{index:X}, {value})", meter, palace, before, index);
            return;
        }

        OriginalSetCount!(index, value);
    }

    public int RefreshAlertHud()
    {
        // Something wrote a count directly and asks the meter to show it.
        if (_enabled)
        {
            var meter = Meter;
            var palace = InPalace(meter);
            var before = Counts != null ? Counts[AlertCount] : -1;
            if (palace) Force(meter);
            var result = OriginalRefreshAlertHud!();
            Log("meter refresh", meter, palace, before, AlertCount);
            return result;
        }

        return OriginalRefreshAlertHud!();
    }

    public void ActivateMeter(byte on)
    {
        // A Palace area is loading and the meter is about to read the count: give the counts and the meter the level.
        if (_enabled && on != 0)
        {
            var meter = Meter;
            var palace = InPalace(meter);
            var before = Counts != null ? Counts[AlertCount] : -1;
            if (palace && meter != null) Sync(meter, SetCounts());
            OriginalActivateMeter!(on);
            Log("meter on", meter, palace, before, AlertCount);
            return;
        }

        OriginalActivateMeter!(on);
    }

    public void ShowMeter(byte show)
    {
        // The meter fades in (after an event, a menu or a battle): it appears at the level.
        if (_enabled && show != 0)
        {
            var meter = Meter;
            var palace = InPalace(meter);
            var before = Counts != null ? Counts[AlertCount] : -1;
            var refresh = false;
            if (palace && meter != null)
            {
                var target = SetCounts();
                if (!OnScreen(meter)) Sync(meter, target);
                else refresh = meter->Count != target; // on screen: animate to it
            }

            OriginalShowMeter!(show);
            if (refresh) OriginalRefreshAlertHud?.Invoke();
            Log("meter fade-in", meter, palace, before, AlertCount);
            return;
        }

        OriginalShowMeter!(show);
    }

    /// <summary>Sets both counts to the target; a meter off screen is given it too. Returns the target.</summary>
    private int Force(AlertMeter* meter)
    {
        var target = SetCounts();
        if (meter != null && !OnScreen(meter)) Sync(meter, target);
        return target;
    }

    /// <summary>Gives the meter a value without animating, the way the game's show, switch and disabled-meter refresh do.</summary>
    private static void Sync(AlertMeter* meter, int value)
    {
        meter->Count = value;
        meter->From = value;
        meter->Shown = value;
        meter->SecondFrom = meter->Second;
        meter->SecondShown = meter->Second;
    }

    /// <summary>Sets both counts to the target, and returns it.</summary>
    internal int SetCounts()
    {
        var target = Target;
        var counts = Counts;
        if (counts == null || (counts[AlertCount] == target && counts[OtherAlertCount] == target)) return target;
        counts[AlertCount] = target;
        counts[OtherAlertCount] = target;
        Held(target);
        return target;
    }

    private void Log(string what, AlertMeter* meter, bool palace, int before, int index)
    {
        var trace = Trace;
        if (trace == null) return;
        var after = Counts != null ? Counts[index] : -1;
        var state = meter == null
            ? "no meter"
            : $"meter {(OnScreen(meter) ? "on screen" : "off screen")}, on={meter->State & MeterOn}, alpha={meter->Alpha:0.##}, shows {meter->Shown}";
        trace($"{what}: field {FieldMajor} ({(palace ? "Palace" : "not a Palace")}), {state}; count 0x{index:X} {before} -> {after}");
    }

    private void Held(int value)
    {
        if (Interlocked.Exchange(ref _held, 1) == 0) FirstHeld?.Invoke(value);
    }
}
