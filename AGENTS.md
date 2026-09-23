# Agent Directives — Bonobo-Bepuphysics2-Demos

Test bed for the **`Bonobo.Bepuphysics2` NuGet package (1.0.0)** and for Bonobo engine
compatibility review. Upstream BepuPhysics2 demos (vendored sources in `Temp/`, not part of the
build) are ported into AOT-safe Bonobo.ECS simulations, rendered by a Babylon.js v9 page over the
zero-copy 64-bit pinned signal ABI. This repo is a **test harness, not a game** (see
`README.md`, `docs/compat-review.md`, `docs/ai-agents/codebase-truth.md`).

**Engine repo note:** `X:\PROJECTS\Repos\bonoboengine.wasm.3D` is reference material only — never
edit it; this repo mirrors the ABI locally (`src/DemoEngine`) instead of referencing it.

---

## Non-negotiable rules

### ZERO_COPY_INTEROP
- All high-frequency render state crosses C# → JS as pinned `Float64Array` memory
  (`PinnedRenderBuffer<double>` + `GCHandle.Alloc(..., GCHandleType.Pinned)`, fixed capacity,
  never re-pinned). Never JSON, never per-entity interop, never managed array clones.
- Desktop (only host): `CoreWebView2SharedBuffer` + `PostSharedBufferToScript(..., ReadOnly)`;
  the page reads the same mapping as `Float64Array` and calls `chrome.webview.releaseBuffer`
  after dispatch. `PostWebMessageAsArrayBuffer` does **not** exist.
- One signal for every demo: header 8 float64 (`seq, epoch, transformCount, stride, stepMs,
  tickMs, lineCount, lineStride`) + `Transform3DState` records (stride 12: id, xyz, quaternion
  xyzw, scale xyz, lifecycle) + optional `LineState` records (stride 12: id, start xyz, end xyz,
  rgba, reserved) appended after the transforms. Compound children are parent ∘ local records;
  particles are short-lived records; only the P2c debug-visual demos emit lines (`lineCount = 0`
  everywhere else). Adding a record type requires a hand-written TS decoder (no generator in this
  repo) + an `AbiPinTests` update.

### INPUT_RING_ONLY
- Player input crosses JS → C# through the single pinned input ring
  (`DemoEngine.Inputs.InputRingLayout`, 8 slots × 100 records): slot 0 = packet id, slots 1..7 =
  fields; JS publishes with `Atomics.store`; the host drains it in `SimulationHost.Tick`.
