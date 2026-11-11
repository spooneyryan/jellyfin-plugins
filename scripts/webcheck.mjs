#!/usr/bin/env node
// Headless-browser checks against a sandbox, using only Node 22 built-ins and the Chrome DevTools
// Protocol. Launches Chrome (or Edge) headless with a throwaway profile under .sandbox/<name>/browser, runs a
// JSON steps file or steps given as flags, collects console messages, uncaught exceptions and failed
// requests, prints one JSON result and always closes the browser it started.
//
// Safety: requests from the page may only go to loopback hosts, never to port 8096 (the real server), and not to
// loopback routes that make the sandbox server download from the internet (Jellyfin Enhanced's CDN proxy,
// sandbox-policy.mjs); any other request is failed by the Fetch domain unless --allow-external is given. Passwords
// from credentials.json are typed into the login form but never printed, and session tokens and API keys in the result
// (jellyfin-web's console lines, failed request URLs) are replaced by REDACTED before it is printed or saved (redact.mjs).
//
// Uncaught exceptions that jellyfin-web itself throws in a headless browser whatever the page does (page-noise.mjs, such
// as its new-item notification without notification permission) are kept in the result marked `known`, and neither a
// noExceptions step nor --fail-on-exception counts them.
//
// Usage: see scripts/README.md, or run with --help.

import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertSafeUrl, isAllowedRequest } from './sandbox-policy.mjs';
import { knownPageNoise } from './page-noise.mjs';
import { redactSecrets } from './redact.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..');

const BROWSERS = {
    chrome: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    edge: 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
};

const HELP = `webcheck.mjs: headless browser checks against a sandbox

node scripts/webcheck.mjs --sandbox <name> [options] [steps...]

Options:
  --sandbox <name>        sandbox under .sandbox/<name> (default: default); URL from its sandbox.json
  --url <url>             base URL instead (loopback only, never port 8096)
  --steps <file.json>     run the steps in this file (relative paths: cwd, then scripts/webchecks/)
  --browser chrome|edge   default chrome, falling back to edge
  --headed                show the browser window
  --timeout <ms>          default step timeout (default 20000)
  --fail-on-exception     fail when the page throws an uncaught exception (jellyfin-web's known noise,
                          page-noise.mjs, is recorded with "known" and never counts, nor in noExceptions steps)
  --allow-external        let the page reach non-loopback hosts and Jellyfin Enhanced's CDN proxy route
                          (blocked by default, sandbox-policy.mjs)
  --keep-profile          keep the temporary browser profile
  --out <file>            also write the JSON result to this file

Steps as flags (run in the order given, after any --steps file):
  --login <user>          sign in through the web login form (password from credentials.json)
  --goto <url|path>       navigate; paths are relative to the sandbox base URL
  --wait <js expr>        wait until the expression is truthy
  --eval <js expr>        evaluate and record the value (await allowed)
  --assert <js expr>      fail unless the expression is truthy
  --click <selector>      mouse click on the element's centre
  --type <text>           insert text into the focused element
  --key <combo>           key press, e.g. Shift+B, Enter, Escape, ArrowLeft, Shift (the modifier alone)
  --screenshot <file>     PNG (relative paths: .sandbox/<name>/browser/screenshots/)
  --sleep <ms>            wait
`;

// ------------------------------------------------------------------------------------------------ arguments

function parseArgs(argv) {
    const options = { sandbox: 'default', timeout: 20000, browser: null, steps: [], stepsFile: null };
    const stepFlags = { '--login': 'login', '--goto': 'goto', '--wait': 'waitFor', '--eval': 'eval', '--assert': 'assert',
        '--click': 'click', '--type': 'type', '--key': 'key', '--screenshot': 'screenshot', '--sleep': 'sleep' };
    for (let i = 0; i < argv.length; i++) {
        const arg = argv[i];
        const next = () => {
            if (i + 1 >= argv.length) throw new Error(`${arg} needs a value`);
            return argv[++i];
        };
        if (arg === '--help' || arg === '-h') { options.help = true; }
        else if (arg === '--sandbox') { options.sandbox = next(); }
        else if (arg === '--url') { options.url = next(); }
        else if (arg === '--steps') { options.stepsFile = next(); }
        else if (arg === '--browser') { options.browser = next(); }
        else if (arg === '--headed') { options.headed = true; }
        else if (arg === '--timeout') { options.timeout = Number(next()); }
        else if (arg === '--fail-on-exception') { options.failOnException = true; }
        else if (arg === '--allow-external') { options.allowExternal = true; }
        else if (arg === '--keep-profile') { options.keepProfile = true; }
        else if (arg === '--out') { options.out = next(); }
        else if (stepFlags[arg]) {
            const value = next();
            const action = stepFlags[arg];
            options.steps.push({ [action]: action === 'sleep' ? Number(value) : value });
        }
        else { throw new Error(`Unknown argument ${arg} (see --help)`); }
    }
    return options;
}

// ------------------------------------------------------------------------------------------------ sandbox

function sandboxPaths(name) {
    if (!/^[a-z0-9][a-z0-9-]{0,30}$/.test(name)) throw new Error(`Bad sandbox name ${name}`);
    const root = path.join(repoRoot, '.sandbox', name);
    return {
        root,
        state: path.join(root, 'sandbox.json'),
        credentials: path.join(root, 'credentials.json'),
        browser: path.join(root, 'browser')
    };
}

