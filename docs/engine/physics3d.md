# Physics 3D

`MNVTEngine.Physics3D` simulates 3D rigid bodies with [BepuPhysics v2](https://github.com/bepu/bepuphysics2) (2.4.0,
Apache-2.0, pure C#, no native libraries). Gameplay code uses engine types only; Bepu stays inside the module. It mirrors
[Physics2D](physics2d.md): the same component pattern, callbacks and lifecycle, in 3D.

## Components

| Component | Settings |
|---|---|
| `Rigidbody` | `BodyType` (Dynamic, Kinematic, Static), `Mass` (kg), `UseGravity`, `LinearDamping` and `AngularDamping` (fraction lost per second), `FreezeRotation`, `Continuous` (no tunneling for fast bodies) |
| `BoxCollider` | `Size` (full size before scale) |
| `SphereCollider` | `Radius` (scaled by the largest scale axis) |
| `CapsuleCollider` | `Radius`, `Height` (along local Y, including both caps, like the Capsule primitive) |

Every collider has `IsTrigger`, `Center` (local offset), `Friction` (combined as the geometric mean) and `Bounciness`.

- **Static colliders.** Colliders without a `Rigidbody` are static (floors, walls).
- **One body per GameObject.** A GameObject's colliders form one body (a compound when there are several or an offset). A GameObject holds one component of each type, so combine different collider types; use child GameObjects with their own bodies for more parts.
- **Scale.** Colliders follow the transform's scale. Changing the scale rebuilds the body.
- **Editor.** **GameObject > 3D Object** primitives come with a matching collider: box, sphere, capsule (cylinders too), and a thin box for planes and quads. Add a `Rigidbody` to make them fall.

## Scripting

```csharp
body.AddForce(new Vector3(0f, 500f, 0f));                    // newtons, over the next step
body.AddForce(Vector3.Forward * 5f, ForceMode.Impulse);      // newton-seconds, now
body.AddTorque(new Vector3(0f, 2f, 0f));
body.Velocity = new Vector3(0f, 0f, -3f);                    // m/s; AngularVelocity is in degrees per second
if (Physics.Raycast(origin, Vector3.Down, 2f) is { } hit) { /* hit.Collider, hit.Point, hit.Normal, hit.Distance */ }
Physics.Gravity = new Vector3(0f, -20f, 0f);
```

## Callbacks

- **Collisions.** Implement `ICollisionHandler` on a component. `OnCollisionEnter` and `OnCollisionExit` receive a `Collision`:
  - `Collider`, the collider of this GameObject, and `Other`;
  - `Normal`, pointing from this collider towards the other, as in 2D;
  - `Point`, the deepest contact point (both are zero on exit).
- **Triggers.** Implement `ITriggerHandler`: `OnTriggerEnter` and `OnTriggerExit` receive the other collider. Triggers never push.
- **When.** Callbacks run after the physics step, with the scene's script exception handling.
- **How enter and exit are found.** Bepu has no contact events. The narrow phase records the touching collider pairs of each step, and the engine compares them with the previous step. Pairs of sleeping bodies keep touching. Disabling or destroying a collider ends its contacts with an exit.

## How it runs

- **One simulation per scene.** `PhysicsWorld3D` is an ECS `GameSystem` (`Order = 1001`) stepped once per fixed update, after the components' `FixedUpdate`. It runs single-threaded, so it is deterministic. It is created by the first 3D physics component of a running scene and released with the last body or on `Scene.Unload`.
- **Rebuilds and transforms** work as in 2D:
  - Changed GameObjects are rebuilt at the next settled point.
  - Moving a transform from code teleports its body; scale changes rebuild it.
  - After the step, dynamic and kinematic bodies move their transforms.
- **What the engine adds to Bepu:** gravity opt-out per body, damping, and forces accumulated over a step (`ForceMode.Force`).
- **Solver:** 8 velocity iterations, 1 substep.

## Limitations

- **Bounciness** is approximate. Bepu has no restitution; a bouncy contact gets a softer, less damped spring.
- **Shapes.** No mesh or convex-hull colliders yet; use boxes, spheres and capsules. Cylinders use a capsule.
- **Missing features.** No joints, character controller, collision layers or masks, continuous detection for kinematic bodies, or multithreaded stepping.
- **Kinematic bodies** are moved by teleporting (setting their transform or velocity). Dynamic bodies they hit are pushed out rather than carried along.
