// Unit tests of kit.shortcuts and kit.dialog({ scopedKeys }) in the fake page
// of fake-dom.mjs, against the cases of kit/testdata/shortcuts/combos.json, which ShortcutComboTests runs on the C# side.
//   node --test kit/web/tests/shortcuts.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, loadKit, plain } from './fake-dom.mjs';

const BUILD = { plugin: 'Kit Test', prefix: 'jfkt', route: 'Fx', version: '1.2.3.0', kit: '0.1.0', mvid: '0123abcd' };
const CASES = JSON.parse(fs.readFileSync(new URL('../../testdata/shortcuts/combos.json', import.meta.url), 'utf8'));

function setup() {
    const page = createPage();
    const exports = loadKit(page);
    const kit = exports.createKit(BUILD, '', {});
    return { page, kit, exports };
}

// Jellyfin Enhanced 12.8-12.11's document capture-phase listener, as observed in a sandbox: it stops its own keys (B on the player)
// with preventDefault and stopImmediatePropagation, and leaves every other combination alone.
function installJeLike(page, keys) {
    const seen = [];
    page.document.addEventListener('keydown', (event) => {
        const combo = (event.shiftKey ? 'Shift+' : '') + (event.metaKey ? 'Meta+' : '') + (event.ctrlKey ? 'Ctrl+' : '') + (event.altKey ? 'Alt+' : '')
            + (/^[a-zA-Z]$/.test(event.key) ? event.key.toUpperCase() : event.key);
        seen.push(combo);
        if (keys.indexOf(combo) >= 0) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    }, true);
    return seen;
}

// --------------------------------------------------------------------------------------------- the notation

test('normalize follows the shared cases', () => {
    const { kit } = setup();
    for (const c of CASES.normalize) {
        assert.equal(kit.shortcuts.normalize(c.in), c.out, JSON.stringify(c.in));
    }

    assert.equal(kit.shortcuts.normalize(null), '');
    assert.equal(kit.shortcuts.normalize(undefined), '');
});

test('comboOf builds the combination of a keydown as Jellyfin Enhanced does, with Space for the space bar', () => {
    const { kit, page } = setup();
    for (const c of CASES.events) {
        const event = new page.Event('keydown', { key: c.key, shiftKey: !!c.shiftKey, ctrlKey: !!c.ctrlKey, altKey: !!c.altKey, metaKey: !!c.metaKey });
        assert.equal(kit.shortcuts.comboOf(event), c.out, JSON.stringify(c));
    }
});

test('isPressable and keyOf follow the shared cases', () => {
    const { kit } = setup();
    for (const c of CASES.pressable) {
        assert.equal(kit.shortcuts.isPressable(c.combo), c.out, c.combo);
    }

    assert.equal(kit.shortcuts.keyOf('Shift+Ctrl+K'), 'K');
    assert.equal(kit.shortcuts.keyOf('Shift++'), '+');
    assert.equal(kit.shortcuts.keyOf(''), null);
});

test('reserved finds a combination in the list the server sent, in any spelling', () => {
    const { kit } = setup();
    const list = ['B', 'Shift+H', '/'];
    assert.deepEqual(kit.shortcuts.reserved('b', list), ['B']);
    assert.deepEqual(kit.shortcuts.reserved('shift+h', list), ['Shift+H']);
    assert.deepEqual(plain(kit.shortcuts.reserved('Shift+B', list)), []);
    assert.deepEqual(plain(kit.shortcuts.reserved('', list)), []);
    assert.deepEqual(plain(kit.shortcuts.reserved('B', null)), []);
    assert.deepEqual(kit.shortcuts.reserved('B', [{ Combo: 'B', Owner: 'JellyfinEnhanced' }]), [{ Combo: 'B', Owner: 'JellyfinEnhanced' }]);
});

