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
| `src/DemoEngine` — ABI mirror (pinned buffers, header=6, stride=12, globals=8, input ring 8×100, dispatcher) | done | 31 unit tests (ABI pins) |
| `src/Game.BepuDemos` — demo fixtures (Bonobo.Bepuphysics2 1.0.0, Bonobo.ECS 1.0.1, SourceGenerators 1.0.0) | done | determinism, no-NaN, verb tests |
| `src/DemoHost.WinApp` — WinUI3 + WebView2 + Native AOT host | done | Release AOT publish green |
| `src/BepuDemos.UI` — Babylon v9 bundle (thin instances, GridMaterial, main menu grid + back navigation, stats, ring producer) | done | `npm run typecheck` + `npm run build` |
| `src/Game.Tests.UI` — Playwright CDP suite + screenshots → `docs/screenshots/` | done | 14/14 passing |
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

### Done (20/30)

- [x] **SimpleSelfContainedDemo** — sphere on static floor + ECS orbit markers, tap-fire. Tests 31/31 · E2E green · `docs/screenshots/simple-self-contained*.png`
- [x] **PyramidDemo** — 12 box pyramids (upstream 40, documented), click cannonball. · `docs/screenshots/pyramid.png`
- [x] **BouncinessDemo** — 40×40 material sweep (upstream 100×100), 8 substeps. · `docs/screenshots/bounciness.png`
- [x] **PlanetDemo** — inverse-square gravity, 24×8×24 orbiting sheet (upstream 40×20×40). · `docs/screenshots/planet.png`

### Pending (10/30)

Each task = `[ ] C# sim → scene → unit tests → E2E + screenshot → compat-review table`.

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

#### P2c — debug-visual demos (4, need line/ray records + `LinesMesh` client-side)

- [ ] **RayCastingDemo** — raycast visualization
- [ ] **SweepDemo** — sweep test visualization
- [ ] **CollisionQueryDemo** — broadphase/narrowphase query visualization
- [ ] **SolverContactEnumerationDemo** — contact enumeration display

#### P3 — mesh/assets (6)

- [ ] **NewtDemo** — OBJ mesh collidable (C# span parser) + weld/volume constraints; OBJ loaded client-side via `@babylonjs/loaders`
- [ ] **SponsorDemo** — `newt.obj` + 27 Sponsor PNGs as billboards
- [ ] **CharacterDemo** — capsule character controller + `newt.obj` static mesh
- [ ] **CarDemo** — procedural car body/wheels (no external asset)
- [ ] **TankDemo** — procedural tank (no external asset)
- [ ] **ClothDemo** — vertex-level signal records → client-side `VertexData` update (needs design note first: cloth vertex records in the transform buffer or a demo-specific record struct + hand-written TS decoder)

---

## Cross-cutting follow-ups

- [x] Main menu scene: 30-demo card grid (20 live / 10 disabled placeholders), `Menu` back
      button on every demo, memory reset on switch (Babylon scene dispose + C# sim/pinned-buffer
      release + host shared-buffer channel cleanup) — `menu.spec.ts` round-trip E2E
- [ ] Cloth vertex-record design note in `docs/compat-review.md` before the ClothDemo port
- [x] Per-demo deviations table (grid reductions, input remaps, sequential dancer solves) kept up to date in `docs/compat-review.md` (P2a + P2b rows updated)
- [x] Shared P2b helper ports: `SubgroupFilter`, `RopeFilter`, `RagdollBuilder`, `DemoDancers`, `ClothFilter`, `DeformableFilter` + the dancer-scene TS factory
- [ ] E2E: one spec per demo (switch scene → assert instances → screenshot), current `demos.spec.ts` loop extended;
      new demos become live cards in `src/BepuDemos.UI/src/scenes/menu/sceneMenu.ts`
- [ ] Keep engine repo untouched; ABI re-check when the engine ABI changes
