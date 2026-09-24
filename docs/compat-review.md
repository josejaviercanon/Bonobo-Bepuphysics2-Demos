# Compatibility review — Bonobo.Bepuphysics2 / Bonobo.ECS vs the Bonobo engine

This repo is a **test bed**, not a game. It exists to (a) exercise the `Bonobo.Bepuphysics2`
NuGet package against the upstream demo corpus and (b) review how that corpus behaves under the
Bonobo engine's constraints: Native AOT, Bonobo.ECS component mapping, the 64-bit pinned
shared-memory signal ABI, and Babylon.js v9 presentation.

## 1. ABI mirror (self-contained by design)

`src/DemoEngine` mirrors the engine's host seam instead of referencing engine projects
(the repo must stay self-contained). The mirrored surface:

| Contract | Mirror location | Value | Pinned by |
| --- | --- | --- | --- |
| Signal header | `DemoEngine.ECS.SignalBuffer` | 8 doubles: seq, epoch, count, stride, stepMs, tickMs, lineCount, lineStride | `AbiPinTests.SignalHeader_HasEngineShape` |
| `Transform3DState` stride | `DemoEngine.ECS.SignalBufferLayout` | 12 doubles (id, xyz, quat xyzw, scale xyz, lifecycle) | `AbiPinTests.Transform3DLayout_IsStride12Float64` |
| `LineState` region | `DemoEngine.ECS.SignalBufferLayout` | stride 12 doubles (id, start xyz, end xyz, rgba, reserved), appended after the transform records; `lineCount = 0` for every demo except the P2c debug-visual ports | `AbiPinTests.LineStateLayout_IsStride12Float64`, `AbiPinTests.LineEncoder_WritesLineRegionAfterTransforms` |
| Globals clock block | `DemoEngine.ECS.SignalBuffer` | 8 doubles (seq, time, delta, stepCount, paused, alpha, processed, dropped) | `AbiPinTests.GlobalClock_IsEightFloat64Elements` |
| Input ring | `DemoEngine.Inputs.InputRingLayout` | 8 slots × 100 records | `AbiPinTests.InputRing_IsEightSlotsByHundredRecords` |
| Packet ids / slots | `DemoEngine.Inputs` | 1 click-move (1..4), 2 fire-ball (1..6), 3 scene-loaded (1), 4 character-move (1..4), 5 vehicle-control (1..4), 6 tank-control (1..7) | `AbiPinTests.InputPacketIds_MatchEngineValues`, `AbiPinTests.InputDispatcher_RoutesP3PlayerIntents`, TS `src/signalLayout.ts` |
| Scalar type | every signal | pure `double` / `Float64Array` | encoder test + TS decoder |

Mirrored from: `bonoboengine.wasm.3D` — `Game.Engine/ECS/SignalBuffer.cs`,
`Game.Engine/ECS/Transform3DState.cs`, `Game.Engine/Inputs/InputRingLayout.cs`.
Deviation: the engine generates the input dispatcher and the TS layout tables from Roslyn
generators; this repo hand-writes both (`DemoEngine.Inputs.InputDispatcher`,
`BepuDemos.UI/src/signalLayout.ts`) and pins them with tests.

## 2. Host path (WinUI 3 + WebView2 + Native AOT)

- `src/DemoHost.WinApp` runs the simulations in-process (`SimulationHost` mirror) and
  publishes committed pinned buffers via `CoreWebView2SharedBuffer` + `PostSharedBufferToScript`
  (ReadOnly, 3-buffer rotation). No JSON, no per-entity interop.
- Scene switching is a memory-reset boundary: `Connect(game)` stops the previous simulation and
  disposes its per-connect pinned signal buffer (`GCHandle.Free`), and the page drops the old
  shared-buffer channels (`MainPage.DisposeChannels`). The main menu connects the reserved
  `menu` key — unknown to the host by design — so the engine idles with zero demos resident.
- Input is the ReadWrite shared ring (`SharedInputChannel`): the page writes records and
  publishes the head with `Atomics.store`; the host polls on its dispatcher timer. One
  low-frequency message path exists for commands (`connect`, `pause`, `command:{game}:{verb}`).
- `LocalAssetServer` serves the page over loopback HTTP with COOP/COEP (the page is
  cross-origin isolated, asserted by E2E).
- Determinism: every solve calls `Simulation.Timestep(dt)` with a null `ThreadDispatcher`;
  `PortedDemoTests.PortedSet_DeterministicAcrossRuns` compares two runs bit-for-bit (10 dp).

## 3. Authoritative vs presentation-only (per constraint 4)

