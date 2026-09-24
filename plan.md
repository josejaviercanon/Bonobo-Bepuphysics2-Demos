# Plan — Bonobo-Bepuphysics2 Demos

Test bed for the `Bonobo.Bepuphysics2` NuGet package and Bonobo engine compatibility review.
Self-contained repo: WinUI 3 + WebView2 + Native AOT host, Bonobo.ECS simulations, Babylon.js v9
presentation over the zero-copy 64-bit pinned signal ABI. Engine repo (`bonoboengine.wasm.3D`)
is reference material only and is never modified.

Scope: port the 30 upstream demos (DemoSet 29 + SimpleSelfContained) from `Temp/Demos/`.
Every demo = 1 C# fixture simulation + 1 Babylon scene + unit tests + E2E test with a committed
screenshot for human review + `docs/compat-review.md` table update.

---

## Current state (verified green)

| Piece | State | Verification |
| --- | --- | --- |
| `src/DemoEngine` — ABI mirror (pinned buffers, header=8, stride=12, globals=8, input ring 8×100, dispatcher) | done | 10 ABI pin tests |
| `src/Game.BepuDemos` — demo fixtures (Bonobo.Bepuphysics2 1.0.0, Bonobo.ECS 1.0.1, SourceGenerators 1.0.0) | done | 89 unit tests (behavior, determinism, no-NaN, verbs) |
| `src/DemoHost.WinApp` — WinUI3 + WebView2 + Native AOT host | done | Release AOT publish green |
| `src/BepuDemos.UI` — Babylon v9 bundle (thin instances, GridMaterial, OBJ loader, billboards, menu grid + back navigation, stats, ring producer) | done | `npm run typecheck` + `npm run build` |
| `src/Game.Tests.UI` — Playwright CDP suite + screenshots → `docs/screenshots/` | done | 34/34 passing |
| `docs/compat-review.md` — 30-demo status table + deviations | done | — |

Commands:

```powershell
npm run build                                        # bundle
dotnet build bonoboBepuDemos.slnx
dotnet run --project src/Game.BepuDemos.Tests        # xUnit v3 (dotnet test quirk, see compat-review §5.3)
dotnet run --project src/Game.BepuDemos.Tests.Aot    # TUnit
dotnet publish src/DemoHost.WinApp/DemoHost.WinApp.csproj -c Release -r win-x64 -p:Platform=x64
npm run test:e2e                                     # Playwright over WebView2 CDP
```

## Demos

### Done (30/30)

- [x] **SimpleSelfContainedDemo** — sphere on static floor + ECS orbit markers, tap-fire. Tests 31/31 · E2E green · `docs/screenshots/simple-self-contained*.png`
- [x] **PyramidDemo** — 12 box pyramids (upstream 40, documented), click cannonball. · `docs/screenshots/pyramid.png`
- [x] **BouncinessDemo** — 40×40 material sweep (upstream 100×100), 8 substeps. · `docs/screenshots/bounciness.png`
- [x] **PlanetDemo** — inverse-square gravity, 24×8×24 orbiting sheet (upstream 40×20×40). · `docs/screenshots/planet.png`


#### P2a — shapes / materials / solver (9) — done

- [x] **FrictionDemo** — material-property pattern (same as Bounciness); boxes + friction sweep
- [x] **ContinuousCollisionDetectionDemo** — CCD boxes/spheres (discrete/passive/continuous + spinner pairs)
- [x] **SubsteppingDemo** — substepping solver comparisons (substeps±/iters± verbs)
- [x] **CompoundDemo** — compound collidables; child-primitive render ids (parent ∘ local records)
- [x] **ColosseumDemo** — boxes/spheres fixture (3 layers, upstream 6)
- [x] **PerBodyGravityDemo** — per-body gravity via `CollidableProperty` (20×4×20, upstream 20×20×20)
- [x] **ContactEventsDemo** — `INarrowPhaseCallbacks` contact accumulation buffers (full 8-event layer)
- [x] **CollisionTrackingDemo** — contact tracking visualization
- [x] **CustomVoxelCollidableDemo** — custom voxel shape; voxel thin-instance render (20×15×20, upstream 40×30×40)

#### P2b — constraints (7) — done

