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
| `LineState` region | `DemoEngine.ECS.SignalBufferLayout` | stride 12 doubles (id, start xyz, end xyz, rgba, reserved), appended after the transform records; `lineCount = 0` for every pre-P2c demo | `AbiPinTests.LineStateLayout_IsStride12Float64`, `AbiPinTests.LineEncoder_WritesLineRegionAfterTransforms` |
| Globals clock block | `DemoEngine.ECS.SignalBuffer` | 8 doubles (seq, time, delta, stepCount, paused, alpha, processed, dropped) | `AbiPinTests.GlobalClock_IsEightFloat64Elements` |
| Input ring | `DemoEngine.Inputs.InputRingLayout` | 8 slots × 100 records | `AbiPinTests.InputRing_IsEightSlotsByHundredRecords` |
| Packet ids / slots | `DemoEngine.Inputs` | 1 click-move (slots 1..4), 2 fire-ball (1..6), 3 scene-loaded (1) | `AbiPinTests.InputPacketIds_MatchEngineValues`, TS `src/signalLayout.ts` |
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
| 1 | CarDemo | mesh | planned (P3) | body/wheels are shapes; no external asset needed |
| 2 | TankDemo | mesh | planned (P3) | procedural body; no external asset needed |
| 3 | CharacterDemo | mesh | planned (P3) | uses `Content/newt.obj` (present) |
| 4 | RagdollTubeDemo | constraints | **ported** | 4×4×11 ragdolls (upstream 4×4×44) + 12-panel tube (upstream 20), `SubgroupFilteredCallbacks` |
| 5 | PyramidDemo | shapes | **ported** | 12 pyramids (upstream 40, documented reduction) |
| 6 | ColosseumDemo | shapes | **ported** | 3 ring layers (upstream 6, documented reduction); fire-ball + `shoot-big` replace the Z/X keys |
| 7 | NewtDemo | mesh + constraints | planned (P3) | OBJ mesh collidable + weld/volume constraints |
| 8 | ClothDemo | vertex mesh | planned (P3) | needs vertex-level signal records (design noted) |
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
| 29 | SponsorDemo | mesh + textures | planned (P3) | Sponsor PNGs present in `Temp/Demos/Content/Sponsors` |
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
