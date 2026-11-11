#!/usr/bin/env node
// GET /api-docs/openapi.json in a running sandbox. One schema-id collision anywhere among
// the loaded plugins makes the server answer 500 for the whole OpenAPI document, on every request and silently at
// startup; the endpoints themselves keep working, so nothing else notices. Node 22 built-ins only. Loopback only, never
// port 8096; the document is anonymous, so no credentials are read.
//
//   node scripts/api-docs-probe.mjs --sandbox <name> [--timeout 180] [--summary] [--out result.json] [--inventory-out ids.json]
//   node scripts/api-docs-probe.mjs --url http://127.0.0.1:<port>[/<BaseUrl>] --log-dir <dir> [--plugins A,B] ...
//                                   [--schema-prefix A=Prefix,B=Prefix] [--foreign-ids <file>]
//
// One request at a time, each awaited: the first generation after a start takes about 10-12 s, and a request that
// arrives while it runs gets 500 "OpenApi document is generating" by design (Jellyfin's CachingOpenApiProvider).
// On a 500 the server log written since the probe started decides: "OpenApi document is generating" means wait and ask
// again; a SwaggerGeneratorException ("Can't use schemaId ...") is a collision, reported with the log line, and is not
// retried; anything else is retried until --timeout. A 200 must be an OpenAPI 3 document with the server's own paths and
// at least one path at /<Route> or under it for each of our plugins in the sandbox (sandbox.json "Plugins", route = folder
// name).
//
// Each plugin's own schemas are the ones its paths reach through $ref and nothing else
// does: no other path, and no schema those paths don't reach (Jellyfin adds unreferenced ones, such as its WebSocket
// messages). Every own schema id must start with the plugin's schema prefix (KitOptions.SchemaPrefix, read from the
// plugin's source, or --schema-prefix), and none may be in the pinned foreign-id list kit/testdata/openapi-schema-ids.txt:
// in a sandbox without the community plugin that publishes an id, a collision with it doesn't show as a 500.
//
// Prints one JSON result (or with --summary one line) on stdout and exits with 0 when every check passed.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertSafeUrl } from './sandbox-policy.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const CORE_PATHS = ['/System/Info', '/Plugins'];
const GENERATING = 'OpenApi document is generating';
const SCHEMA_REF = '#/components/schemas/';
const FOREIGN_IDS = path.join(repoRoot, 'kit', 'testdata', 'openapi-schema-ids.txt');
const COLLISION = /SwaggerGeneratorException|Can't use schemaId/;

function usage() {
    return 'usage: node scripts/api-docs-probe.mjs (--sandbox <name> | --url <loopback url> --log-dir <dir>) [--plugins A,B] '
        + '[--schema-prefix A=Prefix,B=Prefix] [--foreign-ids <file>] '
        + '[--timeout <s>] [--request-timeout <s>] [--retry-delay <ms>] [--summary] [--out <file>] [--inventory-out <file>]';
}

function parseArgs(argv) {
    const o = {
        sandbox: null, url: null, logDir: null, plugins: null, schemaPrefixes: {}, foreignIds: FOREIGN_IDS,
        timeout: 180, requestTimeout: 120, retryDelay: 2000, summary: false, out: null, inventoryOut: null
    };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        const next = () => {
            if (i + 1 >= argv.length) throw new Error(`${a} needs a value. ${usage()}`);
            return argv[++i];
        };
        if (a === '--sandbox') o.sandbox = next();
        else if (a === '--url') o.url = next();
        else if (a === '--log-dir') o.logDir = next();
        else if (a === '--plugins') o.plugins = next().split(',').map((p) => p.trim()).filter(Boolean);
        else if (a === '--schema-prefix') {
            for (const pair of next().split(',').map((p) => p.trim()).filter(Boolean)) {
                const m = /^([A-Za-z][A-Za-z0-9]{1,39})=([A-Za-z][A-Za-z0-9]{0,39})$/.exec(pair);
                if (!m) throw new Error(`bad --schema-prefix entry ${pair}; expected <Plugin>=<Prefix>. ${usage()}`);
                o.schemaPrefixes[m[1]] = m[2];
            }
        }
        else if (a === '--foreign-ids') o.foreignIds = path.resolve(next());
        else if (a === '--timeout') o.timeout = Number(next());
        else if (a === '--request-timeout') o.requestTimeout = Number(next());
        else if (a === '--retry-delay') o.retryDelay = Number(next());
        else if (a === '--summary') o.summary = true;
        else if (a === '--out') o.out = next();
        else if (a === '--inventory-out') o.inventoryOut = next();
        else if (a === '--help' || a === '-h') { console.log(usage()); process.exit(0); }
        else throw new Error(`Unknown argument ${a}. ${usage()}`);
    }
    if (!o.sandbox && !o.url) throw new Error(usage());
    if (o.sandbox && !/^[a-z0-9][a-z0-9-]{0,30}$/.test(o.sandbox)) throw new Error(`bad sandbox name ${o.sandbox}`);
    for (const key of ['timeout', 'requestTimeout', 'retryDelay']) {
        if (!Number.isFinite(o[key]) || o[key] < 0) throw new Error(`bad --${key} value. ${usage()}`);
    }
    return o;
}