test('isBackKey knows a TV remote\'s or gamepad\'s Back key by code or by name, and nothing else', () => {
    const { kit } = setup();
    for (const init of [{ key: 'Unidentified', keyCode: 10009 }, { key: 'Unidentified', keyCode: 461 }, { key: 'Unidentified', keyCode: 196 }, { key: 'Back' }, { key: 'BrowserBack' }, { key: 'XF86Back' }, { key: 'GamepadB' }]) {
        assert.equal(kit.shortcuts.isBackKey(init), true, JSON.stringify(init));
    }

    for (const init of [{ key: 'Escape' }, { key: 'Enter', keyCode: 13 }, { key: 'b' }, { key: 'Unidentified', keyCode: 0 }]) {
        assert.equal(kit.shortcuts.isBackKey(init), false, JSON.stringify(init));
    }

    assert.equal(kit.shortcuts.isBackKey(null), false);
});

test('arrowOf reads a key\'s direction as jellyfin-web\'s keyboard navigation does: arrow names, and TV and gamepad D-pad codes', () => {
    const { kit } = setup();
    const cases = [
        [{ key: 'ArrowLeft' }, 'ArrowLeft'], [{ key: 'ArrowRight', keyCode: 39 }, 'ArrowRight'], [{ key: 'ArrowUp' }, 'ArrowUp'], [{ key: 'ArrowDown' }, 'ArrowDown'],
        [{ key: 'Left', keyCode: 37 }, 'ArrowLeft'], [{ key: 'Unidentified', keyCode: 40 }, 'ArrowDown'],
        [{ key: 'NavigationLeft', keyCode: 140 }, 'ArrowLeft'], [{ key: 'NavigationUp', keyCode: 138 }, 'ArrowUp'],
        [{ key: 'GamepadDPadRight', keyCode: 206 }, 'ArrowRight'], [{ key: 'GamepadDPadDown', keyCode: 204 }, 'ArrowDown'],
        [{ key: 'GamepadLeftThumbstickRight', keyCode: 213 }, 'ArrowRight'], [{ key: 'GamepadLeftThumbstickLeft', keyCode: 214 }, 'ArrowLeft'],
        [{ key: 'Home', keyCode: 36 }, null], [{ key: 'PageUp', keyCode: 33 }, null], [{ key: 'Enter', keyCode: 13 }, null], [{ key: 'a' }, null]
    ];
    for (const [init, expected] of cases) {
        assert.equal(kit.shortcuts.arrowOf(init), expected, JSON.stringify(init));
    }

    assert.equal(kit.shortcuts.arrowOf(null), null);
});

// ------------------------------------------------------------------------------------------------ bindings

test('a binding fires once for Shift+B on the page, never for B that Jellyfin Enhanced stopped, and skips handled keys', () => {
    const { kit, page } = setup();
    const seenByJe = installJeLike(page, ['B']);
    let fired = 0;
    kit.shortcuts.bind('shift+b', () => fired++);

    const shiftB = page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(fired, 1, 'Shift+B fires');
    assert.equal(shiftB, false, 'the event is defaultPrevented afterwards, so a later bubble listener skips it');

    let later = 0;
    page.document.addEventListener('keydown', (event) => {
        if (!event.defaultPrevented) {
            later++;
        }
    });
    page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(fired, 2);
    assert.equal(later, 0, 'Bookmarks 1.0-style listeners after ours see it defaultPrevented');

    const plainB = page.key(page.document.body, 'b');
    assert.equal(fired, 2, 'plain B never reaches the bubble phase: JE stopped it');
    assert.equal(plainB, false);
    assert.deepEqual(seenByJe, ['Shift+B', 'Shift+B', 'B']);

    const prevented = new page.Event('keydown', { bubbles: true, cancelable: true, key: 'B', shiftKey: true });
    prevented.preventDefault();
    page.document.body.dispatchEvent(prevented);
    assert.equal(fired, 2, 'a key another plugin handled is skipped');

    page.key(page.document.body, 'B', { shiftKey: true, repeat: true });
    assert.equal(fired, 2, 'key repeats are skipped');
});

