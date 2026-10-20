# Confidant Ability & Showtime Toggles (P5R PC, Reloaded-II)

Every confidant ability and every Showtime gets its own on/off switch in the mod's
Reloaded-II configuration, plus one switch for Futaba's All-Out Attack takeover.

- **Off** = the ability or Showtime never takes effect, even if you already have it or
  unlock it later.
- **On** = vanilla behaviour: it works as soon as you have unlocked it in game.
- Your save is never modified. Ranks and Showtime unlock flags keep progressing normally,
  so you can flip a switch at any time.

## Install

1. Download `p5rpc.confidanttoggles-<version>.zip` from the
   [Releases page](https://github.com/amarret/p5r-mods/releases) and extract it into `Reloaded-II\Mods`.
2. In Reloaded-II, select Persona 5 Royal and tick **Confidant Ability & Showtime Toggles**.
   It needs `reloaded.sharedlib.hooks` and `Reloaded.Memory.SigScan.ReloadedII`; if they are
   missing, Reloaded-II offers to download them.
3. Right-click the mod → **Configure Mod** to set the switches.

Changes saved in the configuration window apply straight away, even while the game is
running. The Reloaded log shows
`[p5rpc.confidanttoggles] N ability IDs and M Showtimes disabled, Futaba's All-Out Attack takeover on/off.`
each time.

## What's in the config

| Section | Contents |
| --- | --- |
| 01. Showtimes | Morgana & Ann, Morgana & Haru, Ann & Yusuke, Ryuji & Yusuke, Ryuji & Makoto, Joker & Akechi, Makoto & Haru, Joker & Kasumi |
| 02–23 | One section per confidant (Igor … Maruki), one switch per ability, labelled with the rank that grants it. |
| 11. Hermit – Futaba | Also has **Futaba's All-Out Attack takeover**: off stops her from replacing the party's All-Out Attack with her bomb animation (Explosive Scheme). |
| 22. Faith – Kasumi / Sumire | **Rank 4: Chaînés Hook (Joker's grappling-hook attack)**: off stops Joker from attacking enemies with the grappling hook. Grappling onto ledges and grapple points is not a confidant ability and works as usual. |

Not listed, on purpose:

- **Second Awakening** (and Royal's third-tier awakenings): these permanently evolve the
  Persona when they happen; there is no lasting effect a switch could turn off.
- **Baton Pass**: Royal gives it to everyone from the start, so it isn't a confidant ability
  in this version.
- **Sae (Judgement)**: not included.

## How it works

The mod hooks two game functions and patches one byte, all found by byte signature at startup:

- `CmmCheckEnableFunc(abilityId)`: the game's single "is this confidant ability active?"
  check. It is called from about 310 places in battle and field code, and by the
  `CMM_CHECK_ENABLE_FUNC` flowscript function that field scripts use (Leblanc coffee/curry,
  Kawakami, Chihaya's readings, Haru's garden, Yusuke's cards, infiltration tools…).
  For a disabled ability the hook answers "not unlocked"; otherwise it calls the original.
- `GetShowtimeIndex(unitA, unitB)`: returns which Showtime two party members share, if its
  unlock flag (`0x30000105`–`0x3000010B`, `0x30000116`) is set. For a disabled Showtime the
  hook answers "none", which is what the game sees for a Showtime that isn't unlocked yet.

- **Futaba's takeover**: when an All-Out Attack starts, the game rolls `rand() % 100` against
  a 50% (or 70%) chance and, on success, runs `mov bl, 1` to switch to her bomb sequence
  (`bksk_attack_futaba_0X.BCD` + `bksk_futaba.BCD`). With the switch off the mod writes
  `mov bl, 0` there, which is exactly the game's own "roll failed" path, so the normal
  All-Out Attack and finish play. Switching it on writes the `1` back.

The signatures were made for the current Steam `P5R.exe`. If a game update moves the code,
the log says which part could not be found and that part simply stays vanilla.

## Caveats (not yet tested in game)

- **Wild Talk** and **Third Eye** are needed by parts of the story. Turning them off can
  get in the way of tutorials and Palace puzzles.
- **Power / Super / Ultra Stock**: unknown what happens if you already hold more Personas
  than the smaller stock allows. Shrink your stock first.
- The confidant menu may show a disabled ability as not acquired yet.
- With Futaba's takeover off, an All-Out Attack that she would have turned into a "defeat
  all foes" bomb is a normal All-Out Attack, so some enemies can survive it.

## Rebuilding

```
dotnet build -c Release
```

The output goes to `..\dist\p5rpc.confidanttoggles`. `Configuration/Config.cs` is generated
from the game's data by `tools/gen_config.py` (see the comment at the top of that script).
To rename or reword something, edit the script and re-run it.