- Never add a per-input `[JSExport]`, JSON payload, or DOM event path for gameplay input.
  Low-frequency sim commands go through the existing `command:{game}:{verb}` message path
  (`IDemoCommands.TryCommand` on the C# side).

### DETERMINISM_AND_MEMORY
- Every solve is a **null-`ThreadDispatcher`** `Simulation.Timestep(dt)`. The dancer demos step
  their per-dancer cosmetic simulations **sequentially** (upstream uses a `ParallelLooper`) so
  the whole ported set stays bit-deterministic at 10 dp
  (`PortedDemoTests.PortedSet_DeterministicAcrossRuns`).
- Fixed 1/60 s step (`SimulationHost.FixedStepSeconds`); simulations own no timers and only
  expose `Step(dt)`.
- No managed allocations in hot loops; use struct components + preallocated buffers. Never
  instantiate reference types inside the physics step.
- No `unsafe` in demo code; prefer spans/arrays.

### AOT_STRICT_COMPLIANCE
- Production is Native AOT (`DemoHost.WinApp`, Release publish). No reflection outside
  `#if DEBUG`; the module registry is hand-written static glue
  (`BepuDemosModules.AddGameBepuDemosModules`) — never reflection, assembly scanning, or
  `Activator.CreateInstance` in the host or engine mirror.
- Keep `PublishReadyToRun` disabled whenever `PublishAot=true`.

### CAPSULE_RENDER_CONTRACT
- Capsule records emit **scale 1**; the client bakes one capsule mesh per `(radius, length)`
  pair via `createShapeSet(..., 'capsule', ..., { capsuleHeight: length + 2·radius, capsuleRadius: radius })` and routes by render-id slot.
- Unit box/sphere/cylinder meshes carry their full dimensions in the record scale
  (coins = unit cylinder scaled to `(2r, 2·halfLength, 2r)`; `Capsule(radius, length)` renders as
  total height `length + 2·radius`).

### ECS_PHYSICS_MAPPING
- Store Bepu `BodyHandle`s inside Bonobo.ECS (`PhysicsBody` component); mirror authoritative Bepu
  poses into ECS after each step (`DemoPoseSet.Sync`) and emit the batched render signal from the
  ECS data. Physics stays authoritative in C#; the client never simulates gameplay physics.

---

## Repository shape

| Path | What |
| --- | --- |
| `src/DemoEngine` | Self-contained mirror of the engine ABI: `PinnedRenderBuffer`, `SignalBuffer` (header 8, transform stride 12 + line stride 12, globals 8), input ring (8×100), `DirectRenderTransport`, `SimulationHost` (fixed-step loop, connect/pause/commands), `DemoModule`/`DemoRegistry` |
| `src/Game.BepuDemos` | The ported demos + shared helpers (`DemoPoseSet`, `DemoCallbacks`, `RopeHelpers`, `SubgroupFilter`, `RopeFilter`, `RagdollBuilder`, `DemoDancers`, `ClothFilter`, `DeformableFilter`, `DemoMeshHelper`) and the hand-written module glue |
| `src/DemoHost.WinApp` | WinUI 3 + WebView2 + Native AOT host (the only host): `SimulationHost` in-process, shared-buffer publish, `LocalAssetServer`, dispatcher-timer pump |
| `src/BepuDemos.UI` | Babylon.js v9 frontend workspace package (`bepu-demos-ui`): 30-demo menu, demo scenes, thin-instance rendering, zero-copy decoders, GUI command buttons |
| `src/Game.BepuDemos.Tests` | xUnit v3: ABI pins, demo behavior, determinism across runs |
| `src/Game.BepuDemos.Tests.Aot` | TUnit AOT/trim pattern checks over the demo closure |
| `src/Game.Tests.UI` | Playwright E2E over WebView2 CDP + committed screenshots (`docs/screenshots/`) |
| `docs/compat-review.md` | Per-demo status + documented deviations (authoritative) |
| `docs/ai-agents/codebase-truth.md` | Verified commands, port recipe, API facts |
| `Temp/` | Upstream BepuPhysics2 sources — reference only, never part of the build |

**Demo status: 24/30 ported** (P1–P2c done; P3 mesh demos pending).
Menu shows 24 live cards, 6 disabled placeholders.

---

## Commands

Build frontend assets before .NET commands. Do not run multiple `dotnet` commands concurrently.

```powershell
npm ci && npm run build                  # Vite bundle -> src/BepuDemos.UI/wwwroot/dist
dotnet build bonoboBepuDemos.slnx
dotnet run --project src/Game.BepuDemos.Tests        # xUnit v3 (76 tests)
dotnet run --project src/Game.BepuDemos.Tests.Aot    # TUnit AOT pattern checks
dotnet publish src/DemoHost.WinApp/DemoHost.WinApp.csproj -c Release -r win-x64 -p:Platform=x64
npm run test:e2e                         # Playwright over WebView2 CDP (28 tests, screenshots)
```

- `dotnet test` reports **"Zero tests ran"** on this SDK (MTP quirk) — run the test app projects
  directly. xUnit filters work with `-class "Game.BepuDemos.Tests.PortedDemoTests"`.
- E2E publish path: `src/DemoHost.WinApp/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish/`.
  Republish after any C# or bundle change or the suite silently tests the old build
  (`GAME_WINAPP_PUBLISH=1 npm run test:e2e` forces it; `GAME_WINAPP_REUSE=1` attaches to a running app).
- `playwright-cli` cannot attach to the WebView2 host (no CDP flag on the Debug path); the
  checked-in Playwright suite is the verification tool.
- After touching frontend assets, kill a running `DemoHost.WinApp.exe` before republishing
  (file locks).

---

## Adding one demo (7 touchpoints)

1. `src/Game.BepuDemos/Demos/<Name>Demo.cs` — implement `IDemoSimulation` (+ `IDemoCommands`
   / `IFireBallSink` as needed) with a unique `[a-z0-9-]+` game key in `CreateModule()`;
   `BufferCapacity = SignalBuffer.HeaderLength + records * SignalBufferLayout.Transform3DStride`;
   document render-id ranges in the class doc comment.
2. Register in `src/Game.BepuDemos/BepuDemosModules.cs` (static glue, no reflection).
3. Scene: `src/BepuDemos.UI/src/scenes/<key>/scene<Name>.ts` (copy `sceneBounciness.ts`; use
   `createGround` + `createShapeSet`) + `SCENES` entry in `src/BepuDemos.UI/game.ts`.
4. Menu card: set `key` in `MENU_ITEMS` (`src/BepuDemos.UI/src/scenes/menu/sceneMenu.ts`).
5. Unit tests: behavior test + `[InlineData("<key>")]` in
   `PortedDemoTests.PortedSet_DeterministicAcrossRuns` + factory switch.
6. E2E: `SCENES` + `HostWindow` hook in `src/Game.Tests.UI/tests/demos.spec.ts`; menu counts in
   `menu.spec.ts` (currently `{ total: 30, live: 24, placeholders: 6 }`).
7. Docs: `docs/compat-review.md` status row, `README.md` scene table, `plan.md` counts,
   `docs/ai-agents/codebase-truth.md` when a new API fact is learned.

E2E contract: every scene sets `window.__<camelKey>()` returning at least `{ visibleInstances }`
(asserted before the screenshot to `docs/screenshots/<game-key>.png`); records are routed by
render-id range, never by arrival order.

---

## Agent rules

- Do not change the target framework, SDK, or language version unless explicitly requested.
- Do not add packages unless explicitly requested (Babylon `@babylonjs/*` extensions are
  pre-approved).
- Do not expose secrets, credentials, tokens, or connection strings.
- Do not introduce fallback parameters/methods/actions unless the human confirms them first.
- Do not make implementation assumptions — confirm unknowns with the human before coding.
- Never commit `bin/`, `obj/`, `node_modules/`, `wwwroot/dist` or other generated output.
- Only commit when explicitly asked.
