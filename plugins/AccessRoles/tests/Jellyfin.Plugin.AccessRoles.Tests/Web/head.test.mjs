// Access Roles' head script (the first load in a new browser) in the kit's fake page, and the
// ClientScript's copy of its rule (30-rules.js headClasses): the classes on <html> from the browser's cache before
// jellyfin-web's bundles run. WebTests runs it with the built scripts (guard-page.mjs says how).
import test from 'node:test';
import assert from 'node:assert/strict';
import { skip, blankPage, runHead, start, guard, USER, OTHER, EVERY_SECTION } from './guard-page.mjs';
import { plain as plainCopy } from '../../../../../kit/web/tests/fake-dom.mjs';

const KID = { Managed: true, Locked: ['Profile', 'Home', 'QuickConnect'], AllSettingsLocked: false };
const GUEST = { Managed: true, Locked: EVERY_SECTION.concat(['AdminSurfaces']), AllSettingsLocked: true };
const UNMANAGED = { Managed: false, Locked: [], AllSettingsLocked: false };

test('a managed user\'s cached locks become classes, with jfar-pending until /Me answers', { skip }, () => {
    const page = blankPage({ cache: { [USER]: KID } });
    const head = runHead(page);

    assert.deepEqual(plainCopy(head.classes), ['jfar-lock-profile', 'jfar-lock-home', 'jfar-lock-quickconnect', 'jfar-pending']);
    assert.ok(page.document.documentElement.classList.contains('jfar-lock-home'));
    assert.equal(head.userId, USER);
    assert.equal(head.before.events, false);
    assert.equal(head.error, undefined);
});

test('the head lifts its own jfar-pending after 3 s, should the ClientScript never run', { skip }, async () => {
    const page = blankPage({ cache: { [USER]: KID } });
    runHead(page);
    const root = page.document.documentElement.classList;
    assert.ok(root.contains('jfar-pending'));
    await page.advance(2900);
    assert.ok(root.contains('jfar-pending'));
    await page.advance(200);
    assert.equal(root.contains('jfar-pending'), false);
    assert.ok(root.contains('jfar-lock-home'), 'the cached locks stay');

    // No pending, no timer.
    const quiet = blankPage({ cache: { [USER]: UNMANAGED } });
    assert.equal(runHead(quiet).pendingTimer, undefined);
});

test('the ClientScript takes the head\'s jfar-pending over: the head\'s timer is cleared, its own waits for /Me', { skip }, async () => {
    const page = await start({ cache: { [USER]: KID }, me: { UserId: USER, Managed: true, Locked: ['Home'] }, meExtra: { hang: true } });
    assert.ok(page.document.documentElement.classList.contains('jfar-pending'));
    assert.equal(page.context.__jfarHead.pendingTimer, null, 'the head\'s timer is the ClientScript\'s to clear');
    await page.advance(3100);
    assert.equal(page.document.documentElement.classList.contains('jfar-pending'), false);
    assert.equal(guard(page).rules().source, 'cache');
});

test('every section locked adds jfar-lock-allsettings; a cached Managed false adds nothing, not even jfar-pending', { skip }, () => {
    assert.ok(runHead(blankPage({ cache: { [USER]: GUEST } })).classes.includes('jfar-lock-allsettings'));
    assert.deepEqual(plainCopy(runHead(blankPage({ cache: { [USER]: UNMANAGED } })).classes), []);
});

test('the first-load rule: no cached answer means jfar-pending only while the server manages anyone, and never ShowUntilKnown', { skip }, () => {
    assert.deepEqual(plainCopy(runHead(blankPage({})).classes), ['jfar-pending']);
    assert.deepEqual(plainCopy(runHead(blankPage({ head: { anyManaged: false } })).classes), []);
    assert.deepEqual(plainCopy(runHead(blankPage({ head: { firstLoad: 'ShowUntilKnown' } })).classes), []);
    assert.deepEqual(plainCopy(runHead(blankPage({ head: { firstLoad: 'ShowUntilKnown' }, cache: { [USER]: KID } })).classes), ['jfar-lock-profile', 'jfar-lock-home', 'jfar-lock-quickconnect']);
});

test('signed out, enforcement off, or storage that can\'t be read: no class and no error', { skip }, () => {
    assert.deepEqual(plainCopy(runHead(blankPage({ credentials: false, cache: { [USER]: KID } })).classes), []);
    assert.deepEqual(plainCopy(runHead(blankPage({ head: { enforced: false }, cache: { [USER]: KID } })).classes), []);
    const page = blankPage({});
    page.context.localStorage.getItem = () => {
        throw new Error('storage is off');
    };
    const head = runHead(page);
    assert.deepEqual(plainCopy(head.classes), []);
    assert.match(head.error, /storage is off/);
});

test('the last signed-in server wins, its user id in either form', { skip }, () => {
    const page = blankPage({ credentials: false, cache: { [OTHER]: KID } });
    page.context.localStorage.setItem('jellyfin_credentials', JSON.stringify({
        Servers: [
            { UserId: USER, AccessToken: 'a', DateLastAccessed: 1 },
            { UserId: 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB', AccessToken: 'b', DateLastAccessed: 9 },
            { UserId: 'cccccccccccccccccccccccccccccccc', DateLastAccessed: 99 }
        ]
    }));
    const head = runHead(page);
    assert.equal(head.userId, OTHER);
    assert.ok(head.classes.includes('jfar-lock-home'));
});

test('the ClientScript\'s rule gives exactly the head script\'s classes, over every case', { skip }, async () => {
    const client = guard(await start({ me: { UserId: USER, Managed: false, Locked: [] } }));
    for (const enforced of [true, false]) {
        for (const anyManaged of [true, false]) {
            for (const firstLoad of ['HideUntilKnown', 'ShowUntilKnown']) {
                for (const cached of [null, KID, GUEST, UNMANAGED]) {
                    for (const signedIn of [true, false]) {
                        const flags = { enforced, anyManaged, firstLoad, notice: 'Toast', userPages: true };
                        const page = blankPage({ head: flags, credentials: signedIn, cache: cached ? { [USER]: cached } : {} });
                        const expected = plainCopy(client.headClasses(flags, signedIn ? USER : null, cached));
                        assert.deepEqual(plainCopy(runHead(page).classes), expected, JSON.stringify({ flags, cached, signedIn }));
                    }
                }
            }
        }
    }
});
