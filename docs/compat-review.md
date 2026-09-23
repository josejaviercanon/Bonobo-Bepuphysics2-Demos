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
| Signal header | `DemoEngine.ECS.SignalBuffer` | 6 doubles: seq, epoch, count, stride, stepMs, tickMs | `AbiPinTests.SignalHeader_HasEngineShape` |
| `Transform3DState` stride | `DemoEngine.ECS.SignalBufferLayout` | 12 doubles (id, xyz, quat xyzw, scale xyz, lifecycle) | `AbiPinTests.Transform3DLayout_IsStride12Float64` |
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
| 4 | RagdollTubeDemo | constraints | planned (P2) | capsules + constraints |
| 5 | PyramidDemo | shapes | **ported** | 12 pyramids (upstream 40, documented reduction) |
| 6 | ColosseumDemo | shapes | planned (P2) | |
| 7 | NewtDemo | mesh + constraints | planned (P3) | OBJ mesh collidable + weld/volume constraints |
| 8 | ClothDemo | vertex mesh | planned (P3) | needs vertex-level signal records (design noted) |
| 9 | DancerDemo | constraints | planned (P2) | capsule ragdolls + servos |
| 10 | PlumpDancerDemo | constraints | planned (P2) | as Dancer + volume constraints |
| 11 | ContinuousCollisionDetectionDemo | shapes | planned (P2) | |
| 12 | PlanetDemo | custom gravity | **ported** | 24×8×24 sheet (upstream 40×20×40, documented reduction) |
| 13 | PerBodyGravityDemo | custom gravity | planned (P2) | |
| 14 | CompoundDemo | compounds | planned (P2) | child-instance render ids |
| 15 | RopeStabilityDemo | constraints | planned (P2) | |
| 16 | SubsteppingDemo | solver | planned (P2) | |
| 17 | ChainFountainDemo | constraints | planned (P2) | |
| 18 | RopeTwistDemo | constraints | planned (P2) | |
| 19 | FrictionDemo | materials | planned (P2) | same material-property pattern as Bounciness |
| 20 | BouncinessDemo | materials | **ported** | 40×40 grid (upstream 100×100, documented reduction) |
| 21 | RayCastingDemo | debug visuals | planned (P2) | needs line/ray records |
| 22 | SweepDemo | debug visuals | planned (P2) | needs line records |
| 23 | ContactEventsDemo | contact events | planned (P2) | contact accumulation buffers |
| 24 | CollisionTrackingDemo | contact tracking | planned (P2) | |
| 25 | CollisionQueryDemo | debug visuals | planned (P2) | |
| 26 | SolverContactEnumerationDemo | debug visuals | planned (P2) | |
| 27 | CustomVoxelCollidableDemo | custom shape | planned (P3) | voxel thin-instance render |
| 28 | BlockChainDemo | constraints | planned (P2) | |
| 29 | SponsorDemo | mesh + textures | planned (P3) | Sponsor PNGs present in `Temp/Demos/Content/Sponsors` |
| 30 | SimpleSelfContainedDemo | shapes | **ported** | upstream fixture + ECS orbit markers |

Ported demos are covered by unit tests (`Game.BepuDemos.Tests`), AOT pattern tests
(`Game.BepuDemos.Tests.Aot`) and Playwright E2E with committed screenshots
(`docs/screenshots/<game-key>.png`).

## 5. Known deviations / follow-ups

1. **Grid reductions** (Pyramid, Bounciness, Planet) keep the desktop host interactive; the
   upstream constants are documented at each demo's `CreateModule` doc comment.
2. **No audio**: the engine's host-scope "audio" event ring is not mirrored (not needed for a
   physics test bed); the scene-loaded packet is still produced so a future audio bridge can
   consume it.
3. **`dotnet test` quirk**: on this SDK (10.0.401) `dotnet test` reports "Zero tests ran" for
   both this repo and the engine repo (MTP integration issue). Run tests directly:
   `dotnet run --project src/Game.BepuDemos.Tests` and
   `dotnet run --project src/Game.BepuDemos.Tests.Aot` (or execute the built exe).
4. **Generator parity**: the engine enforces signal/input layouts with Roslyn generators; this
   repo uses hand-written mirrors + unit tests. Adding a demo-specific record struct requires a
   matching hand-written TS decoder (no generator writes `signalLayout.ts` here).
5. **`Game.Engine` is never modified**: this repo consumes nothing from it at build time
   (`AGENTS.md` rule); the mirror is reviewed manually when the engine ABI changes.
