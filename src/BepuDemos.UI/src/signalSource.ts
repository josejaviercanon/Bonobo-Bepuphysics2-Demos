import type { ScalarArray } from './signalLayout';

/**
 * Zero-copy bridge contract between the host page shell (`wwwroot/js/webview-bridge.js`)
 * and the bundle. Mirrors `@bonoboengine/core`'s `LocalBufferProvider` for the subset this
 * test bed uses.
 */
export interface SignalStream {
    /** Subscribe to one named signal as a raw float64 view of the pinned buffer. */
    addBufferListener(eventName: string, onData: (values: ScalarArray) => void): void;
    /** Send one command to the sim host (`/api/{game}/{verb}`). */
    postCommand(path: string): void;
    /** Drop all listeners (scene teardown). */
    close(): void;
}

/** Zero-copy views over the pinned input ring owned by the co-located host. */
export interface InputRingViews {
    readonly data: Float64Array;
    readonly head: Int32Array;
    readonly capacity: number;
}

export interface LocalBufferProvider {
    onSignal(eventName: string, onData: (values: ScalarArray) => void): void;
    postCommand?(path: string): void;
    getInputViews?(): InputRingViews | null;
    close?(): void;
}

let localBufferProvider: LocalBufferProvider | null = null;

export function registerLocalBufferProvider(provider: LocalBufferProvider): void {
    localBufferProvider = provider;
}

/** Raw provider (or null when the bundle runs outside the host shell). */
export function getLocalBufferProvider(): LocalBufferProvider | null {
    return localBufferProvider;
}

/**
 * Creates the stream for one sim (`/api/{gameKey}/stream`): subscribes to the provider's
 * per-channel shared-buffer pushes and sends the connect handshake. Returns null when no
 * provider is registered (bundle served outside DemoHost.WinApp).
 */
export function connectSignalStream(url: string): SignalStream | null {
    const provider = localBufferProvider;
    if (!provider) {
        console.error(
            '[bepu-demos] no local buffer provider registered. This bundle must be served by ' +
            'DemoHost.WinApp (WebView2 shared buffers).');
        return null;
    }

    const stream: SignalStream = {
        addBufferListener: (eventName, onData) => provider.onSignal(eventName, onData),
        postCommand: (path) => {
            if (!provider.postCommand) {
                console.warn(`[bepu-demos] local provider has no command handler for ${path}`);
                return;
            }
            provider.postCommand(path);
        },
        close: () => provider.close?.(),
    };

    const gameKey = url.match(/^\/?api\/([a-z0-9-]+)\/stream$/)?.[1];
    if (gameKey) stream.postCommand(`/api/${gameKey}/connect`);
    return stream;
}
