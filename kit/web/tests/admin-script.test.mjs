// Checks of a plugin's AdminScript ES module, imported into a fake page. The C# test support
// (KitScriptChecks.RunAdminScriptTestsAsync) writes the module to a .mjs file and runs
//   KIT_ADMIN_SCRIPT=<file> KIT_PREFIX=<prefix> [KIT_ADMIN_EXPECT=<text>] node --test kit/web/tests/admin-script.test.mjs
// Without KIT_ADMIN_SCRIPT the test is skipped. Modules run in Node's own realm, so the fake page's window and document
// become globals here (each test file runs in its own process), and so does its fetch while the test runs: a module's
// first requests would otherwise go to Node's own fetch (Access Roles had prepended its own shim).
import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import { createPage } from './fake-dom.mjs';

const file = process.env.KIT_ADMIN_SCRIPT;
const prefix = process.env.KIT_PREFIX;
const expected = process.env.KIT_ADMIN_EXPECT;
const skip = !file || !prefix ? 'KIT_ADMIN_SCRIPT and KIT_PREFIX are not set' : false;

test('the module exports only mount(view, params), touches nothing on import, and mounts into the view', { skip }, async () => {
    const page = createPage();
    globalThis.window = page.context;
    globalThis.document = page.document;
    const nodeFetch = globalThis.fetch;
    globalThis.fetch = page.context.fetch;
    try {
        await checkModule(page);
    } finally {
        globalThis.fetch = nodeFetch;
    }
});

async function checkModule(page) {
    page.installApiClient({ userId: 'admin-1', admin: true });
    const module = await import(pathToFileURL(file).href);
    assert.deepEqual(Object.keys(module), ['mount']);
    assert.equal(typeof module.mount, 'function');
    assert.equal(page.document.head.childNodes.length, 0, 'importing adds nothing to the page');
    assert.equal(page.context.__jfkit, undefined, 'the dashboard module does not register as the ClientScript');
    const view = page.element('div', { 'data-role': 'page' });
    page.document.body.append(view);
    module.mount(view, { name: 'fixture' });
    await page.flush();
    const style = page.document.getElementById(prefix + '-admin-styles');
    if (style) {
        assert.equal(style.textContent.includes('__PFX__'), false);
        assert.ok(style.textContent.includes('.' + prefix + '-toast'));
    }

    if (expected) {
        assert.equal(view.textContent, expected);
        assert.ok(style, 'the admin styles were injected');
    }

    assert.equal(page.errors.length, 0, page.errors.join('\n'));
}
