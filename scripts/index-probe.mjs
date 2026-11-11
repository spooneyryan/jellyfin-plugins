#!/usr/bin/env node
// Raw HTTP checks of the kit's index.html injection, with the kit prefix. Node 22 built-ins only. Loopback only, never port 8096. The admin token
// from the sandbox's credentials.json is used for the plugin settings and is never printed.
//
//   node scripts/index-probe.mjs --sandbox default [--plugin Bookmarks] [--fault] [--timing 10] [--wait 60] [--out result.json]
//
// For every plugin of the sandbox (sandbox.json "Plugins", or --plugin), it finds the plugin's tag by its route
// (src="../<Route>/ClientScript?v=<version>-<mvid8>", with the marker data-<pfx>-plugin) and checks:
//   - HEAD passes through; a browser-like GET with Accept-Encoding, If-None-Match and If-Modified-Since, /web/,
//     a query string, a Range request, /WEB/INDEX.HTML and, under a BaseUrl, the BaseUrl in upper case each get 200,
//     one tag per plugin, no ETag, Last-Modified, Content-Encoding or Accept-Ranges, no-cache and the right length;
//   - the same behind Jellyfin's legacy /emby and /mediabrowser prefixes (removed by its UsePathTrim inside the BaseUrl),
//     where the tag's src must also reach the plugin's ClientScript;
//   - /web (no slash) and, under a BaseUrl, /web/index.html without it are redirects without our tags;
//   - the tag's src resolves under the BaseUrl to an anonymous ClientScript with an ETag and a 304;
//   - the bundle, a stylesheet and an API response stay compressed, the stylesheet answers 304, and the bundle is
//     byte-identical with the plugin's EnableWebUi setting on and off;
//   - EnableWebUi=false removes that plugin's tag on the next request and leaves every other tag; true brings it back;
//   - community tags (Plugin Pages through File Transformation, Jellyfin Enhanced, JavaScript Injector) appear once each
//     when sandbox.json lists them (the probe first waits for File Transformation's late tags), and the retired
//     File Transformation tag of Bookmarks 1.0 (data-bookmarks-plugin) never appears;
//   - with --fault (sandboxes without File Transformation only): an index.html that isn't valid UTF-8 is served byte for
//     byte as the file, twice, with at most one warning per server process in the sandbox log. The sandbox's own copy
//     of the web client is changed for this and restored afterwards.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCTION_PORT = 8096;
const RETIRED_MARKERS = ['data-bookmarks-plugin'];
const COMMUNITY_MARKERS = {
    PluginPages: 'plugin="PluginPages"',
    JellyfinEnhanced: '/JellyfinEnhanced/script',
    JavaScriptInjector: 'JavaScriptInjector/public.js'
};

function usage() {
    return 'usage: node scripts/index-probe.mjs --sandbox <name> [--plugin <Name>] [--fault] [--timing <n>] [--wait <s>] [--out <file>]';
}

function parseArgs(argv) {
    const o = { sandbox: null, plugins: [], fault: false, timing: 10, wait: 60, out: null };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--sandbox') o.sandbox = argv[++i];
        else if (a === '--plugin') o.plugins.push(argv[++i]);
        else if (a === '--fault') o.fault = true;
        else if (a === '--timing') o.timing = Number(argv[++i]);
        else if (a === '--wait') o.wait = Number(argv[++i]);
        else if (a === '--out') o.out = argv[++i];
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(usage());
    if (!Number.isInteger(o.timing) || o.timing < 0 || !Number.isFinite(o.wait) || o.wait < 0) throw new Error(usage());
    return o;
}

const opts = parseArgs(process.argv.slice(2));
const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);
const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));
const state = readJson(path.join(sandboxRoot, 'sandbox.json'));
const port = Number(state.Port);
if (!Number.isInteger(port) || port <= 0 || port === PRODUCTION_PORT) throw new Error(`refusing port ${state.Port} (8096 is the real server)`);
const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
const community = new Set(state.Community || []);
const credentialsFile = path.join(sandboxRoot, 'credentials.json');
const token = fs.existsSync(credentialsFile) ? readJson(credentialsFile).users?.admin?.token : null;
const webDir = path.join(sandboxRoot, 'web');
const logDir = path.join(sandboxRoot, 'log');

