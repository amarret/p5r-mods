# Persona 5 Royal (PC) mods

[Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) mods for the Steam version of
Persona 5 Royal. Made by Claude by reverse engineering the P5R executable, wild stuff.

| Mod | Version | What it does |
| --- | --- | --- |
| [Confidant Ability & Showtime Toggles](p5rpc.confidanttoggles) | 1.1.1 | An on/off switch for every confidant ability and Showtime, plus Futaba's All-Out Attack takeover. |
| [Quiet Navigator (Battle)](p5rpc.quietnavigator) | 1.3.0 | Silences all or individual navigators for different battle line categories. |
| [No Sprint Blur](p5rpc.nodashblur) | 1.2.0 | Removes the ugly motion blur while sprinting, recentering the camera, and using the grappling hook. |
| [Quiet Turn-Start Recovery](p5rpc.quietregen) | 1.0.0 | Silences the sound played when HP/SP is restored at the start of a turn by abilities (Regenerate, Invigorate...) or accessories. |
| [P5R Performance Fix](p5rpc.performancefix) | 0.7.3 | Raises the frame rate in crowded places such as Shibuya central station. Improves frame pacing during loading transitions, and smoothen fast travel animations. |
| [Set Palace Security Level](p5rpc.setpalacesecurity) | 1.2.1 | Keeps the Palace security level at a fixed value (0-100%). Being spotted, ambushes, battles and safe rooms no longer change it. |

## Install

1. Download the zip of the mod you want from the [Releases page](https://github.com/amarret/p5r-mods/releases).
2. Extract it into `Reloaded-II\Mods` (each zip contains one mod folder).
3. In Reloaded-II, select Persona 5 Royal and tick the mod. If it needs a library mod you don't have (such as
   `reloaded.sharedlib.hooks` or `Reloaded.Memory.SigScan.ReloadedII`), Reloaded-II offers to download it.
4. Right-click the mod → **Configure Mod** to cnfigure options. Changes are applied while the game is running.

## Source code and building

Each mod's full source code is in its folder here. Each folder is a .NET 9 project (`net9.0-windows`):

```
cd <mod folder>
dotnet build -c Release
```

The output goes to `dist\<mod id>` next to the project folders.