const readJson = (file) => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));

function target(o) {
    let base = o.url;
    let logDir = o.logDir;
    let plugins = o.plugins;
    if (o.sandbox) {
        const root = path.join(repoRoot, '.sandbox', o.sandbox);
        const stateFile = path.join(root, 'sandbox.json');
        if (!fs.existsSync(stateFile)) throw new Error(`${stateFile} not found; start the sandbox with dev-server.ps1`);
        const state = readJson(stateFile);
        base ??= `http://127.0.0.1:${state.Port}${state.BaseUrl || ''}`;
        logDir ??= state.LogDir || path.join(root, 'log');
        plugins ??= state.Plugins || [];
    }
    base = base.replace(/\/+$/, '');
    assertSafeUrl(base);
    for (const p of plugins || []) if (!/^[A-Za-z][A-Za-z0-9]{1,39}$/.test(p)) throw new Error(`bad plugin name ${p}`);
    return { base, logDir, plugins: plugins || [] };
}

// ------------------------------------------------------------------------------------------------ server log

function newestLog(logDir) {
    if (!logDir || !fs.existsSync(logDir)) return null;
    const files = fs.readdirSync(logDir).filter((f) => /^log_.*\.log$/.test(f))
        .map((f) => ({ file: path.join(logDir, f), mtime: fs.statSync(path.join(logDir, f)).mtimeMs }))
        .sort((a, b) => a.mtime - b.mtime);
    return files.length ? files[files.length - 1].file : null;
}

// What the log gained since the mark (a new day's file counts from its start).
function logSince(mark) {
    const file = newestLog(mark.logDir);
    if (!file) return '';
    const offset = file === mark.file ? mark.size : 0;
    const fd = fs.openSync(file, 'r');
    try {
        const size = fs.fstatSync(fd).size;
        if (size <= offset) return '';
        const buffer = Buffer.alloc(size - offset);
        fs.readSync(fd, buffer, 0, buffer.length, offset);
        return buffer.toString('utf8');
    } finally {
        fs.closeSync(fd);
    }
}

function logMark(logDir) {
    const file = newestLog(logDir);
    return { logDir, file, size: file ? fs.statSync(file).size : 0 };
}

// ------------------------------------------------------------------------------------------------ schemas

// KitOptions.SchemaPrefix from the plugin's source (plugins/<P>/Jellyfin.Plugin.<P>/*.cs), or null.
function schemaPrefixFromSource(plugin) {
    const dir = path.join(repoRoot, 'plugins', plugin, `Jellyfin.Plugin.${plugin}`);
    if (!fs.existsSync(dir)) return null;
    for (const file of fs.readdirSync(dir).filter((f) => f.endsWith('.cs')).sort()) {
        const m = /\bSchemaPrefix\s*=\s*"([A-Za-z][A-Za-z0-9]{0,39})"/.exec(fs.readFileSync(path.join(dir, file), 'utf8'));
        if (m) return m[1];
    }
    return null;
}

function readForeignIds(file) {
    return new Set(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '').split(/\r?\n/)
        .map((l) => l.trim()).filter((l) => l && !l.startsWith('#')));
}

