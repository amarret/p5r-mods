using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.setpalacesecurity.Template.Configuration;
using Reloaded.Mod.Interfaces.Structs;

namespace p5rpc.setpalacesecurity.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Security level")]
    [DisplayName("Fix the security level")]
    [Description("On = in the Palaces, the security level always stays at the level below: being spotted, ambushes " +
                 "and safe rooms no longer change it, and it is back at that level each time you enter a Palace. " +
                 "Outside the Palaces nothing is changed. Off = vanilla.")]
    [DefaultValue(true)]
    [Display(Order = 0)]
    public bool FixLevel { get; set; } = true;

    [Category("01. Security level")]
    [DisplayName("Level")]
    [Description("The security level the Palaces stay at, in percent (0 = no security, 100 = the most). After the " +
                 "calling card has been sent, the game keeps it at 80 or more, as usual. A change applies the next time " +
                 "a Palace area loads, or the next time the level would change.")]
    [DefaultValue(0)]
    [Display(Order = 1)]
    [SliderControlParams(minimum: 0, maximum: 100, smallChange: 1, largeChange: 10, tickFrequency: 10, isSnapToTickEnabled: false,
        tickPlacement: SliderControlTickPlacement.BottomRight, showTextField: true, isTextFieldEditable: true, textValidationRegex: "\\d{1,3}")]
    public int Level { get; set; } = 0;

    [Category("02. Troubleshooting")]
    [DisplayName("Log security changes")]
    [Description("Writes one line per change the game makes to the security level, with what the mod did, to the " +
                 "Reloaded console and to events.log in the mod's folder.")]
    [DefaultValue(false)]
    [Display(Order = 2)]
    public bool LogEvents { get; set; } = false;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
