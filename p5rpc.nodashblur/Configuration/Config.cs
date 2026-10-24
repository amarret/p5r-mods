using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.nodashblur.Template.Configuration;

namespace p5rpc.nodashblur.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Blur to remove")]
    [DisplayName("Sprinting")]
    [Description("On = sprinting no longer blurs the screen or washes out its colours. The white speed lines (in " +
                 "Palaces) and the camera's FOV change play as usual. Off = vanilla.")]
    [DefaultValue(true)]
    [Display(Order = 0)]
    public bool RemoveSprintBlur { get; set; } = true;

    [Category("01. Blur to remove")]
    [DisplayName("Turning the camera")]
    [Description("On = the camera no longer blurs the screen or washes out its colours while it swings round quickly, " +
                 "as when it recentres behind Joker. Off = vanilla.")]
    [DefaultValue(true)]
    [Display(Order = 1)]
    public bool RemoveTurnBlur { get; set; } = true;

    [Category("01. Blur to remove")]
    [DisplayName("Leaping to a target point")]
    [Description("On = the short dash effect played when Joker leaps to a target point (and scenes that use the same " +
                 "effect) loses its blur and washed-out colours too; its white lines stay. Off = vanilla.")]
    [DefaultValue(false)]
    [Display(Order = 2)]
    public bool RemoveTargetLeapBlur { get; set; } = false;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
