using p5rpc.quietnavigator.Configuration;

namespace p5rpc.quietnavigator;

/// <summary>Battle events whose navigator lines can be silenced.</summary>
internal enum LineKind
{
    CriticalHit,
    Knockdown,
    Analysis,
    BatonPass,
}

/// <summary>Character IDs used as navigator/speaker IDs by the battle.</summary>
internal static class Speakers
{
    public const int Morgana = 3;
    public const int Makoto = 6;
    public const int Futaba = 8;
    public const int Akechi = 9;

    public static string Name(int id) => id switch
    {
        Morgana => "Morgana",
        Makoto => "Makoto",
        Futaba => "Futaba",
        Akechi => "Akechi",
        _ => $"character {id}",
    };
}

/// <summary>
/// An immutable snapshot of which events and speakers are silenced, swapped in whole when the config changes.
/// </summary>
internal sealed class LineFilter
{
    private const int MaxSpeakerId = 16;
    private static readonly int KindCount = Enum.GetValues<LineKind>().Length;

    public static readonly LineFilter None = new(new bool[KindCount], new bool[MaxSpeakerId]);

    private readonly bool[] _kinds;
    private readonly bool[] _speakers;

    private LineFilter(bool[] kinds, bool[] speakers)
    {
        _kinds = kinds;
        _speakers = speakers;
    }

    public static LineFilter FromConfig(Config config)
    {
        var kinds = new bool[KindCount];
        kinds[(int)LineKind.CriticalHit] = config.SilenceCriticalHits;
        kinds[(int)LineKind.Knockdown] = config.SilenceKnockdowns;
        kinds[(int)LineKind.Analysis] = config.SilenceAnalysis;
        kinds[(int)LineKind.BatonPass] = config.SilenceBatonPass;

        var speakers = new bool[MaxSpeakerId];
        speakers[Speakers.Morgana] = config.Morgana;
        speakers[Speakers.Makoto] = config.Makoto;
        speakers[Speakers.Futaba] = config.Futaba;
        speakers[Speakers.Akechi] = config.Akechi;

        return new LineFilter(kinds, speakers);
    }

    public bool IsOn(LineKind kind) => _kinds[(int)kind];

    public bool Silences(LineKind kind, int speaker) =>
        _kinds[(int)kind] && (uint)speaker < MaxSpeakerId && _speakers[speaker];

    public string Describe()
    {
        var kinds = Enum.GetValues<LineKind>().Where(k => _kinds[(int)k]).Select(k => k.ToString()).ToArray();
        var speakers = new[] { Speakers.Morgana, Speakers.Makoto, Speakers.Futaba, Speakers.Akechi }
            .Where(s => _speakers[s]).Select(Speakers.Name).ToArray();
        return $"{(kinds.Length == 0 ? "nothing" : string.Join(", ", kinds))} for {(speakers.Length == 0 ? "no one" : string.Join(", ", speakers))}";
    }
}

/// <summary>
/// Layout of a queued navigator line (the payload of a list node, 0x10 bytes into it), as written by the queueing
/// code and read by the per-frame line routine.
/// </summary>
internal static unsafe class LineEntry
{
    public const int MessageIdOffset = 0x08;   // int: index of the message in the navigator's message file (name number - 1)
    public const int ExpressionOffset = 0x0C;  // int: portrait looked up together with the speaker
    public const int SpeakerOffset = 0x14;     // short: speaking character, -1 = the current navigator
    public const int PhaseOffset = 0x24;       // int: 0 = delay before the line, 1 = the line itself
    public const int StepOffset = 0x28;        // ushort: step within phase 1, 0 = nothing shown yet

    public static int MessageId(nint entry) => *(int*)(entry + MessageIdOffset);

    public static int Speaker(nint entry, int currentNavigator)
    {
        var speaker = *(short*)(entry + SpeakerOffset);
        return speaker != -1 ? speaker : currentNavigator;
    }

    /// <summary>True while nothing of the line has been shown or voiced yet.</summary>
    public static bool NotStarted(nint entry)
    {
        var phase = *(int*)(entry + PhaseOffset);
        return phase == 0 || (phase == 1 && *(ushort*)(entry + StepOffset) == 0);
    }
}

/// <summary>
/// Remembers which queued lines were created by which battle event, so they can be dropped later by the
/// per-frame line routine. Entries are matched by address and message ID and forgotten after a while, in case
/// the game frees them some other way (e.g. when an analysis comment clears the queue).
/// </summary>
internal sealed class EventTags
{
    private readonly record struct Tag(LineKind Kind, int MessageId, long Time);

    private const long MaxAgeMs = 30_000;
    private readonly Dictionary<nint, Tag> _tags = new();
    private readonly object _lock = new();

    public void Add(nint entry, LineKind kind, int messageId)
    {
        lock (_lock)
            _tags[entry] = new Tag(kind, messageId, Environment.TickCount64);
    }

    public bool TryGet(nint entry, int messageId, out LineKind kind)
    {
        lock (_lock)
        {
            if (_tags.TryGetValue(entry, out var tag))
            {
                if (tag.MessageId == messageId && Environment.TickCount64 - tag.Time <= MaxAgeMs)
                {
                    kind = tag.Kind;
                    return true;
                }

                _tags.Remove(entry);
            }
        }

        kind = default;
        return false;
    }

    public void Remove(nint entry)
    {
        lock (_lock)
            _tags.Remove(entry);
    }

    public void Clear()
    {
        lock (_lock)
            _tags.Clear();
    }

    public int Count
    {
        get { lock (_lock) return _tags.Count; }
    }
}