function readJson(file) {
    return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
}

// ------------------------------------------------------------------------------------------------ CDP

class Cdp {
    constructor(ws) {
        this.ws = ws;
        this.nextId = 1;
        this.pending = new Map();
        this.listeners = [];
        ws.addEventListener('message', (event) => {
            const message = JSON.parse(typeof event.data === 'string' ? event.data : Buffer.from(event.data).toString('utf8'));
            if (message.id && this.pending.has(message.id)) {
                const { resolve, reject } = this.pending.get(message.id);
                this.pending.delete(message.id);
                if (message.error) reject(new Error(`${message.error.message}${message.error.data ? ': ' + message.error.data : ''}`));
                else resolve(message.result);
            } else if (message.method) {
                for (const listener of this.listeners) listener(message);
            }
        });
        ws.addEventListener('close', () => {
            for (const { reject } of this.pending.values()) reject(new Error('DevTools connection closed'));
            this.pending.clear();
        });
    }

    static connect(url) {
        return new Promise((resolve, reject) => {
            const ws = new WebSocket(url);
            ws.addEventListener('open', () => resolve(new Cdp(ws)), { once: true });
            ws.addEventListener('error', () => reject(new Error(`Cannot connect to ${url}`)), { once: true });
        });
    }

    send(method, params = {}, sessionId = undefined, timeoutMs = 30000) {
        const id = this.nextId++;
        const payload = { id, method, params };
        if (sessionId) payload.sessionId = sessionId;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                if (this.pending.delete(id)) reject(new Error(`${method} timed out`));
            }, timeoutMs);
            this.pending.set(id, {
                resolve: (value) => { clearTimeout(timer); resolve(value); },
                reject: (error) => { clearTimeout(timer); reject(error); }
            });
            this.ws.send(JSON.stringify(payload));
        });
    }

    on(listener) { this.listeners.push(listener); }

    close() { try { this.ws.close(); } catch { /* already closed */ } }
}

// ------------------------------------------------------------------------------------------------ browser

function findBrowser(preference) {
    const order = preference ? [preference] : ['chrome', 'edge'];
    for (const name of order) {
        const exe = BROWSERS[name];
        if (!exe) throw new Error(`Unknown browser ${name}`);
        if (fs.existsSync(exe)) return { name, exe };
    }
    throw new Error(`No browser found (${order.map((n) => BROWSERS[n]).join(', ')})`);
}

async function launchBrowser(browser, state, headed) {
    const profileDir = state.profileDir;
    fs.mkdirSync(profileDir, { recursive: true });
    const args = [
        `--user-data-dir=${profileDir}`,
        '--remote-debugging-port=0',
        '--remote-debugging-address=127.0.0.1',
        '--no-first-run', '--no-default-browser-check', '--no-service-autorun',
        '--disable-background-networking', '--disable-component-update', '--disable-sync', '--disable-default-apps',
        '--disable-extensions', '--disable-client-side-phishing-detection', '--disable-domain-reliability',
        '--metrics-recording-only', '--safebrowsing-disable-auto-update', '--password-store=basic',
        '--disable-features=Translate,OptimizationHints,MediaRouter,DialMediaRouteProvider,AutofillServerCommunication,InterestFeedContentSuggestions',
        '--mute-audio', '--autoplay-policy=no-user-gesture-required', '--window-size=1366,900'
    ];
    if (!headed) args.unshift('--headless=new');
    args.push('about:blank');
    const child = spawn(browser.exe, args, { stdio: 'ignore', windowsHide: !headed });
    const exited = new Promise((resolve) => child.once('exit', resolve));
    child.once('error', () => { /* reported through the DevToolsActivePort timeout */ });
    // Recorded at once, so cleanup can stop this PID even if startup fails below.
    state.child = child;
    state.exited = exited;

    const portFile = path.join(profileDir, 'DevToolsActivePort');
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline) {
        if (child.exitCode !== null) throw new Error(`${browser.name} exited during startup (code ${child.exitCode})`);
        if (fs.existsSync(portFile)) {
            const [port, wsPath] = fs.readFileSync(portFile, 'utf8').split(/\r?\n/);
            if (port && wsPath) return `ws://127.0.0.1:${port.trim()}${wsPath.trim()}`;
        }
        await delay(100);
    }
    throw new Error(`${browser.name} did not write ${portFile}`);
}

function listProcessesWithMarker(marker) {
    // Our browser's helper processes carry --user-data-dir=<profile>; match that exact, unique folder.
    try {
        const script = `Get-CimInstance Win32_Process -Filter "Name = 'chrome.exe' or Name = 'msedge.exe'" | ` +
            `Select-Object ProcessId, CommandLine | ConvertTo-Json -Compress`;
        // Without PSModulePath: one inherited from PowerShell 7 (node started from pwsh) makes Windows PowerShell 5.1
        // autoload 7's CimCmdlets, which it can't load, and the list would come back empty.
        const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => name.toLowerCase() !== 'psmodulepath'));
        const output = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', windowsHide: true, env });
        if (!output.trim()) return [];
        const rows = [].concat(JSON.parse(output));
        return rows.filter((row) => row.CommandLine && row.CommandLine.toLowerCase().includes(marker.toLowerCase())).map((row) => row.ProcessId);
    } catch {
        return [];
    }
}

