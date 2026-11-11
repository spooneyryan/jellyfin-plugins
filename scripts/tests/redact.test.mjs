// node --test scripts/tests/redact.test.mjs (run by tests/Repo.Tests ScriptsTests).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { REDACTED, redactSecrets } from '../redact.mjs';

const TOKEN = '0123456789abcdef0123456789abcdef';
const ITEM = 'fedcba9876543210fedcba9876543210';

test('a token in a URL, a header or a JSON property is replaced', () => {
    for (const [text, expected] of [
        [`ws://127.0.0.1:8099/socket?api_key=${TOKEN}&deviceId=abc`, `ws://127.0.0.1:8099/socket?api_key=${REDACTED}&deviceId=abc`],
        [`http://127.0.0.1:8099/Items/${ITEM}/Download?ApiKey=${TOKEN}`, `http://127.0.0.1:8099/Items/${ITEM}/Download?ApiKey=${REDACTED}`],
        [`/Videos/${ITEM}/stream?static=true&access_token=${TOKEN}#t=1`, `/Videos/${ITEM}/stream?static=true&access_token=${REDACTED}#t=1`],
        [`X-Emby-Token: ${TOKEN}`, `X-Emby-Token: ${REDACTED}`],
        [`X-MediaBrowser-Token=${TOKEN}`, `X-MediaBrowser-Token=${REDACTED}`],
        [`MediaBrowser Client="Jellyfin Web", Device="Chrome", DeviceId="d1", Version="10.11.5", Token="${TOKEN}"`,
            `MediaBrowser Client="Jellyfin Web", Device="Chrome", DeviceId="d1", Version="10.11.5", Token="${REDACTED}"`],
        [`{"AccessToken": "${TOKEN}", "User": {"Id": "${ITEM}"}}`, `{"AccessToken": "${REDACTED}", "User": {"Id": "${ITEM}"}}`],
        [`{"api_key":"${TOKEN}"}`, `{"api_key":"${REDACTED}"}`],
    ]) {
        assert.equal(redactSecrets(text), expected, text);
        assert.ok(!redactSecrets(text).includes(TOKEN), text);
    }
});

test('inside a JSON string, as in a saved webcheck result, the escaped forms are replaced too', () => {
    const result = JSON.stringify({
        console: [
            { type: 'log', text: `Requesting url with Token="${TOKEN}" and {"AccessToken":"${TOKEN}"}` },
            { type: 'log', text: `Opening web socket with url: ws://127.0.0.1:8099/socket?api_key=${TOKEN}&deviceId=d1` },
        ],
        failedRequests: [{ url: `http://127.0.0.1:8099/Items/${ITEM}/Images/Primary?api_key=${TOKEN}`, status: 404 }],
    }, null, 2);
    const redacted = redactSecrets(result);
    assert.ok(!redacted.includes(TOKEN));
    assert.ok(redacted.includes(ITEM), 'ids are kept');
    assert.deepEqual(JSON.parse(redacted).failedRequests[0], { url: `http://127.0.0.1:8099/Items/${ITEM}/Images/Primary?api_key=${REDACTED}`, status: 404 });
    assert.equal(JSON.parse(redacted).console.length, 2, 'still valid JSON');
});

test('ids and ordinary text are left alone', () => {
    for (const text of [
        `/Users/${ITEM}/Items?ParentId=${ITEM}&Fields=Path`,
        `{"Id":"${ITEM}","DeviceId":"${TOKEN}","Name":"Token"}`,
        'The token expired; sign in again.',
        'ApiKey is required',
        '',
    ]) {
        assert.equal(redactSecrets(text), text, text);
    }
    assert.equal(redactSecrets(undefined), undefined);
    assert.equal(redactSecrets(null), null);
});
