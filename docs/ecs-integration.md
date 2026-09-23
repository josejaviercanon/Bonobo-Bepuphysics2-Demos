# Bonobo.Bepuphysics2 ↔ BonoboECS integration

`Bonobo.Bepuphysics2` has no dependency on BonoboECS. It exposes contiguous, blittable data that an ECS system can
consume directly, per the engine's `ZERO_COPY_INTEROP_MANDATE`.

## Package facts

- Package: `Bonobo.Bepuphysics2` version `1.0.0`, target `net10.0`.
- Contents: `Bonobo.Bepuphysics2.dll`, `Bonobo.BepuUtilities.dll`, and both XML documentation files.
- Assemblies are unsigned (no strong name).
- No external NuGet dependencies.

## ECS-facing API surface

### Blittable handles

`BodyHandle` and `StaticHandle` are blittable structs and can be embedded directly as component payloads:

```csharp
public struct PhysicsBody
{
    public BodyHandle Handle;
}
```

### Zero-copy pose access

`simulation.Bodies.ActiveSet.DynamicsState` is a `Buffer<BodyDynamics>`. `Buffer<T>` implicitly converts to `Span<T>`
and exposes a `ref` indexer, so a system loop can read pose state without intermediate copies:

```csharp
Span<BodyDynamics> dynamics = simulation.Bodies.ActiveSet.DynamicsState;
for (int i = 0; i < simulation.Bodies.ActiveSet.Count; ++i)
{
    ref var pose = ref dynamics[i].Motion.Pose;          // RigidPose (position + orientation)
    // Write pose.Position.X/Y/Z and pose.Orientation.X/Y/Z/W into the engine's
    // PinnedRenderBuffer<double> (stride 12) Float64Array shared-memory bridge.
}
```

If the physics state is `float` (32-bit) and the presentation layout is `float64`, widen at this boundary
(`Vector128.Widen` / scalar casts) while writing into the pinned buffer.

### Contact accumulation (no C# events)

Implement `INarrowPhaseCallbacks` and accumulate contact data sequentially into preallocated buffers:

```csharp
bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
    out PairMaterialProperties material) where TManifold : unmanaged, IContactManifold<TManifold>
{
    // Append pair + contact data into a preallocated Buffer<ContactEvent>; no managed events.
    material = new PairMaterialProperties(1f, 2f, new SpringSettings(30, 1));
    return true;
}
```

Drain the buffer after `Timestep` returns.

### Deterministic solve

```csharp
var simulation = Simulation.Create(pool, narrowPhaseCallbacks, poseIntegratorCallbacks, new SolveDescription(8, 1));
simulation.Timestep(dt, null);   // null IThreadDispatcher: single-threaded, deterministic, zero steady-state allocation
```

## Migrating the game engine off the vendored copy

1. The internal flat folder feed `X:\DEVSERVER\Nuget` (also reachable as `\\172.20.176.1\devserver\Nuget`) is already
   registered in the engine's root `nuget.config`.
2. In `src/Game.Engine/Game.Engine.csproj`, replace the vendored project references:

   ```xml
   <!-- remove -->
   <ProjectReference Include="..\bepuphysics2\BepuPhysics\BepuPhysics.csproj" />
   <ProjectReference Include="..\bepuphysics2\BepuUtilities\BepuUtilities.csproj" />

   <!-- add -->
   <PackageReference Include="Bonobo.Bepuphysics2" Version="1.0.0" />
   ```

3. Rename namespaces across `Game.Engine`, `Game.Demos`, and `Game.Tests`:

   | Old | New |
   | --- | --- |
   | `using BepuPhysics;` | `using Bonobo.Bepuphysics2;` |
   | `using BepuPhysics.Collidables;` | `using Bonobo.Bepuphysics2.Collidables;` |
   | `using BepuPhysics.CollisionDetection;` | `using Bonobo.Bepuphysics2.CollisionDetection;` |
   | `using BepuPhysics.Constraints;` | `using Bonobo.Bepuphysics2.Constraints;` |
   | `using BepuPhysics.Trees;` | `using Bonobo.Bepuphysics2.Trees;` |
   | `using BepuUtilities;` | `using Bonobo.BepuUtilities;` |
   | `using BepuUtilities.Memory;` | `using Bonobo.BepuUtilities.Memory;` |
   | `using BepuUtilities.Collections;` | `using Bonobo.BepuUtilities.Collections;` |
   | `using BepuUtilities.TaskScheduling;` | `using Bonobo.BepuUtilities.TaskScheduling;` |

   The same mapping applies to fully qualified type names and `<see cref="..."/>` documentation links.

4. Delete the vendored `src/bepuphysics2/` tree.
5. Rebuild, run `dotnet test`, then run the E2E suite (`GAME_WEB_PUBLISH=1 npx playwright test`).

No public type, member, or signature changed in the fork — only the namespace prefix.
