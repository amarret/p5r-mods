# Set Palace Security Level (P5R PC, Reloaded-II)

Keeps the Palace security level at a value you choose, from 0 to 100%, while you are in a Palace. Being spotted, ambushes,
battles, retreating and safe rooms no longer change it, and the meter always shows that level. Outside the Palaces the mod
does nothing.

| Switch | Default | Effect |
| --- | --- | --- |
| Fix the security level | On | In the Palaces, the security level always stays at **Level**. Off: vanilla. |
| Level | 0 | The level to hold, in percent (0-100). |
| Log security changes | Off | One line per security change and what the mod did, in the Reloaded console and in `events.log` in the mod's folder. |

After the calling card has been sent, the game keeps the security level at 80% or more. The mod respects that, so a
lower setting is held at 80% until the Palace is over.

## Install

1. Download `p5rpc.setpalacesecurity-<version>.zip` from the [Releases page](https://github.com/amarret/p5r-mods/releases)
   and extract it into `Reloaded-II\Mods`.
2. In Reloaded-II, select Persona 5 Royal and tick **Set Palace Security Level**. It needs
   `Reloaded.Memory.SigScan.ReloadedII` and `reloaded.sharedlib.hooks`; if they are missing, Reloaded-II offers to
   download them.
3. Right-click the mod → **Configure Mod** to set the level or switch it off. The mod only acts when the game changes
   the level, so a new setting takes effect the next time a Palace area loads, the meter fades back in (after a menu,
   an event or a battle), or the level would change.

At start-up the Reloaded console shows `Hooked the security level (setter at 0x..., ...)`. After a config change it
shows `Security level fixed at N%.` or `Security level: vanilla.`. The first time the mod holds the level, it logs
`Holding the security level at N%`. With **Log security changes** switched on (for troubleshooting), each change then gets a line such as
`setter(0x83, 70): field 101 (Palace), meter on screen, ...; count 0x83 42 -> 42`.

## How it works

The security level is one of the game's COUNT variables. It is count 131, or count 135 in the parts of the story that
use the second meter. Nothing runs every frame. The mod finds five game functions by byte signature at start-up and
acts only when one of them runs:

- **the security-level setter.** Scripts (being spotted, ambushes, safe rooms) and the game's own code go through it.
  The mod replaces the value with your level, and the game then applies its usual 0-100 clamp and calling-card
  minimum;
- **the generic count setter** (SET_COUNT), for scripts that set the security count directly;
- **the security meter's refresh**, for code that writes the count itself and then refreshes the meter
  (CALL_ALERT_CHANGE, the delayed update after a battle). The level is put back before the meter reads it;
- **the meter's on/off switch.** When a Palace area loads and the game turns the meter on, both counts and the meter
  are set to your level, so the meter appears at it. A change made outside (a new day lowers the level) is undone
  there;
- **the meter's fade** (FLD_ALERT_VISIBLE). When the meter fades back in after a menu, an event or a battle, it
  appears at your level.

"In a Palace" is the game's own FLD_CHECK_DUNGEON test: the current field's major ID is 100-199. It depends only on
where you are, not on the HUD, so battles, events, menus and HUD-hiding mods do not change it. Outside, the mod leaves
everything to the game.

The mod never makes the meter come up by itself. Bringing the meter up where no Palace applies makes the game load a
danger texture that does not exist (`calendar/P5_danger_cha09_a.dds`), and the game then hangs. Version 1.0.0 did that at
start-up, because it corrected the level every frame and refreshed the meter. So a meter off screen is given the level
directly, the way the game's own show and switch code do, and only a meter on screen animates a change.

Version 1.1.0 judged "in a Palace" by the meter being switched on. Hiding the meter (events, battles) can switch it off,
so the level could change there and was not put back. 1.2.0 uses the field test.

Nothing on disk or in the save is changed. The saved level is whatever the mod held. Switching the mod off or
disabling it gives back the vanilla behaviour from the next change on.

## Notes

- Tested in game with 1.2.0: the level is held in a Palace.
- Works with [Persona 5 Royal UI Toggler](https://github.com/AnimatedSwine37/p5rpc.uitoggler) hiding the date and alert
  HUD. That mod only skips drawing HUD parts: the field test does not depend on the HUD, and none of the UI Toggler's
  patches touch the functions hooked here.
- If a game update moves the code, the log says what could not be found. Without the setter, the mod does nothing.
  Each of the other hooks is optional.

## Rebuilding

```
dotnet build -c Release
```

The output goes to `..\dist\p5rpc.setpalacesecurity`.
