# Codebase truth — verified facts for agent sessions

Repo-local companion to `docs/compat-review.md`. Every entry below was verified by running the
code in this repo (builds, tests, E2E, screenshots). Prefer these facts over memory.

## Commands that actually work

```powershell
npm ci && npm run build                  # bundle -> src/BepuDemos.UI/wwwroot/dist
dotnet build bonoboBepuDemos.slnx
dotnet run --project src/Game.BepuDemos.Tests        # xUnit v3 (89 tests)
dotnet run --project src/Game.BepuDemos.Tests.Aot    # TUnit
dotnet publish src/DemoHost.WinApp/DemoHost.WinApp.csproj -c Release -r win-x64 -p:Platform=x64
npm run test:e2e                         # root shortcut -> Playwright over WebView2 CDP (34 tests)
```

- `dotnet test` reports "Zero tests ran" on this SDK (MTP quirk) — run the test apps directly.
- xUnit v3 in-process runner simple filters: `-class "Game.BepuDemos.Tests.PortedDemoTests"`
  works; the bare `-method "Name"` filter did not match. Use `-class`, or `-class` + full name.
- E2E publish path: `src/DemoHost.WinApp/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish/`.
  Republish after any C# or bundle change or the suite silently tests the old build.
- Playwright filter for one spec: `npx playwright test --config=playwright.winapp.config.ts tests/demos.spec.ts`
  from `src/Game.Tests.UI`. The full suite is ~2 minutes after a fresh publish.
- `playwright-cli` cannot attach to the WebView2 host (no CDP flag); the checked-in suite is the
  verification tool for this repo.

## Adding one demo (7 touchpoints)

1. `src/Game.BepuDemos/Demos/<Name>Demo.cs` — implement `IDemoSimulation` (+ `IDemoCommands` /
   `IFireBallSink` as needed); `CreateModule()` with a unique `[a-z0-9-]+` game key;
   `BufferCapacity = SignalBuffer.HeaderLength + records * SignalBufferLayout.Transform3DStride`.
2. Register in `src/Game.BepuDemos/BepuDemosModules.cs` (static glue, no reflection).
3. Scene: `src/BepuDemos.UI/src/scenes/<key>/scene<Name>.ts` (copy `sceneBounciness.ts`; use
   `createGround` + `createShapeSet` helpers) + `SCENES` entry in `src/BepuDemos.UI/game.ts`.
4. Menu card: set `key` in `MENU_ITEMS` (`src/BepuDemos.UI/src/scenes/menu/sceneMenu.ts`).
5. Unit tests: behavior test + `[InlineData("<key>")]` in
   `PortedDemoTests.PortedSet_DeterministicAcrossRuns` + factory switch.
6. E2E: `SCENES` + `HostWindow` hook in `src/Game.Tests.UI/tests/demos.spec.ts`; menu counts in
   `menu.spec.ts` (currently `{ total: 30, live: 30, placeholders: 0 }`). Add the key to
   `LONG_SETTLE` when the fixture needs more than 2.5 s to settle.
7. Docs: `docs/compat-review.md` status row, `README.md` scene table, `plan.md` counts.

Shared C# helpers: `DemoPoseSet` (render-id → body/static registry, pose sync, batched emit),
`RopeHelpers` (rope + wrecking ball builders), `SubgroupFilter` (`SubgroupCollisionFilter` +
`SubgroupFilteredCallbacks`), `RopeFilter` (`RopeFilter` + `RopeNarrowPhaseCallbacks`),
`RagdollBuilder` (`AddRagdoll`, capsule/pose helpers), `DemoDancers` (main dancer + per-dancer
cosmetic simulations, motion-history replay), `ClothFilter`/`DeformableFilter` (self-collision
filters for the dancer dress/suit), `DemoMeshHelper.CreateDeformedPlane`, `ObjMeshParser` +
`DemoContent` (embedded `Content/newt.obj` text), `CarHelpers` (simple car/controller/track),
`TankHelpers` (tank parts/controller/AI/callbacks), `NewtTetrahedralizer`, `Characters/`
(`CharacterControllers` + `CharacterMotionConstraint` + `CharacterInput` +
`CharacterNarrowphaseCallbacks` — demo-side dynamic character controller system).
Shared TS helpers: `rendering/ground.ts`, `rendering/shapeSets.ts` (box/sphere/capsule/cylinder),
`rendering/instanceSets.ts`, `rendering/thinInstances.ts`, `rendering/lineSets.ts` (fixed-capacity
per-vertex-color `LinesMesh`), `rendering/deformedPlane.ts` (client duplicate of
`DemoMeshHelper.CreateDeformedPlane`), `gui/commandButtons.ts`,
`scenes/dancers/dancerSceneShared.ts` (dancer/plump-dancer scene factory).

