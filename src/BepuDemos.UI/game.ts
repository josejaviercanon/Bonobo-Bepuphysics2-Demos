import { startGame, postCommandToSim, type SceneDefinition } from './src/sceneRunner';
import { registerLocalBufferProvider } from './src/signalSource';
import { createSimpleSelfContainedScene } from './src/scenes/simpleSelfContained/sceneSimpleSelfContained';
import { createPyramidScene } from './src/scenes/pyramid/scenePyramid';
import { createBouncinessScene } from './src/scenes/bounciness/sceneBounciness';
import { createPlanetScene } from './src/scenes/planet/scenePlanet';

/**
 * Scene registry: one entry per imported Bepu demo. The game key is the C# module key
 * (`BepuDemosModules.AddGameBepuDemosModules`); switching scenes retargets both the C#
 * simulation and the Babylon scene.
 */
const SCENES: Record<string, SceneDefinition> = {
    'simple-self-contained': {
        gameKey: 'simple-self-contained',
        label: 'Simple Self Contained',
        create: createSimpleSelfContainedScene,
    },
    pyramid: {
        gameKey: 'pyramid',
        label: 'Pyramid',
        create: createPyramidScene,
    },
    bounciness: {
        gameKey: 'bounciness',
        label: 'Bounciness',
        create: createBouncinessScene,
    },
    planet: {
        gameKey: 'planet',
        label: 'Planet',
        create: createPlanetScene,
    },
};

window.initGame = async (containerId: string, initialScene?: string) =>
    startGame({ containerId, scenes: SCENES, initialScene: initialScene ?? 'simple-self-contained' });

// Test/agent hook (same shape as the engine bundles): post a sim command by path.
window.__simCommand = postCommandToSim;

// Host shell contract: the WebView2 bridge registers its shared-buffer provider here
// before booting the renderer.
window.registerLocalBufferProvider = registerLocalBufferProvider;

// Host shell fallback: `webview-bridge.js` registers the provider and waits for this event
// when the bundle finished loading after the shell script started.
window.dispatchEvent(new Event('babylon-bundle-ready'));
