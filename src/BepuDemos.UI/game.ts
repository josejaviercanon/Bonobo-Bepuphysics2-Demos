import { startGame, postCommandToSim, type SceneDefinition } from './src/sceneRunner';
import { registerLocalBufferProvider } from './src/signalSource';
import { createMenuScene } from './src/scenes/menu/sceneMenu';
import { createSimpleSelfContainedScene } from './src/scenes/simpleSelfContained/sceneSimpleSelfContained';
import { createPyramidScene } from './src/scenes/pyramid/scenePyramid';
import { createBouncinessScene } from './src/scenes/bounciness/sceneBounciness';
import { createPlanetScene } from './src/scenes/planet/scenePlanet';
import { createFrictionScene } from './src/scenes/friction/sceneFriction';
import { createPerBodyGravityScene } from './src/scenes/perBodyGravity/scenePerBodyGravity';
import { createColosseumScene } from './src/scenes/colosseum/sceneColosseum';
import { createContinuousCollisionDetectionScene } from './src/scenes/continuousCollisionDetection/sceneContinuousCollisionDetection';
import { createSubsteppingScene } from './src/scenes/substepping/sceneSubstepping';
import { createCompoundScene } from './src/scenes/compound/sceneCompound';
import { createContactEventsScene } from './src/scenes/contactEvents/sceneContactEvents';
import { createCollisionTrackingScene } from './src/scenes/collisionTracking/sceneCollisionTracking';
import { createCustomVoxelCollidableScene } from './src/scenes/customVoxelCollidable/sceneCustomVoxelCollidable';
import { createRopeStabilityScene } from './src/scenes/ropeStability/sceneRopeStability';
import { createRopeTwistScene } from './src/scenes/ropeTwist/sceneRopeTwist';
import { createChainFountainScene } from './src/scenes/chainFountain/sceneChainFountain';
import { createBlockChainScene } from './src/scenes/blockChain/sceneBlockChain';
import { createRagdollTubeScene } from './src/scenes/ragdollTube/sceneRagdollTube';
import { createDancerScene } from './src/scenes/dancer/sceneDancer';
import { createPlumpDancerScene } from './src/scenes/plumpDancer/scenePlumpDancer';

/**
 * Scene registry: one entry per imported Bepu demo plus the main menu. The game key is the
 * C# module key (`BepuDemosModules.AddGameBepuDemosModules`); switching scenes retargets both
 * the C# simulation and the Babylon scene. The menu key is unknown to the host on purpose:
 * connecting it releases the previous simulation and its pinned signal buffer, then idles.
 */
const SCENES: Record<string, SceneDefinition> = {
    menu: {
        gameKey: 'menu',
        label: 'Menu',
        create: createMenuScene,
    },
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
    friction: {
        gameKey: 'friction',
        label: 'Friction',
        create: createFrictionScene,
    },
    'per-body-gravity': {
        gameKey: 'per-body-gravity',
        label: 'Per-Body Gravity',
        create: createPerBodyGravityScene,
    },
    colosseum: {
        gameKey: 'colosseum',
        label: 'Colosseum',
        create: createColosseumScene,
    },
    'continuous-collision-detection': {
        gameKey: 'continuous-collision-detection',
        label: 'Continuous Collision',
        create: createContinuousCollisionDetectionScene,
    },
    substepping: {
        gameKey: 'substepping',
        label: 'Substepping',
        create: createSubsteppingScene,
    },
    compound: {
        gameKey: 'compound',
        label: 'Compound',
        create: createCompoundScene,
    },
    'contact-events': {
        gameKey: 'contact-events',
        label: 'Contact Events',
        create: createContactEventsScene,
    },
    'collision-tracking': {
        gameKey: 'collision-tracking',
        label: 'Collision Tracking',
        create: createCollisionTrackingScene,
    },
    'custom-voxel-collidable': {
        gameKey: 'custom-voxel-collidable',
        label: 'Custom Voxel',
        create: createCustomVoxelCollidableScene,
    },
    'rope-stability': {
        gameKey: 'rope-stability',
        label: 'Rope Stability',
        create: createRopeStabilityScene,
    },
    'rope-twist': {
        gameKey: 'rope-twist',
        label: 'Rope Twist',
        create: createRopeTwistScene,
    },
    'chain-fountain': {
        gameKey: 'chain-fountain',
        label: 'Chain Fountain',
        create: createChainFountainScene,
    },
    'block-chain': {
        gameKey: 'block-chain',
        label: 'Block Chain',
        create: createBlockChainScene,
    },
    'ragdoll-tube': {
        gameKey: 'ragdoll-tube',
        label: 'Ragdoll Tube',
        create: createRagdollTubeScene,
    },
    dancer: {
        gameKey: 'dancer',
        label: 'Dancer',
        create: createDancerScene,
    },
    'plump-dancer': {
        gameKey: 'plump-dancer',
        label: 'Plump Dancer',
        create: createPlumpDancerScene,
    },
};

window.initGame = async (containerId: string, initialScene?: string) =>
    startGame({ containerId, scenes: SCENES, initialScene: initialScene ?? 'menu' });

// Test/agent hook (same shape as the engine bundles): post a sim command by path.
window.__simCommand = postCommandToSim;

// Host shell contract: the WebView2 bridge registers its shared-buffer provider here
// before booting the renderer.
window.registerLocalBufferProvider = registerLocalBufferProvider;

// Host shell fallback: `webview-bridge.js` registers the provider and waits for this event
// when the bundle finished loading after the shell script started.
window.dispatchEvent(new Event('babylon-bundle-ready'));
