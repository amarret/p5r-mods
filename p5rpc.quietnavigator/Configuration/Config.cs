using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.quietnavigator.Template.Configuration;

namespace p5rpc.quietnavigator.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Lines to silence")]
    [DisplayName("Critical hit on an enemy")]
    [Description("On = everything the navigator says because a party member landed a critical hit on an enemy " +
                 "(\"Nice crit!\", \"A critical hit! Nice one, Crow!\"...) is dropped: no voice and no dialogue window. " +
                 "Off = it plays as usual.")]
    [DefaultValue(true)]
    [Display(Order = 0)]
    public bool SilenceCriticalHits { get; set; } = true;

    [Category("01. Lines to silence")]
    [DisplayName("Enemy knocked down")]
    [Description("On = everything the navigator says because enemies were knocked to the floor, whether by a critical hit " +
                 "or a weakness (\"One enemy on the floor!\", \"Three enemies knocked down!\", \"Now! Surround 'em!\"...) is " +
                 "dropped: no voice and no dialogue window. Lines about party members being knocked down still play. Off = it plays as usual.")]
    [DefaultValue(true)]
    [Display(Order = 1)]
    public bool SilenceKnockdowns { get; set; } = true;

    [Category("01. Lines to silence")]
    [DisplayName("Weakness comments when analysing enemies")]
    [Description("On = the comment made when an enemy is analysed (\"Fire is its weak point!\", \"It's strong, but weak to " +
                 "gunfire!\", \"Don't use Ice skills!\", \"I don't see a weakness...\") is dropped: no voice and no dialogue " +
                 "window. Off = it plays as usual. When Makoto is in the party she gives these comments, so her switch applies.")]
    [DefaultValue(true)]
    [Display(Order = 2)]
    public bool SilenceAnalysis { get; set; } = true;

    [Category("01. Lines to silence")]
    [DisplayName("Baton Pass")]
    [Description("On = everything the navigator says because a Baton Pass was made (\"All right! On to the next!\", " +
                 "\"A second Baton Pass! Nice!\", \"Everyone passed the baton!\") and the comments on the power boost it gives " +
                 "(\"The Baton Pass powered you up!\", \"Your power's going up!\"...) is dropped: no voice and no dialogue window. " +
                 "Hints suggesting a Baton Pass still play. Off = it plays as usual.")]
    [DefaultValue(true)]
    [Display(Order = 3)]
    public bool SilenceBatonPass { get; set; } = true;

    [Category("02. Navigators")]
    [DisplayName("Morgana")]
    [Description("On = the lines switched on above are silenced when Morgana is speaking.")]
    [DefaultValue(true)]
    [Display(Order = 4)]
    public bool Morgana { get; set; } = true;

    [Category("02. Navigators")]
    [DisplayName("Makoto")]
    [Description("On = the lines switched on above are silenced when Makoto is speaking.")]
    [DefaultValue(true)]
    [Display(Order = 5)]
    public bool Makoto { get; set; } = true;

    [Category("02. Navigators")]
    [DisplayName("Futaba")]
    [Description("On = the lines switched on above are silenced when Futaba is speaking.")]
    [DefaultValue(true)]
    [Display(Order = 6)]
    public bool Futaba { get; set; } = true;

    [Category("02. Navigators")]
    [DisplayName("Akechi")]
    [Description("On = the lines switched on above are silenced when Akechi is navigating.")]
    [DefaultValue(true)]
    [Display(Order = 7)]
    public bool Akechi { get; set; } = true;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
