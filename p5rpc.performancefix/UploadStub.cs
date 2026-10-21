using System.Runtime.InteropServices;

namespace p5rpc.performancefix;

/// <summary>
/// The native routine that replaces the engine's <c>call [ctx_vtbl+180h]</c> (UpdateSubresource of a whole constant
/// buffer) in its three constant-buffer uploaders (vertex, pixel and a third shader stage).
///
/// At the call the registers hold UpdateSubresource(ctx, buffer, 0, NULL, source, 0, 0), and rbp holds the slot. Just
/// before the call the engine stored the number of bytes it actually filled for that stage and slot. Relative to the
/// source pointer, that size word is always at <c>source + 18804h - slot * 1BFEh</c> (the per-stage offsets cancel).
/// The routine:
/// <code>
///   size = that word; if (enabled and 0 &lt; size &lt; buffer.ByteWidth)
///       UpdateSubresource1(ctx, buffer, 0, &amp;box{0..size}, source, 0, 0, DISCARD)   ; only the filled bytes
///   else
///       UpdateSubresource(ctx, buffer, 0, NULL, source, 0, 0)                        ; exactly what the game did
/// </code>
/// Direct3D only takes a box on a constant buffer with DISCARD or NO_OVERWRITE (with neither, the update is silently
/// dropped), and NO_OVERWRITE crashes on deferred contexts. With DISCARD the rest of the buffer is undefined: in tests it
/// held zeros or data from an older version of the buffer. In the vanilla full upload that rest is whatever earlier,
/// bigger uploads left in the engine's shadow copy. The callers pass a fixed size per slot (16 to 512 bytes, the size of
/// that slot's block of constants), so shaders are not expected to read past it.
/// </summary>
public static unsafe class UploadStub
{
    public const int SizeWordOffset = 0x18804;
    public const int SlotStride = 0x1BFE;
    public const byte CopyDiscard = 2;              // D3D11_COPY_DISCARD
    public const int UpdateSubresourceSlot = 48;    // ID3D11DeviceContext
    public const int UpdateSubresource1Slot = 116;  // ID3D11DeviceContext1
    public const int BufferGetDescSlot = 10;        // ID3D11Buffer

