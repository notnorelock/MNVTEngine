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
| `CylinderCollider` | `Radius`, `Height` (along local Y, like the Cylinder primitive) |
| `MeshCollider` | `Convex`. Uses the triangles of a component implementing `IMeshDataSource` (such as `WorldTerrain`), else of the GameObject's `MeshRenderer` (primitive or model mesh). Call `Physics.RefreshShape(collider)` after procedural triangles change |
| `FixedJoint`, `BallJoint`, `HingeJoint` (`Axis`), `DistanceJoint` (`MinDistance`, `MaxDistance`), `SpringJoint` (`Distance`, `Frequency`, `DampingRatio`) | `ConnectedBody`, `Anchor` (this body's local space) |
| `CharacterController` | `Radius`, `Height`, `Center`, `SlopeLimit` (degrees), `StepOffset`, `SkinWidth`, `Layer` |

Every collider has `IsTrigger`, `Center` (local offset), `Friction` (combined as the geometric mean), `Bounciness` and `Layer` (0–31).

- **Static colliders.** Colliders without a `Rigidbody` are static (floors, walls).
- **One body per GameObject.** A GameObject's colliders form one body (a compound when there are several or an offset). A GameObject holds one component of each type, so combine different collider types; use child GameObjects with their own bodies for more parts.
- **Scale.** Colliders follow the transform's scale. Changing the scale rebuilds the body.
- **Mesh colliders.** Static and kinematic bodies collide with the exact triangles, from the side they are drawn on. Such a collider must be the only one on its GameObject. With `Convex`, or on a dynamic body, the collider is the convex hull of the vertices; a hull can be combined with other colliders. The mesh comes from the scene's `IMeshDataProvider`, which every host sets.
- **Layers.** `Physics.IgnoreLayerCollision(a, b)` makes two layers pass through each other, triggers included. `Physics.Raycast(..., layerMask)` skips the layers whose bit is clear.
- **Joints** connect this GameObject's body to `ConnectedBody`. Without one, the joint holds the body to the world at the anchor's position when the joint is created. A static connected body, or one without colliders, also holds to the world, with a warning. Joints are rebuilt whenever a body or joint changes.
- **Editor.** **GameObject > 3D Object** primitives come with a matching collider: box, sphere, capsule, cylinder, and a thin box for planes and quads. Add a `Rigidbody` to make them fall.

## Scripting

```csharp
body.AddForce(new Vector3(0f, 500f, 0f));                    // newtons, over the next step
body.AddForce(Vector3.Forward * 5f, ForceMode.Impulse);      // newton-seconds, now
body.AddTorque(new Vector3(0f, 2f, 0f));
body.Velocity = new Vector3(0f, 0f, -3f);                    // m/s; AngularVelocity is in degrees per second
if (Physics.Raycast(origin, Vector3.Down, 2f) is { } hit) { /* hit.Collider, hit.Point, hit.Normal, hit.Distance */ }
Physics.Gravity = new Vector3(0f, -20f, 0f);
Physics.IgnoreLayerCollision(8, 9);                          // layers 8 and 9 pass through each other
Physics.ThreadCount = 4;                                     // multithreaded stepping (default 1, deterministic)

// Character: gravity and input are the caller's; Move collides, slides and climbs steps.
_fall = controller.IsGrounded ? -1f : _fall - (9.81f * Time.FixedDeltaTime);
controller.Move((input * speed + new Vector3(0f, _fall, 0f)) * Time.FixedDeltaTime);
```

## Character controller

`CharacterController.Move(motion)` sweeps a capsule through the scene. It does not use forces.

- **Collide and slide.** It slides along what it hits, up to 4 times per move, and stops `SkinWidth` short of surfaces.
- **Slopes and steps.** Walkable ground is a surface flatter than `SlopeLimit`, or a ledge edge whose top is walkable. Walkable ground stops falling. Steeper surfaces are walls. A wall up to `StepOffset` high is stepped onto: the capsule moves up, forward, then down onto walkable ground.
- **Results.** `Move` returns the `CharacterCollision` flags (`Sides`, `Above`, `Below`) and sets `IsGrounded`, `Collisions` and `Velocity`.
- **Other bodies.** The capsule is a kinematic body for dynamic bodies, so they land on it and are pushed. It ignores triggers and other characters.
- **Sizes** are in world units; the transform's scale is not applied.

## Callbacks

- **Collisions.** Implement `ICollisionHandler` on a component. `OnCollisionEnter` and `OnCollisionExit` receive a `Collision`:
  - `Collider`, the collider of this GameObject, and `Other`;
  - `Normal`, pointing from this collider towards the other, as in 2D;
  - `Point`, the deepest contact point (both are zero on exit).
- **Triggers.** Implement `ITriggerHandler`: `OnTriggerEnter` and `OnTriggerExit` receive the other collider. Triggers never push.
- **When.** Callbacks run after the physics step, with the scene's script exception handling.
- **How enter and exit are found.** Bepu has no contact events. The narrow phase records the touching collider pairs of each step, and the engine compares them with the previous step. Pairs of sleeping bodies keep touching. Disabling or destroying a collider ends its contacts with an exit.

## How it runs

- **One simulation per scene.** `PhysicsWorld3D` is an ECS `GameSystem` (`Order = 1001`) stepped once per fixed update, after the components' `FixedUpdate`. By default it runs single-threaded, so it is deterministic. `ThreadCount` above 1 steps it on a Bepu `ThreadDispatcher`. It is created by the first 3D physics component of a running scene and released with the last body or on `Scene.Unload`.
- **Rebuilds and transforms** work as in 2D:
  - Changed GameObjects are rebuilt at the next settled point.
  - Moving a dynamic or static body's transform from code teleports it; scale changes rebuild it.
  - Kinematic bodies moved from code travel there by velocity during the step. Bodies they touch are pushed and carried along, like a moving platform. A move of more than 2 m in one step teleports.
  - After the step, dynamic and kinematic bodies move their transforms.
- **What the engine adds to Bepu:** gravity opt-out per body, damping, and forces accumulated over a step (`ForceMode.Force`).
- **Friction on face contacts.** In Bepu 2.4, a convex manifold with several contacts gets only about 1/n of the expected friction: a box on a floor slid at a quarter of μg. The engine scales the coefficient by the contact count, so sliding friction is μ times the load.
- **Solver:** 8 velocity iterations, 1 substep.

## Limitations

- **Bounciness** is approximate. Bepu has no restitution; a bouncy contact gets a softer, less damped spring.
- **Mesh colliders** on dynamic bodies are convex hulls; concave dynamic shapes need several hulls or primitives on child bodies.
- **Joints** have no limits or motors (no hinge angle limits, no powered hinges).
- **Characters** are capsules that are not rotated or scaled. They do not push dynamic bodies they walk into; those bodies only collide with them during the physics step.
- **Multithreaded stepping** is not deterministic between runs.
