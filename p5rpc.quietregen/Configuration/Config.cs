using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.quietregen.Template.Configuration;

namespace p5rpc.quietregen.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Sounds to silence")]
    [DisplayName("Turn-start HP / SP recovery")]
    [Description("On = no sound when HP or SP is restored at the start of a turn by an ability (Regenerate, Invigorate, " +
                 "Holy Whisper, Holy Embrace, Heat Up...) or an accessory. The recovery itself and its numbers still " +
                 "happen. Healing skills and items used in battle keep their sound. Off = vanilla.")]
    [DefaultValue(true)]
    [Display(Order = 0)]
    public bool SilenceTurnStartRecovery { get; set; } = true;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