test('bindings stay quiet in text fields, in our dialogs and while a jellyfin-web dialog is open; when() can decline', () => {
    const { kit, page } = setup();
    let fired = 0;
    let allow = true;
    kit.shortcuts.bind('Shift+B', () => fired++, { when: () => allow });

    const input = page.element('input');
    const area = page.element('textarea');
    const select = page.element('select');
    const editable = page.element('div');
    editable.isContentEditable = true;
    page.document.body.append(input, area, select, editable);
    for (const target of [input, area, select, editable]) {
        assert.equal(page.key(target, 'B', { shiftKey: true }), true);
    }

    assert.equal(fired, 0);

    const dialog = kit.dialog({ title: 'Ours' });
    page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(fired, 0, 'our dialog is open');
    dialog.close();
    page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(fired, 1);

    const native = page.element('div', { class: 'dialogContainer' });
    page.document.body.append(native);
    page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(fired, 1, 'a jellyfin-web dialog is open');
    native.remove();

    allow = false;
    assert.equal(page.key(page.document.body, 'B', { shiftKey: true }), true, 'declined: left to others, not prevented');
    assert.equal(fired, 1);
    allow = true;
    page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(fired, 2);
});

test('update follows a settings change, an empty or invalid combination binds nothing, unbind removes the binding', () => {
    const { kit, page } = setup();
    let fired = 0;
    const binding = kit.shortcuts.bind('', () => fired++);
    assert.deepEqual(plain(kit.shortcuts.bindings()), ['']);
    page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(fired, 0);

    binding.update('ctrl+shift+k');
    assert.deepEqual(plain(kit.shortcuts.bindings()), ['Shift+Ctrl+K']);
    page.key(page.document.body, 'K', { shiftKey: true, ctrlKey: true });
    assert.equal(fired, 1);
    page.key(page.document.body, 'k', { ctrlKey: true });
    assert.equal(fired, 1, 'the whole combination must match');

    binding.update('Disabled');
    assert.deepEqual(plain(kit.shortcuts.bindings()), ['']);

    binding.update('B');
    binding.unbind();
    page.key(page.document.body, 'b');
    assert.equal(fired, 1);
    assert.deepEqual(plain(kit.shortcuts.bindings()), []);
    assert.throws(() => kit.shortcuts.bind('B'), { name: 'TypeError', message: 'kit.shortcuts.bind needs a handler' });
});

test('the first matching binding handles a press, and a handler that throws is reported, not thrown', () => {
    const { kit, page } = setup();
    const order = [];
    kit.shortcuts.bind('B', () => {
        order.push('first');
        throw new Error('boom');
    });
    kit.shortcuts.bind('B', () => order.push('second'));
    assert.equal(page.key(page.document.body, 'b'), false);
    assert.deepEqual(order, ['first']);
    assert.equal(page.warnings.length, 1);
});

// ---------------------------------------------------------------------------------------- scoped dialog keys

