using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.cbtrim.Template.Configuration;

namespace p5rpc.cbtrim.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Rendering")]
    [DisplayName("Upload only the filled part of constant buffers")]
    [Description("On = for each draw the renderer sends the GPU only the shader constants it actually filled in, instead " +
                 "of the whole buffer (usually 7 KB). Busy places like Shibuya station issue thousands of these uploads per " +
                 "frame, so this saves a lot of copying on the render thread. Shaders get the same values. The unused rest of " +
                 "each buffer still holds leftovers from earlier draws, as in vanilla, but not always the same leftovers. " +
                 "If anything looks wrong (colours, lighting, missing objects), switch this off. Off = vanilla full uploads. " +
                 "Applies straight away.")]
    [DefaultValue(true)]
    [Display(Order = 0)]
    public bool TrimUploads { get; set; } = true;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
