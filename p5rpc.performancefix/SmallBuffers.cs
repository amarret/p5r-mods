using System.Runtime.InteropServices;

namespace p5rpc.performancefix;

/// <summary>
/// Gives each of the renderer's 7 KB constant buffers a few small companions (256 B to 4 KB), and sends each upload to
/// the smallest one that holds everything the engine has ever filled in that buffer.
///
/// Why: on a deferred context the NVIDIA driver backs every constant-buffer update with fresh memory the size of the
/// whole buffer, whatever part of it is written (measured: the trimmed 224-byte DISCARD update of a 7 KB buffer costs
/// as much driver memory as the vanilla full one). An area's first frames make 3,000-4,000 uploads, against a few
/// hundred in a loading screen, and the driver frees memory it has not used for about a second. So every area change
/// made it allocate 40-50 MB again in 128 KB pieces, inside two render-thread frames of 10-30 ms. With 256-byte buffers
/// the same frames need a dozen pieces, under 1 ms.
///
/// Where: the engine's six upload sites (three shader stages, each with a single-slot uploader and a per-shader loop)
/// all do <c>UpdateSubresource(ctx, buffer, 0, NULL, source, 0, 0)</c> through <c>call [r10+180h]</c>, then bind the
/// buffer held in a local variable through a per-context cache:
/// <code>
///   mov rax, [state+8]; mov rcx, [rsp+var]; cmp [rax+slot*8+8], rcx; je skip; mov [rax+slot*8+8], rcx; bind(&amp;var)
/// </code>
/// The routine replaces that call. It updates the small buffer with a full UpdateSubresource (its first 256 B to 4 KB
/// come straight from the engine's 7 KB shadow copy, as in vanilla) and stores the small buffer in the caller's
/// variable, so the engine's own code binds it and keeps its cache right. Shaders that declare more constants than the
/// bound buffer holds read zeros past its end (Direct3D defines out-of-bounds constant reads as 0); those constants
/// were never written by the engine, so in vanilla they held only the shadow copy's initial contents.
/// <code>
///   n = filled size (word at source + 18804h - slot*1BFEh); e = entry for the game's buffer (hash map, by pointer)
///   e.max = max(e.max, n); if enabled and 0 &lt; e.max &lt;= 4096:
///       small = e.buffer[class of e.max] (created on first use); UpdateSubresource(ctx, small, 0, NULL, source, 0, 0)
///       [caller's variable] = small
///   else  the single-slot sites go on to <see cref="UploadStub"/> (trimmed update of the game's buffer),
///         the loop sites to the original full UpdateSubresource
/// </code>
/// The game's buffer is only bound right after one of these updates, at these six sites (checked in the exe), so a
/// bound buffer always holds the latest data whichever of the two it is.
/// </summary>
public static unsafe class SmallBuffers
{
    /// <summary>Byte sizes of the companions: 256 << class, class 0 to 4.</summary>
    public const int Classes = 5;
    public const int SmallestBytes = 256;
    public const int LargestBytes = SmallestBytes << (Classes - 1);

    // Data block (one per process): flag, counters, callbacks, then the map of game buffers.
    public const int EnabledOffset = 0, UploadsOffset = 8, OthersOffset = 16, RegisterOffset = 24, CreateOffset = 32, TrimOffset = 40;
    public const int MapOffset = 64, Entries = 512, EntrySize = 64;
    // Entry: +0 game buffer (key), +8 largest filled size seen (u32), +16 companions by class (0 = not made yet, 1 = failed).
    public const int EntryMaxOffset = 8, EntryBuffersOffset = 16;
    public const ulong HashMultiplier = 0x9E3779B97F4A7C15;

    /// <summary>Entry points of the routine: the single-slot uploaders (slot in rbp, variable at rsp+50h) and the
    /// per-shader loops (slot in rdi, variable at rsp+70h).</summary>
    public const int SingleEntry = 0;
    public static int LoopEntry { get; private set; }

    /// <summary>The bytes after each kind of site's call: the engine reloads the variable and checks its bind cache.</summary>
    public static readonly byte[] SinglePostCall = [0x48, 0x8B, 0x47, 0x08, 0x48, 0x8B, 0x4C, 0x24, 0x50, 0x48, 0x39, 0x4C, 0xE8, 0x08];
    public static readonly byte[] LoopPostCall = [0x48, 0x8B, 0x46, 0x08, 0x48, 0x8B, 0x4C, 0x24, 0x70, 0x48, 0x39, 0x4C, 0xF8, 0x08];

