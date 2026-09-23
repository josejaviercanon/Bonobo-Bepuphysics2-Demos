# Bonobo-Bepuphysics2-Demos

Test bed for the `Bonobo.Bepuphysics2` NuGet package (1.0.0) and for Bonobo engine compatibility.

Upstream BepuPhysics2 demos (vendored sources in `Temp/`) are ported into AOT-safe Bonobo.ECS
simulations and rendered by a Babylon.js v9 page through a zero-copy, pure-64-bit pinned
signal bridge — the same ABI the Bonobo engine uses (`Float64Array` over a WebView2 shared
buffer on this host). A WinUI 3 + WebView2 + Native AOT host runs the simulations in-process.

This repo is a **test harness**, not a game: every demo is a fixture that exercises the physics
library, the ECS mapping and the host bridge.

## Layout

| Path | What |
| --- | --- |
| `src/DemoEngine` | Self-contained mirror of the Bonobo engine ABI: pinned buffers, signal header/layout, fixed-step host loop, input ring, module registry |
| `src/Game.BepuDemos` | Ported demos (`Bonobo.Bepuphysics2` + `Bonobo.ECS` / `Bonobo.ECS.SourceGenerators`) |
| `src/DemoHost.WinApp` | WinUI 3 + WebView2 + Native AOT host (only host target) |
| `src/BepuDemos.UI` | Babylon.js v9 frontend: main menu (30-demo grid), demo scenes, zero-copy decoders |
| `src/Game.BepuDemos.Tests` | xUnit v3 unit/determinism/layout tests |
| `src/Game.BepuDemos.Tests.Aot` | TUnit AOT/trim pattern checks |
| `src/Game.Tests.UI` | Playwright E2E over CDP (WebView2) with screenshots |
| `docs/compat-review.md` | Per-demo compatibility review (authoritative sim vs client render-only) |
| `docs/ai-agents/codebase-truth.md` | Verified repo facts for agent sessions (commands, port recipe, API gotchas) |
| `docs/screenshots/` | Committed E2E screenshots for human review |
| `Temp/` | Upstream BepuPhysics2 demo sources (not part of the build) |

## Build and run

```powershell
npm ci                                   # frontend dependencies (workspace: bepu-demos-ui)
npm run build                            # Vite bundle -> src/BepuDemos.UI/wwwroot/dist
dotnet build bonoboBepuDemos.slnx

# Tests (MTP runners; `dotnet test` reports "Zero tests ran" on this SDK — see
# docs/compat-review.md §5.3, run the apps directly):
dotnet run --project src/Game.BepuDemos.Tests        # xUnit v3: ABI pins + demo behavior
dotnet run --project src/Game.BepuDemos.Tests.Aot    # TUnit: AOT/trim pattern checks

# Desktop host (Release, Native AOT, unpackaged):
dotnet publish src/DemoHost.WinApp/DemoHost.WinApp.csproj -c Release -r win-x64 -p:Platform=x64
# → src/DemoHost.WinApp/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish/DemoHost.WinApp.exe
```

E2E (Playwright over the WebView2 CDP endpoint; writes `docs/screenshots/<demo>.png`):

```powershell
npm --prefix src/Game.Tests.UI install
npm run test:e2e
```

## Demo scenes

The app boots into the **main menu** (`menu`): a 30-card grid of the upstream DemoSet in
`docs/compat-review.md` order. The twenty-four ported demos are live cards; the remaining 6 are
disabled "(soon)" placeholders. Every demo scene has a `Menu` back button; switching scenes
disposes the Babylon scene and releases the C# simulation + its pinned signal buffer.