function readPlugin(name) {
    if (!/^[A-Za-z][A-Za-z0-9]{1,39}$/.test(name)) throw new Error(`bad plugin name ${name}`);
    const yaml = fs.readFileSync(path.join(repoRoot, 'plugins', name, 'build.yaml'), 'utf8');
    const scalar = (key) => (new RegExp(`^${key}:\\s*"?([^"\\r\\n]+)"?\\s*$`, 'm').exec(yaml) || [])[1];
    const assembly = ((/^artifacts:\s*\r?\n\s*-\s*"?([^"\r\n]+)\.dll"?/m.exec(yaml)) || [])[1];
    return { name, route: name, guid: scalar('guid'), version: scalar('version'), assembly };
}

const plugins = (opts.plugins.length ? opts.plugins : state.Plugins || []).map(readPlugin);
if (plugins.length === 0) throw new Error('the sandbox has no plugins of ours');

function authHeader() {
    return `MediaBrowser Client="index-probe", Device="sandbox", DeviceId="index-probe", Version="1.0.0"${token ? `, Token="${token}"` : ''}`;
}

function request(method, p, headers = {}, body = null) {
    return new Promise((resolve, reject) => {
        const started = process.hrtime.bigint();
        const req = http.request({ host: '127.0.0.1', port, method, path: p, headers, agent: false }, (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks), ms: Number(process.hrtime.bigint() - started) / 1e6 }));
        });
        req.on('error', reject);
        req.setTimeout(30000, () => req.destroy(new Error(`timeout ${method} ${p}`)));
        if (body) req.write(body);
        req.end();
    });
}

function decode(res) {
    const enc = (res.headers['content-encoding'] || '').toLowerCase();
    if (enc === 'br') return zlib.brotliDecompressSync(res.body);
    if (enc === 'gzip') return zlib.gunzipSync(res.body);
    if (enc === 'deflate') return zlib.inflateSync(res.body);
    return res.body;
}

const sha = (buf) => crypto.createHash('sha256').update(buf).digest('hex').slice(0, 16);
const count = (text, needle) => text.toLowerCase().split(needle.toLowerCase()).length - 1;
const pick = (h, names) => Object.fromEntries(names.filter((n) => h[n] !== undefined).map((n) => [n, h[n]]));
const HEADERS = ['content-type', 'content-length', 'content-encoding', 'etag', 'last-modified', 'cache-control', 'accept-ranges', 'vary', 'location'];
const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

const checks = [];
const evidence = { sandbox: { name: opts.sandbox, port, baseUrl, community: [...community], time: new Date().toISOString() }, plugins: {} };
function check(name, pass, detail) {
    checks.push({ name, pass: !!pass, detail });
}

async function api(method, p, bodyObject) {
    if (!token) throw new Error('no admin token in credentials.json; run Initialize-Sandbox.ps1');
    const body = bodyObject === undefined ? null : Buffer.from(JSON.stringify(bodyObject));
    const headers = { Authorization: authHeader(), Accept: 'application/json' };
    if (body) { headers['Content-Type'] = 'application/json'; headers['Content-Length'] = body.length; }
    const res = await request(method, baseUrl + p, headers, body);
    if (res.status >= 300) throw new Error(`${method} ${p} -> ${res.status}`);
    return res.body.length ? JSON.parse(res.body.toString('utf8')) : null;
}

async function setWebUi(plugin, enabled) {
    const current = await api('GET', `/Plugins/${plugin.guid}/Configuration`);
    if (!('EnableWebUi' in current)) throw new Error(`${plugin.name}'s configuration has no EnableWebUi`);
    await api('POST', `/Plugins/${plugin.guid}/Configuration`, { ...current, EnableWebUi: enabled });
}

// Our tag for a plugin, found by its route; the marker gives the prefix.
function findTag(text, plugin) {
    const re = new RegExp(`<script data-(jf[a-z0-9]{2,8})-plugin="([^"]*)" src="\\.\\./${escapeRegex(plugin.route)}/ClientScript\\?v=([^"]+)" defer></script>`, 'g');
    const found = [...text.matchAll(re)];
    return found.map((m) => ({ tag: m[0], prefix: m[1], version: m[2], v: m[3], src: `../${plugin.route}/ClientScript?v=${m[3]}` }));
}

const BROWSER = { Accept: 'text/html,application/xhtml+xml', 'Accept-Encoding': 'gzip, deflate, br, zstd', 'User-Agent': 'index-probe' };

function expectedCommunity() {
    const expected = {};
    if (community.has('FileTransformation') && community.has('PluginPages')) expected.PluginPages = 1;
    if (community.has('JellyfinEnhanced')) expected.JellyfinEnhanced = 1;
    if (community.has('JavaScriptInjector')) expected.JavaScriptInjector = 1;
    return expected;
}

