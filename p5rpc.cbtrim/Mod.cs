using System.Diagnostics;
using System.Runtime.InteropServices;
using p5rpc.cbtrim.Configuration;
using p5rpc.cbtrim.Template;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.cbtrim;

/// <summary>
/// Makes the renderer upload only the part of each shader constant buffer it actually filled, instead of the whole
/// buffer (usually 7 KB) on every draw. In busy places this cuts tens of MB of copying per frame on the "render"
/// thread. See <see cref="UploadStub"/>. Nothing on disk or in the save is changed.
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

    public static readonly byte[] OriginalCall = [0x41, 0xFF, 0x92, 0x80, 0x01, 0x00, 0x00];

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private readonly IMod _owner;
    private Config _configuration;
    private readonly IModConfig _modConfig;

    private readonly object _gate = new();
    private bool _suspended;

    private nint _uploadRoutine;
    private byte* _uploadsEnabled;
    private int _patched;

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

        var (ok, message) = D3DCheck.Run();
        Log($"Driver check: {message}.");
        if (!ok)
        {
            Log("Uploads stay as they are.");
            return;
        }

        using var process = Process.GetCurrentProcess();
        var baseAddress = process.MainModule!.BaseAddress;
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

    /// <summary>Points one uploader's UpdateSubresource call at the upload routine (built on first use).</summary>
    private void PatchUploader(string stage, nint call)
    {
        if (!new ReadOnlySpan<byte>((void*)call, OriginalCall.Length).SequenceEqual(OriginalCall))
        {
            Log($"The {stage} uploader's call does not look as expected; left alone.");
            return;
        }

        if (_uploadRoutine == 0)
        {
            var (code, enabledOffset) = UploadStub.Build();
            _uploadRoutine = Place(call, code);
            if (_uploadRoutine == 0)
            {
                Log("No memory near the game code for the upload routine; uploads stay as they are.");
                return;
            }

            _uploadsEnabled = (byte*)(_uploadRoutine + enabledOffset);
            ApplyConfig(log: false);
        }

        var patch = new byte[OriginalCall.Length];
        patch[0] = 0xE8;
        BitConverter.TryWriteBytes(patch.AsSpan(1), (int)(_uploadRoutine - (call + 5)));
        patch[5] = 0x66; patch[6] = 0x90; // 2-byte nop
        VirtualProtect(call, (nuint)patch.Length, 0x40, out var old);
        patch.CopyTo(new Span<byte>((void*)call, patch.Length));
        VirtualProtect(call, (nuint)patch.Length, old, out _);
        FlushInstructionCache(GetCurrentProcess(), call, (nuint)patch.Length);
        _patched++;
        Log($"Trimming {stage} constant-buffer uploads (call at 0x{call:X}, routine at 0x{_uploadRoutine:X}).");
    }

    private void ApplyConfig(bool log = true)
    {
        if (_uploadsEnabled != null)
        {
            var on = _configuration.TrimUploads && !_suspended;
            Volatile.Write(ref *_uploadsEnabled, (byte)(on ? 1 : 0));
            if (log)
                Log(on ? $"Constant-buffer uploads: only the filled part ({_patched} of {Uploaders.Length} uploaders)." : "Constant-buffer uploads: full (vanilla).");
        }
    }

    private void Log(string message) => _logger.WriteLine($"[{_modConfig.ModId}] {message}");

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Log("Config updated, applying.");
        lock (_gate) ApplyConfig();
    }

    // The call sites keep pointing at the routine; switching off (or suspending) makes it do the original full upload.
    public override bool CanSuspend() => true;

    public override void Suspend()
    {
        lock (_gate) { _suspended = true; ApplyConfig(); }
    }

    public override void Resume()
    {
        lock (_gate) { _suspended = false; ApplyConfig(); }
    }
    #endregion

    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);
    [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