| Scene key | Upstream demo | Notes |
| --- | --- | --- |
| `menu` | — | 30-demo card grid; host stays idle (`menu` is an unknown sim key) |
| `simple-self-contained` | SimpleSelfContainedDemo | sphere on a static floor + ECS orbit markers, tap-to-fire |
| `pyramid` | PyramidDemo | 12 box pyramids (upstream 40), click cannonball |
| `bounciness` | BouncinessDemo | 40×40 material sweep (upstream 100×100), 8 substeps |
| `planet` | PlanetDemo | inverse-square gravity, 24×8×24 orbiting sheet (upstream 40×20×40) |
| `friction` | FrictionDemo | 100 boxes, friction sweep 0 → 0.75, colour-banded |
| `per-body-gravity` | PerBodyGravityDemo | 20×4×20 grid (upstream 20×20×20), per-body gravity by shape |
| `colosseum` | ColosseumDemo | 3 ring layers (upstream 6), click bullets + `Shoot Big` |
| `continuous-collision-detection` | ContinuousCollisionDetectionDemo | discrete/passive/continuous grids + hinge/motor spinner pairs |
| `substepping` | SubsteppingDemo | 10000:1 rope + capstone stack + motorized chains, `Substeps±`/`Iters±` |
| `compound` | CompoundDemo | compound children as individual records + `BigCompound` 128-child bodies |
| `contact-events` | ContactEventsDemo | full 8-event handler layer, particles on contact add (`Drop`) |
| `collision-tracking` | CollisionTrackingDemo | deferred pair analysis, particles on new touching ids (`Drop`) |
| `custom-voxel-collidable` | CustomVoxelCollidableDemo | 20×15×20 voxel terrain (upstream 40×30×40) + 1600 boxes |
| `rope-stability` | RopeStabilityDemo | 7 rope configs + skip-constraint rope, static wrap post |
| `rope-twist` | RopeTwistDemo | 2×65-link ropes (upstream 4×131) on a spinning 10000-mass ball, 30 substeps (upstream 60) |
| `chain-fountain` | ChainFountainDemo | 2048 capsule beads (upstream 4096) launching out of the container |
| `block-chain` | BlockChainDemo | 20×20 ball-socket chains, `ICO` verb = upstream Z key (coins) |
| `ragdoll-tube` | RagdollTubeDemo | 4×4×11 ragdolls (upstream 4×4×44) in a 12-panel spinning tube (upstream 20) |
| `dancer` | DancerDemo | 64 dancers 8×8 (upstream 256), cloth dresses LOD-clamped, sequential solves |
| `plump-dancer` | PlumpDancerDemo | 16 dancers 4×4 (upstream 64), weld/voxel fat suits LOD-clamped |
| `ray-casting` | RayCastingDemo | 16384 rays × 3 sources (random/frustum/wall) as colored `LineState` segments, `Cycle`/`Rotate`/source buttons |
| `sweep` | SweepDemo | 16 rotating scene-wide sweeps + 20-pose ghost trails + impact tangent lines |
| `collision-query` | CollisionQueryDemo | 5×5 shape queries through a `CollisionBatcher`, green/red touched routing |
| `solver-contact-enumeration` | SolverContactEnumerationDemo | solver contact extraction on a sensor box, impulse-scaled contact cylinders |

The remaining 6 demos and their porting status live in `docs/compat-review.md`.

## Compatibility rules

- All render state crosses C# -> JS as pinned `Float64Array` memory (never JSON, never
  per-entity interop calls). `DemoEngine` mirrors the engine header (`8`, extended with the P2c
  `lineCount`/`lineStride` fields), the `Transform3DState` stride (`12`), the appended
  `LineState` stride (`12`) and the globals clock block (`8`) — pinned by unit tests.
- Physics is authoritative in C#: deterministic null-`ThreadDispatcher` solves.
- Presentation-only concerns (camera, GUI, shaders, terrain visuals, cloth display) are
  client-side and never feed simulation state back except through the input ring.
- Memory reset on scene switch: the Babylon scene (meshes, materials, GUI textures) is
  disposed, `SimulationHost.Connect` stops the previous sim and frees its pinned signal
  buffer's `GCHandle`, and the host drops the old WebView2 shared-buffer channels.
- AOT: no runtime reflection, no dynamic IL; ECS components register through
  `Bonobo.ECS.SourceGenerators` generated code.
