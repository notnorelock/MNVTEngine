# Particle systems

Particle systems are the `particles` [module](modules.md) (`MNVTEngine.Particles`), available in 2D and 3D projects. A build includes the module only when a scene uses a `ParticleSystem` or the game ships a `.mnvtparticle` asset. This page covers the simulation, which is shared by both profiles. Drawing is done by `particles.2d` and `particles.3d`.

| Phase | Status |
|---|---|
| Core: data model, lifecycle, storage, emission, bursts, shapes, curves, gradients, lifetime modules, `.mnvtparticle` | Done |
| 2D rendering (`particles.2d`), editor inspector and preview, presets, sample `samples/Particles2D` | Done |
| 3D rendering (`particles.3d`): billboard modes, depth, sorting; editor preview in the 3D Scene view; sample `samples/Particles3D` | Done |
| Noise, collision (world planes), sub-emitters | Done |
| Diagnostics: simulation and rendering metrics, budgets | Done |
| Performance: parallel CPU update for large systems, allocation-free steps | Done |
| GPU simulation (Vulkan compute) | Evaluated, not implemented (see below); the simulation is CPU-only and is reported as such |
| Exports: JIT and NativeAOT exports of both samples run (2D: 80.7 MB JIT, 23 MB AOT; 3D: 24 MB AOT), each with only its profile's particle renderer | Done |

## Using a particle system

```csharp
var fire = gameObject.AddComponent<ParticleSystem>();
fire.Duration = 2f;
fire.Loop = true;
fire.MaxParticles = 500;
fire.Main.StartLifetime = new MinMaxCurve(0.5f, 1.2f);   // random per particle
fire.Main.StartColor = new MinMaxGradient(ParticleGradient.Between(Color.Yellow, Color.Red));
fire.Emission.RateOverTime = 30;
fire.Emission.AddBurst(new ParticleBurst { Time = 0.5f, Count = 50 });
fire.Shape.Kind = ParticleShapeKind.Cone;
fire.Settings.ColorOverLifetime.Enabled = true;
fire.Play();

fire.Pause(); fire.Stop(); fire.Stop(ParticleStopBehavior.StopEmittingAndClear); fire.Clear(); fire.Restart(); fire.Emit(10);
bool playing = fire.IsPlaying, paused = fire.IsPaused, emitting = fire.IsEmitting; int alive = fire.ParticleCount;
```

- **Lifecycle:** `PlayOnAwake` plays on `Start`. `Stop()` stops emitting and lets alive particles finish; `StopEmittingAndClear` removes them too.
- **Finishing:** a non-looping system finishes when its cycle is over and its last particle has died. It then runs its `StopAction` (none, disable or destroy the object) and raises `Stopped`.
- **Settings:** `Settings` (`ParticleSystemSettings`) is saved with the scene. Setting `Asset` to a `.mnvtparticle` replaces it with the asset's settings when the system starts, or immediately when set at run time.

## Simulation

- **Storage:** `ParticleBuffer` keeps one array per property, with alive particles packed at the front. A dead particle is replaced by the last one, so there is no allocation per particle. The arrays double up to `MaxParticles` and are reused after that.
- **Time:** one `Step` per frame on scaled time, or on unscaled time with `UseUnscaledTime`, multiplied by `SimulationSpeed`. `StartDelay` delays the first emission. `Duration` is one cycle; `Loop` repeats it.
- **Emission:** `RateOverTime` and `RateOverDistance` add to accumulators, so fractions carry over between frames. Continuous emission covers at most 0.25 s per step (`MaxCatchUpSeconds`), so a hitch does not release a burst of backlog. Bursts have a time, a count, cycles (0 repeats until the cycle ends), an interval and a probability. At `MaxParticles`, extra particles are dropped, not saved up.
- **Determinism:** with `AutoRandomSeed` off, `RandomSeed` makes the same steps produce the same particles. Each particle has a seed. Its "random between" choices (`ParticleRandom.Value`) stay fixed through its life without storing one number per property.
- **Spaces:** `Local` particles move with the emitter (positions are relative to it). `World` particles stay where they were emitted, with velocity rotated by the emitter at emission. Velocity and force modules have their own `Local`/`World` space and are converted.
- **Modules:** a disabled module is skipped for the whole step, so it costs nothing per particle.

