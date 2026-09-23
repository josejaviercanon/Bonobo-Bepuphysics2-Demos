import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { GridMaterial } from '@babylonjs/materials/grid';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import { CreateSphere } from '@babylonjs/core/Meshes/Builders/sphereBuilder';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { ThinInstanceSet } from '../../rendering/instanceSets';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'bounciness';

const FLOOR_RENDER_ID = 0;
const MAX_BALLS = 1600;

/** Row index of a render id: ids are laid out row-major as in the C# fixture. */
const GRID_ROWS = 40;
const BALL_RENDER_ID_BASE = 100;

declare global {
    interface Window {
        __bounciness?: () => {
            floor: number;
            balls: number;
            visibleInstances: number;
            /** Mean height of the sampled spheres (bounce sanity readout for E2E). */
            meanHeight: number;
        };
    }
}

/**
 * Port of the upstream `BouncinessDemo`: a sphere grid with per-column spring frequency and
 * per-row damping ratio drops onto a static floor. Spheres are tinted by damping ratio so a
 * screenshot shows the material sweep (client-side presentation only — colours never feed
 * back into the simulation).
 */
export async function createBouncinessScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.1, 190, new Vector3(0, 12, -90), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.2, -1, 0.35), scene);
    sun.intensity = 0.65;

    const ground = CreateBox('demo-floor', { size: 1 }, scene);
    ground.material = new GridMaterial('floor-mat', scene);
    (ground.material as GridMaterial).gridRatio = 1;
    (ground.material as GridMaterial).majorUnitFrequency = 5;
    (ground.material as GridMaterial).minorUnitVisibility = 0.3;
    (ground.material as GridMaterial).mainColor = new Color3(0.1, 0.13, 0.19);
    (ground.material as GridMaterial).lineColor = new Color3(0.3, 0.4, 0.55);
    ground.isPickable = false;
    ground.alwaysSelectAsActiveMesh = true;
    ground.doNotSyncBoundingInfo = true;
    const floorSet = new ThinInstanceSet(ground, 1, 1);

    // Four material bands (damping ratio quadrants) so the sweep is visible in a still.
    const bandMaterials = [0.15, 0.4, 0.7, 0.95].map((damping, index) => {
        const material = new StandardMaterial(`ball-mat-${index}`, scene);
        const hue = 0.62 - damping * 0.62;
        const color = Color3.FromHSV(hue * 360, 0.75, 0.95);
        material.diffuseColor = color;
        material.emissiveColor = color.scale(0.22);
        return material;
    });

    const ballsMeshes = bandMaterials.map((material, index) => {
        const mesh = CreateSphere(`demo-balls-${index}`, { diameter: 1, segments: 12 }, scene);
        mesh.material = material;
        mesh.isPickable = false;
        mesh.alwaysSelectAsActiveMesh = true;
        mesh.doNotSyncBoundingInfo = true;
        return new ThinInstanceSet(mesh, MAX_BALLS, 512);
    });
    const ballSets = ballsMeshes;

    let lastSeq = -1;
    let means: number[] = [];

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneBounciness: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorSet.reset();
            for (const set of ballSets) set.reset();

            const sums = new Array(ballSets.length).fill(0);
            const counts = new Array(ballSets.length).fill(0);

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floorSet.write(state);
                    continue;
                }

                const row = Math.floor((state.id - BALL_RENDER_ID_BASE) / GRID_ROWS);
                const band = Math.min(ballSets.length - 1, Math.floor((row / GRID_ROWS) * ballSets.length));
                ballSets[band].write(state);
                sums[band] += state.y;
                counts[band] += 1;
            }

            floorSet.commit();
            for (let i = 0; i < ballSets.length; i++) ballSets[i].commit();
            means = sums.map((sum, index) => (counts[index] > 0 ? sum / counts[index] : 0));
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__bounciness = () => {
        const balls = ballSets.reduce((sum, set) => sum + set.count, 0);
        const meanHeight = means.length > 0 ? means.reduce((a, b) => a + b, 0) / means.length : 0;
        return { floor: floorSet.count, balls, visibleInstances: floorSet.count + balls, meanHeight };
    };

    return {
        scene,
        cleanup: () => {
            window.__bounciness = undefined;
            stream?.close();
        },
    };
}
