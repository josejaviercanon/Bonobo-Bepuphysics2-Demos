---
name: babylonjs
description: "Babylon.js 9 3D engine development expertise with curated API patterns, code examples, and on-demand documentation access. Use when working with Babylon.js scenes, meshes, materials (PBR/OpenPBR/Standard), cameras (including GeospatialCamera), lights (Clustered Lighting, Textured Area Lights), shadows (Dynamic IBL), GUI (2D/3D), animations (retargeting, Flow Maps), physics (Havok), thin instances, glTF/Gaussian Splat/3D Tile loading, post-processing, Frame Graph, WebXR, procedural/code-built 3D models, or any 3D rendering task. Covers: (1) Scene setup and engine initialization (WebGPU/WebGL2), (2) Mesh creation, transforms, instancing, and merging, (3) PBR and OpenPBR materials with textures, (4) Camera types (ArcRotate, Universal, Follow, Geospatial), (5) Lighting and shadows (Clustered Lighting, IBL), (6) 2D/3D GUI with AdvancedDynamicTexture, (7) Animation system, retargeting, and Node Particle Editor, (8) Asset loading (glTF, OBJ, STL, Splats, 3D Tiles), (9) Performance optimization, Frame Graph, and Large World Rendering, (10) ASRS/warehouse digital twin visualization patterns, (11) Procedural modeling: building high-quality 3D models from primitives with animation-ready parent/pivot hierarchies, CSG2 booleans, lathes, extrudes, and custom VertexData."
---

# Babylon.js 9

## Quick Reference

Babylon.js is a powerful open-source 3D engine for the web. Version 9 is the largest release to date, bringing heavy advancements in rendering capabilities (WebGPU & WebGL2), clustered lighting, and large-world/geospatial support.

**Coordinate system:** Left-handed (X=right, Y=up, Z=forward). Rotations in radians.

**NPM packages:**
- `@babylonjs/core` - Engine, scene, meshes, materials, cameras, lights, Frame Graph
- `@babylonjs/gui` - 2D/3D GUI controls
- `@babylonjs/loaders` - glTF, OBJ, STL loaders, splats
- `@babylonjs/materials` - Extra material types
- `@babylonjs/inspector` - Debug inspector (Inspector v2)

**Tree-shaking:** Import from deep paths for minimal bundles:
```typescript
import { Scene } from "@babylonjs/core/scene";
import { Vector3 } from "@babylonjs/core/Maths/math.vector";
import { Color3, Color4 } from "@babylonjs/core/Maths/math.color";
import { Mesh } from "@babylonjs/core/Meshes/mesh";
import { PBRMaterial } from "@babylonjs/core/Materials/PBR/pbrMaterial";
import { ArcRotateCamera } from "@babylonjs/core/Cameras/arcRotateCamera";
import { ClusteredLightContainer } from "@babylonjs/core/Lights/clusteredLightContainer";

```

**Side-effect imports** (enable features without referencing exports):

```typescript
import "@babylonjs/core/Meshes/thinInstanceMesh";
import "@babylonjs/core/Rendering/edgesRenderer";
import "@babylonjs/core/Collisions/collisionCoordinator";
import "@babylonjs/loaders/glTF/2.0/glTFLoader";

```

## Minimal Scene Setup

```typescript
import { Engine } from "@babylonjs/core/Engines/engine";
import { Scene } from "@babylonjs/core/scene";
import { ArcRotateCamera } from "@babylonjs/core/Cameras/arcRotateCamera";
import { HemisphericLight } from "@babylonjs/core/Lights/hemisphericLight";
import { Vector3 } from "@babylonjs/core/Maths/math.vector";
import { CreateSphere } from "@babylonjs/core/Meshes/Builders/sphereBuilder";
import { CreateGround } from "@babylonjs/core/Meshes/Builders/groundBuilder";

const engine = new Engine(canvas, true);
const scene = new Scene(engine);
const camera = new ArcRotateCamera("cam", 0, Math.PI/4, 10, Vector3.Zero(), scene);
camera.attachControl(canvas, true);
const light = new HemisphericLight("light", new Vector3(0, 1, 0), scene);
const sphere = CreateSphere("sphere", { diameter: 2 }, scene);
const ground = CreateGround("ground", { width: 6, height: 6 }, scene);
sphere.position.y = 1;

engine.runRenderLoop(() => scene.render());
window.addEventListener("resize", () => engine.resize());

```

