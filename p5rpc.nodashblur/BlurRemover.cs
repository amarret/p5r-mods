using Reloaded.Hooks.Definitions.X64;

namespace p5rpc.nodashblur;

/// <summary>
/// CameraSpeedEffect(camera, ?, float frameTime): per-frame update of the field camera's speed effect, switched on
/// while Joker sprints. It fades the effect in and out, submits a full-screen radial blur scaled by how fast the
/// camera moves forward, and (in Palaces) queues the white speed lines.
/// </summary>
[Function(CallingConventions.Microsoft)]
public delegate nint CameraSpeedEffectFn(nint camera, nint unused, float frameTime);

/// <summary>
/// DashFilterUpdate(task, float frameTime): per-frame update of the <c>fld_dash_filter</c> task, played when Joker
/// leaps to a target point. It computes the effect's intensity (also used by its speed lines), submits a radial blur,
/// then queues the speed-line sprites.
/// </summary>
[Function(CallingConventions.Microsoft)]
public delegate nint DashFilterUpdateFn(nint task, float frameTime);

/// <summary>
/// SubmitRadialBlur(params): queues one radial-blur pass for this frame. Shared by every effect that blurs the screen
/// (battle, cutscenes...); the return value is not used by its callers.
/// </summary>
[Function(CallingConventions.Microsoft)]
public delegate nint SubmitRadialBlurFn(nint parameters);

/// <summary>Effects whose radial blur can be removed.</summary>
public enum BlurSource
{
    None,
    /// <summary>The field camera's speed effect while sprinting.</summary>
    Sprint,
    /// <summary>The dash filter played when leaping to a target point.</summary>
    TargetLeap,
}

/// <summary>
/// Drops the radial blur submitted by chosen effects, and only by them: a blur submitted while one of their update
/// functions is running on the same thread is skipped when that effect is switched on. The rest of each update
/// (fades, speed lines) runs as usual.
/// </summary>
public sealed class BlurRemover
{
    [ThreadStatic] private static BlurSource _running;

    private volatile bool _sprint = true;
    private volatile bool _targetLeap;
    private int _droppedSprint;
    private int _droppedTargetLeap;

    public CameraSpeedEffectFn? OriginalCameraSpeedEffect { get; set; }
    public DashFilterUpdateFn? OriginalDashFilterUpdate { get; set; }
    public SubmitRadialBlurFn? OriginalSubmitRadialBlur { get; set; }

    /// <summary>Called the first time a blur from a source is dropped.</summary>
    public Action<BlurSource>? FirstDrop { get; set; }

    public bool RemoveSprint
    {
        get => _sprint;
        set => _sprint = value;
    }

    public bool RemoveTargetLeap
    {
        get => _targetLeap;
        set => _targetLeap = value;
    }

    /// <summary>Number of blur passes dropped so far for <paramref name="source"/>.</summary>
    public int Dropped(BlurSource source) => source switch
    {
        BlurSource.Sprint => Volatile.Read(ref _droppedSprint),
        BlurSource.TargetLeap => Volatile.Read(ref _droppedTargetLeap),
        _ => 0,
    };

    public nint CameraSpeedEffect(nint camera, nint unused, float frameTime)
    {
        var outer = _running;
        _running = BlurSource.Sprint;
        try
        {
            return OriginalCameraSpeedEffect!(camera, unused, frameTime);
        }
        finally
        {
            _running = outer;
        }
    }

    public nint DashFilterUpdate(nint task, float frameTime)
    {
        var outer = _running;
        _running = BlurSource.TargetLeap;
        try
        {
            return OriginalDashFilterUpdate!(task, frameTime);
        }
        finally
        {
            _running = outer;
        }
    }

    public nint SubmitRadialBlur(nint parameters)
    {
        switch (_running)
        {
            case BlurSource.Sprint when _sprint:
                Drop(ref _droppedSprint, BlurSource.Sprint);
                return 0;
            case BlurSource.TargetLeap when _targetLeap:
                Drop(ref _droppedTargetLeap, BlurSource.TargetLeap);
                return 0;
            default:
                return OriginalSubmitRadialBlur!(parameters);
        }
    }

    private void Drop(ref int counter, BlurSource source)
    {
        if (Interlocked.Increment(ref counter) == 1)
            FirstDrop?.Invoke(source);
    }
}