test('a scopedKeys dialog keeps keys inside it from Jellyfin Enhanced, handles Escape and Tab, and is removed on close', () => {
    const { kit, page } = setup();
    const seenByJe = installJeLike(page, ['S', 'O']);
    const keys = [];
    const dialog = kit.dialog({ title: 'Name', scopedKeys: true, onKey: (event) => {
        keys.push(event.key);
        if (event.key === 'Enter') {
            event.preventDefault();
            return true;
        }

        return false;
    } });
    const input = kit.input({ type: 'text' });
    const save = kit.button('Save');
    dialog.body.append(input, save);
    save.focus();

    assert.equal(page.key(save, 'S'), true, 'S on Save keeps its default action');
    assert.equal(page.key(save, 'O'), true);
    assert.deepEqual(seenByJe, [], 'Jellyfin Enhanced never saw them');
    assert.equal(page.key(save, 'Enter'), false, 'onKey handled Enter');
    assert.deepEqual(keys, ['S', 'O', 'Enter']);

    // Tab on the last focusable wraps to the first; Shift+Tab on the first wraps to the last.
    assert.equal(page.key(save, 'Tab'), false);
    assert.equal(page.document.activeElement.tagName, 'BUTTON', 'the close button is first');
    assert.equal(page.key(page.document.activeElement, 'Tab', { shiftKey: true }), false);
    assert.equal(page.document.activeElement, save);

    // Keys on an element outside the panel are left alone, JE included. One whose target is the page's body (nothing has
    // the focus) brings the focus back into the topmost dialog and goes no further.
    const outside = kit.button('Outside');
    page.document.body.append(outside);
    page.key(outside, 'S');
    assert.deepEqual(seenByJe, ['S']);
    save.blur();
    assert.equal(page.key(page.document.body, 'O'), false, 'a key on the body is the dialog\'s');
    assert.deepEqual(seenByJe, ['S'], 'Jellyfin Enhanced never saw it');
    assert.equal(page.document.activeElement, input, 'the focus back in the panel, on its first control');
    save.focus();

    assert.equal(page.key(save, 'Escape'), false);
    assert.equal(kit.dialog.isOpen(), false, 'Escape closed it');
    page.key(page.document.body, 'O');
    assert.deepEqual(seenByJe, ['S', 'O'], 'the window listener is gone with the dialog');
});

test('a key pressed while nothing has the focus brings it back into the topmost scopedKeys dialog, never to the page behind', async () => {
    const { kit, page } = setup();
    const seenByPage = [];
    page.document.addEventListener('keydown', (event) => seenByPage.push(event.key));
    page.document.documentElement.classList.add('layout-tv');
    const row = kit.button('Delete clip');
    page.document.body.append(row);
    row.focus();
    const dialog = kit.dialog({ title: 'Delete clip?', modal: true, scopedKeys: true });
    const cancel = kit.button('Cancel', { onClick: dialog.close });
    const remove = kit.button('Delete');
    dialog.body.append(page.element('p'), cancel, remove);
    dialog.panel.querySelector('button').focus();
    await Promise.resolve();
    assert.ok(dialog.panel.contains(page.document.activeElement), 'the caller\'s own focus stays');

    // The Delete button disabled while its request runs (the browser drops the focus to the body), then enabled again for
    // another try: the D-pad's next key brings the focus back to the dialog, not to the page's own navigation.
    remove.focus();
    remove.blur();
    assert.equal(page.document.activeElement, page.document.body);
    assert.equal(page.key(page.document.body, 'ArrowDown'), false, 'handled');
    assert.equal(page.document.activeElement, cancel, 'into the panel: its body\'s first control');
    page.key(cancel, 'ArrowDown');
    assert.equal(page.document.activeElement, remove);
    remove.blur();
    assert.equal(page.key(page.document.body, 'Enter'), false, 'Enter on the body clicks nothing behind the dialog');
    assert.ok(dialog.panel.contains(page.document.activeElement));
    assert.deepEqual(seenByPage, [], 'none of it reached the page');

    // A dialog over it is the topmost: the key is that one's. Escape and Back still close.
    const top = kit.dialog({ title: 'On top', scopedKeys: true });
    const ok = kit.button('OK');
    top.body.append(ok);
    page.document.activeElement = page.document.body;
    page.key(page.document.body, 'ArrowUp');
    assert.equal(page.document.activeElement, ok);
    page.document.activeElement = page.document.body;
    assert.equal(page.key(page.document.body, 'Escape'), false);
    assert.equal(top.panel.isConnected, false);
    assert.ok(dialog.panel.isConnected, 'only the topmost one');
    page.document.activeElement = page.document.body;
    assert.equal(page.key(page.document.body, 'Back'), false);
    assert.equal(kit.dialog.isOpen(), false);
    assert.equal(page.document.activeElement, row, 'back on the button that opened it');
    assert.equal(page.key(page.document.body, 'ArrowDown'), true, 'with no dialog open the key is the page\'s again');
    assert.deepEqual(seenByPage, ['ArrowDown']);
});

