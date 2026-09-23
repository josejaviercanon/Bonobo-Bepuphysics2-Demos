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
| `src/BepuDemos.UI` | Babylon.js v9 frontend: demo scenes, switcher GUI, zero-copy decoders |
| `src/Game.BepuDemos.Tests` | xUnit v3 unit/determinism/layout tests |
| `src/Game.BepuDemos.Tests.Aot` | TUnit AOT/trim pattern checks |
| `src/Game.Tests.UI` | Playwright E2E over CDP (WebView2) with screenshots |
| `docs/compat-review.md` | Per-demo compatibility review (authoritative sim vs client render-only) |
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

| Scene key | Upstream demo | Notes |
| --- | --- | --- |
| `simple-self-contained` | SimpleSelfContainedDemo | sphere on a static floor + ECS orbit markers, tap-to-fire |
| `pyramid` | PyramidDemo | 12 box pyramids (upstream 40), click cannonball |
| `bounciness` | BouncinessDemo | 40×40 material sweep (upstream 100×100), 8 substeps |
| `planet` | PlanetDemo | inverse-square gravity, 24×8×24 orbiting sheet (upstream 40×20×40) |

The remaining 26 demos and their porting status live in `docs/compat-review.md`.

## Compatibility rules

- All render state crosses C# -> JS as pinned `Float64Array` memory (never JSON, never
  per-entity interop calls). `DemoEngine` mirrors the engine header (`6`), the
  `Transform3DState` stride (`12`) and the globals clock block (`8`) — pinned by unit tests.
- Physics is authoritative in C#: deterministic null-`ThreadDispatcher` solves.
- Presentation-only concerns (camera, GUI, shaders, terrain visuals, cloth display) are
  client-side and never feed simulation state back except through the input ring.
- AOT: no runtime reflection, no dynamic IL; ECS components register through
  `Bonobo.ECS.SourceGenerators` generated code.
