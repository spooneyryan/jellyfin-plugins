// node --test scripts/tests/api-docs-probe.test.mjs (run by tests/Repo.Tests ScriptsTests). A fake server on loopback
// plays Jellyfin's /api-docs/openapi.json and appends to a fake server log what Jellyfin 10.11.5 logs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const probeScript = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'api-docs-probe.mjs');

const DOCUMENT = JSON.stringify({
    openapi: '3.0.1',
    info: { title: 'Jellyfin API', version: '10.11.5' },
    paths: {
        '/System/Info': { get: { operationId: 'GetSystemInfo' } },
        '/Plugins': { get: { operationId: 'GetPlugins' } },
        '/Bookmarks/Items/{itemId}': { get: { operationId: 'GetBookmarksForItem' } },
        '/Bookmarks/Diagnostics/ClientHealth': { post: { operationId: 'PostBookmarksClientHealth' } }
    },
    components: { schemas: { BookmarksBookmarkDto: {}, BaseItemDto: {} } }
});

const GENERATING_LOG = '[2026-09-29 10:00:00.000 +00:00] [ERR] [21] Jellyfin.Server.Middleware.ExceptionMiddleware: Error processing request. URL "GET" "/api-docs/openapi.json".\n'
    + 'System.InvalidOperationException: OpenApi document is generating\n'
    + '   at Jellyfin.Server.Filters.CachingOpenApiProvider.GetSwagger(String documentName, String host, String basePath)\n';
const COLLISION_LOG = '[2026-09-29 10:00:00.000 +00:00] [ERR] [21] Jellyfin.Server.Middleware.ExceptionMiddleware: Error processing request. URL "GET" "/api-docs/openapi.json".\n'
    + 'Swashbuckle.AspNetCore.SwaggerGen.SwaggerGeneratorException: Failed to generate Operation for action - Jellyfin.Plugin.Beta.Api.BetaController.GetItem (Jellyfin.Plugin.Beta). See inner exception\n'
    + ' ---> System.InvalidOperationException: Can\'t use schemaId "$SampleItemDto" for type "$Jellyfin.Plugin.Beta.Api.Models.SampleItemDto". The same schemaId is already used for type "$Jellyfin.Plugin.Alpha.Api.Models.SampleItemDto"\n';

// answers: a list of { status, body, log } handed out one per request; the last one repeats.
async function withServer(answers, run) {
    const logDir = fs.mkdtempSync(path.join(os.tmpdir(), 'api-docs-probe-'));
    const logFile = path.join(logDir, 'log_20260929.log');
    fs.writeFileSync(logFile, '[2026-09-29 09:59:00.000 +00:00] [INF] [1] Main: Startup complete 0:00:05\n');
    const requests = [];
    let inFlight = 0;
    let maxInFlight = 0;
    const server = http.createServer((req, res) => {
        requests.push(req.url);
        inFlight++;
        maxInFlight = Math.max(maxInFlight, inFlight);
        const answer = answers[Math.min(requests.length - 1, answers.length - 1)];
        if (answer.log) fs.appendFileSync(logFile, answer.log);
        setTimeout(() => {
            res.writeHead(answer.status, { 'Content-Type': answer.status === 200 ? 'application/json; charset=utf-8' : 'text/plain' });
            res.end(answer.body ?? 'Error processing request.');
            inFlight--;
        }, 20);
    });
    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
    try {
        return await run({ url: `http://127.0.0.1:${server.address().port}`, logDir, requests, maxInFlight: () => maxInFlight });
    } finally {
        server.close();
        fs.rmSync(logDir, { recursive: true, force: true });
    }
}

function runProbe(args) {
    return new Promise((resolve, reject) => {
        const child = spawn(process.execPath, [probeScript, ...args], { stdio: ['ignore', 'pipe', 'pipe'] });
        let stdout = '';
        let stderr = '';
        child.stdout.on('data', (d) => { stdout += d; });
        child.stderr.on('data', (d) => { stderr += d; });
        child.on('error', reject);
        child.on('close', (code) => resolve({ code, stdout, stderr }));
    });
}

const base = (server, extra = []) => ['--url', server.url, '--log-dir', server.logDir, '--plugins', 'Bookmarks', '--retry-delay', '50', '--timeout', '20', ...extra];

test('a 200 document with the server paths and each plugin path passes', async () => {
    await withServer([{ status: 200, body: DOCUMENT }], async (server) => {
        const run = await runProbe(base(server));
        assert.equal(run.code, 0, run.stdout + run.stderr);
        const result = JSON.parse(run.stdout);
        assert.equal(result.pass, true);
        assert.deepEqual(server.requests, ['/api-docs/openapi.json']);
        assert.equal(result.document.pathCount, 4);
        assert.equal(result.document.schemaCount, 2);
        assert.equal(result.plugins.Bookmarks.paths, 2);
    });
});

