using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.performancefix.Template.Configuration;

namespace p5rpc.performancefix.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Rendering")]
    [DisplayName("Rendering performance fix")]
    [Description("On = for each draw the renderer sends the GPU only the shader constants it actually filled in, instead " +
                 "of the whole buffer (usually 7 KB). Busy places like Shibuya station issue thousands of these uploads per " +
                 "frame, so this saves a lot of copying on the render thread. Shaders get the same values. The unused rest of " +
                 "each buffer still holds leftovers from earlier draws, as in vanilla, but not always the same leftovers. " +
                 "If anything looks wrong (colours, lighting, missing objects), switch this off. Off = vanilla full uploads. " +
                 "With \"Smooth loading\" on, this only concerns blocks over 4 KB. Applies straight away.")]
    [DefaultValue(true)]
    [Display(Order = 0)]
    public bool TrimUploads { get; set; } = true;

    [Category("02. Loading")]
    [DisplayName("Smooth loading")]
    [Description("On = when you change area (train rides, doors, entering a Palace...) the game no longer stops for " +
                 "20-60 ms waiting for the old area's ambient sounds to finish fading out before it frees their sound " +
                 "banks: CRI's own background release frees them a moment later instead. Its file lookups and other " +
                 "file waits also go ahead at once instead of waiting for CRI's next 60 Hz server tick, and the checks " +
                 "for optional files that do not exist take about 0.2 ms instead of 2 ms (shorter polls). Also keeps the Windows " +
                 "timer at 1 ms. And shader constants go into small buffers (256 bytes to 4 KB) sized to what each of the " +
                 "game's 7 KB buffers is ever filled with: the graphics driver backs every upload with fresh memory the size " +
                 "of the whole buffer, so an area's first frames had to find 40-50 MB at once and took 10-30 ms. Shaders get " +
                 "the same values; constants the game never fills read as zero. Off = vanilla (and constant-buffer uploads " +
                 "as set above). Applies straight away.")]
    [DefaultValue(true)]
    [Display(Order = 1)]
    public bool SmoothTransitions { get; set; } = true;

    [Category("02. Loading")]
    [DisplayName("Direct fast travel")]
    [Description("On = when you pick a destination on the fast-travel map, the loading animation (subway, Velvet Room...) " +
                 "starts right over the map, and the map closes behind it. Vanilla first plays the map's closing fade " +
                 "(about 0.3 s) and starts the animation after it, so the field shows for a frame in between. Destinations " +
                 "that use the crowd scene change keep the closing fade. Off = vanilla. Applies straight away.")]
    [DefaultValue(true)]
    [Display(Order = 2)]
    public bool DirectMapTravel { get; set; } = true;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
