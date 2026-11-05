# Profiling

`MNVTEngine.Core.Diagnostics` records where frame time goes.

## Instrumenting code

```csharp
using MNVTEngine.Core.Diagnostics;

public override void Update()
{
    using var _ = Profiler.Sample("AI.Pathfinding");   // constant names
    ...
    Profiler.Count("Enemies", enemies.Count);           // per-frame counter; the last value wins
}
```

- **Nesting.** Samples nest: a region's time includes its children. Regions are identified by their path, for example `Update/Scripts.Update`, so the same name under different parents stays separate.
- **Cost.** `Profiler.Current` is per thread and set by the host. Without a current profiler (normal game runs), `Sample` and `Count` cost a null check and allocate nothing.

## What the engine reports

| Region / counter | Where |
|---|---|
| `FixedUpdate`, `Update`, `Render` | `GameRuntime`, once per frame phase |
| `Scripts.FixedUpdate`, `Scripts.Start`, `Scripts.Update`, `Scripts.LateUpdate`, `Destroy` | `Scene` (all component callbacks of that phase) |
| one region per ECS system, e.g. `PhysicsWorld2D`, `AudioSystem` | `SystemScheduler`, by type name (`GameSystem.ProfileName`) |
| `Scripts.Missions` | `.mnvts` VM ticks (`MissionRunner`) |
| `Render.Sprites`, `Render.Lighting` | `SceneRenderer` |
| `PlayMode`, `Editor.SceneView`, `Editor.GameView` | Editor |
| `Sprites drawn`, `Sprites culled`, `Draw calls`, `Lights`, `Game objects`, `Physics bodies`, `Audio voices` | counters |
| `Meshes drawn`, `Meshes culled`, `Mesh triangles`, `Material switches`, `Lights 3D`, `Skinned meshes`, `GPU skinned meshes`, `Instanced meshes`, `Mesh draw calls`, `Shadow casters`, `Physics 3D bodies` | 3D counters |
| `Render.Meshes`, `Render.Shadows` | 3D samples |

Each frame also records its total time, the bytes allocated on the main thread, and the gen0, gen1 and gen2 collections.

## Using it

- **Editor:** open the **Profiler** panel, docked next to the Console.
  - It shows a frame-time graph of the last 300 frames.
  - It shows the average, p95, allocations and collections over the last second, with every region (avg ms, max ms, calls) as a tree, and the counters.
  - **Pause** freezes the view. **Save report** writes `Library/Profiles/profile-<time>.txt`.
  - It profiles the editor and the Play Mode scene it runs.
- **Games and development runs:** `--profile report.txt` works with an exported game, `MNVTEngine.Player` and `mnvt run`. It profiles every frame of the run, writes a text report at exit, and logs the average and p95 frame time. Combine it with `--frames N` for repeatable measurements:

```text
MyGame.exe --frames 600 --profile perf.txt
```

```text
MNVTEngine profile: 300 frames
Frame time: avg 17.287 ms (58 fps), p95 17.085 ms, max 168.809 ms
Allocated: 14.8 KB/frame; GC gen0 1, gen1 0, gen2 0

Sample                                      avg ms    max ms   calls
FixedUpdate                                  0.485    83.399       1
  PhysicsWorld2D                             0.473    82.744       1
Render                                       0.372    66.029       1
  Render.Lighting                            0.068     7.418       1
  Render.Sprites                             0.284    55.760       1
...
```

With vsync on, frame time includes waiting for the display. Compare region times, or set `"vSync": false` in the project's window settings when measuring throughput. The first frame's maximum includes JIT compilation.

## Limitations

- **CPU only.** `Render` measures command recording and submission, not GPU work. GPU time per frame and per scope comes from timestamp queries in the [diagnostics](diagnostics.md) (HUD, Profiler window).
- **One thread.** Samples from other threads go to that thread's profiler, which no host sets.
- **No timeline capture.** The report shows per-frame aggregates, not a timeline of individual calls.
