# Persona 5 Royal (PC) mods

Small [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) code mods for the Steam version of
Persona 5 Royal. Made by Claude by reverse engineering the P5R executable, wild stuff.

| Mod | Version | What it does |
| --- | --- | --- |
| [Confidant Ability & Showtime Toggles](p5rpc.confidanttoggles) | 1.1.1 | An on/off switch for every confidant ability and Showtime, plus Futaba's All-Out Attack takeover. Off means the ability never takes effect, even after you unlock it; on restores it. |
| [Quiet Navigator (Battle)](p5rpc.quietnavigator) | 1.3.0 | Silences chosen navigator battle lines (voice and dialogue window) by the event that triggers them: critical hits, knockdowns, weakness comments when analysing, Baton Passes. Per navigator. |
| [No Sprint Blur](p5rpc.nodashblur) | 1.1.0 | Removes the radial blur and washed-out colours while sprinting; keeps the speed lines and the FOV change. |
| [Quiet Turn-Start Recovery](p5rpc.quietregen) | 1.0.0 | Silences the sound played when HP/SP is restored at the start of a turn by abilities (Regenerate, Invigorate...) or accessories. |
| [Lean Rendering](p5rpc.cbtrim) | 0.2.1 | Raises the frame rate in crowded places such as Shibuya station: the renderer uploads only the shader constants it filled for each draw, instead of whole 7 KB buffers. Looks the same. |

> Each mod's README says how far it has been tested; mods not yet tested in game say so. Please report anything odd.

## Install

1. Download the zip of the mod you want from the [Releases page](https://github.com/amarret/p5r-mods/releases).
2. Extract it into `Reloaded-II\Mods` (each zip contains one mod folder).
3. In Reloaded-II, select Persona 5 Royal and tick the mod. If it needs a library mod you don't have (such as
   `reloaded.sharedlib.hooks` or `Reloaded.Memory.SigScan.ReloadedII`), Reloaded-II offers to download it.
4. Right-click the mod → **Configure Mod** to change its switches. Changes apply while the game is running.

The mods find the game code by byte signature at start-up. They target the current Steam `P5R.exe`; if an update
moves the code, the Reloaded log says which part could not be found and that part stays vanilla.

## Source code and building

Each mod's full source code is in its folder here. Each folder is a .NET 9 project (`net9.0-windows`):

```
cd <mod folder>
dotnet build -c Release
```

The output goes to `dist\<mod id>` next to the project folders, ready to copy into `Reloaded-II\Mods`.