// The schema ids a JSON value names in "$ref": "#/components/schemas/<id>" (JSON pointer escapes undone).
function collectRefs(node, into) {
    if (Array.isArray(node)) {
        for (const value of node) collectRefs(value, into);
    } else if (node && typeof node === 'object') {
        for (const [key, value] of Object.entries(node)) {
            if (key === '$ref' && typeof value === 'string' && value.startsWith(SCHEMA_REF)) {
                into.add(value.slice(SCHEMA_REF.length).replace(/~1/g, '/').replace(/~0/g, '~'));
            } else {
                collectRefs(value, into);
            }
        }
    }
    return into;
}

// Every schema reachable from the roots through the schemas' own $refs.
function closure(roots, schemas) {
    const seen = new Set();
    const queue = [...roots];
    while (queue.length) {
        const id = queue.pop();
        if (seen.has(id)) continue;
        seen.add(id);
        if (Object.hasOwn(schemas, id)) for (const next of collectRefs(schemas[id], new Set())) if (!seen.has(next)) queue.push(next);
    }
    return seen;
}

// A plugin's paths: /<Route> itself (GET and POST /Bookmarks) and everything under /<Route>/, in any case.
function isPluginPath(p, plugin) {
    const lower = p.toLowerCase();
    const root = `/${plugin.toLowerCase()}`;
    return lower === root || lower.startsWith(`${root}/`);
}

// A plugin's own schemas: reached from its paths and from nothing else. Shared ones are the server's
// (ProblemDetails, or a core enum both use).
function pluginSchemas(doc, plugin) {
    const schemas = doc.components?.schemas || {};
    const ours = new Set();
    const others = new Set();
    for (const [p, item] of Object.entries(doc.paths || {})) collectRefs(item, isPluginPath(p, plugin) ? ours : others);
    const reached = closure(ours, schemas);
    for (const id of Object.keys(schemas)) if (!reached.has(id)) others.add(id);
    const elsewhere = closure(others, schemas);
    return {
        own: [...reached].filter((id) => !elsewhere.has(id)).sort(),
        shared: [...reached].filter((id) => elsewhere.has(id)).sort()
    };
}

// ------------------------------------------------------------------------------------------------ probe

async function fetchDocument(url, timeoutSeconds) {
    const started = performance.now();
    try {
        const response = await fetch(url, { redirect: 'manual', signal: AbortSignal.timeout(timeoutSeconds * 1000) });
        const text = await response.text();
        return { status: response.status, ms: Math.round(performance.now() - started), bytes: Buffer.byteLength(text), contentType: response.headers.get('content-type'), text };
    } catch (error) {
        return { status: null, ms: Math.round(performance.now() - started), error: String(error.message || error) };
    }
}