test('a scopedKeys dialog leaves the keys of an IME composition to the composition (no Save, no Skip), still kept from the player', () => {
    const { kit, page } = setup();
    const seenByJe = installJeLike(page, []);
    const keys = [];
    const dialog = kit.dialog({ title: 'Name', scopedKeys: true, onKey: (event) => {
        keys.push(event.key);
        return false;
    } });
    const input = kit.input({ type: 'text' });
    dialog.body.append(input);
    input.focus();

    assert.equal(page.key(input, 'Enter', { isComposing: true }), true, 'Enter commits the composition: not prevented');
    assert.equal(page.key(input, 'Escape', { isComposing: true }), true, 'Escape cancels the composition: not prevented');
    assert.equal(page.key(input, 'Process', { keyCode: 229 }), true, 'keyCode 229 is a composition too');
    assert.deepEqual(keys, [], 'onKey never saw them');
    assert.ok(kit.dialog.isOpen(), 'the dialog is still open');
    assert.deepEqual(seenByJe, [], 'kept from Jellyfin Enhanced all the same');

    assert.equal(page.key(input, 'Escape'), false, 'a plain Escape closes');
    assert.equal(kit.dialog.isOpen(), false);
});

test('a TV remote\'s Back key closes a scopedKeys dialog, and on the TV layout the D-pad moves the focus through it', () => {
    const { kit, page } = setup();
    const seenByPage = [];
    page.document.addEventListener('keydown', (event) => seenByPage.push(event.key));
    page.document.documentElement.classList.add('layout-tv');
    const dialog = kit.dialog({ title: 'Name', scopedKeys: true });
    const input = kit.input({ type: 'text' });
    const box = page.element('input', { type: 'checkbox' });
    const skip = kit.button('Skip');
    const save = kit.button('Save');
    dialog.body.append(input, box, skip, save);
    const closeButton = dialog.panel.querySelector('button');
    input.focus();

    assert.equal(page.key(input, 'ArrowDown'), false, 'handled');
    assert.equal(page.document.activeElement, box);
    assert.equal(page.key(box, 'ArrowRight'), false, 'Right on a checkbox moves on');
    assert.equal(page.document.activeElement, skip);
    page.key(skip, 'ArrowRight');
    assert.equal(page.document.activeElement, save);
    assert.equal(page.key(save, 'ArrowDown'), false);
    assert.equal(page.document.activeElement, save, 'no wrap past the last');
    page.key(save, 'ArrowUp');
    assert.equal(page.document.activeElement, skip);
    page.key(skip, 'ArrowLeft');
    assert.equal(page.document.activeElement, box);
    page.key(box, 'ArrowUp');
    assert.equal(page.document.activeElement, input);
    assert.equal(page.key(input, 'ArrowLeft'), true, 'Left in a text field is the caret\'s');
    assert.equal(page.key(input, 'ArrowRight'), true);
    assert.equal(page.document.activeElement, input);
    page.key(input, 'ArrowUp');
    assert.equal(page.document.activeElement, closeButton, 'the close button is first');
    assert.equal(page.key(closeButton, 'ArrowUp'), false);
    assert.equal(page.document.activeElement, closeButton, 'no wrap before the first');
    assert.equal(page.key(closeButton, 'Unidentified', { keyCode: 204 }), false, 'GamepadDPadDown by keyCode');
    assert.equal(page.document.activeElement, input);
    assert.equal(page.key(input, 'Unidentified', { keyCode: 139 }), false, 'NavigationDown by keyCode');
    assert.equal(page.document.activeElement, box);
    assert.deepEqual(seenByPage, [], 'none of it reached the page');

    for (const init of [{ key: 'Unidentified', keyCode: 10009 }, { key: 'Unidentified', keyCode: 461 }, { key: 'Unidentified', keyCode: 196 }, { key: 'Back' }, { key: 'BrowserBack' }, { key: 'XF86Back' }]) {
        const open = kit.dialog.isOpen() ? dialog : kit.dialog({ title: 'Again', scopedKeys: true });
        const button = kit.button('Save');
        open.body.append(button);
        button.focus();
        assert.equal(page.key(button, init.key, init), false, JSON.stringify(init) + ' is handled');
        assert.equal(kit.dialog.isOpen(), false, JSON.stringify(init) + ' closes the dialog');
    }

    // Off the TV layout the arrows keep their default action and move nothing; they are still kept from the page.
    page.document.documentElement.classList.remove('layout-tv');
    const desktop = kit.dialog({ title: 'Desktop', scopedKeys: true });
    const a = kit.button('A');
    const b = kit.button('B');
    desktop.body.append(a, b);
    a.focus();
    assert.equal(page.key(a, 'ArrowDown'), true, 'not handled off the TV layout');
    assert.equal(page.document.activeElement, a);
    assert.equal(page.key(a, 'Back'), false, 'Back closes on every layout');
    assert.equal(kit.dialog.isOpen(), false);
    assert.deepEqual(seenByPage, []);
});

