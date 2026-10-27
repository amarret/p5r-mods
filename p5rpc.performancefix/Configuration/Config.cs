using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.performancefix.Template.Configuration;

namespace p5rpc.performancefix.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Rendering")]
    [DisplayName("Rendering performance fix")]
    [Description("Each draw sends the GPU only the shader constants it filled in, not the whole 7 KB buffer, which raises " +
                 "the frame rate in busy places like Shibuya. Off = vanilla uploads; applies straight away.")]
    [DefaultValue(true)]
    [Display(Order = 0)]
    public bool TrimUploads { get; set; } = true;

    [Category("02. Loading")]
    [DisplayName("Smooth loading")]
    [Description("Removes the stalls when changing area: no waiting for the old area's sounds or for CRI's file server, and " +
                 "small constant buffers so the graphics driver no longer allocates tens of MB at once. Off = vanilla; applies straight away.")]
    [DefaultValue(true)]
    [Display(Order = 1)]
    public bool SmoothTransitions { get; set; } = true;

    [Category("02. Loading")]
    [DisplayName("Direct fast travel")]
    [Description("After you pick a fast-travel destination, the loading animation starts right over the map instead of " +
                 "after the map's closing fade. Off = vanilla; applies straight away.")]
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