| Layer | Authoritative (C#) | Client-side, render-only |
| --- | --- | --- |
| Physics | Bepu `Simulation`, shapes, contact callbacks, constraints | — |
| ECS | `Bonobo.ECS` world, components, generated `[Query]` systems (orbit markers, pose mirror) | — |
| Transforms | pose mirror → pinned float64 signal | thin-instance matrix writes, camera |
| Commands | `IDemoCommands` verbs, fire-ball/click-move packets | GUI buttons, tap-to-fire raycast |
| Input | decoded ring records applied in the sim | ring producer (`Float64Array` writes) |
| Visuals | — | floor grid material, ball colour bands, stats overlay, camera |

## 4. Per-demo status (30 = DemoSet 29 + SimpleSelfContained)

`DemoSet` order from `Temp/Demos/DemoSet.cs`. Deviations from upstream are deliberate,
documented test-bed reductions.

| # | Demo | Class | Status | Notes |
| --- | --- | --- | --- | --- |
| 1 | CarDemo | mesh | **ported** | 64 AI cars (upstream 384) + terrain 129×129×6 (upstream 257×257×3, same world extent); player driven by `VehicleControl` ring packets; behind-car camera + C-key toggle dropped; terrain is a client-side deformed plane |
| 2 | TankDemo | mesh | **ported** | 32 AI tanks (upstream 100), 129×129×6 terrain; `TankControl` ring packets (WASD/IJKL/Space/Shift/B); upstream `SpinLock` dropped (null-dispatcher solves); explosion visuals dropped, wrecks still fall apart |
| 3 | CharacterDemo | mesh | **ported** | full `CharacterControllers` port (custom `Static`/`DynamicCharacterMotionConstraint` via `Solver.Register`); capsule walks over legos/fans/tongue/seesaw/platforms and the 15× static newt; `CharacterMove` ring packets; character never sleeps (negative deactivation threshold) and spawns beside the lego field |
| 4 | RagdollTubeDemo | constraints | **ported** | 4×4×11 ragdolls (upstream 4×4×44) + 12-panel tube (upstream 20), `SubgroupFilteredCallbacks` |
| 5 | PyramidDemo | shapes | **ported** | 12 pyramids (upstream 40, documented reduction) |
| 6 | ColosseumDemo | shapes | **ported** | 3 ring layers (upstream 6, documented reduction); fire-ball + `shoot-big` replace the Z/X keys |
| 7 | NewtDemo | mesh + constraints | **ported** | embedded `newt.obj` parsed by the in-repo span parser, voxel-tetrahedralized (1 824 nodes/newt), welded + volume-constrained, heavy ball drop; client renders node spheres + a translucent OBJ ghost |
| 8 | ClothDemo | vertex mesh | **ported** | 4 curtains 10×30 + one 48×48 sheet (upstream 96×96); vertex records reuse the `Transform3DState` stride (design note §6); client rebuilds one grid `VertexData` mesh per panel each frame |
| 9 | DancerDemo | constraints | **ported** | 8×8 = 64 dancers (upstream 16×16), cloth dress LOD clamped to [1, 1.5] (upstream 29×29 at LOD 0), per-dancer sims step sequentially (no `ParallelLooper`) |
| 10 | PlumpDancerDemo | constraints | **ported** | 4×4 = 16 dancers (upstream 8×8), weld-connected voxel fat suit LOD clamped to [1, 1.4] (upstream 23³), sequential solves |
| 11 | ContinuousCollisionDetectionDemo | shapes | **ported** | discrete/passive/continuous grids + hinge/motor spinner pairs; servo oscillation driven from fixed-step time (no mouse aim) |
| 12 | PlanetDemo | custom gravity | **ported** | 24×8×24 sheet (upstream 40×20×40, documented reduction) |
| 13 | PerBodyGravityDemo | custom gravity | **ported** | 20×4×20 grid (upstream 20×20×20, documented reduction); per-body gravity via `CollidableProperty<float>` |
| 14 | CompoundDemo | compounds | **ported** | every compound child is its own transform record (parent ∘ local pose); deformed plane rebuilt client-side |
| 15 | RopeStabilityDemo | constraints | **ported** | full upstream fidelity: 7 configs + 100-link skip-constraint rope, static wrap capsule |
| 16 | SubsteppingDemo | solver | **ported** | `substeps±`/`iters±` verbs replace the Z/X/C/V keys; rope helpers ported into `RopeHelpers` |
| 17 | ChainFountainDemo | constraints | **ported** | 2048 beads (upstream 4096) |
| 18 | RopeTwistDemo | constraints | **ported** | 2×65-link ropes (upstream 4×131) and 30 substeps (upstream 60) |
| 19 | FrictionDemo | materials | **ported** | material-property pattern shared with Bounciness; boxes tinted per friction band |
| 20 | BouncinessDemo | materials | **ported** | 40×40 grid (upstream 100×100, documented reduction) |
| 21 | RayCastingDemo | debug visuals | **ported** | full 16384 rays/source (random/frustum/wall), single unbatched pass; `LineState` ray records (hit green + normal yellow, miss red); zero-radius upstream capsule kept in the sim but rendered with a small baked radius; convex hulls render as unit boxes; batched-vs-unbatched timing overlay dropped |
| 22 | SweepDemo | debug visuals | **ported** | 16 scene-wide sweeps + 20-pose ghost trails (hit/miss id ranges) + impact tangent lines; unsafe pairwise shape matrix dropped (deviation 8) |
| 23 | ContactEventsDemo | contact events | **ported** | full 8-event `IContactEventHandler` layer; particles are a fixed preallocated array with a `drop` verb |
| 24 | CollisionTrackingDemo | contact tracking | **ported** | deferred `CollisionTracker` analysis (current/previous pair state); `drop` verb |
| 25 | CollisionQueryDemo | debug visuals | **ported** | 25 queries + 128 falling boxes + deformed plane; managed `CollisionBatcher.Add(TypedIndex, …)` replaces the raw-pointer path; touched/untouched queries route to green/red id ranges |
| 26 | SolverContactEnumerationDemo | debug visuals | **ported** | full `ISolverContactDataExtractor` port; per-contact cylinders sized by penetration/friction impulse, green (touching) / blue (speculative) id ranges |
| 27 | CustomVoxelCollidableDemo | custom shape | **ported** | 20×15×20 voxels (upstream 40×30×40) + 1600 boxes (upstream 4096); 8 collision + 8 sweep task registrations; voxel thin-instance render |
| 28 | BlockChainDemo | constraints | **ported** | full upstream fidelity; the Z-key ICO is the `ico` verb and replaces the previous coin batch (fixed-capacity signal) |
| 29 | SponsorDemo | mesh + textures | **ported** | 150 AI characters (upstream 1000) + 8 hopping sponsor newts + 8 hut rings (upstream 30) + 60× overlord newt; 27 sponsor PNGs as client-side billboards (upstream screen-space text tiers and mouseover rewards dropped) |
| 30 | SimpleSelfContainedDemo | shapes | **ported** | upstream fixture + ECS orbit markers |

Ported demos are covered by unit tests (`Game.BepuDemos.Tests`), AOT pattern tests
(`Game.BepuDemos.Tests.Aot`) and Playwright E2E with committed screenshots
(`docs/screenshots/<game-key>.png`).

## 5. Known deviations / follow-ups

1. **Grid reductions** (Pyramid, Bounciness, Planet, Colosseum, PerBodyGravity, CustomVoxel,
   ChainFountain, RagdollTube, Dancer, PlumpDancer, RopeTwist) keep the desktop host
   interactive; the upstream constants are documented at each demo's class doc comment. Contact
   -particle counts are capped (256) and rendered as transform records — upstream rendered them
   directly through the demo renderer.
2. **Dancer demos step sequentially**: upstream runs the per-dancer cosmetic simulations through
   a `ParallelLooper`; this port uses a plain `for` loop so every fixture stays bit-deterministic
   (asserted at 10 dp). Cost of the 64/16 dancers is bounded by the LOD clamps; the desktop host
   runs these two fixtures below real time (~4-6 FPS), which the E2E suite absorbs with the
   `LONG_SETTLE` window.
3. **Capsule rendering contract**: capsule records emit scale 1 and the client bakes one mesh per
   (radius, length) pair (`createShapeSet(..., 'capsule', ..., { capsuleHeight, capsuleRadius })`);
   unit box/sphere/cylinder meshes carry their full dimensions in the record scale.
4. **No audio**: the engine's host-scope "audio" event ring is not mirrored (not needed for a
   physics test bed); the scene-loaded packet is still produced so a future audio bridge can
   consume it.