## Key Patterns

### Clustered Lighting (Babylon 9 Forward+)

Crucial for scenes with hundreds or thousands of point/spot lights without tanking performance.

```typescript
import { PointLight } from "@babylonjs/core/Lights/pointLight";
import { ClusteredLightContainer } from "@babylonjs/core/Lights/clusteredLightContainer";

// Important: pass 'true' for dontAddToScene when creating clustered lights
const light1 = new PointLight("pt1", new Vector3(0, 1, 0), scene, true);
const light2 = new PointLight("pt2", new Vector3(2, 1, 0), scene, true);

const clusteredLights = new ClusteredLightContainer("clustered", [light1, light2], scene);
// Additional tuning available (e.g., clusteredLights.verticalTiles)

```

### PBR Material & OpenPBR (Alpha)

```typescript
const pbr = new PBRMaterial("pbr", scene);
pbr.albedoColor = new Color3(1.0, 0.766, 0.336);
pbr.metallic = 0.3;
pbr.roughness = 0.7;
pbr.reflectionTexture = scene.environmentTexture; // Dynamic IBL Shadows supported in V9
mesh.material = pbr;
// Note: Babylon 9 introduces experimental OpenPBR materials bridging standardized material pipelines.

```

### Thin Instances (high-performance batching)

```typescript
import "@babylonjs/core/Meshes/thinInstanceMesh";
const buffer = new Float32Array(16 * count);
for (let i = 0; i < count; i++) {
  Matrix.Translation(x, y, z).copyToArray(buffer, i * 16);
}
mesh.thinInstanceSetBuffer("matrix", buffer, 16, false);
mesh.thinInstanceBufferUpdated("matrix");

```

### Asset Loading (glTF & Splats)

```typescript
import "@babylonjs/loaders/glTF/2.0/glTFLoader";
// Babylon 9 supports Advanced Gaussian Splats alongside standard formats
const container = await BABYLON.LoadAssetContainerAsync("model.glb", scene);
container.addAllToScene();

```

### Procedural Modeling — Animation-Ready Hierarchy

Build complex models from primitives by placing a `TransformNode` at every joint *before* attaching geometry. One pivot per degree of freedom; geometry's local position is its offset from the joint.

```typescript
const robotRoot = new TransformNode("robotRoot", scene);
const shoulderPivot = new TransformNode("shoulderPivot", scene);
shoulderPivot.parent = robotRoot;                  // pivot lives at the joint axis
const upperArm = CreateCapsule("upperArm", { height: 1.2, radius: 0.16 }, scene);
upperArm.parent = shoulderPivot;
upperArm.position.y = 0.6;                          // geometry sticks out from joint
// Animation later: shoulderPivot.rotation.y = ...  // rotates whole arm cleanly

```

## Reference Files

Read these files for detailed API patterns on specific topics:

* **[core-concepts.md](references/core-concepts.md)** - Engine/Scene setup, cameras, lights, shadows, observables, coordinate system
* **[meshes.md](references/meshes.md)** - Mesh builders, transforms, TransformNode, instances, thin instances, clones, merging, picking
* **[procedural-parametric-modeling.md](references/procedural-parametric-modeling.md)** - Building high-quality 3D models in code from primitives. Full primitive catalog (lathe, tube, extrude, polyhedra, geodesics, CSG2 booleans, custom VertexData), quality techniques (tessellation, bevels, edges, material palettes), and a complete animation-ready parent/pivot hierarchy pattern.
* **[materials.md](references/materials.md)** - PBR, Standard, textures, environment/HDR, Node Material, Shader Material
* **[gui.md](references/gui.md)** - AdvancedDynamicTexture, all control types, containers, layout, events
* **[animation-loading.md](references/animation-loading.md)** - Animation API, groups, easing, skeletal animation, asset loading, AssetContainer
* **[performance.md](references/performance.md)** - Scene/mesh/material optimization, instancing strategy comparison, monitoring, memory management