| Module | Effect |
|---|---|
| `Main` | Duration, loop, start delay; start lifetime, speed, size (uniform or per axis), rotation, color; gravity modifier; max particles; space; speed; seed; stop action |
| `Emission` | Rate over time, rate over distance, bursts |
| `Shape` | 2D: point, circle, circle edge, rectangle, rectangle edge, line (in the XY plane). 3D: sphere, sphere surface, hemisphere, cone, box, box surface. Position, rotation, scale, radius thickness, arc, randomized direction. The main axis is +Y |
| `VelocityOverLifetime` | Extra velocity (moves particles without changing their own velocity); speed modifier |
| `ForceOverLifetime` | Acceleration |
| `ColorOverLifetime` | Multiplies the start color |
| `SizeOverLifetime` | Multiplies the start size (uniform or per axis) |
| `RotationOverLifetime` | Degrees per second around the facing axis, or per axis |
| `Noise` | Moves particles through deterministic 3D gradient noise (strength over the lifetime, per axis optional, frequency, scroll speed) without changing their own velocity |
| `Collision` | Infinite world planes: bounce, dampen, lifetime loss per hit, minimum kill speed, radius (half the size times a scale). Optional collision events |
| `SubEmitters` | Other systems emitting where particles are born, die or collide |
| `TextureSheetAnimation` | Grid sprite sheet: frame over the lifetime or at a frame rate, start frame (random for a random frame), cycles, loop |
| `Renderer` | Texture, material, blend (alpha, additive), render mode, sort mode, sorting order. Data for the 2D and 3D renderers |

### Collision and sub-emitters

Collision tests each particle against the module's planes (`collision.planes`, in world space; a default ground plane at the origin). A particle inside a plane and moving into it is pushed out:
- **Velocity:** the normal part is reflected by `bounce`, and the tangential part is reduced by `dampen`.
- **Life:** the particle loses `lifetimeLoss` of its lifetime. It dies when that runs out or when it is slower than `minKillSpeed`.

`ParticleSimulation.CollisionChecksLastStep` counts the tests. With `sendCollisionEvents`, `ParticleSystem.Collided` is raised per hit with the position, normal, velocity and color. Physics colliders are not used: the particle module does not depend on the physics modules.

A sub-emitter names a child object (at any depth) with a `ParticleSystem`. On `birth`, `death` or `collision` of a particle, that system emits `count` particles at the particle's position:
- **Chance:** `probability` per event.
- **Inheritance:** optionally part of the particle's velocity (`inheritVelocity`) and its color (`inheritColor`).
- **Safety:** children only, so emitters cannot form cycles. `maxEmitsPerStep` caps what one step emits, which prevents runaway chains.
- **Recording:** events are recorded only for the kinds something uses, in a reused buffer (`ParticleSimulation.Events`).
- **Targets:** the target system usually has `playOnAwake` off and no emission of its own. The reference is a name, so it also works inside `.mnvtparticle` assets and prefabs.

### Curves and gradients

`MinMaxCurve` is a constant, a random value between two constants, a curve, or a random value between two curves, with a multiplier. `ParticleCurve` is a keyframed cubic Hermite curve with in and out tangents; an infinite tangent makes a step. It is clamped outside its keys and evaluated with a binary search, without allocating. `ParticleGradient` has color keys and alpha keys, blended or fixed. `MinMaxGradient` is a color, a random color between two, a gradient, a random value between two gradients, or a random point of a gradient. None of these types depend on particles.

## 2D rendering (`particles.2d`)

`MNVTEngine.Particles2D` draws every particle system of a 2D scene in the view's sprite batch, from a scene pass with order 210, after the sprites.
- **Drawing:** per particle it uses the texture (a white square without one), the current color, size and rotation, and the sprite-sheet frame (a grid of `columns` x `rows`, row by row from the top-left).
- **Stretched mode:** a particle points along its velocity and grows by `speedScale` per unit of speed.
- **Blending:** `additive` uses an additive material on the sprite shader. A `.mnvtmat` material in `renderer.material` replaces both.
- **Sorting:** particles share the batch with sprites, so they sort with them by `sortingOrder`, then by submission order. `oldestInFront` and `youngestInFront` order a system's particles by age, through reused key arrays without allocating. They batch by texture and material: one draw call per run, never one per particle.
- **Culling and statistics:** particles outside the view are skipped. The pass counts `particles.systems`, `particles.drawn` and `particles.culled`.
- **Spaces:** local-space particles are placed with the emitter's transform, and their size follows its scale.