- [x] **RopeStabilityDemo** — 7 configs + skip-constraint rope (full fidelity) · `docs/screenshots/rope-stability.png`
- [x] **RopeTwistDemo** — 2×65-link ropes (upstream 4×131), 30 substeps (upstream 60) · `docs/screenshots/rope-twist.png`
- [x] **ChainFountainDemo** — 2048 capsule beads (upstream 4096) · `docs/screenshots/chain-fountain.png`
- [x] **BlockChainDemo** — 20×20 chains + `ico` verb (upstream Z key) · `docs/screenshots/block-chain.png`
- [x] **RagdollTubeDemo** — 4×4×11 ragdolls (upstream 4×4×44), 12-panel tube (upstream 20) · `docs/screenshots/ragdoll-tube.png`
- [x] **DancerDemo** — 8×8 = 64 dancers (upstream 16×16), cloth dress LOD-clamped, sequential solves · `docs/screenshots/dancer.png`
- [x] **PlumpDancerDemo** — 4×4 = 16 dancers (upstream 8×8), weld voxel fat suits LOD-clamped · `docs/screenshots/plump-dancer.png`

#### P2c — debug-visual demos (4) — done

- [x] `LineState` ABI extension (header 6 → 8, stride-12 line region) + hand-written TS decoder + `rendering/lineSets.ts` (`LinesMesh`, per-vertex color)
- [x] **RayCastingDemo** — 16384 rays × 3 sources, unbatched `Simulation.RayCast` · `docs/screenshots/ray-casting.png`
- [x] **SweepDemo** — 16 scene-wide sweeps + ghost trails + impact lines (pairwise matrix dropped: unsafe-only API) · `docs/screenshots/sweep.png`
- [x] **CollisionQueryDemo** — 5×5 queries via managed `CollisionBatcher.Add` · `docs/screenshots/collision-query.png`
- [x] **SolverContactEnumerationDemo** — `ISolverContactDataExtractor` contact cylinders · `docs/screenshots/solver-contact-enumeration.png`

#### P3 — mesh/assets (6) — done

- [x] **CarDemo** — compound body + 4 suspension/hinge/motor wheels, 64 AI cars, 129×129×6 terrain, `VehicleControl` ring packets · `docs/screenshots/car.png`
- [x] **TankDemo** — body/turret/barrel + 2×5-wheel treads, twist-servo aiming, 32 AI tanks + CCD projectiles, `TankControl` ring packets · `docs/screenshots/tank.png`
- [x] **NewtDemo** — embedded `newt.obj` span parser + `DumbTetrahedralizer`, weld + volume constraints, heavy ball drop, OBJ ghost client-side · `docs/screenshots/newt.png`
- [x] **CharacterDemo** — full `CharacterControllers` port (custom solver constraints via `Solver.Register`), capsule actions over legos/fans/tongue/seesaw/platforms + 15× newt, `CharacterMove` ring packets · `docs/screenshots/character.png`
- [x] **SponsorDemo** — hopping kinematic newts + 150 AI characters + hut rings + 60× overlord newt; 27 sponsor PNG billboards via `@babylonjs/loaders`/`public/sponsors` · `docs/screenshots/sponsor.png`
- [x] **ClothDemo** — 4×10×30 curtains + 48×48 sheet, `CenterDistanceLimit`/`AreaConstraint` lattice, vertex records → client `VertexData` rebuild (design note: compat-review §6) · `docs/screenshots/cloth.png`

---

## Cross-cutting follow-ups

- [x] Main menu scene: 30-demo card grid (all 30 live), `Menu` back
      button on every demo, memory reset on switch (Babylon scene dispose + C# sim/pinned-buffer
      release + host shared-buffer channel cleanup) — `menu.spec.ts` round-trip E2E
- [x] Cloth vertex-record design note in `docs/compat-review.md` §6 (transform-record reuse approved and implemented)
- [x] Per-demo deviations table (grid reductions, input remaps, sequential dancer solves) kept up to date in `docs/compat-review.md` (P2a + P2b rows updated)
- [x] Shared P2b helper ports: `SubgroupFilter`, `RopeFilter`, `RagdollBuilder`, `DemoDancers`, `ClothFilter`, `DeformableFilter` + the dancer-scene TS factory
- [x] E2E: one spec per demo (switch scene → assert instances → screenshot) — `demos.spec.ts` covers 29 scene specs and `menu.spec.ts` asserts `{ total: 30, live: 30, placeholders: 0 }`
- [x] Keep engine repo untouched; ABI re-check when the engine ABI changes (P3 adds input packet ids 4..6 locally — documented in compat-review §1/§9)