test('the warm-up 500 ("OpenApi document is generating") is asked again, one request at a time', async () => {
    await withServer([{ status: 500, log: GENERATING_LOG }, { status: 500, log: GENERATING_LOG }, { status: 200, body: DOCUMENT }], async (server) => {
        const run = await runProbe(base(server, ['--summary']));
        assert.equal(run.code, 0, run.stdout + run.stderr);
        assert.match(run.stdout, /^api-docs check passed: GET \/api-docs\/openapi\.json 200 in \d+ ms \(answers 500,500,200\); 4 paths, 2 schemas; paths of ours: Bookmarks 2 \(0 own schemas\)\.\s*$/);
        assert.equal(server.requests.length, 3);
        assert.equal(server.maxInFlight(), 1);
    });
});

test('a schema-id collision fails at once and quotes the log line', async () => {
    await withServer([{ status: 500, log: COLLISION_LOG }, { status: 200, body: DOCUMENT }], async (server) => {
        const run = await runProbe(base(server));
        assert.equal(run.code, 1, run.stdout + run.stderr);
        const result = JSON.parse(run.stdout);
        assert.equal(result.pass, false);
        assert.equal(server.requests.length, 1, 'a collision is not retried');
        assert.equal(result.attempts[0].reason, 'collision');
        assert.ok(result.collision.some((line) => line.includes('Can\'t use schemaId "$SampleItemDto"')), JSON.stringify(result.collision));
        assert.ok(result.checks.some((c) => c.name === 'no schema-id collision in the server log' && !c.pass));
    });
});

test('a plugin without paths in the document fails', async () => {
    await withServer([{ status: 200, body: DOCUMENT }], async (server) => {
        const run = await runProbe(['--url', server.url, '--log-dir', server.logDir, '--plugins', 'Bookmarks,UserRatings', '--summary']);
        assert.equal(run.code, 1, run.stdout + run.stderr);
        assert.match(run.stdout, /api-docs check FAILED .*UserRatings: its paths are in it: "0 at \/UserRatings and under it"/);
    });
});

test('an unexplained 500 is retried until the timeout, then fails', async () => {
    await withServer([{ status: 500 }], async (server) => {
        const run = await runProbe(['--url', server.url, '--log-dir', server.logDir, '--plugins', 'Bookmarks', '--retry-delay', '100', '--timeout', '1']);
        assert.equal(run.code, 1, run.stdout + run.stderr);
        const result = JSON.parse(run.stdout);
        assert.ok(server.requests.length >= 2, `${server.requests.length} requests`);
        assert.equal(result.attempts.at(-1).reason, 'unexplained');
    });
});

test('another status is not retried', async () => {
    await withServer([{ status: 404, body: 'not found' }], async (server) => {
        const run = await runProbe(base(server));
        assert.equal(run.code, 1);
        assert.equal(server.requests.length, 1);
    });
});

// A document whose paths reference schemas: core's own, one core schema only Jellyfin's unreferenced WebSocket message
// schema names (so it is shared although no core path reaches it), and Bookmarks' through two levels of $ref.
const ref = (id) => ({ $ref: `#/components/schemas/${id}` });
const json = (schema) => ({ content: { 'application/json': { schema } } });
function schemaDocument({ clip = 'BookmarkClipDto', status = 'BookmarkClipStatus', request = 'BookmarkUpdateRequest' } = {}) {
    return JSON.stringify({
        openapi: '3.0.1',
        info: { title: 'Jellyfin API', version: '10.11.5' },
        paths: {
            '/System/Info': { get: { operationId: 'GetSystemInfo', responses: { 200: json(ref('SystemInfo')), 401: json(ref('ProblemDetails')) } } },
            '/Plugins': { get: { operationId: 'GetPlugins', responses: { 200: json({ type: 'array', items: ref('PluginInfo') }) } } },
            '/Bookmarks/{bookmarkId}': {
                get: { operationId: 'GetBookmark', responses: { 200: json(ref('BookmarkDto')), 404: json(ref('ProblemDetails')) } }
            },
            // The route itself is the plugin's too (POST /Bookmarks), in any case.
            '/BOOKMARKS': {
                post: { operationId: 'CreateBookmark', requestBody: json({ allOf: [ref(request)] }), responses: { 201: json(ref('BookmarkDto')) } }
            },
            '/BookmarksElsewhere': { get: { operationId: 'NotOurs', responses: { 200: json(ref('SystemInfo')) } } }
        },
        components: {
            schemas: {
                SystemInfo: { type: 'object', properties: { Version: { type: 'string' } } },
                PluginInfo: { type: 'object' },
                ProblemDetails: { type: 'object' },
                BaseItemKind: { enum: ['Movie', 'Episode'], type: 'string' },
                OutboundWebSocketMessage: { type: 'object', properties: { Kind: { allOf: [ref('BaseItemKind')] } } },
                BookmarkDto: { type: 'object', properties: { Clip: { allOf: [ref(clip)], nullable: true }, ItemKind: ref('BaseItemKind') } },
                [clip]: { type: 'object', properties: { Status: { allOf: [ref(status)] } } },
                [status]: { enum: ['Queued', 'Completed'], type: 'string' },
                [request]: { type: 'object' }
            }
        }
    });
}

