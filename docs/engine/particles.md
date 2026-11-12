# Particle systems

Particle systems are the `particles` [module](modules.md) (`MNVTEngine.Particles`), available in 2D and 3D projects. A build includes the module only when a scene uses a `ParticleSystem` or the game ships a `.mnvtparticle` asset. This page covers the simulation, which is shared by both profiles. Drawing is done by `particles.2d` and `particles.3d`.

| Phase | Status |
|---|---|
| Core: data model, lifecycle, storage, emission, bursts, shapes, curves, gradients, lifetime modules, `.mnvtparticle` | Done |
| 2D rendering and editor | Next |
| 3D rendering (billboards, sorting) and editor | Planned |
| Noise, collision, sub-emitters | Planned |
| GPU simulation (Vulkan compute) | To be evaluated; the simulation is CPU-only and is reported as such |

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
| `TextureSheetAnimation` | Grid sprite sheet: frame over the lifetime or at a frame rate, start frame (random for a random frame), cycles, loop |
| `Renderer` | Texture, material, blend (alpha, additive), render mode, sort mode, sorting order. Data for the 2D and 3D renderers |

### Curves and gradients

`MinMaxCurve` is a constant, a random value between two constants, a curve, or a random value between two curves, with a multiplier. `ParticleCurve` is a keyframed cubic Hermite curve with in and out tangents; an infinite tangent makes a step. It is clamped outside its keys and evaluated with a binary search, without allocating. `ParticleGradient` has color keys and alpha keys, blended or fixed. `MinMaxGradient` is a color, a random color between two, a gradient, a random value between two gradients, or a random point of a gradient. None of these types depend on particles.

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