// A synthetic user agent with what jellyfin-web 10.11.5's browser.js looks for (tv, hisense and vidaa).
const HISENSE_UA = 'Mozilla/5.0 (Linux; VIDAA 7.0; Hisense SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0 Safari/537.36';
const DESKTOP_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36';

// A dialog with every kind of control, opened from a button of the page; returns its controls.
function backspaceDialog(kit, page, options) {
    const opener = page.element('button');
    page.document.body.append(opener);
    opener.focus();
    const dialog = kit.dialog(Object.assign({ title: 'Edit' }, options));
    const text = page.element('input', { type: 'text' });
    const search = page.element('input', { type: 'search' });
    const area = page.element('textarea');
    const select = page.element('select');
    const editable = page.element('div', { contenteditable: 'true' });
    const inside = page.element('span');
    editable.append(inside);
    const frozen = page.element('span', { contenteditable: 'false' });
    editable.append(frozen);
    const box = page.element('input', { type: 'checkbox' });
    const save = kit.button('Save');
    dialog.body.append(text, search, area, select, editable, box, save);
    return { opener, dialog, text, search, area, select, editable, inside, frozen, box, save };
}

test('Backspace is Back in a dialog on a Hisense TV, off anything editable, and the focus goes back to the opener', async () => {
    for (const scopedKeys of [true, false]) {
        const { kit, page } = setup();
        page.context.navigator = { userAgent: HISENSE_UA };
        const d = backspaceDialog(kit, page, { scopedKeys });
        await page.flush();
        for (const field of [d.text, d.search, d.area, d.select, d.editable, d.inside]) {
            field.focus();
            assert.equal(page.key(field, 'Backspace', { keyCode: 8 }), true, field.tagName + ': Backspace still deletes a character (' + scopedKeys + ')');
            assert.equal(kit.dialog.isOpen(), true, field.tagName + ' keeps the dialog open (' + scopedKeys + ')');
        }

        d.box.focus();
        assert.equal(page.key(d.box, 'Backspace', { keyCode: 8, ctrlKey: true }), true, 'Ctrl+Backspace is not Back');
        assert.equal(kit.dialog.isOpen(), true);
        assert.equal(page.key(d.box, 'Backspace', { keyCode: 8 }), false, 'Backspace on a checkbox is Back (' + scopedKeys + ')');
        assert.equal(kit.dialog.isOpen(), false, 'and closes the dialog (' + scopedKeys + ')');
        assert.equal(page.document.activeElement, d.opener, 'the focus is back on the opener (' + scopedKeys + ')');
    }

    // contenteditable="false" inside an editable element is not editable; a button isn't either.
    const { kit, page } = setup();
    page.context.navigator = { userAgent: HISENSE_UA };
    const d = backspaceDialog(kit, page, { scopedKeys: true });
    await page.flush();
    assert.equal(page.key(d.frozen, 'Backspace', { keyCode: 8 }), false, 'contenteditable="false" is Back');
    assert.equal(kit.dialog.isOpen(), false);
    const again = backspaceDialog(kit, page, { scopedKeys: true });
    await page.flush();
    again.save.focus();
    assert.equal(page.key(again.save, 'Backspace'), false, 'a button: Back by the key name alone');
    assert.equal(kit.dialog.isOpen(), false);
});