    public static byte* Data { get; private set; }
    public static int Created, Failed, Registered;
    private static readonly object Gate = new();

    public static long Uploads => Data == null ? 0 : Volatile.Read(ref *(long*)(Data + UploadsOffset));
    public static long Others => Data == null ? 0 : Volatile.Read(ref *(long*)(Data + OthersOffset));

    public static bool Enabled
    {
        set { if (Data != null) Volatile.Write(ref *Data, (byte)(value ? 1 : 0)); }
    }

    /// <summary>Allocates the data block and emits the routine; <paramref name="trimRoutine"/> is where the
    /// single-slot sites go for buffers the companions cannot take.</summary>
    public static byte[] Build(nint trimRoutine)
    {
        if (Data == null)
        {
            var size = (nuint)(MapOffset + Entries * EntrySize);
            Data = (byte*)NativeMemory.AlignedAlloc(size, 64);
            NativeMemory.Clear(Data, size);
            *(nint*)(Data + RegisterOffset) = (nint)(delegate* unmanaged<nint, nint>)&Register;
            *(nint*)(Data + CreateOffset) = (nint)(delegate* unmanaged<nint, nint, int, nint>)&Create;
        }
        *(nint*)(Data + TrimOffset) = trimRoutine;

        var c = new List<byte>();
        void E(params byte[] b) => c.AddRange(b);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        var fixups = new List<(int At, string Label, bool Short)>();
        var labels = new Dictionary<string, int>();
        void Jcc(byte cc, string label) { E(0x0F, cc); fixups.Add((c.Count, label, false)); I32(0); }   // rel32
        void Jmp(string label) { E(0xE9); fixups.Add((c.Count, label, false)); I32(0); }
        void Short(byte op, string label) { E(op); fixups.Add((c.Count, label, true)); E(0); }        // rel8
        void Label(string name) => labels[name] = c.Count;

        // Entry points.
        E(0x49, 0x89, 0xEA);                                      // mov r10, rbp (slot)
        E(0x4C, 0x8D, 0x5C, 0x24, 0x58);                          // lea r11, [rsp+58h] (the caller's [rsp+50h])
        E(0x31, 0xC0);                                            // xor eax, eax (single-slot site)
        Short(0xEB, "common");
        LoopEntry = c.Count;
        E(0x49, 0x89, 0xFA);                                      // mov r10, rdi (slot)
        E(0x4C, 0x8D, 0x5C, 0x24, 0x78);                          // lea r11, [rsp+78h] (the caller's [rsp+70h])
        E(0xB8, 1, 0, 0, 0);                                      // mov eax, 1 (loop site)

        Label("common");
        E(0x53, 0x56, 0x57, 0x48, 0x83, 0xEC, 0x60);              // push rbx; push rsi; push rdi; sub rsp, 60h
        E(0x48, 0x89, 0x4C, 0x24, 0x38);                          // [rsp+38h] = rcx (ctx)
        E(0x48, 0x89, 0x54, 0x24, 0x40);                          // [rsp+40h] = rdx (game's buffer)
        E(0x4C, 0x89, 0x44, 0x24, 0x48);                          // [rsp+48h] = r8
        E(0x4C, 0x89, 0x4C, 0x24, 0x50);                          // [rsp+50h] = r9
        E(0x8B, 0xD8, 0x49, 0x8B, 0xF3);                          // mov ebx, eax (kind); mov rsi, r11 (variable)
        E(0x48, 0xBF); c.AddRange(BitConverter.GetBytes((long)(nint)Data)); // mov rdi, data
        E(0x80, 0x3F, 0x00); Jcc(0x84, "fallback");               // cmp byte [rdi], 0; je fallback
        E(0x49, 0x83, 0xFA, 0x0E); Jcc(0x83, "fallback");         // cmp r10, 14; jae fallback
        E(0x48, 0x8B, 0x84, 0x24, 0xA0, 0, 0, 0);                 // mov rax, [rsp+A0h] (source)
        E(0x4D, 0x69, 0xC2); I32(UploadStub.SlotStride);          // imul r8, r10, 1BFEh
        E(0x49, 0x2B, 0xC0);                                      // sub rax, r8
        E(0x44, 0x0F, 0xB7, 0x80); I32(UploadStub.SizeWordOffset); // movzx r8d, word [rax+18804h]
        E(0x44, 0x89, 0x44, 0x24, 0x58);                          // [rsp+58h] = filled size

        // Find the entry of the game's buffer (linear probing).
        E(0x48, 0x8B, 0xC2);                                      // mov rax, rdx
        E(0x48, 0xB9); c.AddRange(BitConverter.GetBytes(HashMultiplier)); // mov rcx, multiplier
        E(0x48, 0x0F, 0xAF, 0xC1, 0x48, 0xC1, 0xE8, 0x37);        // imul rax, rcx; shr rax, 55
        E(0xB9); I32(Entries);                                    // mov ecx, 512 (probes left)
        Label("probe");
        E(0x4C, 0x8B, 0xC8, 0x49, 0xC1, 0xE1, 0x06);              // mov r9, rax; shl r9, 6
        E(0x4E, 0x8D, 0x4C, 0x0F, MapOffset);                     // lea r9, [rdi+r9+40h]
        E(0x4D, 0x8B, 0x01, 0x4C, 0x3B, 0xC2);                    // mov r8, [r9]; cmp r8, rdx
        Short(0x74, "found");
        E(0x4D, 0x85, 0xC0); Short(0x74, "miss");                 // test r8, r8; jz miss
        E(0xFF, 0xC0, 0x25); I32(Entries - 1);                    // inc eax; and eax, 511
        E(0xFF, 0xC9); Short(0x75, "probe");                      // dec ecx; jnz probe
        Jmp("fallback");
        Label("miss");
        E(0x48, 0x8B, 0xCA, 0xFF, 0x57, RegisterOffset);          // mov rcx, rdx; call [rdi+18h] (Register)
        E(0x48, 0x85, 0xC0); Jcc(0x84, "fallback");               // test rax, rax; jz fallback (map full)
        E(0x4C, 0x8B, 0xC8);                                      // mov r9, rax
        Label("found");
        E(0x44, 0x8B, 0x44, 0x24, 0x58);                          // mov r8d, [rsp+58h]
        E(0x41, 0x8B, 0x41, EntryMaxOffset);                      // mov eax, [r9+8]
        E(0x44, 0x3B, 0xC0); Short(0x76, "max");                  // cmp r8d, eax; jbe max
        E(0x45, 0x89, 0x41, EntryMaxOffset, 0x41, 0x8B, 0xC0);    // mov [r9+8], r8d; mov eax, r8d
        Label("max");
        E(0x85, 0xC0); Jcc(0x84, "fallback");                     // test eax, eax; jz fallback (nothing filled yet)
        E(0x33, 0xC9, 0xBA); I32(SmallestBytes);                  // xor ecx, ecx; mov edx, 256
        Label("class");
        E(0x3B, 0xC2); Short(0x76, "classFound");                 // cmp eax, edx; jbe classFound
        E(0xFF, 0xC1, 0x03, 0xD2, 0x83, 0xF9, Classes);           // inc ecx; add edx, edx; cmp ecx, 5
        Short(0x72, "class");                                     // jb class
        Jmp("fallback");                                          // over 4 KB: the game's buffer
        Label("classFound");
        E(0x49, 0x8B, 0x44, 0xC9, EntryBuffersOffset);            // mov rax, [r9+rcx*8+10h]
        E(0x48, 0x83, 0xF8, 0x01); Short(0x77, "use");            // cmp rax, 1; ja use
        Jcc(0x84, "fallback");                                    // je fallback (could not be made)
        E(0x44, 0x8B, 0xC1, 0x49, 0x8B, 0xD1);                    // mov r8d, ecx; mov rdx, r9
        E(0x48, 0x8B, 0x4C, 0x24, 0x38, 0xFF, 0x57, CreateOffset); // mov rcx, [rsp+38h]; call [rdi+20h] (Create)
        E(0x48, 0x83, 0xF8, 0x01); Jcc(0x86, "fallback");         // cmp rax, 1; jbe fallback
        Label("use");
        E(0x48, 0x89, 0x06, 0x48, 0x8B, 0xD0);                    // mov [rsi], rax (the engine binds it); mov rdx, rax
        E(0x48, 0x8B, 0x84, 0x24, 0xA0, 0, 0, 0);                 // mov rax, [rsp+A0h]
        E(0x48, 0x89, 0x44, 0x24, 0x20);                          // [rsp+20h] = source
        E(0x33, 0xC0, 0x89, 0x44, 0x24, 0x28, 0x89, 0x44, 0x24, 0x30); // [rsp+28h] = [rsp+30h] = 0 (pitches)
        E(0x48, 0x8B, 0x4C, 0x24, 0x38, 0x45, 0x33, 0xC0, 0x45, 0x33, 0xC9); // rcx = ctx; r8d = 0; r9 = NULL
        E(0x48, 0x8B, 0x01, 0xFF, 0x90); I32(UploadStub.UpdateSubresourceSlot * 8); // call [ctx_vtbl+180h]
        E(0xF0, 0x48, 0xFF, 0x47, UploadsOffset);                 // lock inc qword [rdi+8]
        E(0x48, 0x83, 0xC4, 0x60, 0x5F, 0x5E, 0x5B, 0xC3);        // add rsp, 60h; pop rdi; pop rsi; pop rbx; ret

        Label("fallback");
        E(0xF0, 0x48, 0xFF, 0x47, OthersOffset);                  // lock inc qword [rdi+10h]
        E(0x48, 0x8B, 0x4C, 0x24, 0x38, 0x48, 0x8B, 0x54, 0x24, 0x40); // restore rcx, rdx
        E(0x4C, 0x8B, 0x44, 0x24, 0x48, 0x4C, 0x8B, 0x4C, 0x24, 0x50); // restore r8, r9
        E(0x4C, 0x8B, 0x5F, TrimOffset);                          // mov r11, [rdi+28h]
        E(0x48, 0x83, 0xC4, 0x60, 0x5F, 0x5E);                    // add rsp, 60h; pop rdi; pop rsi
        E(0x85, 0xDB, 0x5B);                                      // test ebx, ebx; pop rbx
        Short(0x75, "vanilla");
        E(0x41, 0xFF, 0xE3);                                      // jmp r11 (the trimmed upload, same arguments)
        Label("vanilla");
        E(0x48, 0x8B, 0x01, 0xFF, 0xA0); I32(UploadStub.UpdateSubresourceSlot * 8); // jmp [ctx_vtbl+180h]

        var code = c.ToArray();
        foreach (var (at, label, isShort) in fixups)
        {
            var delta = labels[label] - (at + (isShort ? 1 : 4));
            if (isShort)
            {
                if (delta is < -128 or > 127) throw new InvalidOperationException($"short jump to {label} out of range");
                code[at] = (byte)(sbyte)delta;
            }
            else BitConverter.TryWriteBytes(code.AsSpan(at, 4), delta);
        }
        return code;
    }

