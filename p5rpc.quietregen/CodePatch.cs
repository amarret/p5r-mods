using System.Runtime.InteropServices;

namespace p5rpc.quietregen;

/// <summary>
/// A few bytes of game code that can be switched between their original and a patched version. The bytes must lie
/// inside one aligned 8-byte block, so each switch is a single atomic write: a thread running that code sees either
/// the old or the new instruction, never a mix.
/// </summary>
public sealed unsafe class CodePatch
{
    private const uint PageExecuteReadWrite = 0x40;

    private readonly byte[] _original;
    private readonly byte[] _patched;

    public CodePatch(string name, nint address, byte[] original, byte[] patched)
    {
        if (original.Length != patched.Length || original.Length == 0)
            throw new ArgumentException("Original and patched bytes must have the same, non-zero length.");
        if ((address & 7) + original.Length > 8)
            throw new ArgumentException($"{name} at 0x{address:X} crosses an 8-byte boundary and cannot be written atomically.");

        Name = name;
        Address = address;
        _original = original;
        _patched = patched;
    }

    public string Name { get; }
    public nint Address { get; }

    /// <summary>True when the patched bytes are in place.</summary>
    public bool IsApplied => Matches(_patched);

    /// <summary>True when the code holds either the original or the patched bytes (nobody else changed it).</summary>
    public bool IsRecognised => Matches(_original) || Matches(_patched);

    /// <summary>Writes the patched (<paramref name="apply"/> = true) or the original bytes. Returns false if the code was changed by someone else.</summary>
    public bool Set(bool apply)
    {
        if (!IsRecognised)
            return false;

        var wanted = apply ? _patched : _original;
        if (!Matches(wanted))
            Write(Address, wanted);
        return true;
    }

    private bool Matches(byte[] bytes) => new ReadOnlySpan<byte>((void*)Address, bytes.Length).SequenceEqual(bytes);

    private static void Write(nint address, byte[] bytes)
    {
        var block = address & ~(nint)7;
        var offset = (int)(address - block);

        VirtualProtect(block, 8, PageExecuteReadWrite, out var oldProtect);
        try
        {
            long current, updated;
            do
            {
                current = Volatile.Read(ref *(long*)block);
                updated = current;
                bytes.CopyTo(new Span<byte>((byte*)&updated + offset, bytes.Length));
            } while (Interlocked.CompareExchange(ref *(long*)block, updated, current) != current);
        }
        finally
        {
            VirtualProtect(block, 8, oldProtect, out _);
            FlushInstructionCache(GetCurrentProcess(), block, 8);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
