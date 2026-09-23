# Babylon.js Performance Optimization

## Table of Contents

* [Scene-Level Optimizations](https://www.google.com/search?q=%23scene-level-optimizations)
* [Mesh Optimizations](https://www.google.com/search?q=%23mesh-optimizations)
* [Material & Lighting Optimizations](https://www.google.com/search?q=%23material--lighting-optimizations)
* [Rendering Optimizations](https://www.google.com/search?q=%23rendering-optimizations)
* [Instancing Strategy](https://www.google.com/search?q=%23instancing-strategy)
* [Performance Monitoring](https://www.google.com/search?q=%23performance-monitoring)
* [Performance Priority Modes](https://www.google.com/search?q=%23performance-priority-modes)
* [Memory Management](https://www.google.com/search?q=%23memory-management)

## Scene-Level Optimizations

```typescript
// Skip pointer movement picking (big CPU win if not needed)
scene.skipPointerMovePicking = true;

// Disable auto-clear when viewport is fully covered
scene.autoClear = false;
scene.autoClearDepthAndStencil = false;

// Freeze active meshes list (static scenes)
scene.freezeActiveMeshes();
scene.unfreezeActiveMeshes(); // re-enable

// Block dirty mechanism during batch operations
scene.blockMaterialDirtyMechanism = true;
// ... batch changes ...
scene.blockMaterialDirtyMechanism = false;

// Block free active meshes during batch dispose
scene.blockfreeActiveMeshesAndRenderingGroups = true;
// ... dispose meshes ...
scene.blockfreeActiveMeshesAndRenderingGroups = false;

// Optimized scene constructor
const scene = new Scene(engine, {
  useGeometryUniqueIdsMap: true,
  useMaterialMeshMap: true,
  useClonedMeshMap: true,
});

```

### Large-Scale & Open-World Scenes (Babylon.js 9.x)

For massive or geospatial environments, leverage Babylon.js 9.x architecture additions to manage precision and spatial data:

* **Large World Rendering**: Utilize specialized coordinate and precision handling for vast virtual worlds[cite: 1].
* **Geospatial Camera**: Integrate the native [Geospatial Camera](https://aka.ms/babylon9GSCDoc) for geographic and planetary-scale visualization[cite: 1].
* **3D Tiles Support**: Stream and render massive 3D geospatial datasets efficiently using built-in [3D Tiles Support](https://aka.ms/babylon93DTDoc)[cite: 1].

## Mesh Optimizations

```typescript
// Use TransformNode for non-renderable containers (not empty Mesh!)
const group = new TransformNode("group", scene);

// Freeze world matrix for static meshes
mesh.freezeWorldMatrix();

// Disable bounding info sync for static meshes
mesh.doNotSyncBoundingInfo = true;
mesh.alwaysSelectAsActiveMesh = true;  // pair with above

// Convert to unindexed mesh (when vertex reuse is low)
mesh.convertToUnIndexedMesh();

// Culling strategy
mesh.cullingStrategy = AbstractMesh.CULLINGSTRATEGY_BOUNDINGSPHERE_ONLY;
// Options: STANDARD, BOUNDINGSPHERE_ONLY,
//          OPTIMISTIC_INCLUSION, OPTIMISTIC_INCLUSION_THEN_BSPHERE_ONLY

// Bake transform to avoid matrix recalculation
mesh.bakeCurrentTransformIntoVertices();

```

## Material & Lighting Optimizations

```typescript
// Freeze static materials
material.freeze();

// Depth pre-pass for complex shaders
material.needDepthPrePass = true;

// Limit simultaneous lights (legacy approach)
material.maxSimultaneousLights = 4; // default is 4

// Use texture LOD
texture.lodGenerationScale = 0.5;

// Prefer PBRMaterial over StandardMaterial for quality/perf ratio

```

### Clustered Lighting (Babylon.js 9.x)

In Babylon.js 9.x, bypass traditional per-material light limits by implementing **[Clustered Lighting](https://aka.ms/babylon9CLDoc)**[cite: 1]. Clustered lighting divides the view frustum into a 3D grid of clusters, allowing scenes to efficiently support dozens or hundreds of dynamic lights with minimal performance degradation.

## Rendering Optimizations

```typescript
// Rendering groups (control draw order)
mesh.renderingGroupId = 0; // 0-3, rendered in order

// Disable auto-clear for specific rendering groups
scene.setRenderingAutoClearDepthStencil(renderingGroupIdx, false, false, false);

// Edge rendering (expensive - use sparingly)
mesh.enableEdgesRendering();
mesh.edgesWidth = 4.0;
mesh.edgesColor = new Color4(0, 0, 0, 1);

// Rendering order optimization
scene.setRenderingOrder(renderingGroupId, opaqueSortFn, alphaTestSortFn, transparentSortFn);

```

### Frame Graph (Babylon.js 9.x)

For advanced rendering pipelines and multi-pass effects, adopt the **[Frame Graph](https://aka.ms/babylon9FGDoc)**[cite: 1]. The Frame Graph explicitly manages render passes, dependencies, and resource lifetimes, minimizing redundant state changes and memory allocations for superior rendering efficiency.

## Instancing Strategy

Choose the right approach based on needs:

| Approach | Draw Calls | JS Objects | Per-Instance Material | Individual Culling | Best For |
| --- | --- | --- | --- | --- | --- |
| **Thin Instances** | 1 per source | None | No (custom attrs only) | No | Thousands of identical meshes |
| **Instances** | 1 per source | 1 per instance | No (share source) | Yes | Hundreds with individual control |
| **Clones** | 1 per clone | 1 per clone | Yes | Yes | Few copies needing different materials |
| **Mesh.MergeMeshes** | 1 total | 1 total | Multi-material option | No | Static geometry batching |

### Thin Instances Checklist

```typescript
import "@babylonjs/core/Meshes/thinInstanceMesh";

// Batch creation (fastest)
const buffer = new Float32Array(16 * count);
for (let i = 0; i < count; i++) {
  Matrix.Translation(x, y, z).copyToArray(buffer, i * 16);
}
mesh.thinInstanceSetBuffer("matrix", buffer, 16, false); // false = updateable

// Signal update after modifying buffer
mesh.thinInstanceBufferUpdated("matrix");

// Limit visible count
mesh.thinInstanceCount = visibleCount;

```

## Performance Monitoring

```typescript
// Engine instrumentation
import { EngineInstrumentation } from "@babylonjs/core/Instrumentation/engineInstrumentation";
const engineInstr = new EngineInstrumentation(engine);
engineInstr.captureGPUFrameTime = true;
engineInstr.captureShaderCompilationTime = true;

// Scene instrumentation
import { SceneInstrumentation } from "@babylonjs/core/Instrumentation/sceneInstrumentation";
const sceneInstr = new SceneInstrumentation(scene);
sceneInstr.captureFrameTime = true;
sceneInstr.captureActiveMeshesEvaluationTime = true;
sceneInstr.captureRenderTime = true;

// Read metrics
const fps = engine.getFps();
const drawCalls = sceneInstr.drawCallsCounter.current;
const activeMeshes = scene.getActiveMeshes().length;
const totalVertices = scene.getTotalVertices();

// Inspector v2 (Babylon.js 9.x debug tool)
import "@babylonjs/inspector";
scene.debugLayer.show(); // Utilizes Inspector v2 architecture
scene.debugLayer.hide();

```

## Performance Priority Modes

```typescript
// Automatic optimization levels
scene.performancePriority = ScenePerformancePriority.BackwardCompatible; // default, no changes
scene.performancePriority = ScenePerformancePriority.Intermediate;      // auto-freeze, skip picking
scene.performancePriority = ScenePerformancePriority.Aggressive;        // skip frustum, disable bounds

// Intermediate mode auto-enables:
// - scene.skipPointerMovePicking = true
// - material.freeze() on all materials
// - mesh.doNotSyncBoundingInfo = true
// - mesh.isSkeletonAnimationSheet = true (when applicable)

// Aggressive mode additionally:
// - Skips frustum clipping
// - Disables bounding info updates

```

## Memory Management

```typescript
// Dispose individual mesh
mesh.dispose();

// Dispose material (checks if shared)
material.dispose(true);  // true = force even if shared

// Dispose texture
texture.dispose();

// Dispose entire scene
scene.dispose();

// Engine disposal
engine.dispose();

// Check if disposed
if (!mesh.isDisposed()) { /* safe to use */ }

// Handle WebGL context loss
engine.onContextLostObservable.add(() => { /* pause */ });
engine.onContextRestoredObservable.add(() => { /* rebuild */ });

```
