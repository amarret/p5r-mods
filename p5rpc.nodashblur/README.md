# No Sprint Blur (P5R PC, Reloaded-II)

Removes the radial blur that covers the screen while Joker sprints, and with it the washed-out colours. The
white speed lines and the camera's FOV change stay exactly as they are.

| Switch | Default | Effect |
| --- | --- | --- |
| Sprinting | On | No blur, no colour wash while sprinting. Off: vanilla. |
| Leaping to a target point | Off | Also removes the blur of the short dash effect played when Joker leaps to a target point. Its white lines stay. |

## Install

1. Download `p5rpc.nodashblur-<version>.zip` from the [Releases page](https://github.com/amarret/p5r-mods/releases)
   and extract it into `Reloaded-II\Mods` (replace any older copy).
2. In Reloaded-II, select Persona 5 Royal and tick **No Sprint Blur**. It needs `reloaded.sharedlib.hooks` and
   `Reloaded.Memory.SigScan.ReloadedII`; if they are missing, Reloaded-II offers to download them.
3. Right-click the mod → **Configure Mod** to change the switches. Changes apply straight away, even while sprinting.

At start-up the Reloaded console shows `Hooked the sprint camera effect at 0x...`, `Hooked the target-point leap
effect at 0x...` and `Hooked the radial blur at 0x...`. The first time you sprint it shows
`Removed the sprint blur (shown once per session).`

## How it works

The sprint effect belongs to the **field camera**. When the player's movement state is "sprint", the camera's speed
effect fades in (1/3 s) and, every frame:

1. submits one full-screen **radial blur** pass, stronger the faster the camera moves forward. It blends a stretched
   copy of the screen over the image (white, up to 75% opacity), which is what flattens the contrast and washes the
   colours out;
2. in Palaces, queues the white speed lines, whose opacity follows the same fade.

The FOV change is done by the camera just before this effect. When you stop, the effect fades out (0.5 s).

The mod hooks the camera speed effect and the game's shared "submit radial blur" function, all found by byte
signature at start-up. A blur submitted while the speed effect is running is skipped; every other caller (battle,
cutscenes...) is untouched. Step 2 and the FOV change still run. Nothing on disk or in the save is changed.

The second switch does the same for a separate field effect, `fld_dash_filter`. It plays when Joker leaps to a
target point, and also from the `FLD_DASH_EFFECT` script command.

## Notes

- Not yet tested in game.
- v1.0.0 only handled the target-point leap effect, which is why it did nothing while sprinting.
- If a game update moves the code, the log says which part could not be found and that blur simply stays.

## Rebuilding

```
dotnet build -c Release
```

The output goes to `..\dist\p5rpc.nodashblur`.
