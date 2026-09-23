// Launches the published DemoHost.WinApp (WinUI 3 + WebView2, Native AOT) for the Playwright
// `winapp` project and waits until its Chrome DevTools Protocol endpoint answers.
//
// Why the published exe and not `dotnet run`: the Debug run activates the packaged app
// through the WinApp CLI, which does not inherit the shell environment, so the
// WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS debugging flag never reaches WebView2. The Release
// publish is unpackaged + self-contained and is launched directly here.
//
// ESM (src/Game.Tests.UI/package.json has "type": "module").
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, rmSync } from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = findRepoRoot(SCRIPT_DIR);
const PUBLISH_DIR = path.join(
  REPO_ROOT,
  'src', 'DemoHost.WinApp', 'bin', 'x64', 'Release',
  'net10.0-windows10.0.26100.0', 'win-x64', 'publish',
);
const EXE_PATH = path.join(PUBLISH_DIR, 'DemoHost.WinApp.exe');
const DIST_BUNDLE = path.join(REPO_ROOT, 'src', 'BepuDemos.UI', 'wwwroot', 'dist', 'game-bundle.js');

const PORT = Number(process.env.GAME_WINAPP_CDP_PORT ?? 9223);
const CDP_URL = `http://127.0.0.1:${PORT}/json/version`;
const READY_TIMEOUT_MS = 90_000;
const USER_DATA_DIR = path.join(os.tmpdir(), `bepu-demos-winapp-e2e-${process.pid}`);

const log = (...args) => console.log('[demohost-e2e]', ...args);

function findRepoRoot(startDir) {
  let dir = startDir;
  for (;;) {
    if (existsSync(path.join(dir, 'bonoboBepuDemos.slnx'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error('bonoboBepuDemos.slnx not found above ' + startDir);
    dir = parent;
  }
}

function publishIfNeeded() {
  const force = process.argv.includes('--publish') || process.env.GAME_WINAPP_PUBLISH === '1';
  if (!force && existsSync(EXE_PATH)) return;

  if (!existsSync(DIST_BUNDLE)) {
    log('frontend bundle missing — run `npm ci && npm run build` at the repo root first');
    process.exit(1);
  }

  log('publishing DemoHost.WinApp (Release, win-x64, Native AOT — first run takes minutes)');
  const result = spawnSync(
    'dotnet',
    ['publish', 'src/DemoHost.WinApp/DemoHost.WinApp.csproj', '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64'],
    { cwd: REPO_ROOT, stdio: 'inherit' },
  );
  if (result.status !== 0) {
    log('publish failed');
    process.exit(result.status ?? 1);
  }
}

function waitForCdp() {
  const started = Date.now();
  return new Promise((resolve, reject) => {
    const check = () => {
      const request = http.get(CDP_URL, (response) => {
        response.resume();
        if (response.statusCode && response.statusCode < 400) resolve();
        else retry();
      });
      request.on('error', retry);
      request.setTimeout(1000, () => request.destroy(new Error('timeout')));
    };
    const retry = () => {
      if (Date.now() - started > READY_TIMEOUT_MS) {
        reject(new Error(`CDP endpoint ${CDP_URL} did not become ready within ${READY_TIMEOUT_MS} ms`));
      } else {
        setTimeout(check, 250);
      }
    };
    check();
  });
}

function killTree(pid) {
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(pid), '/t', '/f'], { stdio: 'ignore' });
  } else {
    try { process.kill(-pid, 'SIGKILL'); } catch { /* already gone */ }
  }
}

publishIfNeeded();

log(`launching ${EXE_PATH}`);
// Debugging + no-throttle flags: the desktop window is never foreground in E2E runs, and
// Chromium throttles rAF/timers for occluded windows — specs sample thin-instance counts
// per frame, so keep the renderer at full rate.
const BROWSER_ARGS = [
  `--remote-debugging-port=${PORT}`,
  '--disable-background-timer-throttling',
  '--disable-backgrounding-occluded-windows',
  '--disable-renderer-backgrounding',
].join(' ');

const app = spawn(EXE_PATH, [], {
  cwd: PUBLISH_DIR,
  stdio: 'ignore',
  env: {
    ...process.env,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: BROWSER_ARGS,
    WEBVIEW2_USER_DATA_FOLDER: USER_DATA_DIR,
  },
});

let shuttingDown = false;
function shutdown(code = 0) {
  if (shuttingDown) return;
  shuttingDown = true;
  killTree(app.pid);
  try { rmSync(USER_DATA_DIR, { recursive: true, force: true }); } catch { /* best effort */ }
  process.exit(code);
}

app.on('exit', (code) => {
  log(`host exited with code ${code}`);
  shutdown(code ?? 0);
});

process.on('SIGINT', () => shutdown(0));
process.on('SIGTERM', () => shutdown(0));

waitForCdp()
  .then(() => log(`CDP ready on ${CDP_URL}`))
  .catch((error) => {
    log(String(error));
    shutdown(1);
  });
