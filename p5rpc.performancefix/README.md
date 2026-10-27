# P5R Performance Fix (PC, Reloaded-II)

Improves the frame rate in busy places like Shibuya station, where the game's single "render" thread can't keep up,
and smooths loading: no more stalls when you change area (train rides, doors, entering a Palace), and fast travel
starts its loading animation straight from the map. The game itself looks the same.

| Switch | Default | Effect |
| --- | --- | --- |
| Rendering performance fix | On | Partial uploads into the game's buffers (with "Smooth loading" on: only blocks over 4 KB). Off: vanilla full uploads. |
| Smooth loading | On | The game no longer waits for the old area's sounds to fade out before freeing them, its file waits go ahead at once instead of on CRI's next 60 Hz tick, and the Windows timer is kept at 1 ms. Shader constants go into small buffers (256 B to 4 KB) sized to what each game buffer is ever filled with, which removes the 10-30 ms driver hitches in an area's first frames. Off: vanilla. |
| Direct fast travel | On | After you pick a fast-travel destination, the loading animation starts over the map instead of after the map's closing fade (no frame of the field in between). Off: vanilla. |

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

At start-up the Reloaded console shows `Windows timer resolution at start-up: N ms.`, then
`Area changes release sound banks without waiting (...)`, `File waits run CRI's file-system step right away (...)`, `CRI FS's request wait polls every 0.1 ms (...)`,
`Driver check: the driver supports partial constant-buffer updates.`, `Small constant buffers for vertex shader / pixel
shader / third shader stage uploads, trimmed above 4 KB (...)`, `Small constant buffers for the ... per-shader loop
(...)` and `Fast travel: the loading animation starts over the map (...)`. Every 30 seconds with something new it logs
`So far: N sound banks released without waiting, M file-wait steps run right away, K short file-wait polls.` and
`Small constant buffers: N for M of the game's buffers; X uploads went to them, Y to the game's own buffers.` and
`Fast travel: N map closing fades skipped, ...`. The same
lines go to `performancefix-log.txt` in the mod's folder.

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

### Small constant buffers (part of "Smooth loading")

After 0.5.0, each area change still had one or two frames of 20-31 ms. Probe 0.9.0 timed every Direct3D call in those
frames: the time went into the trimmed `UpdateSubresource1` calls themselves. There were 3,000-4,000 of them in each of
those frames, at about 4 µs each instead of 0.35 µs, and the render thread spent half of the frame in the driver's
memory allocation (`NtGdiDdDDICreateAllocation`, `Lock2`, `MapGpuVirtualAddress`). Some of these frames created no
resources at all.

A stand-alone test on the same PC (deferred context, Present every frame, 140 uploads a frame and then 3,600, as in a
train ride) showed why:
- the NVIDIA driver backs every constant-buffer update recorded on a deferred context with new memory the size of the
  whole buffer. A 224-byte update of a 7 KB buffer costs the same as the full one: 1 KB buffers needed 40 new 128 KB
  pieces for the same frames, 7 KB buffers 380, 16 KB buffers 870;
- going from 140 to 3,600 uploads a frame, the first two frames allocate about 170 pieces each (40-50 MB in all) and take
  8-15 ms to record, as in the game;
- during the quieter frames that follow, a driver thread frees that memory again within a second or two, so the next
  area change allocates it again. Keeping it alive from another thread or with occasional dummy uploads does not
  work: the memory belongs to each deferred context, and it is freed again after every burst;
- with 256-byte buffers the same jump allocates 9 pieces per frame and the frames take under 1 ms, and the memory
  stays.

So the mod now gives each of the game's 7 KB buffers up to five small companions, of 256 bytes to 4 KB, created on
first use. It keeps the largest size the engine has ever filled in that buffer, and each upload goes, with a full
`UpdateSubresource`, into the smallest companion that holds it. The first 256 bytes to 4 KB of the engine's shadow copy
go to the GPU, as in vanilla. The engine's own code then binds the companion: after the upload it binds the buffer held in
a local variable, through a cache of what is bound, and the mod puts the companion in that variable. Shaders that
declare more constants than the companion holds read zeros past its end (Direct3D defines it that way). Those constants
were never filled by the engine, so in vanilla they held only the shadow copy's first contents.