5. **`dotnet test` quirk**: on this SDK (10.0.401) `dotnet test` reports "Zero tests ran" for
   both this repo and the engine repo (MTP integration issue). Run tests directly:
   `dotnet run --project src/Game.BepuDemos.Tests` and
   `dotnet run --project src/Game.BepuDemos.Tests.Aot` (or execute the built exe).
6. **Generator parity**: the engine enforces signal/input layouts with Roslyn generators; this
   repo uses hand-written mirrors + unit tests. Adding a demo-specific record struct requires a
   matching hand-written TS decoder (no generator writes `signalLayout.ts` here). Every P2a/P2b
   demo reuses the `Transform3DState` ABI: compound children are parent ∘ local records, contact
   particles are short-lived records, and the compound deformed plane is presentation-only. The
   P2c debug-visual demos added the `LineState` region (header 6 → 8 doubles): transient colored
   line segments appended after the transform records, decoded by `decodeTransform3D.ts` and
   rendered through one `LinesMesh` per scene (`rendering/lineSets.ts`, per-vertex `Color4`).
   The engine-side header does not carry the line fields yet — this is the one documented ABI
   extension in the mirror.
   **P3 cloth vertex-record design note (resolved)**: `ClothDemo` emits one `Transform3DState`
   per cloth node (id = `10000 + panel·4096 + row·width + column`, quaternion unused, scale =
   node diameter) instead of a new demo-specific record struct. Rationale: the stride-12 decoder
   already fits vertex data, so no hand-written TS decoder or `AbiPinTests` surface is added; the
   wasted quaternion/scale fields cost ~340 KB of pinned buffer at the ported panel sizes, which
   is irrelevant for a fixed-capacity signal. The client (`scenes/cloth/sceneCloth.ts`) keeps one
   updatable grid `VertexData` mesh per panel and rewrites positions/normals per dispatch.