function summarize(res) {
    const text = decode(res).toString('utf8');
    const ours = Object.fromEntries(plugins.map((p) => [p.name, findTag(text, p).length]));
    const markers = Object.fromEntries(plugins.map((p) => [p.name, evidence.plugins[p.name]?.prefix ? count(text, `data-${evidence.plugins[p.name].prefix}-plugin`) : null]));
    const others = Object.fromEntries(Object.entries(COMMUNITY_MARKERS).map(([k, v]) => [k, count(text, v)]));
    const retired = Object.fromEntries(RETIRED_MARKERS.map((m) => [m, count(text, m)]));
    return { status: res.status, headers: pick(res.headers, HEADERS), bytes: res.body.length, sha: sha(decode(res)), ours, markers, community: others, retired, ms: Math.round(res.ms * 10) / 10 };
}

function checkIndex(name, res, { without = null } = {}) {
    const s = summarize(res);
    const text = res.body.toString('utf8');
    const problems = [];
    if (s.status !== 200) problems.push(`status ${s.status}`);
    for (const p of plugins) {
        const want = p.name === without ? 0 : 1;
        if (s.ours[p.name] !== want) problems.push(`${p.name} tag x${s.ours[p.name]} (want ${want})`);
        if (s.markers[p.name] !== null && s.markers[p.name] !== want) problems.push(`${p.name} marker x${s.markers[p.name]} (want ${want})`);
    }
    for (const [k, want] of Object.entries(expectedCommunity())) if (s.community[k] !== want) problems.push(`${k} x${s.community[k]} (want ${want})`);
    for (const [k, n] of Object.entries(s.retired)) if (n !== 0) problems.push(`retired ${k} x${n}`);
    if (!without) {
        if (res.headers['etag']) problems.push('ETag present');
        if (res.headers['last-modified']) problems.push('Last-Modified present');
        if (res.headers['accept-ranges']) problems.push(`Accept-Ranges ${res.headers['accept-ranges']}`);
        if (res.headers['content-encoding']) problems.push(`Content-Encoding ${res.headers['content-encoding']}`);
        if (!/no-cache/i.test(res.headers['cache-control'] || '')) problems.push(`Cache-Control ${res.headers['cache-control']}`);
        if (Number(res.headers['content-length']) !== res.body.length) problems.push(`Content-Length ${res.headers['content-length']} vs body ${res.body.length}`);
        if (!/^text\/html/i.test(res.headers['content-type'] || '')) problems.push(`Content-Type ${res.headers['content-type']}`);
        if (!/<\/body>\s*<\/html>\s*$/i.test(text)) problems.push('document does not end with </body></html>');
    }
    check(name, problems.length === 0, { problems, ...s });
    return s;
}

async function waitForIndex(idx) {
    // File Transformation registers its callbacks about 3 s after the server's first answer (measured on 10.11.5).
    const expected = expectedCommunity();
    const deadline = Date.now() + opts.wait * 1000;
    let last = null;
    for (;;) {
        last = await request('GET', idx, BROWSER);
        const s = summarize(last);
        const ready = last.status === 200 && plugins.every((p) => s.ours[p.name] >= 1) && Object.keys(expected).every((k) => s.community[k] >= 1);
        if (ready || Date.now() > deadline) return { ready, waitedMs: opts.wait * 1000 - (deadline - Date.now()), summary: s };
        await delay(500);
    }
}

function countLogLines(pattern) {
    if (!fs.existsSync(logDir)) return 0;
    let n = 0;
    for (const f of fs.readdirSync(logDir).filter((x) => /\.log$/i.test(x))) {
        const text = fs.readFileSync(path.join(logDir, f), 'utf8');
        n += text.split(/\r?\n/).filter((line) => pattern.test(line)).length;
    }
    return n;
}

