using System.Runtime.InteropServices;
using System.Text;

namespace p5rpc.performancefix;

/// <summary>
/// Starts the loading animation straight over the fast-travel map.
///
/// After you pick a destination from a district's list, <c>FIELD/PANEL/MESSAGESEL.BF</c> runs <c>Fade_check_sys</c>:
/// it picks the animation for the destination, calls <c>FLD_LMAP_FADE(0)</c> (the map's own closing fade, ~270 ms,
/// waited for frame by frame), then starts the animation (<c>FADEOUT(8, 10)</c> for the subway) and waits for it
/// to cover the screen. The frame in which the map has gone and the animation has not started shows the field. Then
/// <c>FLD_PANEL_MAP_PROC_END</c> closes the map and <c>CALL_FIELD</c> changes area. The destinations without a list
/// (Ichigaya, Kanda, Jinbocho in <c>LMAP.BF</c>) already skip the closing fade and start the animation over the map.
///
/// The mod points three entries of the game's flowscript function table at its own handlers:
/// <code>
///   FLD_LMAP_FADE           called from Fade_check_sys: "done" at once, and the fade is noted as pending;
///                           anywhere else: the original
///   FLD_REQ_SCN_CHANGE      the crowd scene change, which has no fade to wait for: if a fade is pending for this
///                           script, it runs the original FLD_LMAP_FADE first and waits for it (vanilla order)
///   FLD_PANEL_MAP_PROC_END  clears the pending fade, then the original
/// </code>
/// Handlers take no arguments (they read the script's stack themselves) and return 0 to be called again next frame.
/// The script being run is in a global that the interpreter's native-call step sets around each handler.
///
/// Fade_check_sys is recognised by its code, not by name: in memory the script's procedure labels are not where the
/// file has them, but the interpreter reads each instruction from the context's code pointer and instruction index
/// (swapping its 16-bit halves when the flag at <c>[[ctx+1E0h]+18h]</c> bit 0 is set, as for P5R's big-endian
/// scripts). Right after its <c>FLD_LMAP_FADE(0)</c> comes the choice of animation:
/// <code>
///   PUSHIS k; PUSHLIX v; EQ; IF; CALL Fld_FadeOut_...; GOTO     k = 1 to 5 (call field, subway, symbol, Velvet, crowd)
///   PUSHIS 6; PUSHLIX v; EQ; IF; PUSHIS 10; PUSHIS 2; COMM FADEOUT; COMM FADE_SYNC
/// </code>
/// That shape (the same local v, k counting up, ending in FADEOUT(2, 10)) is matched instruction by instruction,
/// stopping at the first difference, so other scripts' code is barely read. Procedure indices and jump labels are
/// not compared, so a copy of the script recompiled by another mod still matches.
/// </summary>
public static unsafe class MapTravel
{
    /// <summary>Start of the flowscript interpreter's native-call step, up to its <c>lea r15, [function tables]</c>.</summary>
    public const string DispatcherSig =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 20 48 8B 81 00 02 00 00 48 8B D9 4C 63 71 28 " +
        "42 0F B7 4C B0 02 48 8B 83 E0 01 00 00 F6 40 18 01 74 04 66 C1 C1 08 0F BF E9 4C 8D 3D ?? ?? ?? ?? 8B F5 C6 43 5F 06 " +
        "C1 FE 0C 81 E5 FF 0F 00 00 83 E6 0F 48 89 1D ?? ?? ?? ??";

    /// <summary><c>lea r15, [rip+X]</c>: the table of function tables, {entries, count} per module.</summary>
    public const int ModuleTableLeaOffset = 0x40;

    /// <summary><c>mov [rip+X], rbx</c>: the global holding the script context while a handler runs.</summary>
    public const int CurrentScriptMovOffset = 0x59;

    // Script context: +0x200 code (4-byte instructions: opcode, operand), +0x28 instruction index, +0x1E0 a header
    // whose byte +0x18 bit 0 says the halves are byte-swapped.
    public const int CodeOffset = 0x200, IndexOffset = 0x28, HeaderOffset = 0x1E0, SwapFlagOffset = 0x18;
    public const ushort Comm = 8, Call = 11, Goto = 13, Eq = 22, If = 28, PushIs = 29, PushLix = 30;
    public const ushort FadeId = 0x11A0, FadeOutId = 0x0008, FadeSyncId = 0x000A;
    public const int FieldModule = 1, EntrySize = 24, EntryArgcOffset = 8, EntryNameOffset = 16;
    public const string FadeName = "FLD_LMAP_FADE", SceneChangeName = "FLD_REQ_SCN_CHANGE", PanelEndName = "FLD_PANEL_MAP_PROC_END";

    public static volatile bool Enabled = true;
    public static int Skipped, Deferred, Elsewhere;

    private static nint _currentScript, _fade, _sceneChange, _panelEnd;
    private static nint _pending;