## Bepu / C# API facts (learned the hard way)

- `new Box(width, height, length)` takes **full** sizes (half extents are /2). `Capsule(radius,
  length)` renders as total height `length + 2·radius`. A render scale should equal the full size.
- `default(RigidPose)` has a **zero quaternion** — adding a body with it corrupts the broadphase
  tree (`IndexOutOfRangeException` inside `Tree.GetOverlapsInNode`). Use `RigidPose.Identity`.
- `x % 3 switch { ... }` parses as `x % (3 switch { ... })` in C#. Parenthesize:
  `((x) % 3) switch { ... }`.
- `simulation.Bodies[handle]` returns a `BodyReference` value (not a ref); write through
  `new BodyReference(handle, simulation.Bodies)` and wake with `simulation.Awakener.AwakenBody(handle)`.
- `CompoundBuilder.BuildDynamicCompound(out children, ...)` children expose `LocalPosition` +
  `LocalOrientation` (not `LocalPose`); `CompoundPairCollisionTask` is arity 5 in this fork.
- Null-dispatcher `Simulation.Timestep(dt)` only; every ported fixture is bit-deterministic
  (asserted at 10 dp).
- `ContinuousDetection.Discrete/Passive/Continuous(min, conv)` and the `CollidableDescription`
  overloads behave like upstream; the 20-thick ground is not tunneled at 150 m/s and 1/60 s, so
  do not assert tunneling.
- Contact-event/tracking fixtures are single-threaded ports: one worker cache, fixed particle
  arrays, `drop` verb to re-trigger contacts.
- Constraint API facts: `SwingLimit` stores `MinimumDot` but exposes a settable
  `MaximumSwingAngle` property (object initializers work); `Solver.CountConstraints()` exists and
  is used by `RagdollTubeDemo`; `CollidableProperty<T>` lives in `Bonobo.Bepuphysics2` (not
  `.CollisionDetection`) and its callbacks own `Dispose`.
- `Simulation.Create(pool, narrow, pose, solveDescription, null, initialAllocationSizes: ...)`
  creates the per-dancer cosmetic sims; `HashHelper.Rehash` / `QuickQueue` are in
  `Bonobo.BepuUtilities.Collections`; `Bodies.ActiveSet.Count` is the allocated body count.
- `BodyDescription.CreateDynamic(pose|position, [velocity], inertia, shape, sleepThreshold)`
  relies on implicit `TypedIndex` → `CollidableDescription` and `float` → `BodyActivityDescription`
  conversions. `BigCompound(Buffer<CompoundChild>, Shapes, BufferPool, IThreadDispatcher)` takes a
  dispatcher (pass `null`); compare compound children by `ShapeIndex.Packed`.
- Dancer demos use one `DemoPoseSet` per simulation (main + one per background dancer) sharing the
  same ECS `World`; render ids use a per-dancer stride (512 dress / 4096 suit) because the dress
  node count varies with LOD.
- P2c query API facts:
  - `Simulation.RayCast<THitHandler>(Vector3 origin, Vector3 direction, float maximumT,
    BufferPool pool, ref THitHandler handler, int id)` is the only overload; `RayData` lives in
    `Bonobo.Bepuphysics2.Trees` and carries `Origin`/`Direction`/`Id` only (no `MaximumT`).
    `IRayHitHandler.OnRayHit(in RayData, ref float maximumT, …)` receives the running max-T by
    ref, so the handler must assign `maximumT = t` to clamp the traversal.
  - `SimulationRayBatcher<T>` has **no public constructor** in this package (the batched-vs-
    unbatched comparison cannot be ported); use one unbatched pass.
  - `Simulation.Sweep<TShape, TSweepHitHandler>(TShape shape, in RigidPose pose, in BodyVelocity
    velocity, float maximumT, BufferPool pool, ref TSweepHitHandler handler)` is the managed path;
    `SweepTaskRegistry.Sweep(void*)` (the pairwise matrix) is unsafe-only.
  - `CollisionBatcher<T>` has managed `Add(TypedIndex, TypedIndex, offsetB, orientA, orientB,
    margin, in PairContinuation)` and `Add<TShapeA,TShapeB>(…)` overloads; `AddDirectly`/
    `CacheShapeB`/`GetShapeData` are unsafe-only. Both shapes must be registered in
    `Simulation.Shapes`. A batcher **cannot be reused across flushes** once nonconvex shapes
    (meshes) were added — create a fresh one per pass (upstream does this per frame).
  - `ISolverContactDataExtractor` lives in `Bonobo.Bepuphysics2.Constraints.Contact`;
    `NarrowPhase.TryExtractSolverContactData(ConstraintHandle, ref TExtractor)` and the sensor's
    `BodyReference.Constraints[i].ConnectingConstraintHandle` (`QuickList<BodyConstraintReference>`)
    drive the enumeration. `NonconvexContactManifold` is in `.CollisionDetection`.
  - `QuickList<T>` exposes `Count` and `Span` as **fields** (`Span` is a `Buffer<T>`); `Buffer<T>`
    converts implicitly to `Span<T>`.
  - `BodyDescription.CreateConvexDynamic(position, mass, Shapes, shape)` builds the sensor body.