test('a plugin\'s own schemas are the ones only its paths reach, and they carry its schema prefix', async () => {
    await withServer([{ status: 200, body: schemaDocument() }], async (server) => {
        const run = await runProbe(base(server));
        assert.equal(run.code, 0, run.stdout + run.stderr);
        const result = JSON.parse(run.stdout);
        const bookmarks = result.plugins.Bookmarks;
        assert.equal(bookmarks.schemaPrefix, 'Bookmark', 'read from plugins/Bookmarks/Jellyfin.Plugin.Bookmarks/BookmarksKit.cs');
        assert.equal(bookmarks.paths, 2, '/Bookmarks/{bookmarkId} and /BOOKMARKS, not /BookmarksElsewhere');
        assert.deepEqual(bookmarks.ownSchemas, ['BookmarkClipDto', 'BookmarkClipStatus', 'BookmarkDto', 'BookmarkUpdateRequest']);
        assert.deepEqual(bookmarks.sharedSchemas, ['BaseItemKind', 'ProblemDetails']);
        assert.ok(result.checks.some((c) => c.name === 'Bookmarks: its own schema ids start with Bookmark' && c.pass && c.detail === '4 own schema(s)'));
        assert.ok(result.checks.some((c) => c.name === 'Bookmarks: none of its own schema ids is in kit/testdata/openapi-schema-ids.txt' && c.pass));
    });
});

test('an own schema id without the prefix fails (Bookmarks 1.0\'s ClipDto and ClipStatus)', async () => {
    await withServer([{ status: 200, body: schemaDocument({ clip: 'ClipDto', status: 'ClipStatus' }) }], async (server) => {
        const run = await runProbe(base(server, ['--summary']));
        assert.equal(run.code, 1, run.stdout + run.stderr);
        assert.match(run.stdout, /api-docs check FAILED .*Bookmarks: its own schema ids start with Bookmark: \{"unprefixed":\["ClipDto","ClipStatus"\]\}/);
    });
});

test('an own schema id in the pinned foreign-id list fails, also where nothing collides with it (JE\'s BookmarkItem)', async () => {
    await withServer([{ status: 200, body: schemaDocument({ request: 'BookmarkItem' }) }], async (server) => {
        const run = await runProbe(base(server));
        assert.equal(run.code, 1, run.stdout + run.stderr);
        const result = JSON.parse(run.stdout);
        const listed = result.checks.find((c) => c.name === 'Bookmarks: none of its own schema ids is in kit/testdata/openapi-schema-ids.txt');
        assert.deepEqual(listed, { name: listed.name, pass: false, detail: { listed: ['BookmarkItem'] } });
        assert.ok(result.checks.some((c) => c.name === 'Bookmarks: its own schema ids start with Bookmark' && c.pass), 'BookmarkItem has the prefix');
    });
});

test('--schema-prefix and --foreign-ids override the defaults, and a plugin without either skips only the prefix check', async () => {
    await withServer([{ status: 200, body: schemaDocument() }], async (server) => {
        const strict = await runProbe(base(server, ['--schema-prefix', 'Bookmarks=BookmarkClip']));
        assert.equal(strict.code, 1, strict.stdout + strict.stderr);
        const failed = JSON.parse(strict.stdout).checks.find((c) => c.name === 'Bookmarks: its own schema ids start with BookmarkClip');
        assert.deepEqual(failed.detail, { unprefixed: ['BookmarkDto', 'BookmarkUpdateRequest'] });

        const foreignFile = path.join(server.logDir, 'foreign.txt');
        fs.writeFileSync(foreignFile, '# a test list\n\nBookmarkDto\n');
        const listed = await runProbe(base(server, ['--foreign-ids', foreignFile]));
        assert.equal(listed.code, 1, listed.stdout + listed.stderr);
        const check = JSON.parse(listed.stdout).checks.find((c) => c.name.startsWith('Bookmarks: none of its own schema ids is in'));
        assert.deepEqual(check.detail, { listed: ['BookmarkDto'] });

        const unknown = await runProbe(['--url', server.url, '--log-dir', server.logDir, '--plugins', 'Bookmarks,Zebra', '--retry-delay', '50']);
        const result = JSON.parse(unknown.stdout);
        assert.equal(result.plugins.Zebra.schemaPrefix, null);
        assert.match(result.plugins.Zebra.note, /prefix check was skipped/);
        assert.ok(!result.checks.some((c) => c.name.startsWith('Zebra: its own schema ids start')));
        assert.ok(result.checks.some((c) => c.name === 'Zebra: its paths are in it' && !c.pass));
    });
});

test('a bad --schema-prefix entry is refused', async () => {
    const run = await runProbe(['--url', 'http://127.0.0.1:1', '--log-dir', os.tmpdir(), '--schema-prefix', 'Bookmarks']);
    assert.equal(run.code, 2, run.stdout);
    assert.match(run.stdout, /bad --schema-prefix entry Bookmarks/);
});

test('port 8096 and other hosts are refused before any request', async () => {
    for (const url of ['http://127.0.0.1:8096', 'http://example.org:8097']) {
        const run = await runProbe(['--url', url, '--log-dir', os.tmpdir()]);
        assert.equal(run.code, 2, run.stdout);
        assert.match(run.stdout, /api-docs check FAILED: Refusing/);
    }
});