async function closeBrowser(state) {
    const closed = { pid: state.child ? state.child.pid : null, method: null, leftovers: [] };
    if (!state.child) return closed;
    if (state.cdp) {
        try { await state.cdp.send('Browser.close', {}, undefined, 5000); closed.method = 'Browser.close'; } catch { /* fall through */ }
        state.cdp.close();
    }
    const exitedInTime = await Promise.race([state.exited.then(() => true), delay(10000).then(() => false)]);
    if (!exitedInTime && state.child.exitCode === null) {
        state.child.kill();                     // only the PID we started
        closed.method = 'kill';
        await Promise.race([state.exited, delay(5000)]);
    }
    // Helper processes that outlived their browser: stop those whose command line names our profile folder.
    for (const pid of listProcessesWithMarker(state.profileDir)) {
        try { process.kill(pid); closed.leftovers.push(pid); } catch { /* already gone */ }
    }
    if (!state.keepProfile) {
        for (let attempt = 0; attempt < 10; attempt++) {
            try { fs.rmSync(state.profileDir, { recursive: true, force: true }); break; } catch { await delay(500); }
        }
    }
    return closed;
}

// ------------------------------------------------------------------------------------------------ keys

const NAMED_KEYS = {
    Enter: { code: 'Enter', keyCode: 13, text: '\r' }, Escape: { code: 'Escape', keyCode: 27 }, Tab: { code: 'Tab', keyCode: 9 },
    Backspace: { code: 'Backspace', keyCode: 8 }, Delete: { code: 'Delete', keyCode: 46 }, Space: { key: ' ', code: 'Space', keyCode: 32, text: ' ' },
    ArrowLeft: { code: 'ArrowLeft', keyCode: 37 }, ArrowUp: { code: 'ArrowUp', keyCode: 38 }, ArrowRight: { code: 'ArrowRight', keyCode: 39 },
    ArrowDown: { code: 'ArrowDown', keyCode: 40 }, Home: { code: 'Home', keyCode: 36 }, End: { code: 'End', keyCode: 35 },
    PageUp: { code: 'PageUp', keyCode: 33 }, PageDown: { code: 'PageDown', keyCode: 34 },
    // A modifier on its own, as a real keyboard sends it before the combo ("Shift" then "Shift+B", as measured in Chrome).
    Shift: { code: 'ShiftLeft', keyCode: 16, modifier: 8 }, Control: { code: 'ControlLeft', keyCode: 17, modifier: 2 },
    Alt: { code: 'AltLeft', keyCode: 18, modifier: 1 }, Meta: { code: 'MetaLeft', keyCode: 91, modifier: 4 }
};
for (let n = 1; n <= 12; n++) NAMED_KEYS[`F${n}`] = { code: `F${n}`, keyCode: 111 + n };

const PUNCTUATION = {
    ',': ['Comma', 188], '.': ['Period', 190], '/': ['Slash', 191], ';': ['Semicolon', 186], "'": ['Quote', 222],
    '[': ['BracketLeft', 219], ']': ['BracketRight', 221], '-': ['Minus', 189], '=': ['Equal', 187], '`': ['Backquote', 192],
    '\\': ['Backslash', 220], '+': ['Equal', 187]
};

function parseKey(combo) {
    // "Shift+B", "Ctrl+Enter", "+", "Shift++". Letter case is ignored: Shift decides it.
    let keyName;
    let parts;
    if (combo === '+') { keyName = '+'; parts = []; }
    else if (combo.endsWith('++')) { keyName = '+'; parts = combo.slice(0, -2).split('+'); }
    else { parts = combo.split('+'); keyName = parts.pop(); }
    let modifiers = 0;
    for (const part of parts) {
        const m = part.toLowerCase();
        if (m === 'alt') modifiers |= 1;
        else if (m === 'ctrl' || m === 'control') modifiers |= 2;
        else if (m === 'meta' || m === 'cmd') modifiers |= 4;
        else if (m === 'shift') modifiers |= 8;
        else throw new Error(`Unknown modifier ${part} in ${combo}`);
    }
    const shift = (modifiers & 8) !== 0;
    if (NAMED_KEYS[keyName]) {
        const named = NAMED_KEYS[keyName];
        if (named.modifier) modifiers |= named.modifier;
        return { key: named.key || keyName, code: named.code, keyCode: named.keyCode, text: named.text, modifiers };
    }
    if (keyName.length !== 1) throw new Error(`Unknown key ${keyName} in ${combo}`);
    if (/^[a-z]$/i.test(keyName)) {
        const upper = keyName.toUpperCase();
        const key = shift ? upper : upper.toLowerCase();
        return { key, code: `Key${upper}`, keyCode: upper.charCodeAt(0), text: key, modifiers };
    }
    if (/[0-9]/.test(keyName)) return { key: keyName, code: `Digit${keyName}`, keyCode: keyName.charCodeAt(0), text: keyName, modifiers };
    const punctuation = PUNCTUATION[keyName];
    return { key: keyName, code: punctuation ? punctuation[0] : '', keyCode: punctuation ? punctuation[1] : 0, text: keyName, modifiers };
}

