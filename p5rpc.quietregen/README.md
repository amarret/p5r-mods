# Quiet Turn-Start Recovery (P5R PC, Reloaded-II)

Silences the sound played when HP or SP is restored at the start of a turn by an ability or an accessory. The
recovery itself, its numbers and its effect still happen; only the sound is gone.

| Switch | Default | Effect |
| --- | --- | --- |
| Turn-start HP / SP recovery | On | No sound for turn-start recovery. Off: vanilla. |

Covered: Regenerate 1-3, Invigorate 1-3, Holy Whisper, Holy Embrace, Heat Up, and accessories with a turn-start HP/SP
recovery effect, for every unit that has them. Healing skills (Dia, Media...), items used in battle and other heals
keep their sound.

## Install

1. Download `p5rpc.quietregen-<version>.zip` from the [Releases page](https://github.com/amarret/p5r-mods/releases)
   and extract it into `Reloaded-II\Mods`.
2. In Reloaded-II, select Persona 5 Royal and tick **Quiet Turn-Start Recovery**. It needs
   `Reloaded.Memory.SigScan.ReloadedII` (and lists `reloaded.sharedlib.hooks`); if they are missing, Reloaded-II
   offers to download them.
3. Right-click the mod → **Configure Mod** to switch it off or on. Changes apply straight away, even mid-battle.

At start-up the Reloaded console shows `Found the turn-start recovery #1/#2/#3 at 0x...`; after a config change it shows
`Turn-start recovery sound: silenced (3 of 3 places).`

## How it works

The battle has three copies of the turn-start recovery step. Each one:

1. works out how much HP and SP the unit recovers. It checks the passives above (skill IDs 830-832, 835-837, 937,
   939, 947) and the accessory effects 0xB0-0xB5;
2. rolls the passive that boosts that recovery;
3. plays SYSTEM sound cue 263 (`heal`);
4. shows the recovery.

The mod removes step 3 only, with a small code patch found by byte signature at start-up:

- in two copies, the 5-byte call that plays cue 263 becomes a 5-byte no-op;
- in the third, the existing "skip the sound if there is no sound player" jump is made unconditional.

Each patch is one atomic write and is undone when you switch the option off or disable the mod. Nothing on disk or in
the save is changed, so it works the same with English and Japanese audio, and alongside sound replacement mods.

## Notes

- Not yet tested in game.
- If a game update moves the code, the log says which place could not be found and its sound simply stays.

## Rebuilding

```
dotnet build -c Release
```

The output goes to `..\dist\p5rpc.quietregen`.
