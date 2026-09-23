import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { CreateSphere } from '@babylonjs/core/Meshes/Builders/sphereBuilder';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { ThinInstanceSet } from '../../rendering/instanceSets';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'planet';

const PLANET_RENDER_ID = 0;
const MAX_BALLS = 4608;

declare global {
    interface Window {
        __planet?: () => {
            planet: number;
            balls: number;
            visibleInstances: number;
            /** Mean distance of the orbiting spheres from the planet center. */
            meanDistance: number;
        };
    }
}

/**
 * Port of the upstream `PlanetDemo`: inverse-square gravity pulls every sphere toward the
 * planet center while a sheet of spheres orbits it. The static planet is record id 0.
 */
export async function createPlanetScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.03, 0.04, 0.08, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2.2, Math.PI / 2.4, 340, new Vector3(0, 80, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.85;
    const sun = new DirectionalLight('sun', new Vector3(-0.35, -0.6, 0.35), scene);
    sun.intensity = 0.7;

    const planetMaterial = new StandardMaterial('planet-mat', scene);
    planetMaterial.diffuseColor = new Color3(0.45, 0.4, 0.32);
    planetMaterial.emissiveColor = new Color3(0.05, 0.04, 0.03);
    planetMaterial.specularColor = new Color3(0.15, 0.15, 0.15);

    const planet = CreateSphere('demo-planet', { diameter: 1, segments: 32 }, scene);
    planet.material = planetMaterial;
    planet.isPickable = false;
    planet.alwaysSelectAsActiveMesh = true;
    planet.doNotSyncBoundingInfo = true;
    const planetSet = new ThinInstanceSet(planet, 1, 1);

    const ballMaterial = new StandardMaterial('orbiter-mat', scene);
    ballMaterial.diffuseColor = new Color3(0.35, 0.8, 0.95);
    ballMaterial.emissiveColor = new Color3(0.08, 0.28, 0.34);

    const balls = CreateSphere('demo-orbiters', { diameter: 1, segments: 8 }, scene);
    balls.material = ballMaterial;
    balls.isPickable = false;
    balls.alwaysSelectAsActiveMesh = true;
    balls.doNotSyncBoundingInfo = true;
    const ballSet = new ThinInstanceSet(balls, MAX_BALLS, 1024);

    let lastSeq = -1;
    let meanDistance = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] scenePlanet: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            planetSet.reset();
            ballSet.reset();

            let distanceSum = 0;
            for (const state of snapshot.states) {
                if (state.id === PLANET_RENDER_ID) {
                    planetSet.write(state);
                    continue;
                }

                ballSet.write(state);
                distanceSum += Math.hypot(state.x, state.y, state.z);
            }

            planetSet.commit();
            ballSet.commit();
            meanDistance = ballSet.count > 0 ? distanceSum / ballSet.count : 0;
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__planet = () => ({
        planet: planetSet.count,
        balls: ballSet.count,
        visibleInstances: planetSet.count + ballSet.count,
        meanDistance,
    });

    return {
        scene,
        cleanup: () => {
            window.__planet = undefined;
            stream?.close();
        },
    };
}
