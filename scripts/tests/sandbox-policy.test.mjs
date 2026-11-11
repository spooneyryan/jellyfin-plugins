// node --test scripts/tests/sandbox-policy.test.mjs (run by tests/Repo.Tests ScriptsTests).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { assertSafeUrl, isAllowedRequest, isProductionPort, PRODUCTION_PORTS, serverDownloadRoute } from '../sandbox-policy.mjs';

test('loopback sandbox URLs are allowed, other hosts and the real server\'s port 8096 is not', () => {
    for (const url of ['http://127.0.0.1:8112/web/index.html', 'http://127.0.0.1:8097/web/index.html', 'http://localhost:8098/Bookmarks/ClientScript?v=1', 'http://[::1]:8099/System/Info/Public',
        'http://127.0.0.1:8100/jf/JellyfinEnhanced/script', 'data:text/plain,x', 'blob:http://127.0.0.1:8113/x', 'about:blank']) {
        assert.equal(isAllowedRequest(url), true, url);
    }

    for (const url of ['https://cdn.jsdelivr.net/gh/n00bcodr/Jellyfish/colors/aurora.css', 'http://127.0.0.1:8096/web/index.html',
        'http://localhost:8096/System/Info', 'http://[::1]:8096/System/Info', 'http://192.168.1.2:8097/web/', 'not a url']) {
        assert.equal(isAllowedRequest(url), false, url);
    }

    assert.throws(() => assertSafeUrl('http://127.0.0.1:8096/'), /port 8096/);
    assert.deepEqual([...PRODUCTION_PORTS], [8096]);
    assert.equal(isProductionPort('8096'), true);
    assert.equal(isProductionPort(8097), false);
    assert.equal(assertSafeUrl('http://localhost:8097/').port, '8097');
    assert.equal(isProductionPort(8112), false);
    assert.throws(() => assertSafeUrl('https://example.org/'), /loopback only/);
    assert.equal(assertSafeUrl('http://127.0.0.1:8098/jf').port, '8098');
});

test("Jellyfin Enhanced's CDN proxy is blocked on loopback too: the sandbox server would download the asset", () => {
    for (const url of [
        'http://127.0.0.1:8098/JellyfinEnhanced/cdn/elsewhere-res/regions.txt',
        'http://127.0.0.1:8098/JellyfinEnhanced/cdn/flag-icons/flags/4x3/gb.svg?v=12.8.0.0',
        'http://127.0.0.1:8100/jf/JellyfinEnhanced/cdn/selfhst/svg/sonarr.svg',
        'http://127.0.0.1:8098/emby/JellyfinEnhanced/cdn/locales/en.json',
        'http://127.0.0.1:8098/jellyfinenhanced/CDN/jellyfish/colors/aurora.css',
        'http://127.0.0.1:8098/JellyfinEnhanced%2Fcdn/x',
        'http://127.0.0.1:8098/JellyfinEnhanced/cdn'
    ]) {
        assert.equal(serverDownloadRoute(url), 'Jellyfin Enhanced CDN proxy', url);
        assert.equal(isAllowedRequest(url), false, url);
        assert.equal(isAllowedRequest(url, { allowExternal: true }), true, `${url} with --allow-external`);
    }

    for (const url of ['http://127.0.0.1:8098/JellyfinEnhanced/script', 'http://127.0.0.1:8098/JellyfinEnhanced/locales/en.json',
        'http://127.0.0.1:8098/JellyfinEnhanced/cdnx/a', 'http://127.0.0.1:8098/web/cdn/x.js', 'http://127.0.0.1:8098/JellyfinEnhanced/fonts/MaterialSymbols.woff2']) {
        assert.equal(serverDownloadRoute(url), null, url);
        assert.equal(isAllowedRequest(url), true, url);
    }
});