async function faultCheck(idx) {
    if (community.has('FileTransformation')) {
        check('fault: skipped (File Transformation re-encodes index.html inside our middleware; run --fault in a sandbox without it)', true, null);
        return;
    }
    const file = path.join(webDir, 'index.html');
    const original = fs.readFileSync(file);
    const at = original.lastIndexOf(Buffer.from('</body>'));
    if (at < 0) throw new Error('the sandbox index.html has no </body>');
    // 0xC3 0x28 is not valid UTF-8: the kit's strict decoder throws, and the middleware must serve the file unchanged.
    const broken = Buffer.concat([original.subarray(0, at), Buffer.from('<!-- index-probe fault '), Buffer.from([0xc3, 0x28]), Buffer.from(' -->'), original.subarray(at)]);
    const warnings = Object.fromEntries(plugins.map((p) => [p.name, new RegExp(`${escapeRegex(p.assembly)}\\.Kit\\.WebInjection.*Adding the "?${escapeRegex(p.name)}"? web UI to index\\.html failed`)]));
    const before = Object.fromEntries(plugins.map((p) => [p.name, countLogLines(warnings[p.name])]));
    let first;
    let second;
    try {
        fs.writeFileSync(file, broken);
        first = await request('GET', idx, BROWSER);
        second = await request('GET', idx, BROWSER);
    } finally {
        fs.writeFileSync(file, original);
    }
    const restored = sha(fs.readFileSync(file)) === sha(original);
    await delay(3000);
    const after = Object.fromEntries(plugins.map((p) => [p.name, countLogLines(warnings[p.name])]));
    evidence.fault = {
        first: { status: first.status, headers: pick(first.headers, HEADERS), identicalToFile: Buffer.compare(first.body, broken) === 0 },
        second: { status: second.status, identicalToFile: Buffer.compare(second.body, broken) === 0 },
        warningsBefore: before,
        warningsAfter: after,
        fileRestored: restored
    };
    check('fault: invalid UTF-8 in index.html is served byte for byte (twice), with no tag', first.status === 200 && second.status === 200
        && evidence.fault.first.identicalToFile && evidence.fault.second.identicalToFile && restored, evidence.fault);
    check('fault: at most one warning per plugin and server process, and one is logged',
        plugins.every((p) => after[p.name] - before[p.name] <= 1 && after[p.name] >= 1), { before, after });
}

