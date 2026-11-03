# Physics2D

`MNVTEngine.Physics2D` adds rigid-body physics to scenes. The simulation is **Box2D v3** through
[Box2D.NET](https://github.com/ikpil/Box2D.NET) (3.1.654), a pure C# port. Box2D and Box2D.NET are both MIT-licensed.
The package's nuspec omits the license field, but the repository's LICENSE is MIT.

Box2D types never appear in the public API: game code sees engine components, `Vector2` and degrees. The project references only Scenes (enforced: no Graphics, Platform or Assets).

**Why a library instead of a custom solver:**

- Stable stacking, continuous collision, sleeping, and well-tuned friction and restitution are years of work in Box2D.
- A homegrown impulse solver would need the same work before it could be trusted.
- Box2D.NET is pure C#, so the game needs no extra native binaries.
- Its MIT license allows commercial use.

## Components

| Component | Settings |
|---|---|
| `Rigidbody2D` | `BodyType` (`Dynamic`, `Kinematic`, `Static`), `GravityScale`, `LinearDamping`, `AngularDamping`, `FixedRotation`, `Continuous` (bullet CCD) |
| `BoxCollider2D` | `Size`, plus the common collider settings |
| `CircleCollider2D` | `Radius` (scaled by the larger axis of the transform's scale), plus the common collider settings |
| `TilemapCollider2D` | `IgnoredTiles`; one box per row run of solid tiles (see [tilemaps](tilemaps.md)) |

Common collider settings: `IsTrigger`, `Offset`, `Density` (mass = density × area), `Friction` (0.6), `Bounciness` (0–1).

**How components become bodies:**

- A collider without a `Rigidbody2D` on the same GameObject is **static**.
- Colliders on the same GameObject as a `Rigidbody2D` form its shape.
- Colliders on child objects are **not** merged into a parent's body yet.

**Runtime API on `Rigidbody2D`:**

- `Velocity` and `AngularVelocity` (degrees per second). Before the scene runs, these set the initial values.
- `Mass`.
- `AddForce(force, ForceMode2D.Force | Impulse)` and `AddTorque`. Both work on dynamic bodies only.

## Callbacks

Implement the interfaces on any component of a GameObject that has a collider. Each interface method has a default implementation, so implement only what you need.

```csharp
public sealed class Coin : Component, ITriggerHandler2D
{
    public void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GameObject.Name == "Player") GameObject.Scene.Destroy(GameObject);
    }
}

public sealed class Crate : Component, ICollisionHandler2D
{
    public void OnCollisionEnter2D(Collision2D c) => Logger.Info($"hit {c.GameObject.Name}, normal {c.Normal}");
}
```

- **Collisions.** `OnCollisionEnter2D` and `OnCollisionExit2D` receive a `Collision2D`.
  - It contains `Collider`, `Other`, `GameObject` (the other object), `Rigidbody` (the other body), `Normal` and `Point`.
  - `Normal` points from the receiving collider towards the other one.
  - Both sides are called.
- **Triggers.** `OnTriggerEnter2D` and `OnTriggerExit2D` receive the other collider. Both the trigger and the visitor are called.
- **When.** Callbacks run after the physics step, with the scene's script exception handling: an exception is logged with context and the frame continues.
- **Removed colliders.** When a collider is disabled or destroyed, the other side receives an exit with the next physics step. Box2D itself reports nothing for removed shapes; the engine generates the exit.

## Queries

```csharp
if (Physics2D.Raycast(transform.Position, Vector2.Down, 1.1f) is { } hit) { /* grounded: hit.Collider, hit.Point, hit.Normal, hit.Distance */ }
Physics2D.Gravity = new Vector2(0, -20);
```

- Ray casts ignore triggers.
- `Physics2D` works on the scene whose callbacks are running. Outside callbacks, use `PhysicsWorld2D.For(scene)`.

## How it runs

- **One world per scene.** Each scene with physics components has one `PhysicsWorld2D`. It is an ECS `GameSystem` with `Order = 1000`.
  - Each fixed step, it runs after the components' `FixedUpdate` and after ECS systems with the default order. Forces applied in `FixedUpdate` therefore take effect in the same step.
  - It steps Box2D with 4 substeps.
- **Lifetime.** The world is created by the first physics component of a running scene. The Box2D world is freed when the last body goes away, or on `Scene.Unload`, because Box2D limits the number of live worlds per process. Scenes that are not running (the editor's edit scene) have no physics.
- **Rebuilds.**
  - Adding, enabling, disabling or destroying a physics component, or changing one of its settings, marks the GameObject dirty.
  - The body is rebuilt at the next settled point: the next step, a ray cast, or access to `Velocity`, `Mass` or forces. It is never rebuilt half-way through a teardown.
  - A rebuild keeps the body's velocity.
- **Transforms.**
  - Before each step, a transform moved by game code teleports its body, and a scale change rebuilds it.
  - After the step, dynamic and kinematic bodies write their position and rotation back to the transform.
- **Threading.** Box2D.NET keeps worlds in a process-wide table, so creating and destroying worlds is serialized with a lock. Stepping different scenes' worlds is independent.

## Limitations

- Colliders on child objects are not merged into a parent's compound body.
- There are no joints, no polygon or edge colliders, and no collision layers yet. Box2D supports all of these, so adding them is wiring, not new physics.
- Transforms update at the fixed rate, without interpolation. Fast bodies can stutter on high-refresh displays.
- Rebuilding a body (for example after changing `Size`) ends its current contacts and starts them again, so exit and enter callbacks fire.
