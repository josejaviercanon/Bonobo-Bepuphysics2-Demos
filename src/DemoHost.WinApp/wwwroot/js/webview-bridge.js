// Desktop (WebView2) counterpart of the engine's `js/wasm-interop.js`.
//
// The native AOT host runs the ported BepuPhysics2 demos and publishes committed signal
// buffers through WebView2 shared memory:
//
//   host -> script   `sharedbufferreceived`
//                    additionalData = { channel, seq, elementCount }
//                    getBuffer()    = ArrayBuffer over the shared mapping (Float64Array)
//   host -> script   `sharedbufferreceived`
//                    additionalData = { channel:"input", slotSize, capacity }
//                    ReadWrite mapping; the page writes input ring records into it and
//                    bumps the head counter with Atomics.store — no per-input message
//   script -> host   chrome.webview.postMessage("connect:{game}" | "command:{game}:{verb}"
//                                                  | "pause:1|0" | "input-hello")
//
// The page never polls: signals are pushed, and every script-side signal view is released
// immediately after the synchronous dispatch, so consumers must copy out values they keep.
// The input mapping is the one exception: it is the producer surface for the page's lifetime
// and is never released.
//
// Note: `PostWebMessageAsArrayBuffer` does not exist in the WebView2 API surface
// (Microsoft.Web.WebView2.Core exposes PostWebMessageAsJson/AsString and
// PostSharedBufferToScript); the shared buffer is the binary channel.

const dbg = (...args) => console.log('[bepu-demos]', ...args);

const webview = window.chrome?.webview;

/** Input ring views: { data: Float64Array, head: Int32Array, capacity } | null. */
let inputViews = null;

const provider = {
    _listeners: {},
    onSignal(eventName, onData) {
        if (!this._listeners[eventName]) this._listeners[eventName] = [];
        this._listeners[eventName].push(onData);
    },
    postCommand(path) {
        const match = path.match(/^\/api\/([a-z0-9-]+)\/([a-z0-9-]+)$/);
        if (!match) {
            console.warn('[bepu-demos] no sim command handler for', path);
            return;
        }
        const [, gameKey, verb] = match;
        switch (verb) {
            case 'connect':
                webview.postMessage(`connect:${gameKey}`);
                break;
            case 'pause':
                webview.postMessage('pause:1');
                break;
            case 'resume':
                webview.postMessage('pause:0');
                break;
            default:
                // Payload-free module verbs (spawn-ball/reset/…): one generic path.
                webview.postMessage(`command:${gameKey}:${verb}`);
                break;
        }
    },
    getInputViews() {
        return inputViews;
    },
    close() {
        // Scene teardown: drop all buffer listeners so reloads never stack duplicates.
        this._listeners = {};
    }
};

function dispatch(eventName, values) {
    const listeners = provider._listeners[eventName];
    if (!listeners) return;
    for (const cb of listeners) cb(values);
}

if (!webview) {
    console.error(
        '[bepu-demos] window.chrome.webview unavailable — this bundle must be served by ' +
        'DemoHost.WinApp (WebView2 shared buffers).');
} else {
    webview.addEventListener('sharedbufferreceived', (event) => {
        const metadata = event.additionalData ?? {};

        // Input ring: a long-lived ReadWrite mapping the page produces records into. Do NOT
        // release it — releaseBuffer would drop the script-side producer surface.
        if (metadata.channel === 'input') {
            const buffer = event.getBuffer();
            const headOffset = metadata.capacity * metadata.slotSize * 8;
            inputViews = {
                data: new Float64Array(buffer, 0, metadata.capacity * metadata.slotSize),
                head: new Int32Array(buffer, headOffset, 1),
                capacity: metadata.capacity,
            };
            dbg('input ring view ready:', metadata.capacity, 'records @', metadata.slotSize, 'slots');
            window.__inputViews = inputViews;
            return;
        }

        const buffer = event.getBuffer();
        // Every signal buffer is pure 64-bit doubles — one typed-array path.
        const values = new Float64Array(buffer);
        try {
            dispatch(metadata.channel, values);
        } finally {
            webview.releaseBuffer(buffer);
        }
    });
}

function registerProvider() {
    window.registerLocalBufferProvider(provider);
    dbg('webview2 provider registered, booting renderer');
    // Ask the host for the writable input mapping once the provider (and therefore
    // provider.getInputViews) exists. The page re-sends this after every reload.
    webview.postMessage('input-hello');
    // The desktop host boots into the main menu; live cards connect their fixture sim.
    void window.initGame('render-viewport', 'menu');
}

if (typeof window.initGame === 'function' && typeof window.registerLocalBufferProvider === 'function') {
    registerProvider();
} else {
    window.addEventListener('babylon-bundle-ready', registerProvider, { once: true });
}
