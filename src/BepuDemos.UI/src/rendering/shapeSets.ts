import type { Scene } from '@babylonjs/core/scene';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import { CreateSphere } from '@babylonjs/core/Meshes/Builders/sphereBuilder';
import { CreateCapsule } from '@babylonjs/core/Meshes/Builders/capsuleBuilder';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import { ThinInstanceSet } from './instanceSets';

export type ShapeKind = 'box' | 'sphere' | 'capsule';

export interface ShapeSetOptions {
    /** Capsule mesh total height (Bepu `Capsule(radius, length)` renders length + 2·radius). */
    capsuleHeight?: number;
    capsuleRadius?: number;
    sphereSegments?: number;
}

/**
 * One thin-instance mesh per shape kind with a flat shaded material. The emitted
 * `Transform3DState` scale carries the instance dimensions, so a single unit mesh covers
 * every instance (capsules use a fixed mesh aspect: height/radius are baked in and the
 * record scale stays 1).
 */
export function createShapeSet(
    scene: Scene,
    name: string,
    kind: ShapeKind,
    color: Color3,
    maxCapacity: number,
    initialCapacity = 512,
    options: ShapeSetOptions = {}): ThinInstanceSet {
    const mesh = kind === 'box'
        ? CreateBox(name, { size: 1 }, scene)
        : kind === 'sphere'
            ? CreateSphere(name, { diameter: 1, segments: options.sphereSegments ?? 12 }, scene)
            : CreateCapsule(name, {
                height: options.capsuleHeight ?? 1,
                radius: options.capsuleRadius ?? 0.25,
                tessellation: 12,
            }, scene);

    const material = new StandardMaterial(`${name}-mat`, scene);
    material.diffuseColor = color;
    material.emissiveColor = color.scale(0.22);
    mesh.material = material;
    mesh.isPickable = false;
    mesh.alwaysSelectAsActiveMesh = true;
    mesh.doNotSyncBoundingInfo = true;
    return new ThinInstanceSet(mesh, maxCapacity, initialCapacity);
}
