# Quiet Navigator (Battle) (P5R PC, Reloaded-II)

Silences chosen navigator battle lines **by the battle event that triggers them**, before they are voiced or
shown: no voice and no dialogue window. Every other line plays as usual. Works the same with English and
Japanese voices.

| Switch | What is silenced |
| --- | --- |
| Critical hit on an enemy | Everything the navigator says because a party member landed a critical hit ("Nice crit!", "A critical hit! Nice one, Crow!"...) |
| Enemy knocked down | Everything the navigator says because enemies were knocked to the floor, by a crit or a weakness ("One enemy on the floor!", "Three enemies knocked down!", "Now! Surround 'em!"...) |
| Weakness comments when analysing enemies | The comment made when an enemy is analysed ("Fire is its weak point!", "It's strong, but weak to gunfire!", "Don't use Ice skills!", "I don't see a weakness...") |
| Baton Pass | Everything the navigator says because a Baton Pass was made ("All right! On to the next!", "A second Baton Pass! Nice!", "Everyone passed the baton!"), plus the comments on the power boost it gives ("The Baton Pass powered you up!", "Your power's going up!"...). Hints that *suggest* a Baton Pass still play. |
| Morgana / Makoto / Futaba / Akechi | Who the switches above apply to |

A line is silenced when its event is switched on **and** the character saying it is switched on. Makoto
gives the analysis comments when she is in the party, so her switch applies to those.

## Install

1. Download `p5rpc.quietnavigator-<version>.zip` from the [Releases page](https://github.com/amarret/p5r-mods/releases)
   and extract it into `Reloaded-II\Mods` (replace any older copy).
2. In Reloaded-II, select Persona 5 Royal and tick **Quiet Navigator (Battle)**. It needs
   `reloaded.sharedlib.hooks` and `Reloaded.Memory.SigScan.ReloadedII`; if they are missing, Reloaded-II offers to
   download them.
3. Right-click the mod → **Configure Mod** to change the switches. Changes apply straight away, even mid-battle.

At start-up the Reloaded console shows `Listening to MORGANA's / FUTABA's / AKECHI's ... Baton Pass events` and
`Hooked the navigator line routine`. Each silenced line is logged, e.g.
`Silenced CriticalHit line btl_support_08_391 by Futaba.` or `Silenced an analysis comment by Makoto.`

## How it works

The navigator's battle logic is one C++ class per navigator (`btl::support::MORGANA`, `FUTABA`, `AKECHI`,
derived from `btl::InfoSupport`). The battle calls a virtual method on it for each event, and that method
picks what to say. The mod finds the three classes' vtables at start-up from their RTTI names, then replaces
five slots:

- **Slot 17, critical hit**, **slot 13, enemies knocked down** and **slot 62, Baton Pass**: the original
  method runs, and the mod notes every line it queued. The battle plays queued lines from a per-frame routine
  (found by signature); when a noted line is about to start and its event and speaker are switched on, the
  routine is told the line is finished, so it is discarded before it is shown or voiced.
- **Slot 10, status applied to a party member**: same, but only its Baton Pass boost lines (messages
  749-754) are noted; its ailment lines ("Mona's burning!") are left alone.
- **Slot 60, enemy analysed**: this method plays its comment straight away, so when it is switched on for
  the speaker the method is skipped. A separate check from the game decides whether Makoto or the navigator
  would give it.

Because it works on events, every line an event can produce is covered: the per-character reactions,
generic ones, Violet's lines added in Royal, and the voice-only variants. The audio language only changes
which voice bank the game loads afterwards, so English and Japanese behave the same. Nothing on disk or in
the save is touched.

## Notes

- Not yet tested in game.
- Lines about your own party members (knocked down, low HP...) and other events (weakness hits, misses,
  enemies defeated...) are not affected.
- Works with CBT (`p5r.enhance.cbt`): CBT changes where the navigator text comes from, not how the events
  pick their lines.
- Don't combine it with "No Navigator Chatter": both change the same line routine.

## Credits

The per-frame line routine and its "finished" answer were documented by Press Turn Project's
[No Navigator Chatter](https://gamebanana.com/mods/726386), which silences every navigator line.

## Rebuilding

```
dotnet build -c Release
```

The output goes to `..\dist\p5rpc.quietnavigator`. Event slots are listed in `EventSlots` (`NavigatorEvents.cs`);
adding another event means adding its slot there, a `LineKind`, a replacement handler and a config switch.
