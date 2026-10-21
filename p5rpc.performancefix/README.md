# P5R Performance Fix (PC, Reloaded-II)

Improves the frame rate in busy places like Shibuya station, where the game's single "render" thread can't keep up.
Nothing on screen changes.

| Switch | Default | Effect |
| --- | --- | --- |
| Upload only the filled part of constant buffers | On | Partial uploads. Off: vanilla full uploads. |

## Why

In Shibuya the render thread records about 4,000 to 5,000 draws a frame. Before this mod, most of its time went into
about 5,400 constant-buffer uploads a frame. Every buffer is 7 KB, and the game uploaded all of it each time, about
35 MB a frame. The code that fills those buffers passes a fixed size for each slot, from 16 to 512 bytes. The rest of
each upload was old data nobody asked for.

## Install

1. Download `p5rpc.performancefix-<version>.zip` from the [Releases page](https://github.com/amarret/p5r-mods/releases)
   and extract it into `Reloaded-II\Mods`.
2. In Reloaded-II, select Persona 5 Royal and tick **P5R Performance Fix**. It needs
   `Reloaded.Memory.SigScan.ReloadedII` (and lists `reloaded.sharedlib.hooks`); if they are missing, Reloaded-II
   offers to download them.
3. Right-click the mod → **Configure Mod** to switch it off or on. Changes apply straight away, so you can compare
   while standing in Shibuya.

At start-up the Reloaded console shows `Driver check: the driver supports partial constant-buffer updates.`, then
`Trimming vertex shader / pixel shader / third shader stage constant-buffer uploads (...)`.

## How it works

The engine has one constant-buffer uploader per shader stage. Each one calls `UpdateSubresource` on the whole buffer.
Just before that, the engine stores how many bytes it filled. The mod points those three calls at a small native
routine:

- If the switch is on and the filled size is smaller than the buffer, it calls `UpdateSubresource1` with a box that
  covers only the filled bytes.
- Otherwise (switch off, size unknown, or the whole buffer filled) it makes exactly the game's original call.

Direct3D only accepts a partial constant-buffer update with `D3D11_COPY_DISCARD` (without it, the update is silently
dropped). With DISCARD, the part of the buffer past the filled bytes is left undefined. It holds zeros or older data,
the same kind of leftovers the vanilla full upload sends. Shaders are only meant to read their own constants, so they
should get the same values as before.

At start-up the mod checks, on a throwaway device, that the driver supports partial updates and really applies one
on both kinds of context the game uses. If not, uploads stay as they are. Nothing on disk or in the save is changed.

## Notes

- Tested in game at Shibuya central street on a PC with an NVIDIA RTX 4070 GPU and i5-12600KF CPU. Busy scenes
  went from 88 → 120 fps. No visual issues.

## Rebuilding

```
dotnet build -c Release
```

The output goes to `..\dist\p5rpc.performancefix`.