    /// <summary>Called by the routine for a game buffer it has not seen: returns its map entry (0 if the map is full).</summary>
    [UnmanagedCallersOnly]
    private static nint Register(nint buffer)
    {
        lock (Gate)
        {
            var start = (int)(((ulong)buffer * HashMultiplier) >> 55);
            for (int i = 0; i < Entries; i++)
            {
                var entry = Data + MapOffset + ((start + i) & (Entries - 1)) * EntrySize;
                var key = *(nint*)entry;
                if (key == buffer) return (nint)entry;
                if (key != 0) continue;
                Volatile.Write(ref *(nint*)entry, buffer);
                Registered++;
                return (nint)entry;
            }
            return 0;
        }
    }

    /// <summary>Called by the routine to make companion <paramref name="cls"/> of an entry, on the game's device.</summary>
    [UnmanagedCallersOnly]
    private static nint Create(nint context, nint entry, int cls)
    {
        try
        {
            lock (Gate)
            {
                var slot = (nint*)(entry + EntryBuffersOffset) + cls;
                if (*slot != 0) return *slot;
                nint device = 0, buffer = 0;
                ((delegate* unmanaged<nint, nint*, void>)(*(nint**)context)[3])(context, &device);   // GetDevice
                if (device != 0)
                {
                    var desc = stackalloc uint[6] { (uint)(SmallestBytes << cls), 0 /* DEFAULT */, 4 /* CONSTANT_BUFFER */, 0, 0, 0 };
                    if (((delegate* unmanaged<nint, uint*, nint, nint*, int>)(*(nint**)device)[3])(device, desc, 0, &buffer) < 0) buffer = 0;
                    ((delegate* unmanaged<nint, uint>)(*(nint**)device)[2])(device);
                }

                if (buffer != 0) Created++; else Failed++;
                Volatile.Write(ref *slot, buffer != 0 ? buffer : 1);
                return *slot;
            }
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Compiles the two callbacks now, so the render thread does not pay for it on its first draws.</summary>
    public static void Prepare()
    {
        foreach (var name in new[] { nameof(Register), nameof(Create) })
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(
                typeof(SmallBuffers).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle);
    }
}