// A keydown from inside a web component's open shadow root, as a listener outside it sees it: the target is the host, and
// the composed path starts at the element in the shadow root (inner); a path without one (an older engine) when inner is null.
function shadowKey(page, host, inner, init) {
    const event = new page.Event('keydown', Object.assign({ bubbles: true, cancelable: true }, init));
    if (inner) {
        event.composedPath = () => [inner, { host }, host, page.document.body, page.document.documentElement, page.document, page.context];
    }

    return host.dispatchEvent(event);
}

test('Backspace in a text field inside a shadow root is the field\'s, not Back, by the composed path', async () => {
    for (const scopedKeys of [true, false]) {
        const { kit, page } = setup();
        page.context.navigator = { userAgent: HISENSE_UA };
        const d = backspaceDialog(kit, page, { scopedKeys });
        await page.flush();
        const host = page.element('x-field');
        d.dialog.body.append(host);
        const field = page.element('input', { type: 'text' });
        const button = page.element('button');

        assert.equal(shadowKey(page, host, field, { key: 'Backspace', keyCode: 8 }), true, 'the field deletes a character (' + scopedKeys + ')');
        assert.equal(kit.dialog.isOpen(), true, 'the dialog stays open (' + scopedKeys + ')');

        // The control: off a field (a button in the shadow root, or no composed path to tell), Backspace is Back.
        assert.equal(shadowKey(page, host, button, { key: 'Backspace', keyCode: 8 }), false, 'a button in the shadow root: Back (' + scopedKeys + ')');
        assert.equal(kit.dialog.isOpen(), false);
        const again = backspaceDialog(kit, page, { scopedKeys });
        await page.flush();
        const bare = page.element('x-field');
        again.dialog.body.append(bare);
        assert.equal(shadowKey(page, bare, null, { key: 'Backspace', keyCode: 8 }), false, 'no composed path: the host decides (' + scopedKeys + ')');
        assert.equal(kit.dialog.isOpen(), false);
    }
});

test('on the TV layout Left and Right in a text field inside a shadow root keep the caret, by the composed path', () => {
    const { kit, page } = setup();
    page.document.documentElement.classList.add('layout-tv');
    const dialog = kit.dialog({ title: 'Name', scopedKeys: true });
    const host = page.element('x-field');
    const after = kit.button('Save');
    dialog.body.append(host, after);
    const field = page.element('input', { type: 'text' });
    const button = page.element('button');
    host.setAttribute('tabindex', '0');
    host.focus();

    for (const arrow of ['ArrowLeft', 'ArrowRight']) {
        assert.equal(shadowKey(page, host, field, { key: arrow }), true, arrow + ' in the shadow root\'s field is the caret\'s');
        assert.equal(page.document.activeElement, host, arrow + ' moves nothing');
    }

    // The control: off a field (a button in the shadow root, or no composed path to tell), Right moves the focus on.
    assert.equal(shadowKey(page, host, button, { key: 'ArrowRight' }), false, 'a button in the shadow root: the D-pad');
    assert.equal(page.document.activeElement, after);
    host.focus();
    assert.equal(shadowKey(page, host, null, { key: 'ArrowRight' }), false, 'no composed path: the host decides');
    assert.equal(page.document.activeElement, after);
    dialog.close();
});