async function main() {
    const idx = `${baseUrl}/web/index.html`;
    for (const p of plugins) await setWebUi(p, true);

    const ready = await waitForIndex(idx);
    evidence.wait = { ready: ready.ready, waitedMs: Math.max(0, Math.round(ready.waitedMs)) };
    check('index.html carries our tags and the expected community tags (waited for File Transformation)', ready.ready, ready.summary);

    // What each plugin's tag looks like.
    const html0 = decode(await request('GET', idx, BROWSER)).toString('utf8');
    for (const p of plugins) {
        const [found] = findTag(html0, p);
        evidence.plugins[p.name] = found ? { prefix: found.prefix, tag: found.tag } : { prefix: null, tag: null };
        check(`${p.name}: tag data-<pfx>-plugin="${p.version}" with ../${p.route}/ClientScript?v=${p.version}-<mvid8> before </body>`,
            found && found.version === p.version && new RegExp(`^${escapeRegex(p.version)}-[0-9a-f]{8}$`).test(found.v)
            && html0.indexOf(found.tag) < html0.toLowerCase().lastIndexOf('</body>'), found || null);
    }

    // 1. HEAD passes through untouched.
    const head = await request('HEAD', idx, { 'User-Agent': 'index-probe' });
    evidence.head = { status: head.status, headers: pick(head.headers, HEADERS) };
    check('HEAD /web/index.html passes through (200, validators kept, no body)', head.status === 200 && head.body.length === 0 && !!head.headers['etag'], evidence.head);

    // 2. A browser-like GET with every header the middleware strips.
    const conditional = { ...BROWSER, 'If-Modified-Since': head.headers['last-modified'] || new Date().toUTCString(), 'If-None-Match': head.headers['etag'] || '"x"' };
    const main = await request('GET', idx, conditional);
    evidence.index = checkIndex('GET /web/index.html (Accept-Encoding, If-None-Match, If-Modified-Since): 200, one tag each, the injection\'s headers', main);
    const html = main.body.toString('utf8');
    evidence.indexTail = html.slice(Math.max(0, html.length - 1200));
    checkIndex('GET /web/ (default document)', await request('GET', `${baseUrl}/web/`, BROWSER));
    checkIndex('GET /web/index.html?probe=1 (query string)', await request('GET', `${idx}?probe=1`, BROWSER));
    checkIndex('GET with Range: bytes=0-99 and If-Range returns the full 200 document', await request('GET', idx, { ...BROWSER, Range: 'bytes=0-99', 'If-Range': head.headers['etag'] || '"x"' }));

    const upper = summarize(await request('GET', `${baseUrl}/WEB/INDEX.HTML`, BROWSER));
    check('GET /WEB/INDEX.HTML (case): each tag of ours once', upper.status === 200 && plugins.every((p) => upper.ours[p.name] === 1), upper);

    // Jellyfin's UsePathTrim (inside Map(BaseUrl)) serves the web client behind a legacy /emby or /mediabrowser prefix too.
    const legacyIdx = `${baseUrl}/emby/web/index.html`;
    const legacyMain = await request('GET', legacyIdx, conditional);
    checkIndex('GET /emby/web/index.html (legacy prefix, Accept-Encoding and validators): 200, one tag each, the injection\'s headers', legacyMain);
    checkIndex('GET /MediaBrowser/web/ (legacy prefix, any case): 200, one tag each, the injection\'s headers', await request('GET', `${baseUrl}/MediaBrowser/web/`, BROWSER));

    const bare = await request('GET', `${baseUrl}/web`, BROWSER);
    evidence.webNoSlash = { status: bare.status, headers: pick(bare.headers, HEADERS), bytes: bare.body.length };
    check('GET /web (no slash) is a redirect we leave alone', bare.status >= 300 && bare.status < 400 && plugins.every((p) => findTag(bare.body.toString('utf8'), p).length === 0), evidence.webNoSlash);

    if (baseUrl) {
        const outside = await request('GET', '/web/index.html', BROWSER);
        evidence.outsideBaseUrl = { status: outside.status, headers: pick(outside.headers, HEADERS), bytes: outside.body.length };
        check('GET /web/index.html without the BaseUrl: redirected, not transformed', outside.status >= 300 && outside.status < 400
            && plugins.every((p) => findTag(outside.body.toString('utf8'), p).length === 0), evidence.outsideBaseUrl);
        const upperBase = summarize(await request('GET', `${baseUrl.toUpperCase()}/web/index.html`, BROWSER));
        check('GET with the BaseUrl in upper case: each tag of ours once', upperBase.status === 200 && plugins.every((p) => upperBase.ours[p.name] === 1), upperBase);
    }

    // 3. Each src resolves under the BaseUrl to an anonymous ClientScript.
    for (const p of plugins) {
        const [found] = findTag(html, p);
        const resolved = found ? new URL(found.src.replace(/&amp;/g, '&'), `http://127.0.0.1:${port}${idx}`) : null;
        const url = resolved ? resolved.pathname + resolved.search : null;
        check(`${p.name}: the tag's src resolves to ${baseUrl}/${p.route}/ClientScript`, resolved && resolved.pathname === `${baseUrl}/${p.route}/ClientScript`, url);
        const script = await request('GET', url || `${baseUrl}/${p.route}/ClientScript`, { 'Accept-Encoding': 'gzip' });
        const script304 = await request('GET', url || `${baseUrl}/${p.route}/ClientScript`, { 'If-None-Match': script.headers['etag'] || '"x"' });
        evidence.plugins[p.name].clientScript = { url, status: script.status, headers: pick(script.headers, HEADERS), bytes: decode(script).length, conditionalStatus: script304.status };
        check(`${p.name}: ClientScript is anonymous JavaScript with an ETag and no-cache, 304 on If-None-Match`,
            script.status === 200 && /^(text|application)\/javascript/i.test(script.headers['content-type'] || '') && !!script.headers['etag']
            && /no-cache/i.test(script.headers['cache-control'] || '') && script304.status === 304, evidence.plugins[p.name].clientScript);

        const [legacyFound] = findTag(legacyMain.body.toString('utf8'), p);
        const legacyResolved = legacyFound ? new URL(legacyFound.src.replace(/&amp;/g, '&'), `http://127.0.0.1:${port}${legacyIdx}`) : null;
        const legacyUrl = legacyResolved ? legacyResolved.pathname + legacyResolved.search : null;
        const legacyScript = legacyUrl ? await request('GET', legacyUrl, { 'Accept-Encoding': 'gzip' }) : null;
        evidence.plugins[p.name].legacyClientScript = legacyScript
            ? { url: legacyUrl, status: legacyScript.status, headers: pick(legacyScript.headers, HEADERS), sameAsClientScript: sha(decode(legacyScript)) === sha(decode(script)) }
            : null;
        check(`${p.name}: from ${legacyIdx} the tag's src reaches the same ClientScript (${baseUrl}/emby/${p.route}/ClientScript)`,
            legacyScript && legacyResolved.pathname === `${baseUrl}/emby/${p.route}/ClientScript` && legacyScript.status === 200
            && /^(text|application)\/javascript/i.test(legacyScript.headers['content-type'] || '') && evidence.plugins[p.name].legacyClientScript.sameAsClientScript,
            evidence.plugins[p.name].legacyClientScript);
    }

    // 4. Other responses are not ours.
    const bundlePath = `${baseUrl}/web/main.jellyfin.bundle.js`;
    const bundleOn = await request('GET', bundlePath, { 'Accept-Encoding': 'gzip, br' });
    const cssMatch = /href="([^"]+\.css[^"]*)"/.exec(html);
    const cssPath = cssMatch ? new URL(cssMatch[1], `http://127.0.0.1:${port}${idx}`).pathname : null;
    const css = cssPath ? await request('GET', cssPath, { 'Accept-Encoding': 'gzip, br' }) : null;
    const css304 = css ? await request('GET', cssPath, { 'Accept-Encoding': 'gzip, br', 'If-None-Match': css.headers['etag'] || '"x"' }) : null;
    const info = await request('GET', `${baseUrl}/System/Info/Public`, { 'Accept-Encoding': 'gzip', Accept: 'application/json' });
    evidence.nonHtml = {
        bundle: { status: bundleOn.status, headers: pick(bundleOn.headers, HEADERS), decodedSha: sha(decode(bundleOn)) },
        css: css ? { path: cssPath, status: css.status, headers: pick(css.headers, HEADERS), conditionalStatus: css304.status } : null,
        api: { status: info.status, headers: pick(info.headers, HEADERS) }
    };
    check('main.jellyfin.bundle.js stays compressed', bundleOn.status === 200 && !!bundleOn.headers['content-encoding'], evidence.nonHtml.bundle);
    check('a stylesheet stays compressed and answers 304 to If-None-Match', css && css.status === 200 && !!css.headers['content-encoding'] && css304.status === 304, evidence.nonHtml.css);
    check('an API JSON response stays compressed', info.status === 200 && info.headers['content-encoding'] === 'gzip', evidence.nonHtml.api);

    // 5. EnableWebUi, per plugin, takes effect on the next request.
    evidence.webUiOff = {};
    for (const p of plugins) {
        const t0 = Date.now();
        await setWebUi(p, false);
        let off;
        let bundleOff;
        try {
            off = await request('GET', idx, BROWSER);
            bundleOff = await request('GET', bundlePath, { 'Accept-Encoding': 'gzip, br' });
        } finally {
            await setWebUi(p, true);
        }
        evidence.webUiOff[p.name] = { takesEffectMs: Date.now() - t0, index: checkIndex(`EnableWebUi=false for ${p.name}: its tag is gone on the next request, every other tag stays`, off, { without: p.name }) };
        check(`the bundle is byte-identical with ${p.name}'s web UI on and off`, sha(decode(bundleOff)) === evidence.nonHtml.bundle.decodedSha, { on: evidence.nonHtml.bundle.decodedSha, off: sha(decode(bundleOff)) });
        const back = summarize(await request('GET', idx, BROWSER));
        check(`EnableWebUi=true for ${p.name}: its tag is back on the next request`, back.ours[p.name] === 1, back.ours);
    }

    // 6. Timing, middleware on (all plugins) and, for the first plugin, off.
    if (opts.timing > 0) {
        const stats = (a) => { const s = [...a].sort((x, y) => x - y); return { n: s.length, median: +s[Math.floor(s.length / 2)].toFixed(2), max: +s[s.length - 1].toFixed(2) }; };
        const on = [];
        for (let i = 0; i < opts.timing; i++) on.push((await request('GET', idx, BROWSER)).ms);
        const offTimes = [];
        await setWebUi(plugins[0], false);
        try {
            for (let i = 0; i < opts.timing; i++) offTimes.push((await request('GET', idx, BROWSER)).ms);
        } finally {
            await setWebUi(plugins[0], true);
        }
        evidence.timingMs = { on: stats(on), [`${plugins[0].name}Off`]: stats(offTimes) };
    }

    // 7. A failing transform serves the original bytes.
    if (opts.fault) await faultCheck(idx);

    const result = { sandbox: opts.sandbox, pass: checks.every((c) => c.pass), passed: checks.filter((c) => c.pass).length, total: checks.length, checks, evidence };
    const text = JSON.stringify(result, null, 2);
    if (opts.out) fs.writeFileSync(opts.out, text);
    console.log(text);
    process.exitCode = result.pass ? 0 : 1;
}

main().catch((error) => {
    console.error(JSON.stringify({ sandbox: opts.sandbox, pass: false, error: String((error && error.stack) || error) }, null, 2));
    process.exitCode = 2;
});
