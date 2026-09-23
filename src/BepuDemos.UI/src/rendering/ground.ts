import type { Scene } from '@babylonjs/core/scene';
import { GridMaterial } from '@babylonjs/materials/grid';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import { ThinInstanceSet } from './instanceSets';

/**
 * Shared demo floor: a unit box mesh with the engine grid material, driven by a
 * `ThinInstanceSet` (one instance, render id 0 in every ported fixture). The record's
 * scale carries the actual floor dimensions.
 */
export function createGround(scene: Scene, name = 'demo-floor'): ThinInstanceSet {
    const ground = CreateBox(name, { size: 1 }, scene);
    ground.material = new GridMaterial(`${name}-mat`, scene);
    (ground.material as GridMaterial).gridRatio = 1;
    (ground.material as GridMaterial).majorUnitFrequency = 5;
    (ground.material as GridMaterial).minorUnitVisibility = 0.35;
    (ground.material as GridMaterial).mainColor = new Color3(0.11, 0.14, 0.2);
    (ground.material as GridMaterial).lineColor = new Color3(0.32, 0.42, 0.58);
    ground.isPickable = false;
    ground.alwaysSelectAsActiveMesh = true;
    ground.doNotSyncBoundingInfo = true;
    return new ThinInstanceSet(ground, 1, 1);
}