// ------------------------------------------------------------------------------------------------ runner

function substitute(value, vars) {
    if (typeof value !== 'string') return value;
    return value.replace(/\$\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (match, name) => (name in vars ? String(vars[name]) : match));
}

function preview(value) {
    const text = typeof value === 'string' ? value : JSON.stringify(value);
    if (text === undefined) return undefined;
    return text.length > 500 ? text.slice(0, 500) + '…' : text;
}

async function main() {
    const options = parseArgs(process.argv.slice(2));
    if (options.help) { process.stdout.write(HELP); return 0; }

    const sandbox = sandboxPaths(options.sandbox);
    let baseUrl = options.url;
    if (!baseUrl) {
        if (!fs.existsSync(sandbox.state)) throw new Error(`${sandbox.state} not found; start the sandbox with dev-server.ps1 or pass --url`);
        const state = readJson(sandbox.state);
        baseUrl = `http://127.0.0.1:${state.Port}${state.BaseUrl || ''}`;
    }
    baseUrl = baseUrl.replace(/\/+$/, '');
    assertSafeUrl(baseUrl);

    let definition = { steps: [] };
    let stepsDir = process.cwd();
    if (options.stepsFile) {
        let file = path.resolve(options.stepsFile);
        if (!fs.existsSync(file)) file = path.join(scriptDir, 'webchecks', options.stepsFile);
        definition = readJson(file);
        stepsDir = path.dirname(file);
    }
    // initScript {"file": ...}: relative to the steps file's folder, then the current folder.
    const resolveScriptFile = (name) => {
        const candidates = path.isAbsolute(name) ? [name] : [path.join(stepsDir, name), path.resolve(name)];
        const found = candidates.find((candidate) => fs.existsSync(candidate));
        if (!found) throw new Error(`initScript file ${name} not found (looked in ${candidates.join(', ')})`);
        return found;
    };
    const steps = [...(definition.steps || []), ...options.steps];
    const failOnException = options.failOnException || definition.failOnException === true;
    const defaultTimeout = definition.timeout || options.timeout;
    if (steps.length === 0) throw new Error('No steps (use --steps or step flags; see --help)');

    const credentials = fs.existsSync(sandbox.credentials) ? readJson(sandbox.credentials) : null;
    const browser = findBrowser(options.browser);
    const profileDir = path.join(sandbox.browser, `profile-${Date.now()}-${process.pid}`);
    const screenshotDir = path.join(sandbox.browser, 'screenshots');

    const result = {
        ok: false, name: definition.name || null, sandbox: options.sandbox, baseUrl, browser: browser.name, pid: null,
        steps: [], console: [], exceptions: [], failedRequests: [], blockedRequests: [], delayedRequests: [], screenshots: [], vars: {}, closed: null
    };
    const vars = {};
    const state = { child: null, exited: null, cdp: null, profileDir, keepProfile: !!options.keepProfile };

    const cleanup = async () => {
        if (state.closing) return state.closing;
        state.closing = closeBrowser(state);
        return state.closing;
    };
    const onSignal = () => { cleanup().finally(() => process.exit(130)); };
    process.once('SIGINT', onSignal);
    process.once('SIGTERM', onSignal);

    try {
        const wsUrl = await launchBrowser(browser, state, options.headed);
        result.pid = state.child.pid;
        const cdp = await Cdp.connect(wsUrl);
        state.cdp = cdp;

        // The pages the steps run in: "main", the browser's default context, and each {"context": "<name>"} step's own
        // browser context (Target.createBrowserContext: its own cookies and localStorage, as another browser or device has
        // them; signed in on its own), made on first use. Every page gets the same request policy and collection; the
        // steps act on the current one (`current`), which a context step switches.
        const pages = new Map();
        const bySession = new Map();
        let current = null;
        const send = (method, params = {}, timeoutMs) => cdp.send(method, params, current.sessionId, timeoutMs);
        const DESKTOP = { width: 1366, height: 900, deviceScaleFactor: 1, mobile: false, touch: false, media: {} };
        const openPage = async (name, browserContextId) => {
            const { targetId } = await cdp.send('Target.createTarget', browserContextId ? { url: 'about:blank', browserContextId } : { url: 'about:blank' });
            const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
            const page = { name, sessionId, targetId, browserContextId: browserContextId || null, loadWaiters: [], emulation: { ...DESKTOP } };
            pages.set(name, page);
            bySession.set(sessionId, page);
            const own = (method, params = {}) => cdp.send(method, params, sessionId);
            await own('Fetch.enable', { patterns: [{ urlPattern: '*' }] });
            await Promise.all([own('Page.enable'), own('Runtime.enable'), own('Log.enable'), own('Network.enable')]);
            await own('Network.setCacheDisabled', { cacheDisabled: true });
            await own('Network.setBypassServiceWorker', { bypass: true });
            await own('Emulation.setDeviceMetricsOverride', { width: DESKTOP.width, height: DESKTOP.height, deviceScaleFactor: 1, mobile: false });
            return page;
        };

        let delayRule = null;
        const requests = new Map();
        cdp.on((message) => {
            const page = bySession.get(message.sessionId);
            if (!page) return;
            const p = message.params || {};
            // What a page other than the main one logged carries its name.
            const tag = (entry) => (page.name === 'main' ? entry : { ...entry, context: page.name });
            const reply = (method, params) => cdp.send(method, params, page.sessionId);
            switch (message.method) {
                case 'Runtime.consoleAPICalled':
                    result.console.push(tag({
                        type: p.type,
                        text: (p.args || []).map((a) => (a.value !== undefined ? String(a.value) : a.description || a.type)).join(' ').slice(0, 2000),
                        url: p.stackTrace && p.stackTrace.callFrames[0] ? p.stackTrace.callFrames[0].url : undefined
                    }));
                    break;
                case 'Runtime.exceptionThrown': {
                    const d = p.exceptionDetails || {};
                    const exception = tag({
                        text: ((d.exception && d.exception.description) || d.text || 'exception').slice(0, 2000),
                        url: d.url, line: d.lineNumber, column: d.columnNumber
                    });
                    // jellyfin-web's own noise in a headless browser (page-noise.mjs): kept, marked, never counted.
                    const known = knownPageNoise(exception);
                    if (known) exception.known = known;
                    result.exceptions.push(exception);
                    break;
                }
                case 'Log.entryAdded':
                    if (p.entry && (p.entry.level === 'error' || p.entry.level === 'warning')) {
                        result.console.push(tag({ type: `log-${p.entry.level}`, source: p.entry.source, text: String(p.entry.text).slice(0, 2000), url: p.entry.url }));
                    }
                    break;
                case 'Network.requestWillBeSent':
                    requests.set(page.sessionId + ':' + p.requestId, { url: p.request.url, method: p.request.method });
                    break;
                case 'Network.responseReceived':
                    if (p.response && p.response.status >= 400) {
                        result.failedRequests.push(tag({ url: p.response.url, status: p.response.status, method: (requests.get(page.sessionId + ':' + p.requestId) || {}).method }));
                    }
                    break;
                case 'Network.loadingFailed': {
                    const request = requests.get(page.sessionId + ':' + p.requestId) || {};
                    if (p.blockedReason || p.errorText === 'net::ERR_BLOCKED_BY_CLIENT') break;   // listed under blockedRequests
                    if (p.canceled) break;
                    result.failedRequests.push(tag({ url: request.url, method: request.method, error: p.errorText }));
                    break;
                }
                case 'Fetch.requestPaused':
                    if (options.allowExternal || isAllowedRequest(p.request.url)) {
                        // delayRequests: hold matching requests before they reach the server (a slow script).
                        const hold = delayRule && p.request.url.includes(delayRule.url) ? delayRule.ms : 0;
                        if (hold > 0) {
                            result.delayedRequests.push(tag({ url: p.request.url, ms: hold }));
                            setTimeout(() => reply('Fetch.continueRequest', { requestId: p.requestId }).catch(() => {}), hold);
                        } else {
                            reply('Fetch.continueRequest', { requestId: p.requestId }).catch(() => {});
                        }
                    } else {
                        result.blockedRequests.push(p.request.url);
                        reply('Fetch.failRequest', { requestId: p.requestId, errorReason: 'BlockedByClient' }).catch(() => {});
                    }
                    break;
                case 'Page.loadEventFired': {
                    const waiters = page.loadWaiters;
                    page.loadWaiters = [];
                    waiters.forEach((resolve) => resolve());
                    break;
                }
                default:
                    break;
            }
        });

        current = await openPage('main', null);
        let emulation = current.emulation;

        // emulate: {"width", "height", "deviceScaleFactor", "mobile", "touch", "media": {"forced-colors": "active", ...}}
        // or "desktop". Given fields change, the others stay; media features not named are cleared.
        const emulate = async (value) => {
            if (value === 'desktop') {
                value = DESKTOP;
            } else if (!value || typeof value !== 'object' || Array.isArray(value)) {
                throw new Error('emulate needs "desktop" or {width, height, deviceScaleFactor, mobile, touch, media}');
            }

            const known = Object.keys(DESKTOP);
            const unknown = Object.keys(value).filter((k) => !known.includes(k));
            if (unknown.length > 0) throw new Error(`emulate: unknown field(s) ${unknown.join(', ')} (${known.join(', ')})`);
            emulation = { ...emulation, ...value };
            current.emulation = emulation;
            await send('Emulation.setDeviceMetricsOverride', {
                width: Number(emulation.width), height: Number(emulation.height),
                deviceScaleFactor: Number(emulation.deviceScaleFactor), mobile: !!emulation.mobile
            });
            // Touch emulation also makes (pointer: coarse) and (hover: none) match, as on a phone.
            await send('Emulation.setTouchEmulationEnabled', emulation.touch ? { enabled: true, maxTouchPoints: 5 } : { enabled: false });
            const features = Object.entries(emulation.media || {}).map(([name, v]) => ({ name, value: String(v) }));
            await send('Emulation.setEmulatedMedia', { media: '', features });
            return { ...emulation };
        };

        const waitForLoad = (timeoutMs) => new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error('page load timed out')), timeoutMs);
            current.loadWaiters.push(() => { clearTimeout(timer); resolve(); });
        });

        // {"context": "<name>"}: the following steps run in that page ("main" is the first one); a name not used yet
        // gets its own browser context and page, with nothing in its storage (sign in there with a login step).
        const switchContext = async (name) => {
            if (typeof name !== 'string' || !/^[a-z][a-z0-9-]{0,30}$/.test(name)) throw new Error('context needs a name such as "main" or "window-b"');
            let page = pages.get(name);
            const created = !page;
            if (!page) {
                const { browserContextId } = await cdp.send('Target.createBrowserContext', { disposeOnDetach: true });
                page = await openPage(name, browserContextId);
            }
            current = page;
            emulation = page.emulation;
            await cdp.send('Target.activateTarget', { targetId: page.targetId }).catch(() => {});
            return { context: name, created };
        };

        const evaluate = async (expression, { body = false, timeoutMs = defaultTimeout } = {}) => {
            const source = body ? `(async () => { ${expression}\n})()` : `(async () => (${expression}))()`;
            const wrapped = `(() => { globalThis.__vars = ${JSON.stringify(vars)}; return ${source}; })()`;
            const response = await send('Runtime.evaluate', { expression: wrapped, awaitPromise: true, returnByValue: true, userGesture: true }, timeoutMs + 5000);
            if (response.exceptionDetails) {
                const d = response.exceptionDetails;
                throw new Error(((d.exception && d.exception.description) || d.text || 'evaluation failed').split('\n')[0]);
            }
            return response.result ? response.result.value : undefined;
        };

        const resolveUrl = (target) => {
            if (/^[a-z]:[\\/]/i.test(target)) {
                // Git Bash turns an argument such as /web/index.html into C:/Program Files/Git/web/index.html.
                throw new Error(`${target} is a Windows path, not a page path; from Git Bash set MSYS_NO_PATHCONV=1 (or run from PowerShell)`);
            }
            const url = /^[a-z]+:/i.test(target) ? target : baseUrl + (target.startsWith('/') ? target : '/' + target);
            if (!options.allowExternal && !url.startsWith('about:')) assertSafeUrl(url);
            return url;
        };

        const navigate = async (target, timeoutMs) => {
            const url = resolveUrl(target);
            const loaded = waitForLoad(timeoutMs);
            const response = await send('Page.navigate', { url });
            if (response.errorText) throw new Error(`navigation to ${url} failed: ${response.errorText}`);
            if (response.loaderId) await loaded; else loaded.catch(() => {});
            return url;
        };

        const waitFor = async (expression, timeoutMs) => {
            const deadline = Date.now() + timeoutMs;
            let lastError = null;
            while (Date.now() < deadline) {
                try {
                    const value = await evaluate(expression, { timeoutMs: Math.max(1000, deadline - Date.now()) });
                    if (value) return value;
                    lastError = null;
                } catch (error) {
                    lastError = error;
                }
                await delay(250);
            }
            throw new Error(`timed out after ${timeoutMs} ms waiting for ${expression}${lastError ? ` (last error: ${lastError.message})` : ''}`);
        };

        const elementCenter = async (selector) => {
            const box = await evaluate(`(() => {
                const el = document.querySelector(${JSON.stringify(selector)});
                if (!el) return null;
                el.scrollIntoView({ block: 'center', inline: 'center' });
                const r = el.getBoundingClientRect();
                return { x: r.left + r.width / 2, y: r.top + r.height / 2, w: r.width, h: r.height };
            })()`);
            if (!box) throw new Error(`no element matches ${selector}`);
            if (box.w === 0 || box.h === 0) throw new Error(`${selector} is not visible`);
            return box;
        };

        const pressKey = async (combo) => {
            const k = parseKey(combo);
            const base = { key: k.key, code: k.code, windowsVirtualKeyCode: k.keyCode, nativeVirtualKeyCode: k.keyCode, modifiers: k.modifiers };
            const printable = k.text && (k.modifiers & ~8) === 0;
            await send('Input.dispatchKeyEvent', { type: printable ? 'keyDown' : 'rawKeyDown', ...base, ...(printable ? { text: k.text, unmodifiedText: k.text } : {}) });
            await send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
        };

        const login = async (userName, timeoutMs, stay) => {
            if (!credentials || !credentials.users || !credentials.users[userName]) {
                throw new Error(`no credentials for ${userName} in ${sandbox.credentials}; run Initialize-Sandbox.ps1 -Name ${options.sandbox}`);
            }
            const password = credentials.users[userName].password;
            // stay: sign in on the login page already shown (for example after a signed-out deep link redirected to
            // #/login?serverid=…&url=…), so the web client's own redirect back to `url` is exercised.
            if (!stay) await navigate('/web/index.html#/login', timeoutMs);
            // After signing out without a reload, jellyfin-web keeps the earlier login view hidden in its view cache, so
            // the ids below exist more than once: always use the login page that is shown.
            const shownLogin = `[...document.querySelectorAll('.page')].find(p => !p.classList.contains('hide') && p.querySelector('#txtManualName') && p.getClientRects().length > 0)`;
            const clearFields = `(() => { document.querySelectorAll('#txtManualName, #txtManualPassword').forEach(el => { el.value = ''; }); return true; })()`;
            await waitFor(`!!(${shownLogin})`, timeoutMs);
            await evaluate(`(() => {
                const page = ${shownLogin};
                const form = page.querySelector('.manualLoginForm');
                if (form && form.classList.contains('hide')) { const b = page.querySelector('.btnManual'); if (b) b.click(); }
                return true;
            })()`);
            await waitFor(`(() => { const page = ${shownLogin}; const f = page && page.querySelector('.manualLoginForm'); return !!f && !f.classList.contains('hide'); })()`, timeoutMs);
            // jellyfin-web sometimes re-renders the login form while we type (after a hash change to #/login), which
            // leaves the fields empty. Check what the fields hold (the password only by length) and retype. Text is only
            // inserted into the field that has the focus, so the password never lands in the user name field.
            try {
                for (let attempt = 1; ; attempt++) {
                    let focused = true;
                    for (const [selector, text] of [['#txtManualName', userName], ['#txtManualPassword', password]]) {
                        focused = await evaluate(`(() => { const page = ${shownLogin}; const el = page && page.querySelector(${JSON.stringify(selector)});
                            if (!el) return false; el.focus(); el.value = ''; return document.activeElement === el; })()`);
                        if (!focused) break;
                        await send('Input.insertText', { text });
                    }
                    const typed = focused && await evaluate(`(() => { const page = ${shownLogin}; const n = page && page.querySelector('#txtManualName'); const p = page && page.querySelector('#txtManualPassword');
                        return !!n && !!p && n.value === ${JSON.stringify(userName)} && p.value.length === ${password.length}; })()`);
                    if (typed) break;
                    if (attempt >= 3) throw new Error('the login form lost the typed user name or password three times');
                    await evaluate(clearFields);
                    await delay(500);
                }
            } catch (e) {
                // Never leave a password in a field for the failure screenshot.
                await evaluate(clearFields).catch(() => {});
                throw e;
            }
            await evaluate(`(() => { (${shownLogin}).querySelector('.manualLoginForm button[type=submit]').click(); return true; })()`);
            await waitFor(`!!(window.ApiClient && ApiClient.accessToken() && ApiClient.getCurrentUserId() && !location.hash.startsWith('#/login'))`, timeoutMs);
            return evaluate(`({ user: ${JSON.stringify(userName)}, userId: ApiClient.getCurrentUserId(), hash: location.hash })`);
        };

        const actions = ['login', 'goto', 'waitFor', 'eval', 'script', 'assert', 'click', 'type', 'key', 'screenshot', 'sleep', 'reload', 'noExceptions', 'initScript', 'emulate', 'delayRequests', 'cdp', 'context'];
        // After a failure only the steps marked "finally": true run (a webcheck putting back what it changed).
        let failed = false;
        for (const [index, rawStep] of steps.entries()) {
            if (failed && rawStep.finally !== true) continue;
            const action = actions.find((a) => a in rawStep);
            const entry = { index, action, name: rawStep.name || undefined, ok: false };
            const started = Date.now();
            const timeoutMs = rawStep.timeout || defaultTimeout;
            try {
                if (!action) throw new Error(`step ${index} has no action (${actions.join(', ')})`);
                // "if": the step runs only while this page expression is truthy (a retry, a fallback); a skipped step passes,
                // marked skipped, and saves nothing.
                const skipped = 'if' in rawStep && !(await evaluate(substitute(rawStep.if, vars), { timeoutMs }));
                if (skipped) entry.skipped = true;
                const value = skipped ? undefined : substitute(rawStep[action], vars);
                let output;
                switch (skipped ? null : action) {
                    case 'login': output = await login(value, timeoutMs, rawStep.stay === true); break;
                    case 'goto': output = await navigate(value, timeoutMs); break;
                    case 'reload': {
                        const loaded = waitForLoad(timeoutMs);
                        await send('Page.reload', { ignoreCache: true });
                        await loaded;
                        break;
                    }
                    case 'waitFor': output = await waitFor(value, timeoutMs); break;
                    case 'eval': output = await evaluate(value, { timeoutMs }); break;
                    case 'script': output = await evaluate(value, { body: true, timeoutMs }); break;
                    case 'assert': {
                        output = await evaluate(value, { timeoutMs });
                        if (!output) throw new Error(rawStep.message || `assertion failed: ${value}`);
                        break;
                    }
                    case 'click': {
                        if (rawStep.js) {
                            const clicked = await evaluate(`(() => { const el = document.querySelector(${JSON.stringify(value)}); if (!el) return false; el.click(); return true; })()`);
                            if (!clicked) throw new Error(`no element matches ${value}`);
                        } else {
                            const box = await elementCenter(value);
                            for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) {
                                await send('Input.dispatchMouseEvent', { type, x: box.x, y: box.y, button: 'left', clickCount: type === 'mouseMoved' ? 0 : 1 });
                            }
                        }
                        break;
                    }
                    case 'type': {
                        if (rawStep.selector) {
                            const selector = JSON.stringify(rawStep.selector);
                            await evaluate(`(() => { const el = document.querySelector(${selector}); if (!el) throw new Error('no element matches ' + ${selector}); el.focus(); ${rawStep.clear ? "el.value = '';" : ''} return true; })()`);
                        }
                        await send('Input.insertText', { text: value });
                        if (rawStep.secret) entry.secret = true;
                        break;
                    }
                    case 'key': await pressKey(value); break;
                    case 'screenshot': {
                        const file = path.isAbsolute(value) ? value : path.join(screenshotDir, value);
                        fs.mkdirSync(path.dirname(file), { recursive: true });
                        const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
                        fs.writeFileSync(file, Buffer.from(shot.data, 'base64'));
                        result.screenshots.push(file);
                        output = file;
                        break;
                    }
                    case 'sleep': await delay(Number(value)); break;
                    case 'emulate': output = await emulate(value); break;
                    case 'context': output = await switchContext(value); break;
                    case 'cdp': {
                        // {"cdp": "Input.<method>", "params": {...} or "<page expression answering the params>"}: one raw
                        // DevTools command of the Input domain only (a touch gesture, a touch, mouse or key event the other
                        // steps don't make), so the request policy and the browser's other domains stay as they are.
                        if (typeof value !== 'string' || !/^Input\.[A-Za-z]+$/.test(value)) throw new Error('cdp takes an Input.<method> name, such as Input.synthesizeScrollGesture');
                        const params = typeof rawStep.params === 'string'
                            ? await evaluate(substitute(rawStep.params, vars), { timeoutMs })
                            : (rawStep.params || {});
                        if (!params || typeof params !== 'object') throw new Error('cdp params must be an object, or a page expression answering one');
                        output = { method: value, params, result: await send(value, params) };
                        break;
                    }
                    case 'delayRequests': {
                        // {"url": "<substring of the request URL>", "ms": 1500} holds every matching request of the page
                        // that long before it goes out; null, false or {} stops.
                        if (value && typeof value === 'object' && typeof value.url === 'string' && value.url && Number(value.ms) > 0) {
                            delayRule = { url: value.url, ms: Math.min(Number(value.ms), 20000) };
                        } else if (!value || (typeof value === 'object' && Object.keys(value).length === 0)) {
                            delayRule = null;
                        } else {
                            throw new Error('delayRequests needs {"url": "...", "ms": <1..20000>} or null');
                        }
                        output = delayRule;
                        break;
                    }
                    case 'initScript': {
                        // Runs in every later document of this tab before any page script (a probe that must see the
                        // page boot). Takes effect from the next goto, reload or login.
                        let source = value;
                        let file = null;
                        if (value && typeof value === 'object' && typeof value.file === 'string') {
                            file = resolveScriptFile(value.file);
                            source = fs.readFileSync(file, 'utf8');
                        }
                        if (typeof source !== 'string' || !source.trim()) throw new Error('initScript needs JavaScript source or {"file": "..."}');
                        const added = await send('Page.addScriptToEvaluateOnNewDocument', { source });
                        output = { identifier: added.identifier, file: file || undefined, bytes: Buffer.byteLength(source) };
                        break;
                    }
                    case 'noExceptions': {
                        const ignore = rawStep.ignore ? new RegExp(rawStep.ignore) : null;
                        // The pattern is tested against the message and the script URL (for noise such as /CustomRatings/script).
                        // jellyfin-web's known noise (page-noise.mjs) never counts.
                        const relevant = result.exceptions.filter((e) => !e.known && (!ignore || !(ignore.test(e.text) || ignore.test(e.url || ''))));
                        if (relevant.length > 0) throw new Error(`${relevant.length} uncaught exception(s): ${relevant[0].text.split('\n')[0]}`);
                        break;
                    }
                    default: break;
                }
                if (!skipped && rawStep.save) vars[rawStep.save] = output;
                // full: keep the whole value (for evidence); otherwise a 500-character preview.
                if (!skipped && action !== 'type') entry.value = rawStep.full === true ? output : preview(output);
                entry.ok = true;
            } catch (error) {
                entry.error = error.message;
            }
            entry.ms = Date.now() - started;
            result.steps.push(entry);
            if (!entry.ok && !rawStep.optional && !failed) {
                try {
                    fs.mkdirSync(screenshotDir, { recursive: true });
                    const file = path.join(screenshotDir, `failure-${Date.now()}.png`);
                    const shot = await send('Page.captureScreenshot', { format: 'png' });
                    fs.writeFileSync(file, Buffer.from(shot.data, 'base64'));
                    result.screenshots.push(file);
                } catch { /* best effort */ }
                failed = true;
            }
        }

        result.vars = Object.fromEntries(Object.entries(vars).map(([k, v]) => [k, preview(v)]));
        const stepsOk = result.steps.length === steps.length && result.steps.every((s) => s.ok || steps[s.index].optional);
        result.ok = stepsOk && (!failOnException || result.exceptions.every((e) => e.known));
        if (stepsOk && !result.ok) result.error = 'uncaught exceptions on the page';
    } catch (error) {
        result.error = error.message;
    } finally {
        result.closed = await cleanup();
        process.removeListener('SIGINT', onSignal);
        process.removeListener('SIGTERM', onSignal);
    }

    const json = redactSecrets(JSON.stringify(result, null, 2));
    if (options.out) fs.writeFileSync(path.resolve(options.out), json + '\n');
    process.stdout.write(json + '\n');
    return result.ok ? 0 : 1;
}

main().then((code) => { process.exitCode = code; }, (error) => {
    process.stdout.write(redactSecrets(JSON.stringify({ ok: false, error: error.message }, null, 2)) + '\n');
    process.exitCode = 2;
});