The module comes with `particles` in 2D projects (`Accompanies` in the catalog), so a 2D game that uses particles ships both, and a 3D project never gets it.

## 3D rendering (`particles.3d`)

`MNVTEngine.Particles3D` draws every particle system of a 3D scene through the scene's camera, in a pass with order 150: after the opaque meshes have written depth, before sprites.

- **Orientation:** a particle is a quad oriented by `renderer.renderMode` (`ParticleQuads`):

  | Mode | Orientation |
  |---|---|
  | `billboard` | Faces the camera's position, upright with the camera |
  | `viewAligned` | Parallel to the view plane |
  | `horizontalBillboard` | Flat on XZ |
  | `verticalBillboard` | Upright, turning around Y toward the camera |
  | `stretched` | Along the velocity; length = size x `lengthScale` + speed x `speedScale` |
  | `worldOriented` | Rotated by the particle's own 3D rotation |

  The Z rotation turns the quad around its facing axis.
- **Depth and blending:** depth-tested but not written (`RenderState(DepthTest, no DepthWrite)`), so opaque geometry in front hides particles and particles do not hide each other. Blending is alpha or additive.
- **Sorting:** systems are drawn farthest first. Within a system, `byDistance` sorts particles back to front by their distance to the camera, `oldestInFront`/`youngestInFront` by age, and `none` keeps buffer order. The key arrays are reused.
- **Batching and uploads:** quads are built on the CPU, in world space, into one dynamic vertex buffer of `ParticleVertex` (position, UV, packed color). They are uploaded in chunks of 8192 quads (16-bit indices). Each run of systems with the same texture and blend is one draw call.
- **Culling and statistics:** particles outside the view frustum are culled. The pass counts `particles.systems`, `particles.drawn`, `particles.culled` and `particles.drawCalls`.
- **Shader:** `Builtin/Particle3D` is the sprite shader with a 3D position. The module lists it as a built-in shader, so builds precompile it.
- **Cameras:** every view draws with its own camera (the editor's Scene and Game views, multiple cameras).
- **Not supported:**
  - soft particles (no depth-texture sampling);
  - mesh particles;
  - custom materials in 3D: `renderer.material` is ignored, because the quads need the particle vertex format.

The module comes with `particles` in 3D projects, as `particles.2d` does in 2D projects.

## Editor

- **Inspector:** a `ParticleSystem` shows, from top to bottom:
  - preview controls (play/pause, restart, stop, speed);
  - live statistics (particles and maximum, emitted, cycle time, seed, buffer memory, oldest particle, average lifetime, enabled modules);
  - presets, and asset tools: *Save as Asset* writes `Particles/<name>.mnvtparticle` and assigns it; *Apply to Asset* and *Revert to Asset* follow once one is assigned;
  - one section per module, with optional modules enabled by a checkbox in their header.
- **Fields:**
  - curves are drawn as a graph with draggable keys, plus a key table (time, value, in and out tangents);
  - gradients are a preview bar plus color and alpha key tables;
  - bursts are a table;
  - the shape list offers the profile's shapes only.
- **Preview:** in edit mode, the selected object's systems and those of its children play in the Scene view (`ParticlePreview`). They are drawn by the project's own particle renderer, not a separate preview renderer. Selecting something else stops and clears them. In Play Mode the running scene simulates them.
- **Creating:** *Project > Create > Particle System* creates an asset with default settings.

### Presets

`ParticlePresets.Create(name, planar)` builds editable starting settings (2D or 3D shapes): Fire, Smoke, Sparks, Explosion, Dust, Rain, Snow, Magic sparkle, Energy beam, Footsteps dust, Water splash. They are ordinary settings with no special behavior. They set no texture; the inspector keeps the current one.

## Diagnostics and performance

**Metrics.** The `particles` module adds a metrics provider, and the particle passes add render statistics. Both appear in the overlay (`overlay.sections` with `particles`), the profiler and budgets:

| Metric | Source |
|---|---|
| `particles.systems`, `particles.alive`, `particles.capacity` | Active systems (playing or with particles), particles alive, sum of their `maxParticles` |
| `particles.emitted`, `particles.recycled` | Particles emitted and died (slots reused) last frame |
| `particles.modules`, `particles.collisionChecks`, `particles.memory` | Enabled optional modules, particle-plane tests, bytes of the particle buffers |
| `particles.simulationTime` | CPU milliseconds of the systems' steps (timed per step) |
| `particles.gpuSimulationTime` | Always unsupported: there is no GPU simulation, and it is never reported as 0 |
| `render.particles`, `render.particleVertices`, `render.particleTime` | Particles drawn after culling, their vertices, CPU time of the particle pass |
| `render.particleDrawCalls`, `render.particleTextureSwitches` | 3D pass. In 2D, particles share the sprite batch, so these are unsupported there and counted in the renderer's totals |

Budgets `maxParticles` (alive particles) and `particleSimulationMs` warn as other budgets do. Without the particle modules, all render metrics are unsupported, not 0. Collecting the metrics adds no GPU synchronization: rendering time is CPU time measured in the pass.

**Allocation and threads.**
- **Allocation:** a step allocates nothing once the buffer has grown. This is verified by a test for serial and parallel steps.
- **Parallel update:** a system with 32,768 particles or more updates in parallel chunks of 4,096 on the thread pool, and the calling thread waits. Work items are reused. A sweep on the calling thread then removes the dead and records events.
- **When it stays serial:** systems whose collision events are recorded update on one thread, because the event buffer is shared.
- **Determinism:** a parallel step gives exactly the serial result (tested).

**Measured** (Release, x64; median CPU time per step; the machine was busy, so take ±30%):

| Modules | 10k | 100k serial | 100k parallel | 400k parallel |
|---|---:|---:|---:|---:|
| Gravity only | 0.4 ms | 2.3 ms | 1.2 to 3.3 ms | 4.2 ms |
| Color, size, rotation, sprite sheet | 1.2 ms | 11.5 ms | 2.7 to 5.0 ms | 10 ms |
| + noise | 3.7 ms | 34 ms | 5.9 to 8.9 ms | 21 ms |
| + collision (one plane) | 3.8 ms | 13.9 ms | 2.7 to 3.5 ms | 9.7 ms |

**GPU simulation (Phase 8 evaluation): not implemented.**
- **Need:** typical games stay well under 50,000 particles, and the CPU path handles that in a few milliseconds. GPU simulation would pay off for hundreds of thousands of particles, which would also need GPU-driven drawing.
- **Prerequisites missing:** compute pipelines, storage buffers, dispatch, Synchronization2 barriers between compute and graphics, and indirect draws in the backend-independent graphics API. Today it has only graphics draws.
- **Port:** every module would need a GPU version: curves and gradients baked into buffers, noise, collision, sorting. The CPU-visible behavior would also need readback, which stalls: sub-emitters, collision events, particle counts and statistics.
- **Next step, if a game needs it:** an opt-in GPU backend per system, for systems without sub-emitters or collision events, behind the same settings, with its own GPU timing metric.
- **Until then:** the GPU metric reports unsupported, and nothing uploads CPU particles under a GPU label.

## `.mnvtparticle` assets

JSON of `ParticleSystemSettings` with a `schemaVersion`, written and read with Newtonsoft.Json. Missing values take their defaults, and unknown fields are errors. Textures and materials are `asset:` references; nothing else references runtime objects.

```json
{ "schemaVersion": 1,
  "main": { "duration": 2, "startLifetime": { "mode": "randomBetweenConstants", "min": 0.5, "max": 1.2 }, "maxParticles": 500 },
  "emission": { "rateOverTime": { "max": 30 }, "bursts": [ { "time": 0.5, "count": { "max": 50 } } ] },
  "shape": { "kind": "cone", "angle": 20 },
  "renderer": { "texture": "asset:0f5e0000-0000-4000-8000-000000000003", "blend": "additive" } }
```

`ParticleSystemAsset.Parse` validates the asset, and invalid values are errors listed by path:
- duration greater than 0;
- `maxParticles` from 0 to 1,000,000;
- sheet columns and rows from 1 to 256;
- burst values in range;
- finite numbers in curves.

The importer (`ParticleSystemImporter`) reports the same errors when the file is saved. It records the texture and material as dependencies, so builds ship them with the system.
