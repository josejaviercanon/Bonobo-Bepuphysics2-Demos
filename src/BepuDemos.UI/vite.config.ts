import { defineConfig } from 'vite';
import { copyFileSync, mkdirSync } from 'node:fs';
import { resolve } from 'path';

/**
 * Builds the host-compatible bundle: `wwwroot/dist/game-bundle.js` (ES module, loaded by
 * `wwwroot/index.html`) plus the static `dist/app.css`. The host (DemoHost.WinApp) copies
 * the whole `wwwroot` tree into its output/publish folder.
 */
function copyAppCss() {
    return {
        name: 'copy-app-css',
        closeBundle() {
            const outDir = resolve(__dirname, 'wwwroot/dist');
            mkdirSync(outDir, { recursive: true });
            copyFileSync(resolve(__dirname, 'app.css'), resolve(outDir, 'app.css'));
        }
    };
}

export default defineConfig({
    plugins: [copyAppCss()],
    build: {
        lib: {
            entry: resolve(__dirname, 'game.ts'),
            name: 'BepuDemosViewport',
            fileName: 'game-bundle',
            formats: ['es']
        },
        outDir: resolve(__dirname, 'wwwroot/dist'),
        emptyOutDir: true,
        sourcemap: true
    }
});
