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
| `src/Game.Tests.UI` — Playwright CDP suite + screenshots → `docs/screenshots/` | done | 8/8 passing |
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

### Done (4/30)

- [x] **SimpleSelfContainedDemo** — sphere on static floor + ECS orbit markers, tap-fire. Tests 31/31 · E2E green · `docs/screenshots/simple-self-contained*.png`
- [x] **PyramidDemo** — 12 box pyramids (upstream 40, documented), click cannonball. · `docs/screenshots/pyramid.png`
- [x] **BouncinessDemo** — 40×40 material sweep (upstream 100×100), 8 substeps. · `docs/screenshots/bounciness.png`
- [x] **PlanetDemo** — inverse-square gravity, 24×8×24 orbiting sheet (upstream 40×20×40). · `docs/screenshots/planet.png`

### Pending (26/30)

Each task = `[ ] C# sim → scene → unit tests → E2E + screenshot → compat-review table`.

#### P2a — shapes / materials / solver (9)

- [ ] **FrictionDemo** — material-property pattern (same as Bounciness); boxes + friction sweep
- [ ] **ContinuousCollisionDetectionDemo** — CCD boxes/spheres
- [ ] **SubsteppingDemo** — substepping solver comparisons
- [ ] **CompoundDemo** — compound collidables; child-primitive render ids
- [ ] **ColosseumDemo** — boxes/spheres fixture
- [ ] **PerBodyGravityDemo** — per-body gravity via `CollidableProperty`
- [ ] **ContactEventsDemo** — `INarrowPhaseCallbacks` contact accumulation buffers
- [ ] **CollisionTrackingDemo** — contact tracking visualization
- [ ] **CustomVoxelCollidableDemo** — custom voxel shape; voxel thin-instance render (could move to P3)

#### P2b — constraints (7)

- [ ] **RopeStabilityDemo** — capsules + distance constraints
- [ ] **RopeTwistDemo** — twist-stable rope
- [ ] **ChainFountainDemo** — box chain fountain
- [ ] **BlockChainDemo** — block chain
- [ ] **RagdollTubeDemo** — capsule ragdoll in a tube
- [ ] **DancerDemo** — capsule ragdolls + servos
- [ ] **PlumpDancerDemo** — dancer + volume constraints

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

- [x] Main menu scene: 30-demo card grid (4 live / 26 disabled placeholders), `Menu` back
      button on every demo, memory reset on switch (Babylon scene dispose + C# sim/pinned-buffer
      release + host shared-buffer channel cleanup) — `menu.spec.ts` round-trip E2E
- [ ] Cloth vertex-record design note in `docs/compat-review.md` before the ClothDemo port
- [ ] Per-demo deviations table (grid reductions, input remaps) kept up to date in `docs/compat-review.md`
- [ ] E2E: one spec per demo (switch scene → assert instances → screenshot), current `demos.spec.ts` loop extended;
      new demos become live cards in `src/BepuDemos.UI/src/scenes/menu/sceneMenu.ts`
- [ ] Keep engine repo untouched; ABI re-check when the engine ABI changes
