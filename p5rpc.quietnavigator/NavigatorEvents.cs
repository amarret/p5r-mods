using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace p5rpc.quietnavigator;

/// <summary>
/// The navigator's battle logic is a C++ class per navigator (btl::support::MORGANA / FUTABA / AKECHI, derived from
/// btl::InfoSupport). The battle tells it what happened through virtual methods, one vtable slot per event, and each
/// method picks the lines to say. These are the slots this mod listens to.
/// </summary>
internal static class EventSlots
{
    /// <summary>
    /// A status was applied to a party member: ailments ("Mona's burning!"...) and the power boost a Baton Pass gives
    /// ("The Baton Pass powered you up!"...). Only the boost lines, <see cref="BatonPassBoostMessages"/>, are silenced.
    /// </summary>
    public const int PartyStatus = 10;

    /// <summary>Enemies were knocked down (any cause): queues "Three enemies knocked down!", "Now! Surround 'em!"...</summary>
    public const int Knockdown = 13;

    /// <summary>A critical hit landed on an enemy: queues "Nice crit!", "A critical hit! Nice one, Crow!"...</summary>
    public const int CriticalHit = 17;

    /// <summary>An enemy was analysed: picks one comment on its strength and affinities and plays it at once.</summary>
    public const int Analysis = 60;

    /// <summary>A Baton Pass was made: queues "All right! On to the next!", "A second Baton Pass! Nice!"...</summary>
    public const int BatonPass = 62;

    /// <summary>Messages 749-754 of the party-status event: the boost from the first, second and later passes.</summary>
    public static bool BatonPassBoostMessages(int message) => message is >= 749 and <= 754;
}

/// <summary>One navigator class, its vtable and the original handlers of the slots we replace.</summary>
internal sealed class NavigatorClass
{
    public required string Name { get; init; }
    public required int Speaker { get; init; }
    public nint VTable { get; set; }
    public Dictionary<int, nint> Originals { get; } = new();
}

/// <summary>
/// Finds a class's vtable from its MSVC RTTI type name, so nothing depends on fixed addresses. Only the module's
/// readable, non-executable sections are searched (RTTI and vtables live there; the protected code does not).
/// </summary>
internal static unsafe class Rtti
{
    private const uint ScnMemExecute = 0x20000000;
    private const uint ScnMemRead = 0x40000000;

    public static nint FindVTable(nint moduleBase, string typeName)
    {
        var sections = DataSections(moduleBase);

        // TypeDescriptor: { void* vftable; void* spare; char name[]; }
        var name = Find(sections, Encoding.ASCII.GetBytes(typeName + "\0"));
        if (name == 0)
            return 0;

        var typeDescriptorRva = (int)(name - moduleBase - 0x10);

        // CompleteObjectLocator: { int signature = 1; int offset = 0; int cdOffset = 0; int pTypeDescriptor; int pClassDescriptor; int pSelf; }
        var locatorPattern = new byte[16];
        locatorPattern[0] = 1;
        BitConverter.TryWriteBytes(locatorPattern.AsSpan(12), typeDescriptorRva);
        var locator = Find(sections, locatorPattern);
        if (locator == 0 || *(int*)(locator + 0x14) != (int)(locator - moduleBase))
            return 0;

        // vftable[-1] points at the locator. Pointers in the image are based on the PE header's ImageBase, which the
        // loader rewrites if it relocates the module.
        var imageBase = *(long*)(moduleBase + *(int*)(moduleBase + 0x3C) + 0x30);
        var reference = Find(sections, BitConverter.GetBytes(imageBase + (locator - moduleBase)));
        return reference != 0 ? reference + 8 : 0;
    }