test('a shortcut stays quiet while someone types in a field inside a shadow root, by the composed path', () => {
    const { kit, page } = setup();
    let fired = 0;
    kit.shortcuts.bind('Shift+B', () => fired++);
    const host = page.element('x-search');
    page.document.body.append(host);
    for (const tag of ['input', 'textarea', 'select']) {
        assert.equal(shadowKey(page, host, page.element(tag), { key: 'B', shiftKey: true }), true, tag + ': typed, not handled');
    }

    const editable = page.element('div');
    editable.isContentEditable = true;
    shadowKey(page, host, editable, { key: 'B', shiftKey: true });
    assert.equal(fired, 0, 'no shortcut from a field in the shadow root');

    // The control: a button in the shadow root, or a host with no composed path, is no field.
    assert.equal(shadowKey(page, host, page.element('button'), { key: 'B', shiftKey: true }), false, 'a button: the shortcut');
    assert.equal(fired, 1);
    shadowKey(page, host, null, { key: 'B', shiftKey: true });
    assert.equal(fired, 2, 'no composed path: the host decides');
});

test('Backspace is Back on the TV layout off a text field, and changes nothing in a dialog on a desktop browser', async () => {
    const { kit, page } = setup();
    page.context.navigator = { userAgent: DESKTOP_UA };
    for (const scopedKeys of [true, false]) {
        const d = backspaceDialog(kit, page, { scopedKeys });
        await page.flush();
        d.save.focus();
        assert.equal(page.key(d.save, 'Backspace', { keyCode: 8 }), true, 'desktop: not handled (' + scopedKeys + ')');
        d.box.focus();
        page.key(d.box, 'Backspace', { keyCode: 8 });
        assert.equal(kit.dialog.isOpen(), true, 'desktop: Backspace never closes a dialog (' + scopedKeys + ')');
        d.dialog.close();
    }

    page.document.documentElement.classList.add('layout-tv');
    const d = backspaceDialog(kit, page, { scopedKeys: true });
    await page.flush();
    d.text.focus();
    assert.equal(page.key(d.text, 'Backspace', { keyCode: 8 }), true, 'TV: a text field keeps Backspace');
    assert.equal(kit.dialog.isOpen(), true);
    d.save.focus();
    assert.equal(page.key(d.save, 'Backspace', { keyCode: 8 }), false, 'TV: Backspace on a button is Back');
    assert.equal(kit.dialog.isOpen(), false);
    assert.equal(page.document.activeElement, d.opener);
});

test('kit.confirm on a Hisense TV: Backspace dismisses it and the focus goes back to the opener', async () => {
    const { kit, page } = setup();
    page.context.navigator = { userAgent: HISENSE_UA };
    const opener = page.element('button');
    page.document.body.append(opener);
    opener.focus();
    const answer = kit.confirm({ title: 'Remove?', message: 'Sure?', confirmLabel: 'Remove' });
    await page.flush();
    const cancel = page.document.querySelector('.jfkt-actions .jfkt-button:not(.jfkt-button-primary)');
    assert.equal(page.document.activeElement, cancel);
    assert.equal(page.key(cancel, 'Backspace', { keyCode: 8 }), false);
    assert.equal(await answer, false, 'Backspace dismisses it');
    assert.equal(kit.dialog.isOpen(), false);
    assert.equal(page.document.activeElement, opener);
});

test('without scopedKeys the panel stops keys only in the bubble phase, as before', () => {
    const { kit, page } = setup();
    const seenByJe = installJeLike(page, []);
    const dialog = kit.dialog({ title: 'Plain' });
    const save = kit.button('Save');
    dialog.body.append(save);
    page.key(save, 'S');
    assert.deepEqual(seenByJe, ['S'], 'Jellyfin Enhanced\'s capture listener runs first');
});