7. **`Game.Engine` is never modified**: this repo consumes nothing from it at build time
   (`AGENTS.md` rule); the mirror is reviewed manually when the engine ABI changes.
8. **P2c debug-visual deviations**:
   - **RayCasting**: upstream compares a batched and an unbatched ray algorithm and prints timings;
     the Bonobo package exposes no public `SimulationRayBatcher` constructor and the ABI has no
     text channel, so the port runs one unbatched pass. A zero-radius capsule collidable (upstream
     `Capsule(0, 0.5)`) is kept in the simulation but rendered with a small baked radius, and
     convex-hull collidables are drawn as unit boxes (no hull mesh client-side).
   - **Sweep**: the upstream background pairwise shape-vs-shape matrix uses the raw-pointer
     `SweepTaskRegistry.Sweep(void*)` API; unsafe code is forbidden in ported demos, so only the
     managed scene-wide `Simulation.Sweep` part is ported (ghost trails are solid green/red per
     hit/miss id range instead of the per-step fade).
   - **CollisionQuery**: the raw-pointer `AddDirectly`/`CacheShapeB`/`GetShapeData` path is
     replaced by the managed `CollisionBatcher.Add(TypedIndex, TypedIndex, …)` overload (both
     shapes are registered in `Simulation.Shapes`); a fresh batcher is created per pass because a
     batcher cannot be reused across flushes once nonconvex shapes (the deformed mesh) were added.
   - **SolverContactEnumeration**: per-contact render color is replaced by two id ranges (the
     `Transform3DState` record carries no color); the extractor itself is a 1:1 port.
   - All four demos drop the upstream text overlays (no text channel in the ABI, matching every
     other ported demo).

9. **P3 mesh/asset deviations**:
   - **Assets**: `Temp/Demos/Content/newt.obj` (with the `mtllib` line stripped so the client
     loader does not 404 on the missing `.mtl`) is vendored twice: embedded in `Game.BepuDemos`
     (`Content/newt.obj`, parsed by the in-repo span parser `ObjMeshParser`, no `ObjLoader`
     package) and as a Vite public asset (`src/BepuDemos.UI/public/models/newt.obj`) loaded by
     `@babylonjs/loaders`. The 27 sponsor PNGs are copied to `public/sponsors/` and rendered as
     billboards (one plane per image) around the arena.
   - **Input extension**: packet ids 4..6 (`CharacterMove`, `VehicleControl`, `TankControl`)
     extend the engine's input ring ABI; the dispatcher, sinks, TS producers and pins are
     hand-written/updated (`docs/compat-review.md` §1). Keyboard/tap input stays off DOM
     gameplay paths (`INPUT_RING_ONLY`).
   - **Character**: full demo-side `CharacterControllers` port (upstream also ships it as demo
     code); worker caches collapse to one sequential cache (null-dispatcher solves); the
     character uses a negative deactivation threshold so a sleep transition cannot swallow a
     one-step-delayed input packet; no C-key add/remove toggle; no camera override (shared orbit
     camera follows the capsule).
   - **Car/Tank**: OpenTK keyboard polling replaced by ring packets; the behind-car camera,
     on-screen control overlay and explosion visuals are dropped (no text/particle record type);
     tank wrecks still fall apart physically; AI counts and terrain resolutions reduced.
   - **Newt**: `DumbTetrahedralizer` uses managed collections with deterministic insertion order;
     newt count 8 (upstream 8) with 1 824 nodes each; the client shows the reference OBJ ghost.
   - **Sponsor**: 150 AI characters / 8 huts (documented reductions); no screen-space sponsor
     text or mouseover reward images (billboards replace them); billboards are presentation-only
     (never in physics).
   - **Cloth**: 48×48 sheet (upstream 96×96); `ClothFilter`/`ClothCallbacks` reused from the
     dancer ports; panel nodes are sphere bodies rendered as a vertex mesh (no per-node spheres).