    private static List<(nint Start, int Length)> DataSections(nint moduleBase)
    {
        var ntHeaders = moduleBase + *(int*)(moduleBase + 0x3C);
        int count = *(ushort*)(ntHeaders + 6);
        int optionalHeaderSize = *(ushort*)(ntHeaders + 20);
        var header = ntHeaders + 24 + optionalHeaderSize;
        var result = new List<(nint, int)>();
        for (var i = 0; i < count; i++, header += 40)
        {
            var virtualSize = *(int*)(header + 8);
            var virtualAddress = *(int*)(header + 12);
            var characteristics = *(uint*)(header + 36);
            if ((characteristics & ScnMemRead) != 0 && (characteristics & ScnMemExecute) == 0 && virtualSize > 0)
                result.Add((moduleBase + virtualAddress, virtualSize));
        }

        return result;
    }

    private static nint Find(List<(nint Start, int Length)> sections, byte[] pattern)
    {
        foreach (var (start, length) in sections)
        {
            var index = new ReadOnlySpan<byte>((void*)start, length).IndexOf(pattern);
            if (index >= 0)
                return start + index;
        }

        return 0;
    }
}

/// <summary>Writes vtable slots (read-only data) safely.</summary>
internal static unsafe class VTablePatcher
{
    private const uint PageReadWrite = 0x04;

    public static nint Exchange(nint vtable, int slot, nint function)
    {
        var address = vtable + slot * 8;
        VirtualProtect(address, 8, PageReadWrite, out var old);
        var previous = Interlocked.Exchange(ref *(nint*)address, function);
        VirtualProtect(address, 8, old, out _);
        return previous;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
}

/// <summary>
/// The replacement handlers written into the navigator vtables. They are plain native-callable functions that take
/// and forward six arguments (this, battle, two register arguments and two stack slots), which covers every handler
/// we replace: the Baton Pass handler reads one stack argument, the others none. Handlers ignore extra arguments.
/// </summary>
internal static unsafe class EventHandlers
{
    /// <summary>Set by <see cref="Mod"/> before any vtable slot is replaced.</summary>
    public static Mod? Owner;

    public static nint PartyStatusPointer => (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&OnPartyStatus;
    public static nint KnockdownPointer => (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&OnKnockdown;
    public static nint CriticalHitPointer => (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&OnCriticalHit;
    public static nint AnalysisPointer => (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&OnAnalysis;
    public static nint BatonPassPointer => (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&OnBatonPass;

    [UnmanagedCallersOnly]
    private static nint OnPartyStatus(nint self, nint battle, nint a3, nint a4, nint a5, nint a6) =>
        Owner!.OnQueuedEvent(LineKind.BatonPass, EventSlots.PartyStatus, self, battle, a3, a4, a5, a6, EventSlots.BatonPassBoostMessages);

    [UnmanagedCallersOnly]
    private static nint OnKnockdown(nint self, nint battle, nint a3, nint a4, nint a5, nint a6) =>
        Owner!.OnQueuedEvent(LineKind.Knockdown, EventSlots.Knockdown, self, battle, a3, a4, a5, a6);

    [UnmanagedCallersOnly]
    private static nint OnCriticalHit(nint self, nint battle, nint a3, nint a4, nint a5, nint a6) =>
        Owner!.OnQueuedEvent(LineKind.CriticalHit, EventSlots.CriticalHit, self, battle, a3, a4, a5, a6);

    [UnmanagedCallersOnly]
    private static nint OnAnalysis(nint self, nint battle, nint a3, nint a4, nint a5, nint a6) =>
        Owner!.OnAnalysisEvent(self, battle, a3, a4, a5, a6);

    [UnmanagedCallersOnly]
    private static nint OnBatonPass(nint self, nint battle, nint a3, nint a4, nint a5, nint a6) =>
        Owner!.OnQueuedEvent(LineKind.BatonPass, EventSlots.BatonPass, self, battle, a3, a4, a5, a6);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Call(nint function, nint self, nint battle, nint a3, nint a4, nint a5, nint a6) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)function)(self, battle, a3, a4, a5, a6);
}
