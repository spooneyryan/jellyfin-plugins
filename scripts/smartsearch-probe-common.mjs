// What Smart Search's sandbox probes share (smartsearch-differential-probe.mjs, smartsearch-smoke-probe.mjs): the
// sandbox and its users, calls as a user, a search paged to its end, core's /Items, scans and the policy of the
// restricted user. Node 22 built-ins only. Loopback only, never the real server's port (8096: sandbox-policy.mjs'
// PRODUCTION_PORTS). Tokens come from the sandbox's
// credentials.json and never reach the output: every result goes through redact.mjs.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { redactSecrets } from './redact.mjs';
import { isProductionPort, PRODUCTION_PORTS } from './sandbox-policy.mjs';

export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const USERS = ['admin', 'viewer', 'viewer2'];

// The item kinds a query may name, in the order the plugin lists them.
export const ALL_KINDS = ['Movie', 'Series', 'Episode', 'MusicVideo', 'Video', 'BoxSet'];

const readJson = (f) => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''));

// Jellyfin writes GUIDs without dashes; ASP.NET Core's JSON writes them with dashes.
export const guid = (value) => (typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value);

export function openSandbox(name, client) {
    if (!name || !/^[a-z0-9][a-z0-9-]{0,30}$/.test(name)) throw new Error('--sandbox <name> is required (lower-case letters, digits and dashes)');
    const root = path.join(repoRoot, '.sandbox', name);
    const state = readJson(path.join(root, 'sandbox.json'));
    const port = Number(state.Port);
    if (!Number.isInteger(port) || port <= 0 || isProductionPort(port)) throw new Error(`refusing port ${state.Port} (${PRODUCTION_PORTS.join(', ')}: the real server's)`);
    const users = readJson(path.join(root, 'credentials.json')).users || {};
    for (const user of USERS) {
        if (!users[user] || !users[user].token || !users[user].id) throw new Error(`no ${user} in credentials.json; run Initialize-Sandbox.ps1 -Name ${name}`);
    }

    const origin = `http://127.0.0.1:${port}`;
    const baseUrl = (state.BaseUrl || '').replace(/\/+$/, '');
    const work = path.join(root, client);

    async function call(who, method, p, { body, query } = {}) {
        const url = new URL(baseUrl + p, origin);
        if (url.hostname !== '127.0.0.1' || isProductionPort(url.port)) throw new Error('refusing a URL off the sandbox');
        for (const [k, v] of Object.entries(query || {})) {
            if (Array.isArray(v)) for (const item of v) url.searchParams.append(k, String(item));
            else if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
        }
        const headers = {
            Accept: 'application/json',
            Authorization: `MediaBrowser Client="${client}", Device="sandbox", DeviceId="${client}", Version="1.0.0", Token="${users[who].token}"`
        };
        let payload;
        if (body !== undefined) { payload = typeof body === 'string' ? body : JSON.stringify(body); headers['Content-Type'] = 'application/json'; }
        const res = await fetch(url, { method, headers, body: payload, redirect: 'manual', signal: AbortSignal.timeout(180000) });
        const text = await res.text();
        let json = null;
        if (text && /json/i.test(res.headers.get('content-type') || '')) {
            try { json = JSON.parse(text); } catch { json = null; }
        }
        return { status: res.status, json, text };
    }

    async function ok(who, method, p, options) {
        const r = await call(who, method, p, options);
        if (r.status >= 300) throw new Error(`${method} ${p} as ${who}: HTTP ${r.status} ${redactSecrets(r.text.slice(0, 300))}`);
        return r.json;
    }

    // A search paged to its end with the first page's token: the ids, the first page's count, the warnings, or the
    // refusal (status and code) when a page isn't a 200.
    async function searchAll(who, query, { forceResidual = false, limit = 200 } = {}) {
        const ids = [];
        let token = null;
        let total = null;
        const warnings = new Set();
        for (let start = 0; start < 100000; start += limit) {
            const body = { Query: query, StartIndex: start, Limit: limit, ResultToken: token };
            if (forceResidual) body.ForceResidual = true;
            const r = await call(who, 'POST', '/SmartSearch/Search', { body });
            if (r.status !== 200) return { refused: { status: r.status, code: r.json && r.json.code, errors: r.json && r.json.errors } };
            const page = r.json;
            if (start === 0) total = page.TotalRecordCount;
            for (const w of page.Warnings || []) warnings.add(w);
            for (const item of page.Items) ids.push(guid(item.Id));
            token = page.ResultToken;
            if (!page.HasMore) break;
        }
        return { ids, total, warnings: [...warnings] };
    }

    // Every item core's /Items gives a user, a page of 500 at a time.
    async function items(who, query) {
        const out = [];
        for (let start = 0; ; start += 500) {
            const page = await ok(who, 'GET', '/Items', { query: { userId: users[who].id, ...query, StartIndex: start, Limit: 500 } });
            out.push(...page.Items);
            if (page.Items.length === 0 || out.length >= page.TotalRecordCount) return out;
        }
    }

    async function libraries() {
        return (await ok('admin', 'GET', '/Library/VirtualFolders')).map((l) => ({ id: guid(l.ItemId), name: l.Name, type: l.CollectionType, options: l.LibraryOptions || {}, locations: l.Locations || [] }));
    }

    // Our edits must never write next to the shared media: every library has no metadata saver and no local metadata.
    // Jellyfin's Collections library (type boxsets, there once a collection exists) lives in the server's data folder.
    async function assertNoSavers() {
        const unsafe = (await libraries()).filter((l) => l.type !== 'boxsets').filter((l) => l.options.SaveLocalMetadata || !Array.isArray(l.options.MetadataSavers) || l.options.MetadataSavers.length > 0);
        if (unsafe.length > 0) throw new Error(`refusing to edit items: ${unsafe.map((l) => l.name).join(', ')} may save metadata next to the media`);
    }

    async function scan(timeoutSec = 600) {
        const started = Date.now();
        await ok('admin', 'POST', '/Library/Refresh');
        await delay(2000);
        while (Date.now() - started < timeoutSec * 1000) {
            const task = (await ok('admin', 'GET', '/ScheduledTasks', { query: { isHidden: false } })).find((t) => t.Key === 'RefreshLibrary');
            if (task && task.State === 'Idle' && task.LastExecutionResult && Date.parse(task.LastExecutionResult.EndTimeUtc) >= started - 1000) return task.LastExecutionResult.Status;
            await delay(2000);
        }
        throw new Error(`the library scan did not finish in ${timeoutSec} s`);
    }

    // The restricted user: viewer2 without one library, with a parental limit and a blocked tag. The policy it had is
    // saved first and put back by restoreRestricted (also at the start of the next run, after a run that was killed).
    const policyFile = path.join(work, 'saved-policy.json');
    async function restrict(hiddenLibrary, blockedTag, maxRating) {
        await restoreRestricted();
        const user = await ok('admin', 'GET', `/Users/${users.viewer2.id}`);
        fs.mkdirSync(work, { recursive: true });
        fs.writeFileSync(policyFile, JSON.stringify(user.Policy));
        const all = await libraries();
        const policy = { ...user.Policy, EnableAllFolders: false, EnabledFolders: all.filter((l) => l.name !== hiddenLibrary).map((l) => l.id), BlockedTags: [blockedTag], MaxParentalRating: maxRating };
        await ok('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: policy });
        return { hiddenLibrary, blockedTag, maxParentalRating: maxRating };
    }
    async function restoreRestricted() {
        if (!fs.existsSync(policyFile)) return false;
        await ok('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: readJson(policyFile) });
        fs.rmSync(policyFile);
        return true;
    }

    return { name, root, work, port, users, call, ok, searchAll, items, libraries, assertNoSavers, scan, restrict, restoreRestricted };
}

// Prints the result (redacted) and saves it to --out; exits 0 when it passed.
export function finish(result, out) {
    const text = redactSecrets(JSON.stringify(result, null, 1));
    if (out) {
        fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true });
        fs.writeFileSync(out, text);
    }
    console.log(text);
    process.exit(result.pass ? 0 : 1);
}

export function argValue(argv, name) {
    const i = argv.indexOf(name);
    if (i < 0) return null;
    if (i + 1 >= argv.length) throw new Error(`${name} needs a value`);
    return argv[i + 1];
}
