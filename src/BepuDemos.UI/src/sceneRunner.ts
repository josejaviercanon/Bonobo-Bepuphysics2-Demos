import { Engine } from '@babylonjs/core/Engines/engine';
import type { Scene } from '@babylonjs/core/scene';
import { AdvancedDynamicTexture } from '@babylonjs/gui/2D/advancedDynamicTexture';
import { Button } from '@babylonjs/gui/2D/controls/button';
import { TextBlock } from '@babylonjs/gui/2D/controls/textBlock';
import { Control } from '@babylonjs/gui/2D/controls/control';
import { getLocalBufferProvider, connectSignalStream, type LocalBufferProvider, type SignalStream } from './signalSource';
import { readGlobalClock, type GlobalClock } from './globalClock';
import { writeSceneLoaded } from './inputRing';
import type { SceneHandle } from './types';

const dbg = (...args: unknown[]) => console.log('[bepu-demos]', ...args);

/** One registrable scene: a sim game key plus its Babylon scene factory. */
export interface SceneDefinition {
    /** Sim host game key — `/api/{gameKey}/connect` stops the old sim and starts this one. */
    gameKey: string;
    /** Human label for the scene (registry metadata; the menu owns its own card labels). */
    label: string;
    create: (engine: Engine, canvas: HTMLCanvasElement) => Promise<SceneHandle>;
}

export interface GameOptions {
    containerId: string;
    scenes: Record<string, SceneDefinition>;
    /** Scene shown at boot. Defaults to the first registered key. */
    initialScene?: string;
}

let engine: Engine | null = null;
let canvas: HTMLCanvasElement | null = null;
let activeHandle: SceneHandle | null = null;
let scenes: Record<string, SceneDefinition> = {};
let activeSceneName: string | null = null;
let globalsStream: SignalStream | null = null;
let clock: GlobalClock | null = null;

/** Name of the scene currently rendering (null before {@link startGame}). */
export function currentSceneName(): string | null {
    return activeSceneName;
}

/**
 * Boots the Babylon engine in `containerId`, wires the host bridge and builds the initial
 * scene. The bundle calls this from its `window.initGame`.
 */
export async function startGame(options: GameOptions): Promise<void> {
    scenes = options.scenes;
    const initialScene = options.initialScene ?? Object.keys(scenes)[0];

    const container = document.getElementById(options.containerId);
    if (!container) {
        console.error(`[bepu-demos] container '#${options.containerId}' not found`);
        return;
    }

    canvas = document.createElement('canvas');
    canvas.id = 'render-canvas';
    canvas.style.width = '100%';
    canvas.style.height = '100%';
    canvas.style.display = 'block';
    container.appendChild(canvas);

    engine = new Engine(canvas, true, { antialias: true, stencil: true, preserveDrawingBuffer: true });

    if (!initialScene) {
        console.error('[bepu-demos] no scenes registered');
        return;
    }

    await switchScene(initialScene);
    engine.runRenderLoop(() => activeHandle?.scene.render());
    window.addEventListener('resize', () => engine?.resize());
    dbg('engine ready:', engine.getClassName(), canvas.width, 'x', canvas.height);
}

/**
 * Unloads the active scene, routes the sim switch through the host (which stops the previous
 * simulation and starts the requested one) and builds the new scene. Reloading the same
 * scene restarts its simulation fresh.
 */
export async function switchScene(name: string): Promise<void> {
    const definition = scenes[name];
    if (!definition) {
        console.error(`[bepu-demos] unknown scene '${name}'`);
        return;
    }

    if (activeHandle) {
        activeHandle.cleanup();
        activeHandle.scene.dispose();
        activeHandle = null;
    }

    globalsStream?.close();
    globalsStream = null;
    clock = null;

    postCommandToSim(`/api/${definition.gameKey}/connect`);

    let handle: SceneHandle | null = null;
    let loaded = true;
    try {
        handle = await definition.create(engine!, canvas!);
        window.__scene = handle.scene;
        // Mark active before building the overlay so the back button hides on the menu itself.
        activeSceneName = name;
        buildBackButton(handle.scene);
        buildStats(handle.scene);
    } catch (err) {
        loaded = false;
        console.error(`[bepu-demos] scene '${name}' failed to load:`, err);
    }

    activeHandle = handle;
    activeSceneName = name;

    if (handle) {
        globalsStream = connectSignalStream(`/api/${definition.gameKey}/stream`);
        globalsStream?.addBufferListener('globals', (values) => {
            clock = readGlobalClock(values);
        });
    }

    // Engine-side observability: the C# host logs the scene-load outcome via the input ring.
    writeSceneLoaded(loaded);
    window.__engineClock = () => clock;
    dbg('scene switched to', name, loaded ? '(ok)' : '(failed)');
}

