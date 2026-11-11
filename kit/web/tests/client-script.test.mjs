// Checks of a plugin's concatenated ClientScript, run in the fake page. The C# test
// support (KitScriptChecks.RunClientScriptChecks) writes the script to a file and runs
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=<prefix> node --test kit/web/tests/client-script.test.mjs
// Without KIT_CLIENT_SCRIPT every test is skipped, so `node --test kit/web/tests/*.test.mjs` works on its own.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage } from './fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const prefix = process.env.KIT_PREFIX;
const skip = !file || !prefix ? 'KIT_CLIENT_SCRIPT and KIT_PREFIX are not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

function load() {
    const page = createPage();
    page.run(source, file);
    return page;
}

test('the script runs, registers window.__jfkit[prefix] and injects its styles once', { skip }, () => {
    const page = load();
    const entry = page.context.__jfkit[prefix];
    assert.ok(entry, 'window.__jfkit.' + prefix);
    assert.match(entry.version, /^\d+\.\d+\.\d+\.\d+$/);
    assert.match(entry.kit, /^\d+\.\d+\.\d+/);
    assert.equal(typeof entry.plugin, 'string');
    assert.ok(entry.health && entry.timing && entry.instance);
    const styles = page.document.querySelectorAll('#' + prefix + '-styles');
    assert.equal(styles.length, 1);
    assert.equal(styles[0].textContent.includes('__PFX__'), false);
    assert.ok(styles[0].textContent.includes('.' + prefix + '-toast'));
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('loading it twice runs it once (the double-load guard)', { skip }, () => {
    const page = load();
    const entry = page.context.__jfkit[prefix];
    page.run(source, file + ' (second tag)');
    assert.equal(page.context.__jfkit[prefix], entry);
    assert.equal(page.document.querySelectorAll('#' + prefix + '-styles').length, 1);
});

test('no plugin request before window.ApiClient and the signed-in user; the kit starts within 25 ms of ApiClient', { skip }, async () => {
    const page = load();
    const timing = page.context.__jfkit[prefix].timing;
    await page.advance(137);
    assert.equal(timing.client, null);
    page.installApiClient({ userId: null });
    await page.advance(40);
    assert.ok(timing.client !== null && timing.client - page.apiClientAt <= 25, 'start lag ' + (timing.client - page.apiClientAt));
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(600);
    assert.equal(page.fetchLog.length, 0, 'requests while signed out: ' + JSON.stringify(page.fetchLog));
    page.clientState.userId = 'user-1';
    await page.advance(30);
    assert.ok(timing.user !== null);
    page.dispatch(page.document.body, 'viewshow');
    await page.advance(600);
    assert.ok(page.fetchLog.every((f) => f.userId === 'user-1'), JSON.stringify(page.fetchLog));
    assert.ok(timing.firstApi === null || timing.firstApi >= timing.user);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});
