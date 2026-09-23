# Agent Directives: bonoboengine.wasm.3D Architecture

**Directive:** Agents must never implement per-entity draw calls, individual DOM queries, or inefficient JSON string serialization loops for 3D rendering. All high-frequency 3D transformation data must utilize zero-copy memory buffers, mapping pinned C# transform structures directly to Babylon.js mesh transforms via a single `Float64Array` view — over the WebAssembly heap (`localHeapViewF64`) in the browser host, or over a WebView2 shared buffer (`PostSharedBufferToScript`) in the desktop host.

**Directive:** Agents must never serialize entity transform states into JSON, UTF-8 strings, or managed array clones during high-frequency execution loops. All spatial coordinates, velocities, and rotation data must cross the C#↔JS boundary using raw, pinned memory pointers or shared memory buffers exported by C# interop.

---

## Project Context

* **Language:** C# 14 / .NET
* **Framework:** .NET MAUI / WASM Native AOT / WinUI 3 + WebView2 (Windows desktop)
* **ECS Backend:** BonoboECS (Pure C# zero-allocation component architecture)
* **Physics Backend:** `BepuPhysics2` (authoritative 3D simulation loop; deterministic single-threaded solve — null `ThreadDispatcher` — on the threaded browser-wasm runtime)
* **Render Frontend:** Babylon.js v9 (3D WebGL2/WebGPU canvas renderer)
* **UI Overlay:** Tailwind CSS + Vite + TypeScript
* **Target Environment:** Native AOT / WebAssembly (WASM) + Native AOT / win-x64 (WinApp)

---

## Architectural Rules

### AOT_STRICT_COMPLIANCE

* **Description:** Production binaries must compile via Native AOT. Zero runtime reflection or dynamic IL generation is permitted outside of `#if DEBUG`.
* **Enforcement:** Wrap all `System.Reflection` usage strictly inside `#if DEBUG` ... `#endif` blocks or leverage C# source generators.

### PHYSICS_MEMORY_STRICT

* **Description:** `BepuPhysics2` simulation loops must maintain strict memory hygiene to prevent garbage collection pressure and ensure deterministic execution.
* **Enforcement:** Strictly utilize preallocated buffers (Bepu `BufferPool` + `Buffer<T>`) and struct-based component patterns within BonoboECS. Never instantiate managed reference types (`class`) inside hot physics or simulation tick loops. Never pass a `ThreadDispatcher` to `Simulation.Timestep` (null = deterministic single-threaded solve). Threading **is** enabled in browser builds (`WasmEnableThreads=true`, AOT only), so the rule is now about deterministic solves, not about the absence of a thread pool: managed code already runs on the runtime's deputy thread, and no C# → JS callback may be reintroduced on the per-frame path.

### ZERO_COPY_INTEROP_MANDATE

* **Description:** Single-player and local-buffer rendering states must cross the C#↔JS boundary using zero-copy pinned memory buffers on every host.
* **Enforcement:** Allocate transformation buffers using `GCHandle.Alloc(..., GCHandleType.Pinned)` in C# (`PinnedRenderBuffer<T>`, `T` = `float` or `double`) with a **fixed capacity** (never re-pin — the address is handed to script once), and expose the same memory to script:
  * **Game.Wasm (browser):** `GetSignalPointer`/`GetSignalCapacity` `[JSExport]`s report the pinned address; `js/wasm-interop.js` projects a `Float64Array` view over `runtime.localHeapViewF64().buffer`; never re-encode or copy.
  * **Game.WinApp (desktop):** write the pinned span into a `CoreWebView2SharedBuffer` and call `PostSharedBufferToScript(..., ReadOnly, metadata)`; script reads the same mapping as `Float64Array` via `sharedbufferreceived` and calls `chrome.webview.releaseBuffer` after dispatch. Note: `PostWebMessageAsArrayBuffer` does **not** exist in the WebView2 API surface.
  * Never JSON-serialize transforms, never clone arrays, never make per-entity interop calls.
  * The threaded browser runtime forbids `[JSImport]` on the deputy thread and rejects synchronous `[JSExport]` from JS: all bridge verbs are `Task`-returning and the hot path is shared memory only. Never reintroduce a `notifyRender`-style C# → JS callback.

### BABYLON_MESH_TRANSFORM_ALIGNMENT

* **Description:** Babylon.js v9 entity rendering must be driven by batched transform signals or shared-memory float arrays rather than per-entity JavaScript interop calls.
* **Enforcement:** Ensure C# 3D transform structures (`X`, `Y`, `Z`, rotation quaternion, `ScaleX/Y/Z`) align with Babylon mesh expectations. Update mesh positions/rotations in batch loops synchronized with render frames. Use `Mesh.instantiate` / thin instances (`thinInstanceSetBuffer`) for large entity counts (see `.agents/skills/babylonjs`).

### FLOAT_LAYOUT_SYNC

* **Description:** The C# signal layout (`SignalBufferLayout` strides + scalar sizes in `Game.Engine.ECS.SignalBuffer.cs`) and the TypeScript decoders (generated `Frontend/generated/signalLayout.ts` + per-scene `EntityDecoder`s) must never drift.
* **Enforcement:** Mark every render-state record struct with `[TypeScriptExport(elementStride)]`, declaring `Precision = ScalarPrecision.Float64` (default, 8-byte elements) — the shared-memory ABI is pure 64-bit, there is no scalar-size field or per-signal width. The `Game.Engine.Generators` project validates it three ways: a Roslyn analyzer errors on stride mismatch (`BNOBO001`) and unsupported field types (`BNOBO002`), warns on 64-bit integers in float64 structs (`BNOBO003`); an incremental source generator emits `GeneratedSignalLayout` (`*Stride`, `*ScalarSize`, `*ByteLength`) + a `[ModuleInitializer]` static assert that cross-checks them against `SignalBufferLayout` at boot (WASM and WinApp); the same generator writes the TypeScript half (`ScalarArray`, `ScalarSizes`, per-struct strides/scalar sizes). Never hand-maintain stride or scalar-size numbers anywhere else. The 3D transform layout (position + quaternion + scale + lifecycle flag, `Transform3DState`, stride 12) must be used for 3D entities. The host-scope clock block (`GlobalClockState`, stride 8, `"globals"` signal: sim time, delta, step count, paused, interpolation alpha) is written by `SimulationHost` on every tick and is the only sanctioned source for shader time uniforms / render interpolation — see `docs/architecture/global-clock.md`.

### INPUT_RING_BUFFER_STRICT

* **Description:** High-frequency player input (clicks, taps, continuous intents) must cross the JS → C# boundary through the single pinned input ring (`Game.Engine.Inputs.InputRingLayout`, owned by `SimulationHost`), never through a per-input `[JSExport]`, JSON payload, or scene-owned `postMessage` loop.
* **Enforcement:** Packet structs live in `Game.Engine/Inputs`, are marked `[WasmInputPacket(id)]`, carry only `double` fields (≤ `InputRingLayout.SlotSize - 1`) and ride one 8-double record: slot 0 = type id, slots 1..7 = fields. The JS producer writes the pinned `Float64Array` (WASM heap) or the `ReadWrite` WebView2 shared mapping (`SharedInputChannel`, desktop) and publishes with `Atomics.store` on the pinned head counter; the browser host drains it inside `SimulationHost.Tick` (drop-oldest clamp, never blocks) and WinApp polls the head from its `DispatcherQueueTimer` and routes via `SimulationHost.ProcessInputRecord` — never a per-input `postMessage`. Decoded packets flow through the generated `IWasmInputSink`. Host-side counters (`ProcessedInputCount`/`DroppedInputCount`) ride the `"globals"` clock block slots 6–7 and are the sanctioned E2E observability surface (`window.__engineClock().processedInputs/droppedInputs`). The Roslyn generator is decode-only — it must never touch `BonoboECS.Core.World` (render ids are presentation handles, not entity handles; simulations resolve them through their own id maps). `Game.ConfigBuilder` writes the frontend layout (`Frontend/generated/InputLayouts.ts`) from the generator's reflection-free registry; never hand-edit that file, never hand-write slot offsets/indexes in TypeScript, and never add `System.Reflection` to ConfigBuilder. See `docs/architecture/input-ring-buffer.md`.

### GAME_MODULE_ISOLATION

* **Description:** The engine (`Game.Engine`) never knows about a game. Each game is a separate C# module project plus a separate frontend workspace package; the hosts register games through generated, static glue.
* **Enforcement:** A game simulation implements `Game.Engine.Simulations.IGameSimulation`, is marked `[GameModule("key")]` and exposes `public static GameModule<TSignal> CreateModule()`; the `GameModuleGenerator` (BNOBO010 missing `IGameSimulation`, BNOBO011 missing factory, BNOBO012 duplicate key, BNOBO013 invalid key) emits `Add&lt;Assembly&gt;Modules(this EngineBuilder)` from the declaring assembly. Hosts build `new SimulationHost(notify, new EngineBuilder().AddGameDemosModules()...)` with direct calls only — **never** reflection, assembly scanning, `Activator.CreateInstance`, or hardcoded game keys in `SimulationHost`/`WasmInterop`/`MainPage`. Never edit engine code to add a game; demo games live in `src/Game.Demos` and the starter template in `src/Game.MyAdventure` + `src/Game.MyAdventure.UI`. Frontend game packages import only the `@bonoboengine/core` barrel (never relative paths into another package) and produce their own host-compatible bundle, selectable with `-p:GameUIBundleSourceDir=...`. `[WasmInputPacket]` types stay in `Game.Engine/Inputs` (engine ABI). Full guide: `docs/architecture/game-modules.md`.

### ECS_PHYSICS_MAPPING

* **Description:** Keep BonoboECS component data and `BepuPhysics2` rigid body states synchronized without architectural coupling.
* **Enforcement:** Store physics body handles (`BodyHandle`, struct component `PhysicsBody`) inside BonoboECS. Use dedicated system loops to sync physics pose data directly into the pinned rendering transform array. Contact events surface through `INarrowPhaseCallbacks` (`AllowContactGeneration` filtering + `ConfigureContactManifold` begin-touch accumulation, resolved after `Timestep`).

### AUDIO_PIPELINE

* **Description:** Audio is presentation-layer only: sounds that belong to a mesh or a client physics event are attached in Babylon (`sound.spatial.attach(mesh)`, client-side detection; impact points may use a small round-robin spatial-emitter pool); only mesh-less simulation state (scene-load alerts, wave/objective events) crosses the boundary — through the host-scope `"audio"` event ring, never through per-sound interop, JSON, or DOM events.
* **Enforcement:** `SimulationHost` owns the host-scope `"audio"` `PinnedRenderBuffer<double>` ring (16 events × stride 5: `seq`, `soundId`, `volume`, `channel`, `extra`; created in the constructor like `"globals"`, so it also serves scene keys with no C# simulation) and writes it via `EnqueueAudioEvent(SoundIds, volume, channel)`; `[TypeScriptExport(5)] AudioEventState` + `SignalBufferLayout.AudioEvent*` are asserted at boot, indexes/strides are imported from `generated/signalLayout.ts` (never hand-written). Consumers (`Frontend/audio/SimulationAudioBridge.ts`) play only records with `seq > lastSeq` — the ring drops oldest, replays never happen. `SoundIds` (`Game.Engine.Audio`) is the single source of truth: `SoundIdGenerator` emits the reflection-free `GeneratedSoundIds` registry and `Game.ConfigBuilder` mirrors it into `Frontend/audio/generated/soundIds.ts` (never hand-edit). Engine ownership of alerts is mandatory: the UI reports scene-load success/failure via the `SceneLoadedInput` input packet (`[WasmInputPacket(3)]`) and the engine emits `SoundIds.SceneLoaded`/`SceneLoadWarning`; the UI never plays the alert itself. The audio engine (`AudioV2`) is created **only inside a user gesture** (`AudioManager.ensureInitialized()` from a pointerdown/keydown bootstrap; `disableDefaultUI: true`), buses are wired through `outBus` (no raw-node access — Babylon 9 has no public `getAudioNode`/filter), real-time volume changes use ramps via `setVolume(v, { duration })`, and a failed asset load is a single `console.warn` per path (never `console.error`; the E2E suites require an error-free console). The toggle is a Babylon GUI button that only mutes buses and plays a UI sound. See `docs/architecture/audio-system.md`.

### SPATIAL_INDEX_STRICT

* **Description:** ECS-side proximity queries (radius/AABB candidate sets, localized forces, area effects) must use the engine's flat-array spatial hash grid (`Game.Engine.Spatial.SpatialHashGrid`), never `Dictionary<Vector3, List<Entity>>`, per-step `List`/LINQ scans, or per-query full-world iteration. `BepuPhysics2`'s broadphase stays authoritative for collision detection and physics raycasts; the grid only accelerates ECS-layer queries.
* **Enforcement:** Fixed-capacity preallocated `SpatialNode[]`/`int[]` arrays; one sequential `Clear()` + `Insert()` rebuild phase per fixed step (deterministic chunk order, single writer) followed by read-only `QuerySphere`/`QueryAabb` calls that write into caller-owned `Span<Entity>`s (zero allocation per step, no interop, no `unsafe`, overflow fails fast). Never run the grid build inside `ParallelQuery` or write grids from multiple threads; parallel consumers are a measured, gated follow-up, not a default.

### LOOP_DECOUPLING_RULE

* **Description:** Keep the fixed-step physics engine completely isolated from variable-step render ticks.
* **Enforcement:** Execute `BepuPhysics2` steps and BonoboECS updates inside a deterministic fixed-timestep accumulator loop — `SimulationHost.Tick(deltaSeconds)` owns it (1/60 s steps, catch-up cap, pause short-circuit) and every host pumps it: the browser host once per animation frame (in-flight gated), WinApp from a `DispatcherQueueTimer`. Simulations own no timers and expose `Step(dt)`; never tie simulation or physics updates directly to browser `requestAnimationFrame` callbacks.

---

## WebAssembly Architecture Rule: Hybrid Web UI and C# Core Engine

This architectural specification defines the separation of concerns, interop patterns, and execution lifecycles for a high-performance 3D C# WebAssembly game engine utilizing **Babylon.js v9**, **BonoboECS**, **BepuPhysics2**, and a **TypeScript / Tailwind CSS DOM overlay**.

---

### 1. Core Architectural Boundaries

The engine separates performance-critical simulation logic from presentation and interface layers by establishing two distinct execution domains:

* **The Engine Core (C# / WASM / Native AOT):**
* Executes the BonoboECS tick loop, component memory updates, math processing, and `BepuPhysics2` rigid body dynamics.
* Writes transformation state directly into pinned unmanaged memory buffers or batched render signals.
* Acts as the single authoritative source of truth for all gameplay logic.

* **The Presentation and UI Domain (TypeScript / Babylon.js v9 / Tailwind CSS):**
* Executes inside the browser JavaScript runtime.
* Reads transformation matrices and coordinates from the shared WASM heap to update Babylon meshes, cameras, and scene graph.
* Renders HTML/CSS user interface overlays (inventories, health bars, HUDs) above the canvas using Tailwind CSS.

---

### 2. Interop Communication Standards (`[JSImport]` / `[JSExport]`)

To minimize serialization latency and prevent garbage collection pressure across the WebAssembly boundary, all communication must adhere to strict rules:

* **Zero polling rule:** Polling state across the interop boundary per frame via JSON or string serialization is strictly prohibited. Communication must be **event-driven, shared-memory bound, or streamed via batched deltas**.
* **Shared-heap transform bridge:** High-frequency transform updates occur with zero interop overhead by allowing JavaScript `Float64Array` views to read pinned C# transform buffers directly from the WASM memory heap (`localHeapViewF64`), or from a WebView2 shared buffer on desktop.
* **Primitive-first events:** Low-frequency events (e.g., UI interactions, entity spawning) must only pass primitive types (`int`, `float`, `bool`) using `[JSImport]` and `[JSExport]`. Under threading every JS → C# verb is `Task`-returning (synchronous exports throw), and C# → JS `[JSImport]` is off-limits on the deputy thread.

---

### 3. Simulation Lifecycle and Pause States

When displaying complex UI overlays, inventories, or paused states, the engine short-circuits the execution loop to conserve CPU cycles and GPU resources.

```csharp
// Architectural pattern for loop halting
private static bool _isSimulationPaused = false;

[JSExport]
public static void SetSimulationPaused(bool paused)
{
    _isSimulationPaused = paused;
}

public static void MainLoopTick(float deltaTime)
{
    if (_isSimulationPaused) 
    {
        // Skip physics steps and ECS updates.
        // The canvas retains its last rendered frame beneath the DOM overlay.
        return; 
    }

    ExecutePhysicsStep(deltaTime);
    ExecuteEcsSystems();
    SyncTransformsToPinnedBuffer();
}

```

---

### 4. Architectural Comparison Matrix

| Architectural Layer | Implementation Technology | Primary Responsibility | Execution Frequency |
| --- | --- | --- | --- |
| **Simulation & Physics** | C# (BonoboECS / BepuPhysics2 / .NET WASM) | Game rules, entity states, 3D rigid body collisions and dynamics. | Locked to target tick rate (e.g., 60 Hz fixed timestep). |
| **Graphics Pipeline** | TypeScript (Babylon.js v9 / WebGL2 / WebGPU) | Mesh rendering, scene graph, cameras, particles. | Frame-synchronized with browser `requestAnimationFrame`. |
| **Complex UI Layer** | Tailwind CSS / Vite / TypeScript | Menus, HUDs, inventory grids, configuration panels. | Event-driven (DOM-rendered on demand). |
| **Shared Memory Bridge** | Pinned `GCHandle` & `Float64Array` view | Zero-copy 3D transform and coordinate synchronization (WASM heap view; WebView2 shared buffer on desktop). | Direct memory read per render frame. |

---

## Repository Shape

- `bonoboWebGame.slnx` is the solution; projects target .NET 10.
- `src/Game.Engine` is a plain C# class library. Keep engine logic independent of UI and platform code. It hosts the game-module seam (`Game.Engine.Simulations`: `IGameSimulation`, `GameModule<T>`, `EngineBuilder`, `GameModuleAttribute`), the pinned-memory signal machinery (`Game.Engine.ECS`: `SimulationHost`, `PinnedRenderBuffer<T>`, `DirectRenderTransport<TSignal,T>`, `Transform3DState`/`Transform3DRenderSignal` ABI + `SignalBuffer`), the BepuPhysics2 references, and the hand-written input ring contract (`Game.Engine.Inputs`: `WasmInputPacketAttribute`, `ClickMoveInput`, `InputRingLayout`). No game content lives here anymore.
- `src/Game.Engine.Generators` is a Roslyn analyzer + source generator project (netstandard2.0, referenced by `Game.Engine` via `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`). It enforces the zero-copy signal layout contract: the `[TypeScriptExport]` marker attribute (with `ScalarPrecision`), the `LayoutAlignmentAnalyzer` (BNOBO001 stride mismatch / BNOBO002 unsupported field type / BNOBO003 wide integers), and the `TypeScriptInterfaceGenerator` which emits `GeneratedSignalLayout` (`*Stride`, `*ScalarSize`, `*ByteLength`) + a `[ModuleInitializer]` boot-time static assert cross-checking `SignalBufferLayout`, plus the TypeScript half (`src/Game.UI/Frontend/generated/signalLayout.ts`). Game assemblies get an assembly-scoped `<Assembly>SignalLayout` + boot assert instead (declared-vs-computed stride); the engine's `signalLayout.ts` stays the single frontend ABI file. See the `FLOAT_LAYOUT_SYNC` rule. It also enforces the input ring ABI: `InputPacketAnalyzer` (BNOBO004 non-`double` field / BNOBO005 record overflow / BNOBO007 reserved id 0) and `WasmInputGenerator` (BNOBO006 duplicate ids / BNOBO008 empty names) emitting `GeneratedInputLayout` (packet registry), `IWasmInputSink` and `WasmInputDispatcher` — **only in the assembly declaring `InputRingLayout`** (game assemblies keep the diagnostics). See the `INPUT_RING_BUFFER_STRICT` rule. `SoundIdGenerator` (BNOBO009 non-`ushort` `SoundIds` underlying type) emits the reflection-free `GeneratedSoundIds` registry from `Game.Engine.Audio.SoundIds`. `GameModuleGenerator` (BNOBO010/011/012/013) collects `[GameModule]` simulations and emits the AOT-safe `Add<Assembly>Modules(this EngineBuilder)` registration — see the `GAME_MODULE_ISOLATION` rule.
- `src/Game.Demos` is the first "external customer" game module project: the demo simulations (ECS sprites, 3D transform pool + spatial hash detonators, Bepu box pile) with their components/systems/encoders, registered by the hosts through the generated `AddGameDemosModules()`. Tests reference it; the engine does not. `src/Game.MyAdventure` is the minimal starter template (one system + `[GameModule("my-adventure")]`, registered by the hosts through `AddGameMyAdventureModules()`); `src/Game.MyAdventure.UI` is its matching frontend workspace package. Copy them to start a game.
- `src/Game.UI` is a shared class library (non-Razor, plain `Microsoft.NET.Sdk`) whose `Frontend/` folder is the npm workspace package **`@bonoboengine/core`** (`src/Game.UI/Frontend/package.json`): the engine presentation core (zero-copy bridge `signalSource.ts`, `sceneRunner.ts`, `globalClock.ts`, input ring, audio, debug stats, generated tables). The class library references `Game.Engine`; the package is consumed by the game UI bundles. Its generated `wwwroot/dist` is gone — the core package owns no CSS/assets bundle. Tracked generated tables: `Frontend/generated/InputLayouts.ts` (written by `Game.ConfigBuilder`), `Frontend/audio/generated/soundIds.ts` (same tool), `Frontend/generated/signalLayout.ts` (generator). The audio subsystem lives in `Frontend/audio/` — see `docs/architecture/audio-system.md`.
- `src/Game.Examples.UI` is the default game UI workspace package (`@bonoboengine/examples`): the example scenes (`singlePlayer`, `ecs`, `boxpile`), the top-bar switcher GUI, demo shaders and Tailwind entry. It builds `wwwroot/dist/{game-bundle.js,app.css,HavokPhysics.wasm}` with its own Vite config and imports the engine only through `@bonoboengine/core`. `src/Game.MyAdventure.UI` is the starter equivalent. Hosts choose the bundle with `-p:GameUIBundleSourceDir=<pkg>\wwwroot` (default: examples).
- `src/Game.ConfigBuilder` is the console tool that converts `config/world.json` into the blittable `Game.UI/wwwroot/assets/config.bin` (magic `BNBO`, version 1) and mirrors the generator's input packet registry into `Game.UI/Frontend/generated/InputLayouts.ts` plus the sound-id registry into `Game.UI/Frontend/audio/generated/soundIds.ts` (reflection-free). Both hosts load the same config bytes: browser `main.mjs` fetches them and calls the `LoadConfiguration` `[JSExport]`; WinApp reads `wwwroot/assets/config.bin` from disk. Reader: `Game.Engine.Config.BinaryConfigReader` (`MemoryMarshal` only — AOT-safe).
- `src/Game.UI/Game.UIAssets.targets` is the shared MSBuild asset pipeline imported by both hosts. It copies the engine assets from `GameUICoreSourceDir` (default `src/Game.UI/wwwroot`) and the bundle from `GameUIBundleSourceDir` (default `src/Game.Examples.UI/wwwroot`), prunes destinations before every copy, fails fast when `dist/game-bundle.js` is missing, and supports `-p:BuildFrontend=true` (runs `npm run build` at the repo root first). Hosts pick a mode: `ProjectWwwroot` (Game.Wasm — pre-build copy into the project's wwwroot) or `OutputFolder` (Game.WinApp — copy into `$(OutDir)wwwroot` before Build and mirror into `$(PublishDir)wwwroot` after Publish; WinUI `Content` globs are evaluated at project load, so target-time copies are the only reliable path).
- `src/Game.Tests` is the xUnit v3 test project (determinism self-checks, ECS unit tests, snapshot shape). `src/Game.Tests.Aot` is the TUnit test project (AOT/trim pattern checks over the `Game.Engine` closure). Both are in the solution and run under the Microsoft.Testing.Platform runner opted in via root `global.json` — do not delete that file or `dotnet test` misbehaves on .NET 10.
- `src/Game.Tests.UI` is the Node/TypeScript Playwright E2E suite. Not a `.csproj` — run from its folder via npm. Default browser channel is installed Chrome (`channel: 'chrome'`); machines without a system Chrome build can point at a Playwright chromium via `GAME_WEB_CHROME` (see `playwright.config.ts`). Config and host setup: see `docs/testing-ui-E2E/index.md`. `home.spec.ts` asserts the Babylon demo-balls scene (canvas + WebGL2 + drawn pixels). A second config (`playwright.winapp.config.ts`, `npm run test:winapp`) covers the **desktop host** over CDP: `scripts/launch-winapp.mjs` launches the Release publish with `--remote-debugging-port` (+ no-throttle args, fresh `WEBVIEW2_USER_DATA_FOLDER`) and `tests-winapp/winapp.spec.ts` asserts the ECS scene + input ring (`GAME_WINAPP_PUBLISH=1` republishes first, `GAME_WINAPP_REUSE=1` attaches to a running app).
- `src/Game.Wasm` is the browser-wasm host (non-Blazor, `Microsoft.NET.Sdk.WebAssembly`). Threaded by default (`WasmEnableThreads=true` for AOT builds; Debug is single-threaded because the interpreter cannot host the MT runtime). Bootstraps the runtime on the page main thread via direct `import { dotnet } from './_framework/dotnet.js'` (no `blazor.webassembly.js`); managed code runs on the runtime's deputy thread. Its `SimulationHost` is built with `new EngineBuilder().AddGameDemosModules().AddGameMyAdventureModules()` (generated glue). All bridge verbs in `WasmInterop` are `Task`-returning `[JSExport]`s; `wwwroot/js/wasm-interop.js` owns the frame pump (in-flight gated rAF → `Tick`), the `Float64Array` signal views and generic `/api/{game}/{verb}` command routing (`GameCommand`). There is no `notifyRender` `[JSImport]` anymore.
- `src/Game.WinApp` is the Windows desktop host: WinUI 3 (Windows App SDK) + WebView2 + **Native AOT** (`PublishAot`, unpackaged/self-contained in Release). It references `Game.Engine` + the game modules and runs the same ECS/Bepu simulation in-process, then publishes committed buffers to the page through WebView2 **shared buffers** (`SharedBufferChannel`: `CreateSharedBuffer` + `PostSharedBufferToScript`) and receives player input through the `ReadWrite` `SharedInputChannel` input ring (head polled on the simulation timer; `docs/architecture/input-ring-buffer.md`). Page messages: `connect:{game}` | `command:{game}:{verb}` | `pause:1|0` | `input-hello`. The page shell is `wwwroot/index.html` + `wwwroot/js/webview-bridge.js`; the Babylon bundle itself is host-agnostic. Publish: `dotnet publish src/Game.WinApp/Game.WinApp.csproj -c Release -r win-x64 -p:Platform=x64`. See `docs/architecture/desktop-webview2.md`.
- `src/Game.Engine.ECS.SimulationHost` is the host-agnostic simulation control (owns the `EngineBuilder`-registered game modules; `Connect(gameKey)` creates the module's simulation + pinned signal buffer, unknown keys — e.g. the frontend-only `single-player` scene — leave the engine idle). It exposes `Connect`/`SetPaused`/`Tick`/`SendCommand` and a `BufferNotify(eventName, nint ptr, elementCount)` callback, plus `internal ActiveSimulation` for test probes. It also owns the hand-written input ring (`PinnedRenderBuffer<double>` + pinned int head, created once in the constructor) and drains it at the top of every `Tick` through the generated `WasmInputDispatcher`/`IWasmInputSink`, forwarding decoded packets to the active module when it implements `IClickMoveSink`/`IFireBallSink`; hosts read the stable addresses via `TryGetInputInfo` and expose them with `Task`-returning `[JSExport]`s (`docs/architecture/input-ring-buffer.md`). It also owns the host-scope `"globals"` clock block (fixed 8-element `GlobalClockState` buffer, created once in the constructor, republished on every `Tick`/`SetPaused`/`Connect`) and the host-scope `"audio"` event ring (16 events × `AudioEventState`, stride 5, written by `EnqueueAudioEvent`). `Tick(deltaSeconds)` runs the 1/60 s fixed-step accumulator and is pumped by each host (browser rAF pump, WinApp `DispatcherQueueTimer`); simulations expose `Step(dt)` and own no timers. The browser host passes a no-op notify (JS reads the pinned buffer itself); WinApp forwards to its shared-buffer channel. Keep the pointer host-width (`nint`) inside the engine: truncating it to `int` crashes the native x64 AOT process (`WasmInterop` narrows only for its own 32-bit WASM `[JSExport]`s).

- `src/bepuphysics2` is a **vendored** C# physics library (BepuPhysics2, Apache-2.0), **referenced** by `Game.Engine.csproj` as the authoritative physics world: `BepuPhysics` + `BepuUtilities` (net10.0, `CommonSettings.props`). Deterministic single-threaded solves (null `ThreadDispatcher`). `BonoboECS` (ECS, rebranded Arch fork) and `KinematicAI` (game AI: fuzzy utility, influence maps, perception, behavior VM, DotRecast navigation) are **NuGet packages** consumed from the internal feed (`\\172.20.176.1\devserver\Nuget`, registered in root `nuget.config`); the old vendored `src/Arch` and `src/BrainAI` trees are gone. `src/Temp/` holds upstream samples/demos — not part of the build/solution.
- The frontend is an npm **workspace root** (`package.json`): `src/Game.UI/Frontend` (`@bonoboengine/core`, Babylon.js declared there), `src/Game.Examples.UI` (`@bonoboengine/examples`) and `src/Game.MyAdventure.UI` (`@bonoboengine/my-adventure-ui`). Install/build from the repo root (`npm ci`, `npm run build`, `npm run typecheck`). The host/dev scripts live in root `scripts/` (`dev.mjs`, `serve-published.mjs`).

## Agent References

- `.agents/skills/babylonjs/` — vendored Babylon.js 9 skill (API patterns, procedural modeling, thin instances, PBR). Consult it when writing or verifying any Babylon code; prefer its API facts over memory or generic web knowledge.
- `?spector=1` URL param activates Spector.js WebGL debug overlay (on-demand dynamic import, tree-shaken from default prod bundle): `window.__spector` exposed after init.
- `docs/babylonjs/` — Babylon.js documentation subset (core concepts, meshes, materials, animation, performance).
- `docs/bepuphysics2/` — BepuPhysics2 documentation subset (Getting Started, determinism, substepping, stability, performance).
- `net-microsoft-documentation` MCP server — official, up-to-date Microsoft Learn docs for .NET, ASP.NET Core, Blazor, and MAUI. Use it for framework/API verification.
Summary of the scope an agent can search using this server:

  1. Programming Languages & RuntimesCore Languages: TypeScript, JavaScript, C#, F#, VB.NET, C++, Python, Java, Rust, PowerShell, Go.Frameworks & Runtimes: .NET Core / .NET 8+, ASP.NET Core, Node.js, React, Angular, Vue, Blazor, MAUI, WPF, WinUI.
  2. Microsoft Developer Tools & SDKsIDEs & Code Editors: Visual Studio, Visual Studio Code, Visual Studio Code Extensions (Copilot, Azure Tools).
  3. CLI & Command Line: Azure CLI (az), Azure Developer CLI (azd), PowerShell modules, Windows Terminal, WSL.
  4. SDKs: Azure SDKs across languages (Python, TypeScript, .NET, Java), Model Context Protocol (MCP) SDKs.

- `docs/game-development` — game architecture and gamedev workflow references (see `docs/index.md`).
- `docs/architecture/game-modules.md` — how to add a game: C# `[GameModule]` module + generated `Add<Assembly>Modules` glue, frontend workspace package against `@bonoboengine/core`, assets and host bundle selection.
- `docs/architecture/topology.md` — engine topology deep-dive (Implemented vs Target): three-layer runtime, WASM→JS bridge, physics, skeletal pipelines, domain matrix, ecosystem matrix, implementation status.

## Architectural Guardrails

**how data crosses the C#↔JS interop boundary** have been superseded by the migration to the **zero-copy shared memory pipeline**.

---

### Architectural Guardrails Status Matrix

| Guardrail Category | Status | Architectural Impact of the New Interop Layer |
| --- | --- | --- |
| **C# Authority & ECS / BepuPhysics2** | **Valid** | C# remains the sole authoritative simulation engine using BonoboECS and `BepuPhysics2`. Game logic and physics simulation are never executed in JavaScript. |
| **Transport Layer (`fetch` POST / SSE Streams)** | **Superseded** | The legacy SSE stream (`/api/ecs/stream` pushing JSON `SpriteState[]`) and HTTP POST render bridges are deprecated for rendering. They are replaced by direct, zero-copy `Float64Array` views over the WASM memory heap (browser) and WebView2 shared buffers (desktop). |
| **Single-Player Local Default** | **Valid** | Local-buffer builds remain the default (`SINGLE_PLAYER_LOCAL`), avoiding unnecessary network abstraction layers during single-player execution. |
| **Temporal Context & Snapshots** | **Upgraded** | Instead of serializing temporal JSON snapshots over network bridges, hot-path coordinate, rotation, and scale data stream continuously via pinned unmanaged memory pointers (`GCHandle.Alloc` + WebAssembly heap mapping). |
| **Presentation Split (Babylon.js v9)** | **Valid** | Babylon.js v9 remains strictly responsible for rendering, mesh pools, camera control, and interpolation, reading directly from the shared memory buffer without per-entity interop polling. |

---

## Commands

Build frontend assets before .NET commands. Do not run multiple `dotnet` commands concurrently; static-web-asset compression can race.

`src/Game.Examples.UI/wwwroot/dist` (and every other bundle package `dist`) is **not tracked in git** — every host copies the selected bundle at build time (`Game.UIAssets.targets`) and fails fast with an instructive error when it is missing. On a fresh clone run `npm ci && npm run build` at the repo root first (or pass `-p:BuildFrontend=true` to any host build).

Run from repository root:

```powershell
dotnet build bonoboWebGame.slnx
dotnet test          # Game.Tests (xUnit v3) + Game.Tests.Aot (TUnit); MTP runner via global.json
npm ci
npm run build        # DEFAULT: @bonoboengine/examples bundle (__RENDER_SOURCE__='local-buffer')
npm run dev          # one-terminal loop: publishes the threaded AOT site, serves it on :5902 with
                     # COOP/COEP, runs the vite/tailwind watchers, mirrors assets, republishes on C#
npm run typecheck    # every workspace package (core + examples + starter)
npm run build:web    # opt-in SSE/multiplayer bundle
```

Per-package commands run through npm workspaces, e.g. `npm run build -w @bonoboengine/examples` (default bundle) or `-w @bonoboengine/my-adventure-ui` (starter). A game's bundle is selected by the hosts with `-p:GameUIBundleSourceDir=<pkg>\wwwroot`.

The browser host ships **threaded** (`WasmEnableThreads=true`), and the multi-threaded runtime is AOT-only: a Debug/`dotnet run` build is single-threaded (interpreter). To run the threaded configuration by hand:

```powershell
dotnet publish src/Game.Wasm/Game.Wasm.csproj -c Release
node scripts/serve-published.mjs --port 5902   # served with COOP/COEP headers
```

Windows desktop host (`Game.WinApp`, WinUI 3 + WebView2 + Native AOT):

```powershell
dotnet build src/Game.WinApp/Game.WinApp.csproj                    # Debug: unpackaged dev run / F5 packaged
dotnet run   --project src/Game.WinApp/Game.WinApp.csproj          # launches the desktop window
dotnet publish src/Game.WinApp/Game.WinApp.csproj -c Release -r win-x64 -p:Platform=x64 -p:BuildFrontend=true
# → src/Game.WinApp/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/Game.WinApp.exe (+ wwwroot/)
```

The desktop host needs the Edge **WebView2 runtime** installed (ships with current Windows) and, for the Debug inner loop, **Developer Mode** enabled (the debug run registers a package identity). Its page is served from `wwwroot` by the in-process `LocalAssetServer` (loopback HTTP with COOP/COEP headers — the virtual-host mapping cannot send headers and `WebResourceRequested` never fires for virtual-host URLs) and boots the **ECS scene** by default (the native-sim demo); the top bar can switch to `Single Player`. Debug builds are not self-contained (launch via `dotnet run`/F5, or use the Release publish). Desktop dev loop + CDP checklist: `docs/architecture/dev-workflow.md`. Guarded process runner (hard timeouts, TTL kill, no orphans): `scripts/run-guarded.mjs` (see `docs/testing-ui-E2E/index.md`).

Run Playwright E2E from `src/Game.Tests.UI` (Node project; needs `npm ci` first):

```powershell
npm ci
npx playwright test        # publishes the threaded AOT site if missing, serves it with COOP/COEP on
                           # :5902, uses installed Chrome (channel: 'chrome'). GAME_WEB_PUBLISH=1 forces a fresh publish.
npm run typecheck
# Machines without a system Chrome: GAME_WEB_CHROME=/path/to/chromium npx playwright test
```

Run server for testing (non-threaded Debug host — for the threaded site use
`node scripts/serve-published.mjs` after a Release publish):

```powershell
$Env:ASPNETCORE_URLS="http://localhost:5902"

# Start the server and save its Process ID (PID):
$ServerProcess = Start-Process dotnet -ArgumentList "run --project ../../src/Game.Wasm --no-launch-profile" -NoNewWindow -PassThru

# Wait for startup and check status:
Start-Sleep -Seconds 20
curl -s -o /dev/null -w "%{http_code}" http://localhost:5902/

# Automatically stop the server at the end or if stuck:
Stop-Process -Id $ServerProcess.Id -Force

# Targeting the Port Directly (Safest) without affecting other tasks: 
Get-NetTCPConnection -LocalPort 5902 | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
```

Note: Native console commands that hang cannot always be interrupted programmatically without killing the underlying pipeline host. If you want to force specific scripts inside your project to never hang indefinitely, you should wrap your calls inside the native Wait-Job wrapper:

```powershell
$job = Start-Job -ScriptBlock { <# Your Long Command Here #> }
$result = Wait-Job $job -Timeout 300
if ($null -eq $result) { Stop-Job $job; Write-Error "Command Timed Out!" }
```

```bash
export ASPNETCORE_URLS=http://localhost:5902

# Start the server and save its Process ID ($!)
dotnet run --project ../../src/Game.Wasm --no-launch-profile > /dev/null 2>&1 &
SERVER_PID=$!

# Wait for startup and check status
sleep 20
curl -s -o /dev/null -w "%{http_code}" http://localhost:5902/

# Automatically kill the server process at the end
kill -9 $SERVER_PID
```

**⚠️ ESM constraint:** `src/Game.Tests.UI/package.json` declares `"type": "module"`. Any standalone `.js` script written in that directory MUST use ESM `import` syntax (not `require()`). Use `.cjs` extension for CommonJS, or run scripts from the repo root. See `docs/testing-ui-E2E/index.md` §Standalone Screenshot Scripts for the corrected pattern (process lifecycle, path resolution, HTTP readiness polling).

For exploratory agent-driven browser work use the `playwright-cli` skill with Chrome: `playwright-cli open <url> --browser=chrome`. A Playwright MCP server is NOT needed — skills + playwright-cli + the checked-in Playwright suite cover this repo (verdict + rationale in `docs/testing-ui-E2E/index.md`).

Game UI packages build Vite JavaScript first, then Tailwind CSS (`npm run build` at the root builds `@bonoboengine/examples`). Vite reads the package's `game.ts` and writes generated files to that package's `wwwroot/dist`; do not hand-edit generated output. `npm run watch:js` and `npm run watch:css` are separate long-running watchers.

MAUI builds require .NET MAUI workloads. Platform-specific target frameworks may make full-solution builds depend on host OS and installed workloads.

## Verification Notes

- Test projects: `Game.Tests` (xUnit v3), `Game.Tests.Aot` (TUnit), `Game.Tests.UI` (Playwright, Node-only, not in the solution). Full guide: `docs/testing-ui-E2E/index.md`. `Game.Maui` is temporarily commented out of the solution (web-only builds for speed).
- Playwright E2E covers the **browser-wasm host** (published threaded AOT site served by `scripts/serve-published.mjs`, COOP/COEP on every response) and the **desktop host** (`npm run test:winapp`: attaches over CDP to the Release publish launched by `scripts/launch-winapp.mjs`, following the official Playwright WebView2 pattern). The Debug `dotnet run` path activates the packaged app and does not inherit shell env vars, so CDP requires the direct-exe Release launch; `F12` works in Debug too. `dotnet publish` does not copy the WinUI `*.xbf` + `Game.WinApp.pri` files — `Game.WinApp.csproj` mirrors them after Publish; without them the unpackaged app dies at `Application.LoadComponent` (XAML parsing failed, `0xC000027B`).
- After touching frontend assets, kill any running `Game.Wasm.exe`/`Game.WinApp.exe` before rebuilding (file locks). The asset targets prune `dist`/`assets`/`audio`/`games` before every copy, so stale-chunk 500s are now a host-process problem, not a stale-output problem.
- `bin/`, `obj/`, `node_modules/`, every bundle package's `wwwroot/dist/`, and the host-side copies of `dist`/`assets`/`audio`/`games`/`background.png` are ignored. Do not commit them.
- Regenerate `config.bin`, the frontend input layout and the sound-id table after editing `config/world.json`, adding/reordering `[WasmInputPacket]` fields, or changing `Game.Engine.Audio.SoundIds`: `dotnet run --project src/Game.ConfigBuilder` (writes `Game.UI/wwwroot/assets/config.bin` + `Game.UI/Frontend/generated/InputLayouts.ts` + `Game.UI/Frontend/audio/generated/soundIds.ts`), then rebuild the host. Missing `config.bin` is not fatal — both hosts fall back to `GameWorldConfig.Default`; a stale `InputLayouts.ts` / `soundIds.ts` is caught by the `Game.Tests.Aot` tripwires.
- **Stale-publish trap (browser E2E):** `serve-published.mjs` serves the *existing* Release publish unless `--publish` is passed, and Playwright reuses a server already listening on :5902 (`reuseExistingServer`). A stale `node serve-published.mjs` from an earlier session silently tests yesterday's bundle — kill listeners on :5902 first, then republish (`GAME_WEB_PUBLISH=1 npx playwright test`, or `dotnet publish src/Game.Wasm/Game.Wasm.csproj -c Release`). If the publish fails with "asset … can not be found" for pruned Vite chunks, delete `src/Game.Wasm/obj/Release` + `src/Game.Wasm/bin/Release` and republish (stale static-web-asset compression cache).
- **Audio verification is human-only** (sound output cannot be asserted by Playwright): the checklist lives in `docs/architecture/audio-system.md` §Verification, observability hook is `window.__audio()` (engine state, toggle, plays, `loadFailures`, bridge watermark, FX state).
- Trust `.csproj`, `.slnx`, `package.json`, and executable build output over setup prose in `README.md`.
- `docs/index.md` describe architecture; `docs/ai-agents/codebase-truth.md` holds verified API facts; record significant.
- **Bepu on WASM:** never pass a `ThreadDispatcher` to `Simulation.Timestep` (null = deterministic single-threaded solve; a dispatcher is now *possible* under threading but is deliberately not wired until an authoritative solver exists and profiling justifies it). Threading is the default execution model: `WasmEnableThreads=true` on AOT builds only — the threaded Debug/interpreter build fails at boot with `mono runtime and class libraries are out of sync` / `mono_wasm_start_deputy_thread_async() failed`. The box-pile demo mirrors authoritative Bepu poses into ECS components after each null-dispatcher `Timestep` (see `Game.Demos.BoxPileEcsSimulation`).
- **WebView2 API facts:** `PostWebMessageAsArrayBuffer` does not exist — binary data crosses via `PostSharedBufferToScript` (shared memory) and JS must call `chrome.webview.releaseBuffer` after dispatch. The WinRT projection drops parameter names, so those calls take positional arguments. `PublishReadyToRun` must stay disabled whenever `PublishAot=true`.

## Agent Rules

- Do not change target framework, SDK, or language version unless explicitly requested.
- Do not add packages unless explicitly requested. Exception: official Babylon.js extensions (`@babylonjs/*` scoped packages) needed for the task are approved by default; every other package still needs explicit human confirmation.
- Changes stay inside this repository. The Bonobo engine repo (`X:\PROJECTS\Repos\bonoboengine.wasm.3D`) is reference material only — never edit it; this repo mirrors the engine ABI locally (self-contained) instead of referencing engine projects.
- Do not expose secrets, credentials, tokens, or connection strings.
- Do not introduce fallback parameters, methods, actions, or workflows unless the human explicitly confirms them first.
- Do not make implementation assumptions. Confirm unknowns with the human before coding.

### SHELL_TIMEOUT_600
***Enforcement:** Always run any shell command with a timeout of 300 seconds (300000 ms).