/** Routes a sim command through the in-process provider. */
export function postCommandToSim(path: string): void {
    const provider = getLocalBufferProvider();
    if (!provider?.postCommand) {
        console.warn(`[bepu-demos] no provider command handler for ${path}`);
        return;
    }
    provider.postCommand(path);
}

/** Top-left "Menu" button on every non-menu scene (rebuilt per scene; disposed with it). */
function buildBackButton(scene: Scene): void {
    if (activeSceneName === 'menu') return;

    const gui = AdvancedDynamicTexture.CreateFullscreenUI('back-button', true, scene);
    gui.idealWidth = 1920;

    const button = Button.CreateSimpleButton('btn-scene-menu', 'Menu');
    button.width = '120px';
    button.height = '40px';
    button.color = '#e2e8f0';
    button.background = '#1e293b';
    button.cornerRadius = 8;
    button.fontSize = 15;
    button.horizontalAlignment = Control.HORIZONTAL_ALIGNMENT_LEFT;
    button.verticalAlignment = Control.VERTICAL_ALIGNMENT_TOP;
    button.left = '16px';
    button.top = '12px';
    button.onPointerClickObservable.add(() => {
        void switchScene('menu');
    });

    gui.addControl(button);
}

/** Top-right debug stats (FPS, sim clock, instance counts). */
function buildStats(scene: Scene): void {
    const gui = AdvancedDynamicTexture.CreateFullscreenUI('stats', true, scene);
    gui.idealWidth = 1920;

    const text = new TextBlock('debug-stats-text', 'FPS: 0');
    text.color = '#cbd5e1';
    text.fontSize = 14;
    text.textHorizontalAlignment = Control.HORIZONTAL_ALIGNMENT_RIGHT;
    text.textVerticalAlignment = Control.VERTICAL_ALIGNMENT_TOP;
    text.horizontalAlignment = Control.HORIZONTAL_ALIGNMENT_RIGHT;
    text.verticalAlignment = Control.VERTICAL_ALIGNMENT_TOP;
    text.left = '-16px';
    text.top = '12px';
    text.resizeToFit = true;
    text.paddingRight = '12px';
    text.paddingTop = '6px';
    text.paddingBottom = '6px';
    text.shadowColor = '#020617';
    text.shadowBlur = 6;
    gui.addControl(text);

    let elapsed = 0;
    scene.onBeforeRenderObservable.add(() => {
        const deltaSeconds = scene.getEngine().getDeltaTime() / 1000;
        elapsed += deltaSeconds;
        if (elapsed < 0.25) return;
        elapsed = 0;

        const simTime = clock?.timeSeconds ?? 0;
        const paused = clock?.paused ?? 0;
        const instances = (window.__scene?.meshes ?? [])
            .filter((mesh) => mesh.name.startsWith('demo-'))
            .reduce((sum, mesh) => sum + ((mesh as { thinInstanceCount?: number }).thinInstanceCount ?? 0), 0);

        text.text =
            `FPS: ${Math.round(scene.getEngine().getFps())}\n` +
            `Sim: ${simTime.toFixed(2)}s${paused ? ' (paused)' : ''}\n` +
            `Instances: ${instances}`;
    });
}

declare global {
    interface Window {
        /** Host shell entry point (called by `wwwroot/js/webview-bridge.js`). */
        initGame?: (containerId: string, initialScene?: string) => Promise<void>;
        /** Host shell contract: registers the shared-buffer provider before boot. */
        registerLocalBufferProvider?: (provider: LocalBufferProvider) => void;
        /** Test/agent hook: active Babylon scene. */
        __scene?: Scene;
        /** Test/agent hook: latest engine clock from the "globals" signal. */
        __engineClock?: () => GlobalClock | null;
        /** Test/agent hook: post a sim command by path (`/api/{game}/{verb}`). */
        __simCommand?: (path: string) => void;
    }
}