This covers the six places that upload these buffers (each stage's single-slot uploader and its per-shader loop). The
game's buffer is only bound right after one of these uploads, so whichever of the two is bound holds the latest data.
Blocks over 4 KB still go into the game's buffer: trimmed for the single-slot uploaders, full for the loops.

In the offline test (the game's own uploader code on a fresh deferred context), the first frames of 3,600 uploads take
0.2 ms to record with small buffers and 9 ms with trimmed uploads into the 7 KB buffers.

### Smooth loading

Measured with the Performance Probe on a train ride, each area change stopped the game thread twice, for 20 to 60 ms each
time. The first stop (task `bg env [RESRC MNG]`) is the game freeing the old area's sound banks (CRI ADX2 ACBs). CRI's
`criAtomExAcb_Release` stops the bank's voices and then waits until they are silent. While the ambient sounds fade out,
it loops: run the CRI server, `Sleep(10)`, check again.

CRI also has `criAtomExAcb_ReleaseAsync`, which marks the bank and returns. CRI's server thread then stops its voices
and frees it once they are quiet. The game never uses it. The mod points the game's one release call at a small native
routine:

- For a bank loaded from its `.acb`/`.awb` files, which is the usual case, it calls `criAtomExAcb_ReleaseAsync`. CRI
  owns all the data of such a bank.
- For a bank loaded from the game's memory, or with the switch off, it makes the game's original call. The game may
  reuse that memory as soon as the call returns, so it must not be freed later.

The second stop (task `Resource MNG Update`, 17-44 ms) is a file lookup for the next area. The game asks CRI's file
system for a file's size and polls `criFs_ExecuteMain(); Sleep(1)` until the request is done. The probe showed that
during that wait every CRI thread was idle. CRI's file system advances its requests in a server step, and that step runs
on CRI's shared server thread. That thread is paced by a timer at 60 Hz, together with the sound library. With that
setup `criFs_ExecuteMain` does nothing (it would only wake an event-driven server), so each step of the request waited
for the next tick.

Every part of the file-system step is guarded by an atomic busy flag, including the branch the server thread takes, so
any thread may run it: whichever thread holds a flag does that part, and the other skips it. The mod gives
`criFs_ExecuteMain` one more call:
- it makes the original call;
- then, if CRI's server runs on its own thread (thread model 0, P5R's case), it runs the file-system step, so the request
  advances at once.

(0.4.0 had that test the wrong way round and never ran the step in game; 0.4.1 fixes it.)

With 0.4.1 the lookups no longer waited for a server tick, but each area change still stopped for about 19 ms. Probe
0.8.0 timed every lookup. An area change makes a few hundred of them. Files inside a CPK are answered in a few
microseconds. About ten optional files that do not exist (`FIELD/SHT/ENVSE..._....BIN`, `FIELD/OBJECT_HIT/....HTB`,
`FIELD/CULLING/....CHD`...) make CRI try to open them. Each of those waits once in CRI FS's request wait, and that wait
polls with `Sleep(1)`. On the test PC that lasts about 2 ms even with a 1 ms Windows timer, although the open is much
quicker. 0.5.0 points that `Sleep(1)` at a routine that works in two phases:
- for the first millisecond of a wait, it polls every 0.1 ms, yielding the processor. A missing file needs one such poll,
  about 0.2 ms instead of 2;
- after that, it sleeps 0.5 ms at a time on a high-resolution waitable timer, so a long wait does not keep a core busy.

`criFs_ExecuteMain` is only called from wait loops (file lookups, CPK binds, loads), so nothing changes outside them, and
the sound server keeps its normal 60 Hz cadence. The Windows timer is also kept at 1 ms, so those 1 ms polls really last
about 1 ms.

### Direct fast travel

Probe 0.10.0's transition trace showed what happens after you pick a destination from a district's list. The script
`FIELD/PANEL/MESSAGESEL.BF` pays the fare, then runs `Fade_check_sys`:
1. it picks the animation for the destination (subway, symbol, Velvet Room, a plain fade, or the crowd scene change);
2. it calls `FLD_LMAP_FADE(0)`, the map's own closing fade, and waits for it (about 32 frames, 270 ms);
3. in the frame the map has gone it starts the animation (`FADEOUT(8, 10)` for the subway) and waits for it to cover
   the screen (6-7 frames). That frame showed the field;
4. `FLD_PANEL_MAP_PROC_END` closes the map and `CALL_FIELD` changes area.

The destinations without a list (Ichigaya, Kanda, Jinbocho, in `LMAP.BF`) already skip step 2 in vanilla: the animation
starts over the map, and step 4 closes the map behind it. The mod does the same for the others. It points three entries
of the game's flowscript function table at small handlers:
- `FLD_LMAP_FADE`, when the script calling it is in `Fade_check_sys`, reports "done" at once. The mod recognises
  that call by the script code right after it: Fade_check_sys's choice of animation, five
  `PUSHIS k / PUSHLIX v / EQ / IF / CALL / GOTO` blocks and then `FADEOUT(2, 10); FADE_SYNC()`. It reads that code
  the way the interpreter does, from the script context's code pointer and instruction index. 0.7.0 looked up the
  procedure name in the script's labels instead, but in memory the script is not laid out like the file, so it never
  matched.
- `FLD_REQ_SCN_CHANGE` (the crowd scene change, which has nothing to wait for) runs the skipped closing fade first and
  waits for it, as in vanilla.
- `FLD_PANEL_MAP_PROC_END` forgets a skipped fade.

Everywhere else `FLD_LMAP_FADE` works as before (for example before the phone animation, or a confidant's message).
It also saves about 0.3 s per trip. Every 30 s with something new, the log counts the skipped fades, the ones kept
for the crowd scene change, and the map fade calls from other scripts.

## Notes

- Tested in game at Shibuya central street on a PC with an NVIDIA RTX 4070 GPU and i5-12600KF CPU. Busy scenes
  went from 88 → 120 fps. No visual issues.
- Smooth loading: the sound-bank part (0.3.0) is tested in game. A train ride's `bg env` hitches went from 11 to 0.
  The file-wait part: 0.4.1 removed the lookups that waited for two server ticks, and the 0.1 ms polls (0.5.0) cut a
  transition's lookup time from 40-47 ms to 4-10 ms (both tested in game).
- Small constant buffers (0.6.0): tested in game. Field hitches over 20 ms on train rides went from 15 to 4.
- Direct fast travel: 0.7.0 did nothing in game (see above). 0.7.1 works in game. On six trips the log counted every closing fade skipped and none from other scripts. The transition trace shows the animation starting in the frame the destination is picked, about 32 frames sooner. The map closes behind it about 0.3 s later.

## Rebuilding

```
dotnet build -c Release
```

The output goes to `..\dist\p5rpc.performancefix`.