    /// <summary>Emits the routine. Returns the code and the offset of its one-byte "enabled" flag.</summary>
    public static (byte[] Code, int EnabledOffset) Build()
    {
        var c = new List<byte>();
        void E(params byte[] b) => c.AddRange(b);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        var fixups = new List<(int At, string Label)>();
        var labels = new Dictionary<string, int>();
        void Jcc(byte cc, string label) { E(0x0F, cc); fixups.Add((c.Count, label)); I32(0); }   // rel32
        void Jmp(string label) { E(0xE9); fixups.Add((c.Count, label)); I32(0); }
        void Label(string name) => labels[name] = c.Count;
        int enabledRef;

        E(0x53, 0x56, 0x48, 0x83, 0xEC, 0x68);                    // push rbx; push rsi; sub rsp, 68h
        E(0x48, 0x8B, 0xD9, 0x48, 0x8B, 0xF2);                    // mov rbx, rcx (ctx); mov rsi, rdx (buffer)
        E(0x80, 0x3D); enabledRef = c.Count; I32(0); E(0x00);      // cmp byte [rip+enabled], 0
        Jcc(0x84, "full");                                        // je full
        E(0x48, 0x8B, 0x84, 0x24, 0xA0, 0, 0, 0);                 // mov rax, [rsp+A0h] (source)
        E(0x4C, 0x69, 0xD5); I32(SlotStride);                     // imul r10, rbp, 1BFEh
        E(0x4C, 0x8B, 0xD8, 0x4D, 0x2B, 0xDA);                    // mov r11, rax; sub r11, r10
        E(0x45, 0x0F, 0xB7, 0x93); I32(SizeWordOffset);            // movzx r10d, word [r11+18804h]
        E(0x45, 0x85, 0xD2); Jcc(0x84, "full");                   // test r10d, r10d; jz full
        E(0x4C, 0x89, 0x54, 0x24, 0x40);                          // mov [rsp+40h], r10
        E(0x48, 0x8B, 0xCE, 0x48, 0x8D, 0x54, 0x24, 0x48);        // mov rcx, rsi; lea rdx, [rsp+48h]
        E(0x48, 0x8B, 0x06, 0xFF, 0x50, BufferGetDescSlot * 8);   // mov rax, [rsi]; call [rax+50h] (GetDesc)
        E(0x4C, 0x8B, 0x54, 0x24, 0x40);                          // mov r10, [rsp+40h]
        E(0x8B, 0x44, 0x24, 0x48);                                // mov eax, [rsp+48h] (ByteWidth)
        E(0x44, 0x3B, 0xD0); Jcc(0x83, "full");                   // cmp r10d, eax; jae full
        // box { left 0, top 0, front 0, right size, bottom 1, back 1 } at [rsp+48h]
        E(0x48, 0xC7, 0x44, 0x24, 0x48, 0, 0, 0, 0);              // mov qword [rsp+48h], 0
        E(0xC7, 0x44, 0x24, 0x50, 0, 0, 0, 0);                    // mov dword [rsp+50h], 0
        E(0x44, 0x89, 0x54, 0x24, 0x54);                          // mov [rsp+54h], r10d
        E(0xC7, 0x44, 0x24, 0x58, 1, 0, 0, 0);                    // mov dword [rsp+58h], 1
        E(0xC7, 0x44, 0x24, 0x5C, 1, 0, 0, 0);                    // mov dword [rsp+5Ch], 1
        E(0x48, 0x8B, 0x84, 0x24, 0xA0, 0, 0, 0);                 // mov rax, [rsp+A0h]
        E(0x48, 0x89, 0x44, 0x24, 0x20);                          // [rsp+20h] = source
        E(0xC7, 0x44, 0x24, 0x28, 0, 0, 0, 0);                    // [rsp+28h] = 0 (row pitch)
        E(0xC7, 0x44, 0x24, 0x30, 0, 0, 0, 0);                    // [rsp+30h] = 0 (depth pitch)
        E(0xC7, 0x44, 0x24, 0x38, CopyDiscard, 0, 0, 0);          // [rsp+38h] = D3D11_COPY_DISCARD
        E(0x48, 0x8B, 0xCB, 0x48, 0x8B, 0xD6, 0x45, 0x33, 0xC0);  // rcx = ctx; rdx = buffer; r8d = 0
        E(0x4C, 0x8D, 0x4C, 0x24, 0x48);                          // lea r9, [rsp+48h] (box)
        E(0x48, 0x8B, 0x03, 0xFF, 0x90); I32(UpdateSubresource1Slot * 8); // call [ctx_vtbl+3A0h] (UpdateSubresource1)
        Jmp("done");
        Label("full");
        E(0x48, 0x8B, 0x84, 0x24, 0xA0, 0, 0, 0);                 // mov rax, [rsp+A0h]
        E(0x48, 0x89, 0x44, 0x24, 0x20);                          // [rsp+20h] = source
        E(0x8B, 0x84, 0x24, 0xA8, 0, 0, 0, 0x89, 0x44, 0x24, 0x28); // [rsp+28h] = caller's row pitch
        E(0x8B, 0x84, 0x24, 0xB0, 0, 0, 0, 0x89, 0x44, 0x24, 0x30); // [rsp+30h] = caller's depth pitch
        E(0x48, 0x8B, 0xCB, 0x48, 0x8B, 0xD6, 0x45, 0x33, 0xC0, 0x45, 0x33, 0xC9); // ctx, buffer, 0, NULL
        E(0x48, 0x8B, 0x03, 0xFF, 0x90); I32(UpdateSubresourceSlot * 8); // call [ctx_vtbl+180h] (UpdateSubresource)
        Label("done");
        E(0x48, 0x83, 0xC4, 0x68, 0x5E, 0x5B, 0xC3);              // add rsp, 68h; pop rsi; pop rbx; ret
        Label("enabled");
        E(0x01);

        var code = c.ToArray();
        foreach (var (at, label) in fixups)
            BitConverter.TryWriteBytes(code.AsSpan(at, 4), labels[label] - (at + 4));
        // cmp byte [rip+disp32], imm8: rip is after the imm8.
        BitConverter.TryWriteBytes(code.AsSpan(enabledRef, 4), labels["enabled"] - (enabledRef + 5));
        return (code, labels["enabled"]);
    }

    /// <summary>Allocates executable memory within ±1.5 GB of <paramref name="near"/> (rel32 reach).</summary>
    public static nint AllocateNear(nint near, int size)
    {
        const long range = 0x60000000;
        var infoSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        foreach (var upward in new[] { true, false })
        {
            var address = (long)near & ~0xFFFFL;
            while (Math.Abs(address - (long)near) < range && address > 0x10000)
            {
                if (VirtualQuery((nint)address, out var info, infoSize) == 0) break;
                var regionStart = (long)info.BaseAddress;
                var regionEnd = regionStart + (long)info.RegionSize;
                if (info.State == 0x10000) // MEM_FREE
                {
                    var candidate = upward ? (regionStart + 0xFFFF) & ~0xFFFFL : (regionEnd - size) & ~0xFFFFL;
                    if (candidate >= regionStart && candidate + size <= regionEnd && Math.Abs(candidate - (long)near) < range)
                    {
                        var block = VirtualAlloc((nint)candidate, (nuint)size, 0x3000, 0x40);
                        if (block != 0) return block;
                    }
                }
                address = upward ? regionEnd : regionStart - 1;
            }
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")] private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation info, nuint length);
    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
}