- P3 API/asset facts:
  - `Solver.Register<TConstraintDescription>()` works for demo-side custom constraints (the
    package exposes `OneBodyTypeProcessor`/`TwoBodyTypeProcessor`, `IOneBodyConstraintFunctions`/
    `ITwoBodyConstraintFunctions`, `AccessAll`, `GatherScatter.GetOffsetInstance`,
    `TypeProcessor`, `TypeBatch`, `IOneBodyConstraintDescription`/`ITwoBodyConstraintDescription`).
    `CharacterControllers` registers `BatchTypeId` 50/51 and hooks
    `simulation.Timestepper.BeforeCollisionDetection` + `CollisionsDetected`
    (`Action<float, IThreadDispatcher?>`).
  - `new CollidableDescription()` (default) means **no collidable** (verified: overlapping bodies
    with it do not generate contacts) — use it for internal deformable vertices.
  - `BodyReference.Awake` can be true in the same step where the sleeper deactivates the body;
    demo-side character code should use a negative body deactivation threshold (`-1f`) when input
    can arrive one step behind the goals.
  - `new TypedIndex(type, index).Packed` sets high bits for the speculative margin; compare
    collidables by `Packed`, never by `Index` (see `TryReportContacts`).
  - OBJ: vendored `Temp/Demos/Content/newt.obj` (with `mtllib` stripped) lives both embedded in
    `Game.BepuDemos` (`ObjMeshParser.Parse(DemoContent.NewtObjText)`) and as a Vite public asset
    (`src/BepuDemos.UI/public/models/newt.obj`, URL `/dist/models/newt.obj`, loaded by
    `import '@babylonjs/loaders/OBJ'` + `SceneLoader.ImportMeshAsync`). 27 sponsor PNGs live in
    `public/sponsors/` (billboards use `CreatePlane` + `Mesh.BILLBOARDMODE_ALL`).
  - Input ring packet ids 4..6 are the P3 player-intent extension (`CharacterMove`,
    `VehicleControl`, `TankControl`); add new fields to `DemoEngine.Inputs.Inputs.cs`,
    `BepuDemos.UI/src/signalLayout.ts`, `inputRing.ts` and the `AbiPinTests` pin together.
  - Thin instances only count in the stats overlay when the mesh name starts with `demo-`
    (`sceneRunner` filters `mesh.name.startsWith('demo-')`).

## Signal / E2E contract

- One ABI for every demo: header 8 doubles (`seq, epoch, transformCount, stride, stepMs, tickMs,
  lineCount, lineStride`) + `Transform3DState` records (stride 12, float64) + optional
  `LineState` records (stride 12: id, start xyz, end xyz, rgba, reserved) appended after the
  transforms. Compound children are parent ∘ local records; particles are short-lived records.
  Only the P2c debug-visual demos emit lines; every other demo writes `lineCount = 0`.
- Line records are transient (rewritten every step, no lifecycle). The client renders them
  through one fixed-capacity `LinesMesh` per scene (`rendering/lineSets.ts`): Babylon forbids
  changing the point count of an updatable line system, so unused slots are hidden with alpha 0,
  and per-vertex `Color4` removes any palette bucketing (one draw call per scene).
- Scene hook contract: every scene sets `window.__<key>()` returning at least
  `{ visibleInstances }`; E2E polls it before screenshotting to `docs/screenshots/<game-key>.png`.
- Contact demos click `btn-drop` before their screenshot so particles are visible.
- Cloth vertices reuse `Transform3DState` records (id = `10000 + panel·4096 + row·width +
  column`, quaternion unused, scale = node diameter); the client rebuilds one updatable grid
  `VertexData` mesh per panel (design note: compat-review §6).
- The scene must route records by render-id range (never by arrival order); the C# fixture owns
  the id ranges and documents them in its class comment.
- Capsule records emit scale 1: the client bakes one capsule mesh per (radius, length) pair via
  `createShapeSet(..., 'capsule', ..., { capsuleHeight, capsuleRadius })`. Unit box/sphere/cylinder
  meshes carry their full dimensions in the record scale (coins are unit cylinders scaled to
  `(2r, 2·halfLength, 2r)`).