async function probe(o) {
    const { base, logDir, plugins } = target(o);
    const url = `${base}/api-docs/openapi.json`;
    const result = { pass: false, sandbox: o.sandbox, url, attempts: [], checks: [], document: null, plugins: {}, collision: null };
    const check = (name, pass, detail) => result.checks.push({ name, pass: !!pass, ...(detail === undefined ? {} : { detail }) });
    const deadline = Date.now() + o.timeout * 1000;

    let ok = null;
    for (;;) {
        const mark = logMark(logDir);
        const response = await fetchDocument(url, o.requestTimeout);
        const attempt = { status: response.status, ms: response.ms, bytes: response.bytes, contentType: response.contentType, error: response.error };
        result.attempts.push(attempt);
        if (response.status === 200) { ok = response; break; }

        // Give the log a moment: the exception is written as the 500 goes out.
        await delay(300);
        const log = logSince(mark);
        const lines = log.split(/\r?\n/);
        if (COLLISION.test(log)) {
            attempt.reason = 'collision';
            result.collision = lines.filter((l) => /Can't use schemaId|SwaggerGeneratorException: Failed to generate/.test(l)).map((l) => l.trim()).slice(0, 10);
            break;
        }

        attempt.reason = log.includes(GENERATING) ? 'generating' : (response.status === null ? 'no answer' : 'unexplained');
        if (response.status !== null && response.status !== 500) break;
        if (Date.now() + o.retryDelay >= deadline) break;
        await delay(o.retryDelay);
    }

    const last = result.attempts[result.attempts.length - 1];
    check('GET /api-docs/openapi.json answers 200', !!ok, ok ? `${result.attempts.length} request(s)` : `last answer ${last.status ?? last.error}${last.reason ? ` (${last.reason})` : ''}`);
    if (result.collision) check('no schema-id collision in the server log', false, result.collision);

    if (ok) {
        let doc = null;
        try { doc = JSON.parse(ok.text); } catch (error) { check('the document is JSON', false, String(error.message || error)); }
        if (doc) {
            const paths = Object.keys(doc.paths || {});
            const schemas = Object.keys(doc.components?.schemas || {}).sort();
            let operations = 0;
            for (const p of paths) for (const op of Object.values(doc.paths[p] || {})) if (op && op.operationId) operations++;
            result.document = { openapi: doc.openapi, title: doc.info?.title, version: doc.info?.version, pathCount: paths.length, operationCount: operations, schemaCount: schemas.length, bytes: ok.bytes, ms: ok.ms };
            check('an OpenAPI 3 document', typeof doc.openapi === 'string' && doc.openapi.startsWith('3.'), doc.openapi);
            const missingCore = CORE_PATHS.filter((p) => !paths.includes(p));
            check('the server\'s own paths are in it', missingCore.length === 0, missingCore.length ? { missing: missingCore } : undefined);
            const foreign = plugins.length ? readForeignIds(o.foreignIds) : new Set();
            const foreignName = path.relative(repoRoot, o.foreignIds).replace(/\\/g, '/');
            for (const plugin of plugins) {
                const own = paths.filter((p) => isPluginPath(p, plugin)).sort();
                const schemaPrefix = o.schemaPrefixes[plugin] ?? schemaPrefixFromSource(plugin);
                const reached = pluginSchemas(doc, plugin);
                result.plugins[plugin] = { paths: own.length, examples: own.slice(0, 5), schemaPrefix, ownSchemas: reached.own, sharedSchemas: reached.shared };
                check(`${plugin}: its paths are in it`, own.length > 0, `${own.length} at /${plugin} and under it`);
                if (schemaPrefix) {
                    const unprefixed = reached.own.filter((id) => !id.startsWith(schemaPrefix));
                    check(`${plugin}: its own schema ids start with ${schemaPrefix}`, unprefixed.length === 0,
                        unprefixed.length ? { unprefixed } : `${reached.own.length} own schema(s)`);
                } else {
                    result.plugins[plugin].note = 'no SchemaPrefix in the plugin\'s source and no --schema-prefix: the prefix check was skipped';
                }
                const listed = reached.own.filter((id) => foreign.has(id));
                check(`${plugin}: none of its own schema ids is in ${foreignName}`, listed.length === 0, listed.length ? { listed } : undefined);
            }

            if (o.inventoryOut) {
                const ownSchemas = Object.fromEntries(Object.entries(result.plugins).map(([name, p]) => [name, p.ownSchemas]));
                fs.writeFileSync(path.resolve(o.inventoryOut), JSON.stringify({ version: doc.info?.version, paths: paths.sort(), schemas, ownSchemas }, null, 2) + '\n');
            }
        }
    }

    result.pass = result.checks.every((c) => c.pass);
    return result;
}

function summaryLine(result) {
    const statuses = result.attempts.map((a) => a.status ?? 'none').join(',');
    if (result.pass) {
        const d = result.document;
        const plugins = Object.entries(result.plugins).map(([name, p]) => `${name} ${p.paths} (${p.ownSchemas.length} own schemas)`).join(', ');
        return `api-docs check passed: GET /api-docs/openapi.json 200 in ${d.ms} ms (answers ${statuses}); ${d.pathCount} paths, ${d.schemaCount} schemas${plugins ? `; paths of ours: ${plugins}` : ''}.`;
    }
    const failed = result.checks.filter((c) => !c.pass).map((c) => `${c.name}${c.detail === undefined ? '' : `: ${JSON.stringify(c.detail)}`}`);
    return `api-docs check FAILED (answers ${statuses}): ${failed.join('; ')}`;
}

async function main() {
    const o = parseArgs(process.argv.slice(2));
    const result = await probe(o);
    const text = JSON.stringify(result, null, 2);
    if (o.out) fs.writeFileSync(path.resolve(o.out), text + '\n');
    console.log(o.summary ? summaryLine(result) : text);
    return result.pass ? 0 : 1;
}

main().then((code) => { process.exitCode = code; }, (error) => { console.log(`api-docs check FAILED: ${error.message || error}`); process.exitCode = 2; });