    /// <summary>Points the three table entries at the handlers below. Returns null on success, or why it did not.</summary>
    public static string? Install(nint dispatcher)
    {
        var lea = dispatcher + ModuleTableLeaOffset;
        var mov = dispatcher + CurrentScriptMovOffset;
        var modules = lea + 7 + *(int*)(lea + 3);
        _currentScript = mov + 7 + *(int*)(mov + 3);
        var table = *(nint*)(modules + FieldModule * 16);
        var count = *(int*)(modules + FieldModule * 16 + 8);
        if (table == 0 || count is <= 0 or > 0x1000) return "the field function table does not look as expected";

        nint Find(string name)
        {
            for (int i = 0; i < count; i++)
            {
                var entry = table + i * EntrySize;
                if (*(nint*)(entry + EntryNameOffset) is var text && text != 0 && Text(text, 40) == name) return entry;
            }
            return 0;
        }

        var fade = Find(FadeName);
        var sceneChange = Find(SceneChangeName);
        var panelEnd = Find(PanelEndName);
        if (fade == 0 || sceneChange == 0 || panelEnd == 0) return "a script function is missing from the field table";
        if (*(int*)(fade + EntryArgcOffset) != 1 || *(int*)(sceneChange + EntryArgcOffset) != 1 || *(int*)(panelEnd + EntryArgcOffset) != 1)
            return "a script function has an unexpected argument count";

        foreach (var name in new[] { nameof(Fade), nameof(SceneChange), nameof(PanelEnd) })
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(
                typeof(MapTravel).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle);
        _fade = Swap(fade, (nint)(delegate* unmanaged<int>)&Fade);
        _sceneChange = Swap(sceneChange, (nint)(delegate* unmanaged<int>)&SceneChange);
        _panelEnd = Swap(panelEnd, (nint)(delegate* unmanaged<int>)&PanelEnd);
        return null;
    }

    private static nint Swap(nint entry, nint handler)
    {
        var original = *(nint*)entry;
        VirtualProtect(entry, 8, 0x04 /* PAGE_READWRITE */, out var old);
        Interlocked.Exchange(ref *(nint*)entry, handler);
        VirtualProtect(entry, 8, old, out _);
        return original;
    }

    [UnmanagedCallersOnly]
    private static int Fade()
    {
        if (Enabled)
        {
            var script = *(nint*)_currentScript;
            if (script != 0 && IsTravelFade(script))
            {
                _pending = script;
                Interlocked.Increment(ref Skipped);
                return 1;
            }
        }

        return ((delegate* unmanaged<int>)_fade)();
    }

    [UnmanagedCallersOnly]
    private static int SceneChange()
    {
        var script = *(nint*)_currentScript;
        if (_pending != 0 && _pending == script)
        {
            if (((delegate* unmanaged<int>)_fade)() == 0) return 0;   // the map's closing fade first, as in vanilla
            _pending = 0;
            Interlocked.Increment(ref Deferred);
        }

        return ((delegate* unmanaged<int>)_sceneChange)();
    }

    [UnmanagedCallersOnly]
    private static int PanelEnd()
    {
        _pending = 0;
        return ((delegate* unmanaged<int>)_panelEnd)();
    }

    /// <summary>Whether the script is at Fade_check_sys's FLD_LMAP_FADE (see the class summary).</summary>
    public static bool IsTravelFade(nint script)
    {
        var code = *(uint**)(script + CodeOffset);
        var header = *(byte**)(script + HeaderOffset);
        if (code == null || header == null) { Interlocked.Increment(ref Elsewhere); return false; }
        var swap = (header[SwapFlagOffset] & 1) != 0;
        var at = code + *(int*)(script + IndexOffset);
        (ushort Op, ushort Arg) Instruction(int i)
        {
            var word = at[i];
            ushort op = (ushort)word, arg = (ushort)(word >> 16);
            return swap ? ((ushort)((op >> 8) | (op << 8)), (ushort)((arg >> 8) | (arg << 8))) : (op, arg);
        }

        bool Is(int i, ushort op, int arg = -1) => Instruction(i) is var x && x.Op == op && (arg < 0 || x.Arg == arg);
        if (!Is(0, Comm, FadeId) || !Is(2, PushLix)) { Interlocked.Increment(ref Elsewhere); return false; }
        var local = Instruction(2).Arg;
        for (int k = 1, i = 1; k <= 6; k++, i += 6)
        {
            if (!Is(i, PushIs, k) || !Is(i + 1, PushLix, local) || !Is(i + 2, Eq) || !Is(i + 3, If)) { Interlocked.Increment(ref Elsewhere); return false; }
            if (k < 6 && (!Is(i + 4, Call) || !Is(i + 5, Goto))) { Interlocked.Increment(ref Elsewhere); return false; }
            if (k == 6 && (!Is(i + 4, PushIs, 10) || !Is(i + 5, PushIs, 2) || !Is(i + 6, Comm, FadeOutId) || !Is(i + 7, Comm, FadeSyncId)))
            {
                Interlocked.Increment(ref Elsewhere);
                return false;
            }
        }

        return true;
    }

    private static string Text(nint at, int max)
    {
        var bytes = (byte*)at;
        var length = 0;
        while (length < max && bytes[length] is >= 0x20 and < 0x7F) length++;
        return Encoding.ASCII.GetString(bytes, length);
    }

    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);
}
