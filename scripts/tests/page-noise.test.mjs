// node --test scripts/tests/page-noise.test.mjs (run by tests/Repo.Tests ScriptsTests).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { KNOWN_PAGE_NOISE, knownPageNoise } from '../page-noise.mjs';

test("jellyfin-web's notification without permission is known noise, as the browser reports it", () => {
    // The text webcheck.mjs records: the rejection's description (CDP's exceptionDetails.exception.description).
    const rejected = "TypeError: Failed to execute 'showNotification' on 'ServiceWorkerRegistration': No notification permission has been granted for this origin.";
    assert.equal(knownPageNoise({ text: rejected }), "jellyfin-web's new-item notification without notification permission");
    assert.equal(knownPageNoise({ text: rejected + '\n    at http://127.0.0.1:8099/web/44184.678acea44af5dab1fff7.chunk.js:1:2345', url: undefined }), KNOWN_PAGE_NOISE[0].name);
    // Without the description, CDP's own text names it too.
    assert.equal(knownPageNoise({ text: "Uncaught (in promise) TypeError: Failed to execute 'showNotification' on 'ServiceWorkerRegistration': No notification permission has been granted for this origin." }), KNOWN_PAGE_NOISE[0].name);
});

test('anything else is not, so it still fails a check', () => {
    for (const text of [
        "TypeError: Cannot read properties of undefined (reading 'showNotification')",
        "TypeError: Failed to execute 'showNotification' on 'ServiceWorkerRegistration': The notification title is empty.",
        'Error: player cannot be null',
        'TypeError: t.value.pause is not a function',
        "ReferenceError: jfbm is not defined",
        '',
    ]) {
        assert.equal(knownPageNoise({ text }), null, text);
    }

    assert.equal(knownPageNoise(null), null);
    assert.equal(knownPageNoise({}), null);
});

test('every entry is named and has a pattern', () => {
    assert.ok(KNOWN_PAGE_NOISE.length > 0);
    for (const entry of KNOWN_PAGE_NOISE) {
        assert.equal(typeof entry.name, 'string');
        assert.ok(entry.name.length > 0);
        assert.ok(entry.pattern instanceof RegExp);
    }
});