## On-Demand Documentation

For topics not covered in the reference files, fetch from the live docs:

* **Doc site:** `https://doc.babylonjs.com`
* **GitHub raw markdown:** `https://raw.githubusercontent.com/BabylonJS/Documentation/master/content/{path}.md`
* **[doc-urls.md](references/doc-urls.md)** - Complete URL map for all doc sections

### Babylon 9 & Advanced Topics covered only in live docs

* Clustered Lighting: `/features/featuresDeepDive/lights/clusteredLighting`
* Geospatial / Large Worlds (3D Tiles): `/features/featuresDeepDive/geospatial`
* Frame Graph: `/features/featuresDeepDive/frameGraph`
* Gaussian Splatting: `/features/featuresDeepDive/importers` (splat/ply formats)
* Node Particle Editor & Flow Maps: `/features/featuresDeepDive/particles/node_particle_system`
* Physics V2 (Havok): `/features/featuresDeepDive/physics`
* WebXR/VR/AR: `/features/featuresDeepDive/webXR`
* Post-processing pipelines: `/features/featuresDeepDive/postProcesses`
* Flow Graph: `/features/featuresDeepDive/flowGraph`
* Smart Filters: `/features/featuresDeepDive/smartFilters`

## Spector.js — WebGL Frame Inspector

Spector.js captures and inspects individual WebGL frames (draw calls, shaders, textures, buffers). Useful for AI agents debugging GPU rendering from the browser.

**On-demand activation (this repo):** append `?spector=1` to the URL. The bundle lazy-loads `spectorjs` via dynamic `import()` and exposes `window.__spector` after init. Tree-shaken from default production builds.

```typescript
// Manual activation in dev (for standalone Babylon apps):
import { Spector } from 'spectorjs';
const spector = new Spector();
spector.displayUI();

// Programmatic capture:
spector.startCapture(canvas, 30);
```

## Common Gotchas

1. **Clustered Lights Scene Addition:** When creating lights for a `ClusteredLightContainer`, you *must* pass `true` as the `dontAddToScene` parameter in the light constructor. Failing to do this drastically reduces performance.
2. **Node Materials + Clustered Lights:** If using Node Materials with clustered lights in WebGPU, force WGSL compilation using `{ shaderLanguage: BABYLON.ShaderLanguage.WGSL }` when parsing the snippet.
3. **Quaternion vs Euler:** Setting `mesh.rotationQuaternion` disables `mesh.rotation`. To switch back: `mesh.rotationQuaternion = null`.
4. **Side-effect imports:** Features like thin instances, edge rendering, loaders, and collisions require importing their module even if you don't use the export directly.
5. **Dispose everything:** Babylon.js doesn't garbage-collect GPU resources. Always call `.dispose()` on meshes, materials, textures when done.
6. **Material sharing:** Modifying a shared material affects all meshes using it. Clone or create new materials for independent changes.
7. **TransformNode vs Mesh:** Use TransformNode for grouping/hierarchy. Empty Mesh objects waste CPU on frustum evaluation.
8. **Left-handed coordinates:** glTF is right-handed; Babylon auto-converts on import. Manual coordinate math may need adjustment.
9. **Thin instance limitations:** All-or-nothing visibility, single bounding box, no per-instance material. Use regular instances when individual control is needed.
10. **Pivot before geometry:** When code-building animated models, create and parent the joint's `TransformNode` first, then attach geometry as a child with a local offset.
11. **CSG2 setup:** Babylon 8+ `CSG2` (boolean ops backed by Manifold) requires `await InitializeCSG2Async()` once before use and operates in world space. Always dispose intermediate `CSG2.FromMesh` objects — they hold WASM memory.
12. **Negative scaling flips winding:** Mirroring a hierarchy via `scaling.x *= -1` inverts triangle winding and breaks backface culling. Fix with `material.sideOrientation = Mesh.DOUBLESIDE` or rebake.
