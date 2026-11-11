// Unit tests of kit/web/kit.js in the fake page of fake-dom.mjs. Run with
//   node --test kit/web/tests
// Kit.Tests runs them too (KitScriptTests), skipping when Node isn't on PATH.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createPage, loadKit, plain } from './fake-dom.mjs';

const BUILD = { plugin: 'Kit Test', prefix: 'jfkt', route: 'Fx', version: '1.2.3.0', kit: '0.1.0', mvid: '0123abcd' };
const CSS = '.jfkt-toast{}';

function setup(options) {
    const page = createPage(options);
    const exports = loadKit(page);
    const kit = exports.createKit(BUILD, CSS, options && options.kitOptions);
    return { page, kit, exports };
}

async function signedIn(options) {
    const s = setup(options);
    s.kit.start();
    s.page.installApiClient({ userId: 'user-1', items: options && options.items, admin: options && options.admin });
    await s.page.advance(60);
    return s;
}

// ------------------------------------------------------------------------------------------------ DOM builder

test('h writes text as text nodes, never as HTML', () => {
    const { kit } = setup();
    const node = kit.h('div', { class: 'a b', text: '<img src=x onerror=alert(1)>', title: 'T', hidden: true, skipped: false, empty: null }, [kit.h('span', { text: 'child' }), null, false, 'tail']);
    assert.equal(node.className, 'a b');
    assert.equal(node.textContent, '<img src=x onerror=alert(1)>childtail');
    assert.equal(node.querySelectorAll('img').length, 0);
    assert.equal(node.getAttribute('title'), 'T');
    assert.equal(node.getAttribute('hidden'), '');
    assert.equal(node.hasAttribute('skipped'), false);
    assert.equal(node.hasAttribute('empty'), false);
});

test('h wires on* handlers as listeners and sets value as a property', () => {
    const { kit, page } = setup();
    let clicks = 0;
    const input = kit.h('input', { value: 'v', onclick: () => clicks++ });
    input.click();
    assert.equal(clicks, 1);
    assert.equal(input.value, 'v');
    assert.equal(input.hasAttribute('onclick'), false);
    assert.ok(page);
});

test('append skips missing children, where the DOM append would add the text "null"', () => {
    const { kit, page } = setup();
    const parent = page.element('div');
    kit.append(parent, 'a', null, undefined, false, kit.h('b', { text: 'c' }));
    assert.equal(parent.textContent, 'ac');
    const raw = page.element('div');
    raw.append(null);
    assert.equal(raw.textContent, 'null', 'the fake DOM behaves like the real one');
});

test('icon, iconButton and button use the kit classes and the native accent class for primary', () => {
    const { kit } = setup();
    assert.equal(kit.icon('close').className, 'material-icons close');
    assert.equal(kit.icon('close').getAttribute('aria-hidden'), 'true');
    let outer = 0;
    let inner = 0;
    const wrapper = kit.h('div', { onclick: () => outer++ });
    const iconButton = kit.iconButton('edit', 'Edit', () => inner++);
    wrapper.append(iconButton);
    iconButton.click();
    assert.equal(inner, 1);
    assert.equal(outer, 0, 'iconButton stops propagation');
    assert.equal(iconButton.className, 'jfkt-icon-button');
    assert.equal(iconButton.getAttribute('aria-label'), 'Edit');
    assert.equal(kit.button('Save').className, 'jfkt-button');
    assert.equal(kit.button('Save').textContent, 'Save');
    assert.equal(kit.button('Save', { variant: 'primary', type: 'submit' }).className, 'jfkt-button jfkt-button-primary button-submit');
    assert.equal(kit.button('Save', { variant: 'primary', type: 'submit' }).getAttribute('type'), 'submit');
    assert.equal(kit.button('Delete', { variant: 'danger' }).className, 'jfkt-button jfkt-button-danger');
    const withIcon = kit.button('New', { icon: 'add' });
    assert.equal(withIcon.childNodes.length, 2);
    assert.equal(withIcon.textContent, 'New');
});

test('input, select and field use the native emby classes and label the input', () => {
    const { kit } = setup();
    const input = kit.input({ value: 'x', placeholder: 'p' });
    assert.equal(input.className, 'jfkt-input emby-input');
    assert.equal(input.getAttribute('type'), 'text');
    const select = kit.select({ title: 'Mode' }, [{ value: 'a', text: 'A' }, { value: 'b', text: 'B' }]);
    assert.equal(select.className, 'jfkt-select emby-select-withcolor');
    assert.equal(select.children.length, 2);
    const field = kit.field('Start', input, [null, kit.iconButton('clear', 'Clear', () => {})]);
    assert.equal(field.className, 'jfkt-field');
    assert.equal(field.querySelector('label').getAttribute('for'), input.id);
    assert.match(input.id, /^jfkt-field-\d+$/);
    assert.equal(field.querySelector('.jfkt-field-row').children.length, 2);
});

test('textarea uses the native emby-textarea class, keeps its attributes and value, and can be a field', () => {
    const { kit } = setup();
    const note = kit.textarea({ value: 'line one\nline two', maxlength: '500', rows: '3', class: 'extra' });
    assert.equal(note.tagName, 'TEXTAREA');
    assert.equal(note.className, 'jfkt-textarea emby-textarea extra');
    assert.equal(note.getAttribute('maxlength'), '500');
    assert.equal(note.getAttribute('rows'), '3');
    assert.equal(note.getAttribute('type'), null, 'no type attribute, unlike input');
    assert.equal(note.value, 'line one\nline two');
    const field = kit.field('Note', note);
    assert.equal(field.querySelector('label').getAttribute('for'), note.id);
    assert.equal(kit.textarea().className, 'jfkt-textarea emby-textarea');
});

test("headerButton: a header button shaped like jellyfin-web's own, its click the plugin's", () => {
    const { kit } = setup();
    let pressed = 0;
    let prevented = false;
    const button = kit.headerButton({ className: 'jfkt-header', icon: 'manage_search', title: 'Advanced search', onClick: (e) => { pressed++; prevented = e.defaultPrevented; } });
    assert.equal(button.tagName, 'BUTTON');
    assert.equal(button.getAttribute('is'), 'paper-icon-button-light');
    assert.equal(button.getAttribute('type'), 'button');
    assert.equal(button.className, 'jfkt-header headerButton headerButtonRight paper-icon-button-light');
    assert.equal(button.getAttribute('title'), 'Advanced search');
    assert.equal(button.getAttribute('aria-label'), 'Advanced search');
    assert.equal(button.firstChild.className, 'material-icons manage_search');
    assert.equal(button.firstChild.getAttribute('aria-hidden'), 'true');
    button.click();
    assert.equal(pressed, 1);
    assert.equal(prevented, true);
});

test('osdButton, detailButton and section give Bookmarks 1.0 its native markup', () => {
    const { kit } = setup();
    let pressed = 0;
    const osd = kit.osdButton({ className: 'btnX', icon: 'bookmarks', title: 'Bookmarks', onClick: () => pressed++ });
    assert.equal(osd.getAttribute('is'), 'paper-icon-button-light');
    assert.equal(osd.className, 'btnX autoSize paper-icon-button-light');
    assert.equal(osd.firstChild.className, 'xlargePaperIconButton material-icons bookmarks');
    osd.click();
    assert.equal(pressed, 1);
    const detail = kit.detailButton({ className: 'btnY', icon: 'bookmark_add', title: 'Add', onClick: () => {} });
    assert.equal(detail.getAttribute('is'), 'emby-button');
    assert.equal(detail.className, 'button-flat btnY detailButton emby-button');
    assert.equal(detail.firstChild.className, 'detailButton-content');
    assert.equal(detail.firstChild.firstChild.className, 'material-icons detailButton-icon bookmark_add');
    const parts = kit.section({ title: 'Things', className: 'jfkt-section', bodyClass: 'jfkt-list', hidden: true, attrs: { 'data-item-id': 'abc' } });
    assert.equal(parts.section.className, 'verticalSection detailVerticalSection jfkt-section hide');
    assert.equal(parts.section.getAttribute('data-item-id'), 'abc');
    assert.equal(parts.title.className, 'sectionTitle sectionTitle-cards padded-right');
    assert.equal(parts.body.className, 'jfkt-list padded-right');
    assert.equal(kit.isHidden(parts.section), true);
    kit.setHidden(parts.section, false);
    assert.equal(parts.section.className, 'verticalSection detailVerticalSection jfkt-section');
});

test('makeFocusable: a tab stop with the class jellyfin-web\'s TV focus manager looks for, keeping a tabindex it has', () => {
    const { kit } = setup();
    const slider = kit.h('div', { class: 'jfkt-slider', role: 'slider' });
    assert.equal(kit.makeFocusable(slider), slider);
    assert.equal(slider.getAttribute('tabindex'), '0');
    assert.equal(slider.className, 'jfkt-slider focusable');
    kit.makeFocusable(slider);
    assert.equal(slider.className, 'jfkt-slider focusable', 'once');
    const scripted = kit.makeFocusable(kit.h('div', { tabindex: '-1' }));
    assert.equal(scripted.getAttribute('tabindex'), '-1', 'a tabindex it has is kept');
    assert.equal(scripted.className, 'focusable');
});

// ------------------------------------------------------------------------------------------ item markup

test('card writes native classes, data-id in N format and data-type, and links to the details page', async () => {
    const { kit } = await signedIn();
    const card = kit.card({ Id: 'AB12CD34-0000-0000-0000-00000000EF56', Type: 'Movie', Name: '<b>Title</b>', ProductionYear: 2021, ImageTags: { Primary: 'tag1' } });
    assert.equal(card.className, 'card portraitCard jfkt-card');
    assert.equal(card.getAttribute('data-id'), 'AB12CD34-0000-0000-0000-00000000EF56'.split('-').join('').toLowerCase());
    assert.match(card.getAttribute('data-id'), /^[0-9a-f]{32}$/);
    assert.equal(card.getAttribute('data-type'), 'Movie');
    assert.equal(card.hasAttribute('data-isfolder'), false);
    assert.ok(card.querySelector('.cardBox.cardBox-bottompadded > .cardScalable > .cardPadder.cardPadder-portrait'));
    const link = card.querySelector('a.cardImageContainer.cardContent.coveredImage');
    assert.equal(link.getAttribute('href'), '#/details?id=' + card.getAttribute('data-id') + '&serverId=server-1');
    assert.equal(link.getAttribute('aria-label'), '<b>Title</b>');
    const img = link.querySelector('img.jfkt-card-image');
    assert.equal(img.getAttribute('loading'), 'lazy');
    assert.match(img.getAttribute('src'), /Images\/Primary\?tag=tag1&maxWidth=400$/);
    assert.equal(card.querySelector('.cardText.cardText-first').textContent, '<b>Title</b>');
    assert.equal(card.querySelectorAll('b').length, 0);
    assert.equal(card.querySelector('.cardText.cardText-secondary').textContent, '2021');
});

test('card without an image shows the icon on the default background; folders say so', async () => {
    const { kit } = await signedIn();
    const card = kit.card({ id: 'aa-bb', type: 'BoxSet', isFolder: true, title: 'Box', icon: 'folder' }, { shape: 'square' });
    assert.equal(card.className, 'card squareCard jfkt-card');
    assert.equal(card.getAttribute('data-isfolder'), 'true');
    assert.equal(card.getAttribute('data-id'), 'aabb');
    assert.ok(card.querySelector('.cardImageContainer.defaultCardBackground .cardImageIcon.material-icons.folder'));
    assert.ok(card.querySelector('.cardPadder-square'));
});

test('nativeItemId: false writes data-<prefix>-item-id instead of data-id, for admin tools', async () => {
    const { kit } = await signedIn();
    const card = kit.card({ Id: 'ab12', Type: 'Movie', Name: 'X' }, { nativeItemId: false });
    assert.equal(card.hasAttribute('data-id'), false);
    assert.equal(card.hasAttribute('data-type'), false);
    assert.equal(card.getAttribute('data-jfkt-item-id'), 'ab12');
    assert.equal(card.getAttribute('data-jfkt-item-type'), 'Movie');
    const row = kit.listItem({ Id: 'ab12', Type: 'Movie', Name: 'X' }, { nativeItemId: false });
    assert.equal(row.hasAttribute('data-id'), false);
    assert.equal(row.getAttribute('data-jfkt-item-id'), 'ab12');
});

test('listItem, list and grid use native classes; rows carry data-id and link the title', async () => {
    const { kit } = await signedIn();
    const action = kit.iconButton('delete', 'Delete', () => {});
    const row = kit.listItem({ Id: 'CAFE', Type: 'Episode', Name: 'Pilot', ProductionYear: 2020 }, { actions: [action] });
    assert.equal(row.className, 'listItem listItem-border jfkt-listitem');
    assert.equal(row.getAttribute('data-id'), 'cafe');
    assert.equal(row.getAttribute('data-type'), 'Episode');
    assert.equal(row.querySelector('.listItemBody .listItemBodyText a.jfkt-listitem-link').getAttribute('href'), '#/details?id=cafe&serverId=server-1');
    assert.equal(row.querySelector('.listItemBodyText.secondary').textContent, '2020');
    assert.ok(row.querySelector('.listItemImage.jfkt-listitem-image .cardImageIcon'));
    assert.equal(row.querySelector('.jfkt-listitem-actions').firstChild, action);
    assert.equal(row.getAttribute('role'), 'listitem', 'a row is a list item');
    const list = kit.list([row]);
    assert.equal(list.className, 'paperList jfkt-itemlist');
    assert.equal(list.getAttribute('role'), 'list');
    assert.equal(kit.grid([]).className, 'itemsContainer vertical-wrap jfkt-items');
    assert.equal(kit.listItem({ title: 'No image' }, { image: false }).querySelector('.listItemImage'), null);
    assert.equal(kit.listItem({ title: 'A heading inside another list item' }, { role: false }).hasAttribute('role'), false);
    assert.equal(kit.listItem({ title: 'Another role' }, { role: 'option' }).getAttribute('role'), 'option');
});

test('the helpers never add or remove je-hidden', async () => {
    const { kit } = await signedIn();
    const card = kit.card({ Id: '1', Type: 'Movie', Name: 'x' });
    card.classList.add('je-hidden');
    kit.setHidden(card, false);
    assert.ok(card.classList.contains('je-hidden'));
});

// -------------------------------------------------------------------------------------------------- toasts

test('toast: a polite live region, an action that removes it, and a 4 s default timeout', async () => {
    const { kit, page } = setup();
    let acted = 0;
    kit.toast('Saved.', { action: { label: 'Undo', onClick: () => acted++ } });
    const container = page.document.querySelector('.jfkt-toasts');
    assert.equal(container.getAttribute('role'), 'status');
    assert.equal(container.getAttribute('aria-live'), 'polite');
    assert.equal(container.querySelector('.jfkt-toast').textContent, 'Saved.Undo');
    container.querySelector('.jfkt-toast-action').click();
    assert.equal(acted, 1);
    assert.equal(container.querySelectorAll('.jfkt-toast').length, 0);
    kit.toast('Later');
    await page.advance(3999);
    assert.equal(container.querySelectorAll('.jfkt-toast').length, 1);
    await page.advance(1);
    assert.equal(container.querySelectorAll('.jfkt-toast').length, 0);
    const remove = kit.toast('Manual', { timeout: 60000 });
    remove();
    assert.equal(container.querySelectorAll('.jfkt-toast').length, 0);
});

test('toast: the live region is in the page from kit.start, empty, so a screen reader announces the first toast too', () => {
    // A live region added together with its first message isn't announced by every screen reader.
    const { kit, page } = setup();
    assert.equal(page.document.querySelectorAll('.jfkt-toasts').length, 0, 'nothing before start');
    kit.start();
    const regions = page.document.querySelectorAll('.jfkt-toasts');
    assert.equal(regions.length, 1);
    assert.equal(regions[0].getAttribute('role'), 'status');
    assert.equal(regions[0].getAttribute('aria-live'), 'polite');
    assert.equal(regions[0].children.length, 0);
    kit.toast('First');
    assert.equal(page.document.querySelectorAll('.jfkt-toasts').length, 1, 'the first toast goes into it');
    assert.equal(regions[0].textContent, 'First');

    // Gone (a page that cleared the body): the next toast makes it again.
    regions[0].remove();
    kit.toast('Again');
    assert.equal(page.document.querySelector('.jfkt-toasts').textContent, 'Again');
});

test('toast with several actions: buttons run their action, a link action is an upgraded emby-linkbutton without a handler of ours', async () => {
    const { kit, page } = setup();
    const upgraded = [];
    page.context.CustomElements = { upgradeSubtree: (root) => upgraded.push(root) };
    let downloads = 0;
    kit.toast('Clip ready: a.mkv', {
        timeout: 12000,
        actions: [{ label: 'Download', onClick: () => downloads++ }, { label: 'View in My Clips', href: '#/jfkt-clips?highlight=b1' }, null, { label: '' }]
    });
    const container = page.document.querySelector('.jfkt-toasts');
    const toast = container.querySelector('.jfkt-toast');
    const actions = toast.querySelectorAll('.jfkt-toast-action');
    assert.deepEqual(actions.map((a) => [a.tagName, a.textContent]), [['BUTTON', 'Download'], ['A', 'View in My Clips']], 'missing and unlabelled actions are left out');
    assert.equal(actions[1].getAttribute('is'), 'emby-linkbutton');
    assert.equal(actions[1].getAttribute('href'), '#/jfkt-clips?highlight=b1');
    assert.deepEqual(upgraded, [toast], 'a toast with a link is upgraded, so jellyfin-web\'s router shows the link');
    actions[1].click();
    assert.equal(container.querySelectorAll('.jfkt-toast').length, 1, 'the click is handled first');
    await page.advance(1);
    assert.equal(container.querySelectorAll('.jfkt-toast').length, 0, 'then the toast goes');
    assert.equal(downloads, 0);

    kit.toast('Again', { actions: [{ label: 'Download', onClick: () => downloads++ }] });
    container.querySelector('.jfkt-toast-action').click();
    assert.equal(downloads, 1);
    assert.equal(container.querySelectorAll('.jfkt-toast').length, 0);
    assert.equal(upgraded.length, 1, 'a toast without a link isn\'t upgraded');
});

test('toast is raised above the player controls while the configured predicate says so', () => {
    const { kit, page } = setup();
    let raised = true;
    kit.configure({ toastRaised: () => raised });
    kit.toast('a');
    const container = page.document.querySelector('.jfkt-toasts');
    assert.ok(container.classList.contains('jfkt-toasts-player'));
    raised = false;
    kit.toast('b');
    assert.equal(container.classList.contains('jfkt-toasts-player'), false);
    kit.toast('c', { raised: true });
    assert.ok(container.classList.contains('jfkt-toasts-player'));
});

// -------------------------------------------------------------------------------------------------- dialogs

test('dialog: role, aria-modal, aria-labelledby, the native dialog surface, and focus restored on close', () => {
    const { kit, page } = setup();
    const opener = page.element('button');
    page.document.body.append(opener);
    opener.focus();
    let closed = 0;
    const dialog = kit.dialog({ title: 'Edit <thing>' });
    dialog.onClose(() => closed++);
    const panel = dialog.panel;
    assert.equal(panel.getAttribute('role'), 'dialog');
    assert.equal(panel.getAttribute('aria-modal'), 'true');
    assert.equal(panel.className, 'jfkt-panel dialog');
    const title = page.document.getElementById(panel.getAttribute('aria-labelledby'));
    assert.equal(title.tagName, 'H3');
    assert.equal(title.textContent, 'Edit <thing>');
    assert.ok(kit.dialog.isOpen());
    const input = page.element('input');
    dialog.body.append(input);
    input.focus();
    dialog.close();
    dialog.close();
    assert.equal(closed, 1);
    assert.equal(page.document.querySelector('.jfkt-backdrop'), null);
    assert.equal(page.document.activeElement, opener);
    assert.equal(kit.dialog.isOpen(), false);
});

test('dialog: the focus goes into a dialog that doesn\'t take it itself, on initialFocus when given', async () => {
    const { kit, page } = setup();
    const opener = page.element('button');
    page.document.body.append(opener);
    opener.focus();

    // Nothing focused by the caller: after its code (a microtask), the body's first control.
    const plainDialog = kit.dialog({ title: 'Delete?' });
    const cancel = kit.button('Cancel');
    const ok = kit.button('Delete');
    plainDialog.body.append(page.element('p'), cancel, ok);
    assert.equal(page.document.activeElement, opener, 'not before the caller has filled the body');
    await Promise.resolve();
    assert.equal(page.document.activeElement, cancel);
    plainDialog.close();
    assert.equal(page.document.activeElement, opener);

    // initialFocus, an element or a function; a disabled one, or one outside the panel, is passed over.
    let target = null;
    const chosen = kit.dialog({ title: 'Chosen', initialFocus: () => target });
    const first = kit.button('First');
    target = kit.button('Second');
    chosen.body.append(first, target);
    await Promise.resolve();
    assert.equal(page.document.activeElement, target);
    chosen.close();
    const disabled = kit.button('Disabled');
    disabled.disabled = true;
    const passed = kit.dialog({ title: 'Passed over', initialFocus: disabled });
    const fallback = kit.button('Fallback');
    passed.body.append(disabled, fallback);
    await Promise.resolve();
    assert.equal(page.document.activeElement, fallback);
    passed.close();
    const away = kit.dialog({ title: 'Away', initialFocus: opener });
    const inside = kit.button('Inside');
    away.body.append(inside);
    await Promise.resolve();
    assert.equal(page.document.activeElement, inside, 'an initialFocus outside the panel is ignored');
    away.close();

    // The caller's own focus stays; an empty body leaves the header's Close.
    const own = kit.dialog({ title: 'Own', initialFocus: ok });
    const input = page.element('input');
    own.body.append(input);
    input.focus();
    await Promise.resolve();
    assert.equal(page.document.activeElement, input);
    own.close();
    const empty = kit.dialog({ title: 'Empty' });
    await Promise.resolve();
    assert.equal(page.document.activeElement, empty.panel.querySelector('.jfkt-icon-button'));
    empty.close();

    // Closed before the microtask: nothing is focused for it.
    const quick = kit.dialog({ title: 'Quick' });
    quick.body.append(kit.button('Never'));
    quick.close();
    await Promise.resolve();
    assert.equal(page.document.activeElement, opener);
});

test('dialog: on close the focus goes to returnFocus when what had it before was removed, hidden or disabled', async () => {
    const { kit, page } = setup();
    const list = page.element('div');
    const next = page.element('button');
    page.document.body.append(list, next);
    for (const gone of ['removed', 'hidden', 'disabled']) {
        const opener = page.element('button');
        list.append(opener);
        opener.focus();
        const dialog = kit.dialog({ title: gone, returnFocus: gone === 'removed' ? next : () => next });
        await Promise.resolve();
        if (gone === 'removed') {
            opener.remove();
        } else if (gone === 'hidden') {
            list.classList.add('hide');
        } else {
            opener.disabled = true;
        }

        dialog.close();
        assert.equal(page.document.activeElement, next, gone);
        list.classList.remove('hide');
        next.blur();
    }

    // What had it, still there, takes it back; without returnFocus nothing else is focused.
    const opener = page.element('button');
    list.append(opener);
    opener.focus();
    kit.dialog({ title: 'Back', returnFocus: next }).close();
    assert.equal(page.document.activeElement, opener);
    const dialog = kit.dialog({ title: 'None' });
    opener.remove();
    dialog.close();
    assert.ok(page.document.activeElement !== next);
});

test('two kits of one prefix in a document (ClientScript and AdminScript) never generate the same field or dialog id', () => {
    const page = createPage();
    const { createKit } = loadKit(page);
    const client = createKit(BUILD, CSS);
    const admin = createKit(BUILD, CSS, { styleId: 'jfkt-admin-styles' });
    const other = createKit(Object.assign({}, BUILD, { prefix: 'jfzz' }), CSS);
    const ids = [];
    const dialogs = [];
    for (const kit of [client, admin, client, admin, other]) {
        const input = kit.input({});
        const field = kit.field('Label', input);
        page.document.body.append(field);
        assert.equal(field.querySelector('label').getAttribute('for'), input.id);
        ids.push(input.id);
        const dialog = kit.dialog({ title: 'Dialog of ' + kit.prefix });
        dialogs.push(dialog);
        const titleId = dialog.panel.getAttribute('aria-labelledby');
        ids.push(titleId);
        assert.equal(page.document.getElementById(titleId).textContent, 'Dialog of ' + kit.prefix);
    }

    assert.equal(new Set(ids).size, ids.length, 'distinct ids: ' + ids.join(' '));
    for (const id of ids) {
        assert.equal(page.document.querySelectorAll('#' + id).length, 1, id + ' once in the document');
    }

    assert.ok(ids.filter((id) => id.startsWith('jfkt-')).length === 8 && ids.filter((id) => id.startsWith('jfzz-')).length === 2);
    dialogs.reverse().forEach((dialog) => dialog.close());
});

test('dialog: Escape closes only the topmost dialog and is not seen by the page', () => {
    const { kit, page } = setup();
    let pageSaw = 0;
    page.document.addEventListener('keydown', () => pageSaw++);
    const first = kit.dialog({ title: 'One' });
    const second = kit.dialog({ title: 'Two' });
    const event = page.key(page.document.body, 'Escape');
    assert.equal(event, false, 'default prevented');
    assert.equal(page.document.querySelectorAll('.jfkt-backdrop').length, 1);
    assert.ok(first.panel.isConnected);
    assert.equal(second.panel.isConnected, false);
    page.key(page.document.body, 'Escape');
    assert.equal(first.panel.isConnected, false);
    assert.equal(pageSaw, 0);
    page.key(page.document.body, 'Escape');
    assert.equal(pageSaw, 1, 'no listener left once every dialog is closed');
});

test('dialog: an Escape of an IME composition is the composition\'s, and a TV remote\'s Back key closes like Escape', () => {
    const { kit, page } = setup();
    let pageSaw = 0;
    page.document.addEventListener('keydown', () => pageSaw++);
    const dialog = kit.dialog({ title: 'One' });
    const input = page.element('input');
    dialog.body.append(input);
    assert.equal(page.key(input, 'Escape', { isComposing: true }), true, 'not prevented');
    assert.ok(dialog.panel.isConnected, 'still open');
    assert.equal(page.key(input, 'Escape', { keyCode: 229 }), true);
    assert.ok(dialog.panel.isConnected);
    assert.equal(page.key(page.document.body, 'Unidentified', { keyCode: 10009 }), false, 'Tizen\'s Back');
    assert.equal(dialog.panel.isConnected, false);
    const again = kit.dialog({ title: 'Two' });
    assert.equal(page.key(page.document.body, 'BrowserBack'), false);
    assert.equal(again.panel.isConnected, false);
    assert.equal(pageSaw, 0, 'none of it reached the page');
});

test('dialog: keys typed inside never reach the player, and Tab stays inside', () => {
    const { kit, page } = setup();
    let playerSaw = 0;
    page.document.addEventListener('keydown', () => playerSaw++);
    const dialog = kit.dialog({ title: 'Keys' });
    const a = page.element('input');
    const b = page.element('button');
    dialog.body.append(a, b);
    page.key(a, 'b');
    page.key(a, ' ');
    assert.equal(playerSaw, 0);
    b.focus();
    page.key(b, 'Tab');
    assert.equal(page.document.activeElement.className, 'jfkt-icon-button', 'wraps to the first focusable (the close button)');
    page.key(page.document.activeElement, 'Tab', { shiftKey: true });
    assert.equal(page.document.activeElement, b, 'Shift+Tab from the first wraps to the last');
});

test('dialog: a backdrop click closes it unless modal', () => {
    const { kit, page } = setup();
    const loose = kit.dialog({ title: 'Loose' });
    const backdrop = loose.panel.parentNode;
    page.dispatch(loose.panel, 'mousedown');
    assert.ok(backdrop.isConnected, 'a click inside the panel keeps it');
    page.dispatch(backdrop, 'mousedown');
    assert.equal(backdrop.isConnected, false);
    const modal = kit.dialog({ title: 'Modal', modal: true });
    page.dispatch(modal.panel.parentNode, 'mousedown');
    assert.ok(modal.panel.isConnected);
});

test('confirm resolves true, false, or false when dismissed', async () => {
    const { kit, page } = setup();
    const yes = kit.confirm({ title: 'Delete?', message: 'Sure?', confirmLabel: 'Delete' });
    page.document.querySelector('.jfkt-button-primary').click();
    assert.equal(await yes, true);
    const no = kit.confirm({ title: 'Delete?' });
    assert.equal(page.document.querySelector('.jfkt-panel-body').textContent.includes('null'), false, 'no message, no stray text');
    assert.equal(page.document.querySelector('.jfkt-confirm-note'), null, 'no note unless asked');
    page.document.querySelector('.jfkt-actions .jfkt-button:not(.jfkt-button-primary)').click();
    assert.equal(await no, false);
    const dismissed = kit.confirm({ title: 'Delete?' });
    page.key(page.document.body, 'Escape');
    assert.equal(await dismissed, false);
});

test('confirm shows its note under the message, before the buttons', async () => {
    const { kit, page } = setup();
    const answer = kit.confirm({ title: 'Share?', message: 'Share it?', note: 'Others will see what it names.', confirmLabel: 'Share' });
    const body = page.document.querySelector('.jfkt-panel-body');
    const paragraphs = body.querySelectorAll('p');
    assert.deepEqual(paragraphs.map((p) => p.textContent), ['Share it?', 'Others will see what it names.']);
    assert.equal(paragraphs[1].getAttribute('class'), 'jfkt-confirm-note');
    page.document.querySelector('.jfkt-button-primary').click();
    assert.equal(await answer, true);
});

test('confirm on the TV layout: the D-pad reaches its buttons, the confirm button confirms, Back dismisses', async () => {
    const { kit, page } = setup();
    const seenByPage = [];
    page.document.addEventListener('keydown', (event) => seenByPage.push(event.key));
    page.document.documentElement.classList.add('layout-tv');
    const answer = kit.confirm({ title: 'Import?', message: 'Sure?', confirmLabel: 'Import' });
    await page.flush();
    const cancel = page.document.querySelector('.jfkt-actions .jfkt-button:not(.jfkt-button-primary)');
    const confirm = page.document.querySelector('.jfkt-actions .jfkt-button-primary');
    assert.equal(page.document.activeElement, cancel, 'the focus starts on Cancel');
    assert.equal(page.key(cancel, 'ArrowRight'), false, 'the D-pad is the dialog\'s');
    assert.equal(page.document.activeElement, confirm, 'Right reaches the confirm button');
    page.key(confirm, 'ArrowUp');
    assert.equal(page.document.activeElement, cancel);
    page.key(cancel, 'ArrowDown');
    assert.equal(page.document.activeElement, confirm, 'Down too');
    assert.deepEqual(seenByPage, [], 'no key reached the page behind');
    confirm.click();
    assert.equal(await answer, true);

    const back = kit.confirm({ title: 'Import?' });
    await page.flush();
    page.key(page.document.activeElement, 'Back');
    assert.equal(await back, false, 'Back dismisses it');
    assert.equal(kit.dialog.isOpen(), false);
});

// ------------------------------------------------------------------------------------------------------ API

test('api refuses to send anything before ApiClient and the signed-in user exist', async () => {
    const { kit, page, exports } = setup();
    await assert.rejects(kit.api('GET', 'Fx/Capabilities'), (e) => e instanceof exports.KitApiError && e.status === 0 && e.message === 'You are not signed in.');
    page.installApiClient({ userId: null });
    await assert.rejects(kit.api('GET', 'Fx/Capabilities'), /You are not signed in\./);
    assert.equal(page.fetchLog.length, 0);
    assert.equal(kit.timing.firstApi, null);
});

test('api sends JSON with the MediaBrowser token header and returns the parsed body', async () => {
    const { kit, page } = await signedIn();
    page.respond('POST', '/Fx/Things', { Id: 't1' }, 201);
    const result = await kit.api('POST', 'Fx/Things', { body: { Name: 'a"b' }, query: { dryRun: true, skip: undefined } });
    assert.deepEqual(plain(result), { Id: 't1' });
    const call = page.fetchLog[0];
    assert.equal(call.method, 'POST');
    assert.equal(call.path, '/Fx/Things');
    assert.deepEqual(call.query, { dryRun: 'true' });
    assert.deepEqual(call.body, { Name: 'a"b' });
    assert.equal(call.headers['Content-Type'], 'application/json');
    assert.equal(call.headers.Accept, 'application/json');
    assert.equal(call.headers.Authorization, 'MediaBrowser Client="Jellyfin Web", Device="Chrome", DeviceId="device-1", Version="10.11.5", Token="test-token"');
    assert.equal(call.userId, 'user-1');
    assert.ok(kit.timing.firstApi >= kit.timing.user);
});

test('api sends text as it is, not written again as JSON', async () => {
    const { kit, page } = await signedIn();
    page.respond('POST', '/Fx/Import', { Id: 'j1' }, 202);
    const file = '{ "B": 1,\n  "A": "\\u00e9" }';
    assert.deepEqual(plain(await kit.api('POST', 'Fx/Import', { text: file, body: { ignored: true }, query: { dryRun: true } })), { Id: 'j1' });
    const call = page.fetchLog[0];
    assert.equal(call.rawBody, file);
    assert.equal(call.headers['Content-Type'], 'application/json');
    await kit.api('POST', 'Fx/Import', { text: 'a,b', contentType: 'text/csv' });
    assert.equal(page.fetchLog[1].rawBody, 'a,b');
    assert.equal(page.fetchLog[1].headers['Content-Type'], 'text/csv');
});

test('api refuses a text that isn\'t a string (no "null" sent), and sends a blob as it is', async () => {
    const { kit, page } = await signedIn();
    page.respond('POST', '/Fx/Import', { Id: 'j1' }, 202);
    for (const text of [null, 5, { a: 1 }]) {
        await assert.rejects(kit.api('POST', 'Fx/Import', { text }), (e) => e.name === 'TypeError' && /text must be a string/.test(e.message));
    }

    assert.equal(page.fetchLog.length, 0, 'nothing sent');

    const blob = new Blob(['﻿{ "A": "\\u00e9" }'], { type: 'application/json' });
    assert.deepEqual(plain(await kit.api('POST', 'Fx/Import', { blob, body: { ignored: true }, query: { dryRun: true } })), { Id: 'j1' });
    assert.equal(page.fetchLog[0].rawBody, blob, 'the Blob itself, read by fetch');
    assert.equal(page.fetchLog[0].headers['Content-Type'], 'application/json');
    const bytes = new Uint8Array([0xef, 0xbb, 0xbf, 0x7b, 0x7d]);
    await kit.api('POST', 'Fx/Import', { blob: bytes, contentType: 'application/octet-stream' });
    assert.equal(page.fetchLog[1].rawBody, bytes);
    assert.equal(page.fetchLog[1].headers['Content-Type'], 'application/octet-stream');
    await assert.rejects(kit.api('POST', 'Fx/Import', { blob: 'not bytes' }), (e) => e.name === 'TypeError' && /blob must be/.test(e.message));
    assert.equal(page.fetchLog.length, 2);
});

test('api returns null for 204 and the response itself with raw', async () => {
    const { kit, page } = await signedIn();
    page.respond('DELETE', '/Fx/Things/1', null, 204);
    assert.equal(await kit.api('DELETE', 'Fx/Things/1'), null);
    page.respond('GET', '/Fx/File', 'bytes', 200);
    const response = await kit.api('GET', 'Fx/File', { raw: true });
    assert.equal(await response.text(), 'bytes');
});

test('api turns ProblemDetails into KitApiError with detail as the message', async () => {
    const { kit, page, exports } = await signedIn();
    page.respond('GET', '/Fx/A', { title: 'Forbidden', status: 403, detail: 'Downloading is not allowed for your account', type: 'urn:jellyfin-plugin-kit:feature-disabled' }, 403, { contentType: 'application/problem+json' });
    const error = await kit.api('GET', 'Fx/A').then(() => null, (e) => e);
    assert.ok(error instanceof exports.KitApiError);
    assert.equal(error.status, 403);
    assert.equal(error.title, 'Forbidden');
    assert.equal(error.detail, 'Downloading is not allowed for your account');
    assert.equal(error.message, 'Downloading is not allowed for your account');
    assert.equal(error.isFeatureDisabled, true);
    assert.equal(error.isLocked, false);
    assert.equal(error.code, null, 'no code');
    page.respond('GET', '/Fx/B', { title: 'Conflict', status: 409, errors: { Name: ['taken'] } }, 409);
    const titled = await kit.api('GET', 'Fx/B').then(() => null, (e) => e);
    assert.equal(titled.message, 'Conflict');
    assert.deepEqual(plain(titled.errors), { Name: ['taken'] });
    page.respond('GET', '/Fx/D', { title: 'Conflict', status: 409, detail: 'Saved since.', code: 'FxChanged' }, 409);
    const coded = await kit.api('GET', 'Fx/D').then(() => null, (e) => e);
    assert.equal(coded.code, 'FxChanged');
    assert.equal(coded.message, 'Saved since.');
    page.respond('GET', '/Fx/E', { title: 'Conflict', status: 409, detail: 'Changed meanwhile', Code: 'Changed', Users: [{ UserId: 'u1', Name: 'Ann' }] }, 409, { contentType: 'application/problem+json' });
    const extended = await kit.api('GET', 'Fx/E').then(() => null, (e) => e);
    assert.equal(extended.problem.Code, 'Changed', 'a ProblemDetails extension is kept');
    assert.deepEqual(plain(extended.problem.Users), [{ UserId: 'u1', Name: 'Ann' }]);
    page.respond('GET', '/Fx/C', '<html>oops</html>', 500);
    const generic = await kit.api('GET', 'Fx/C').then(() => null, (e) => e);
    assert.equal(generic.message, 'The server returned an error (500).');
    assert.equal(generic.title, null);
    assert.equal(generic.problem, null, 'no body that is ProblemDetails, no problem');
});

test('api raises <prefix>:session-expired once for 401s', async () => {
    const { kit, page } = await signedIn();
    let raised = 0;
    page.document.addEventListener('jfkt:session-expired', () => raised++);
    page.respond('GET', '/Fx/A', '', 401);
    const first = await kit.api('GET', 'Fx/A').then(() => null, (e) => e);
    await kit.api('GET', 'Fx/A').catch(() => {});
    assert.equal(first.status, 401);
    assert.equal(first.message, 'Your session expired. Sign in again.');
    assert.equal(raised, 1);
});

test('api: network errors, the 30 s timeout and the caller\'s abort', async () => {
    const { kit, page } = await signedIn();
    page.respond('GET', '/Fx/Down', null, 200, { networkError: true });
    await assert.rejects(kit.api('GET', 'Fx/Down'), /The server could not be reached\./);
    page.respond('GET', '/Fx/Slow', null, 200, { hang: true });
    const slow = kit.api('GET', 'Fx/Slow').then(() => null, (e) => e);
    await page.advance(29999);
    await page.advance(1);
    assert.equal((await slow).message, 'The server took too long to answer.');
    const controller = new AbortController();
    const cancelled = kit.api('GET', 'Fx/Slow', { signal: controller.signal }).then(() => null, (e) => e);
    controller.abort();
    const error = await cancelled;
    assert.notEqual(error.name, 'KitApiError', 'a caller\'s abort is passed through, not turned into an API error');
});

// ------------------------------------------------------------------------------------------ start-up

test('start never touches window.ApiClient synchronously', () => {
    const { kit, page } = setup();
    let reads = 0;
    Object.defineProperty(page.context, 'ApiClient', { configurable: true, get: () => { reads++; return undefined; } });
    kit.start();
    assert.equal(reads, 0);
    assert.equal(page.document.querySelectorAll('#jfkt-styles').length, 1);
    kit.start();
    assert.equal(page.document.querySelectorAll('#jfkt-styles').length, 1, 'start is idempotent');
});

test('start polls every 25 ms and sees ApiClient within 25 ms of it appearing', async () => {
    for (const appearAt of [0, 3, 11, 24, 25, 26, 49, 97, 180]) {
        const { kit, page } = setup();
        kit.start();
        await page.advance(appearAt);
        page.installApiClient({ userId: null });
        await page.advance(60);
        assert.ok(kit.timing.client !== null, 'client seen for ' + appearAt);
        const lag = kit.timing.client - page.apiClientAt;
        assert.ok(lag >= 0 && lag <= 25, 'lag ' + lag + ' ms for ApiClient at ' + appearAt + ' ms');
    }
});

test('user work waits for getCurrentUserId(): no scanner and no API call before it', async () => {
    const { kit, page } = setup();
    const scans = [];
    kit.observe(() => {
        scans.push(page.clock.now);
        kit.api('GET', 'Fx/Capabilities').catch(() => {});
    });
    kit.start();
    await page.advance(40);
    const client = page.installApiClient({ userId: null });
    await page.advance(500);
    assert.equal(scans.length, 0);
    assert.equal(page.fetchLog.length, 0);
    assert.equal(kit.timing.user, null);
    page.clientState.userId = 'user-1';
    const userAt = page.clock.now;
    await page.advance(400);
    assert.ok(kit.timing.user - userAt <= 25, 'user seen within one poll');
    assert.ok(scans.length >= 1);
    assert.ok(scans[0] >= kit.timing.user);
    assert.ok(page.fetchLog.length >= 1);
    assert.ok(page.fetchLog.every((f) => f.userId === 'user-1'));
    assert.ok(client);
});

test('sign-in without a reload: the user found on viewshow after the poll gave up', async () => {
    const { kit, page } = setup();
    const changes = [];
    kit.user.onChange((id, previous) => changes.push([id, previous]));
    kit.start();
    page.installApiClient({ userId: null });
    await page.advance(61000);
    page.clientState.userId = 'user-2';
    await page.advance(1000);
    assert.equal(kit.user.id(), null, 'the 25 ms user poll stopped after a minute');
    page.dispatch(page.document.body, 'viewshow');
    assert.equal(kit.user.id(), 'user-2');
    page.clientState.userId = null;
    page.dispatch(page.document.body, 'viewshow');
    page.clientState.userId = 'user-3';
    page.dispatch(page.document.body, 'viewshow');
    assert.deepEqual(plain(changes), [['user-2', null], [null, 'user-2'], ['user-3', null]]);
    assert.equal(await kit.user.ready(), 'user-3');
});

test('user.current and isAdmin are cached per user id', async () => {
    const { kit, page } = await signedIn();
    let calls = 0;
    const original = page.context.ApiClient.getCurrentUser;
    Object.getPrototypeOf(page.context.ApiClient).getCurrentUser = function () {
        calls++;
        return original.call(this);
    };
    assert.equal((await kit.user.current()).Id, 'user-1');
    await kit.user.current();
    assert.equal(await kit.user.isAdmin(), false);
    assert.equal(calls, 1);
    page.clientState.userId = 'user-9';
    page.dispatch(page.document.body, 'viewshow');
    await kit.user.current();
    assert.equal(calls, 2);
});

test('user.current does not cache a failed request: the next call, and isAdmin, ask again', async () => {
    const { kit, page } = await signedIn({ admin: true });
    let calls = 0;
    let failures = 2;
    const original = page.context.ApiClient.getCurrentUser;
    Object.getPrototypeOf(page.context.ApiClient).getCurrentUser = function () {
        calls++;
        if (failures === 2) {
            failures--;
            return Promise.reject(new Error('network blip'));
        }

        if (failures === 1) {
            failures--;
            throw new Error('thrown, not rejected');
        }

        return original.call(this);
    };
    await assert.rejects(kit.user.current(), /network blip/);
    await assert.rejects(kit.user.isAdmin(), /thrown, not rejected/, 'a synchronous throw becomes a rejection');
    assert.equal(await kit.user.isAdmin(), true, 'the user is asked for again after each failure');
    assert.equal((await kit.user.current()).Id, 'user-1');
    assert.equal(calls, 3, 'the successful answer is cached');
});

test('a missing ApiClient is reported once after 30 s and polled every second afterwards', async () => {
    const { kit, page } = setup();
    kit.start();
    await page.advance(31000);
    assert.equal(kit.health.notes.length, 1);
    assert.equal(kit.health.notes[0].kind, 'apiClient');
    assert.equal(page.warnings.length, 1);
    const timersBefore = page.clock.timers.length;
    await page.advance(5000);
    assert.equal(page.warnings.length, 1);
    assert.ok(page.clock.timers.length <= timersBefore + 1);
    page.installApiClient({ userId: 'u' });
    await page.advance(1001);
    assert.ok(kit.timing.client !== null);
});

// ------------------------------------------------------------------------------------------------ observers

test('observe: mutations are batched into one scan per 150 ms, and a failing scanner doesn\'t stop the others', async () => {
    const { kit, page } = await signedIn();
    let good = 0;
    kit.observe(() => {
        throw new Error('boom');
    });
    kit.observe(() => Promise.reject(new Error('async boom')));
    kit.observe(() => good++);
    await page.advance(400);
    const base = good;
    for (let i = 0; i < 20; i++) {
        page.document.body.append(page.element('div'));
        await page.flush();
    }

    await page.advance(149);
    assert.equal(good, base);
    await page.advance(40);
    assert.equal(good, base + 1);
    assert.ok(page.warnings.some((w) => w.includes('boom')));
    assert.ok(page.warnings.some((w) => w.includes('async boom')));
});

test('onView calls back on viewshow (and on the events asked for)', () => {
    const { kit, page } = setup();
    const seen = [];
    kit.onView((view, detail, type) => seen.push(type + ':' + view.id));
    kit.onView((view, detail, type) => seen.push(type), { events: ['pageshow'] });
    const view = page.element('div', { id: 'v1' });
    page.document.body.append(view);
    page.dispatch(view, 'viewshow');
    page.dispatch(view, 'pageshow');
    assert.deepEqual(seen, ['viewshow:v1', 'pageshow']);
});

test('onNavigate wraps pushState and replaceState, always calling through', () => {
    const { kit, page } = setup();
    const calls = [];
    const original = page.context.history.pushState;
    page.context.history.pushState = function (...args) {
        calls.push(args[2]);
        return original.apply(this, args) || 'result';
    };
    const seen = [];
    kit.onNavigate((location, kind) => seen.push(kind + ' ' + location.hash));
    assert.equal(page.context.history.pushState({}, '', '#/one'), 'result');
    page.context.history.replaceState({}, '', '#/two');
    page.context.dispatchEvent(new page.Event('popstate'));
    page.context.dispatchEvent(new page.Event('hashchange'));
    assert.deepEqual(calls, ['#/one']);
    assert.deepEqual(seen, ['pushState #/one', 'replaceState #/two', 'popstate #/two', 'hashchange #/two']);
});

// ------------------------------------------------------------------------------------------------- storage

test('storage prefixes keys, round-trips JSON and survives broken storage', () => {
    const { kit, page } = setup();
    assert.equal(kit.storage.key('view'), 'jfkt:view');
    assert.equal(kit.storage.set('view', { mode: 'grid' }), true);
    assert.equal(page.context.localStorage.getItem('jfkt:view'), '{"mode":"grid"}');
    assert.deepEqual(plain(kit.storage.get('view', null)), { mode: 'grid' });
    assert.equal(kit.storage.get('missing', 'fallback'), 'fallback');
    page.context.localStorage.setItem('jfkt:bad', '{not json');
    assert.equal(kit.storage.get('bad', 7), 7);
    kit.storage.remove('view');
    assert.equal(kit.storage.get('view', null), null);
    Object.defineProperty(page.context, 'localStorage', { configurable: true, get: () => { throw new Error('SecurityError'); } });
    assert.equal(kit.storage.set('x', 1), false);
    assert.equal(kit.storage.get('x', 'safe'), 'safe');
    kit.storage.remove('x');
    assert.deepEqual(plain(kit.storage.names('')), [], 'names() with no storage: none');
    assert.equal(kit.layout.isExperimental(), false);
});

test('storage.names lists this plugin\'s entries that start with a name, without the prefix', () => {
    const { kit, page } = setup();
    kit.storage.set('draft:u1:a', { text: 'x' });
    kit.storage.set('draft:u1:b', { text: 'y' });
    kit.storage.set('draft:u2:a', { text: 'z' });
    kit.storage.set('view', 1);
    page.context.localStorage.setItem('jfbm:draft:u1:c', '{}');
    page.context.localStorage.setItem('layout', 'tv');
    assert.deepEqual(plain(kit.storage.names('draft:u1:').sort()), ['draft:u1:a', 'draft:u1:b']);
    assert.deepEqual(plain(kit.storage.names('draft:').sort()), ['draft:u1:a', 'draft:u1:b', 'draft:u2:a'], 'never another plugin\'s');
    assert.equal(kit.storage.names('').length, 4);
    kit.storage.names('draft:u1:').forEach((n) => kit.storage.remove(n));
    assert.deepEqual(plain(kit.storage.names('draft:')), ['draft:u2:a']);
});

// -------------------------------------------------------------------------------------------------- format

test('format.time and parseTime keep Bookmarks 1.0\'s formats', () => {
    const { kit } = setup();
    const f = kit.format;
    assert.equal(f.time(0), '0:00');
    assert.equal(f.time(150000000), '0:15');
    assert.equal(f.time(37230000000), '1:02:03');
    assert.equal(f.time(37235000000, true), '1:02:03.5');
    assert.equal(f.time(-5), '0:00');
    assert.equal(f.parseTime(''), null);
    assert.equal(f.parseTime('  '), null);
    assert.equal(f.parseTime('15'), 150000000);
    assert.equal(f.parseTime('1:02:03.5'), 37235000000);
    assert.equal(f.parseTime('0:07'), 70000000);
    assert.ok(Number.isNaN(f.parseTime('abc')));
    assert.ok(Number.isNaN(f.parseTime('1:2:3:4')));
    assert.ok(Number.isNaN(f.parseTime('1.5:00')));
});

test('format.size, date and stars', () => {
    const { kit } = setup();
    const f = kit.format;
    assert.equal(f.size(undefined), '');
    assert.equal(f.size(512), '512 B');
    assert.equal(f.size(1536), '1.5 KB');
    assert.equal(f.size(50 * 1024 * 1024), '50 MB');
    assert.equal(f.date('not a date'), '');
    assert.notEqual(f.date('2026-09-29T10:00:00Z'), '');
    assert.notEqual(f.dateTime(new Date(0)), '');
    assert.deepEqual(plain(f.stars(3.74)), { value: 3.5, full: 3, half: 1, empty: 1, label: '3.5 out of 5' });
    assert.deepEqual(plain(f.stars(9)), { value: 5, full: 5, half: 0, empty: 0, label: '5 out of 5' });
    assert.deepEqual(plain(f.stars(null)), { value: 0, full: 0, half: 0, empty: 5, label: '0 out of 5' });
});

test('layout, je, env, t and hashParams', () => {
    const { kit, page } = setup();
    assert.equal(kit.layout.isTv(), false);
    page.document.documentElement.classList.add('layout-tv');
    assert.equal(kit.layout.isTv(), true);
    page.document.documentElement.classList.add('layout-mobile');
    assert.equal(kit.layout.isMobile(), true);
    page.document.documentElement.classList.add('layout-desktop');
    page.context.localStorage.setItem('layout', 'experimental');
    assert.equal(kit.layout.isExperimental(), true, 'the experimental layout keeps html.layout-desktop');
    assert.equal(kit.je.present(), false);
    page.context.JellyfinEnhanced = {};
    assert.equal(kit.je.present(), true);
    assert.equal(kit.env.has('Dashboard.dialogHelper'), false);
    page.context.Dashboard = { dialogHelper: {} };
    assert.equal(kit.env.has('Dashboard.dialogHelper'), true);
    kit.addStrings({ hello: 'Hello {name}, {count} new', plain: 'Plain' });
    assert.equal(kit.t('hello', { name: 'Ann', count: 2 }), 'Hello Ann, 2 new');
    assert.equal(kit.t('hello'), 'Hello {name}, {count} new');
    assert.equal(kit.t('missing.key'), 'missing.key');
    page.context.location.hash = '#/details?id=abc&serverId=s1';
    assert.equal(kit.hashParams().get('id'), 'abc');
    page.context.location.hash = '#/home';
    assert.equal(kit.hashParams().get('id'), null);
});

// ------------------------------------------------------------------------------------------------ playback

test('playback reports reach the listeners and the original methods, hooked once per prefix', async () => {
    const page = createPage();
    const { createKit } = loadKit(page);
    const a = createKit(BUILD, CSS);
    const again = createKit(BUILD, CSS);
    const other = createKit(Object.assign({}, BUILD, { prefix: 'jfzz' }), CSS);
    const seen = [];
    a.playback.onReport((kind, info) => seen.push('a ' + kind + ' ' + info.ItemId));
    again.playback.onReport((kind) => seen.push('again ' + kind));
    other.playback.onReport((kind, info) => {
        seen.push('other ' + kind);
        if (info.ItemId === 'boom') {
            throw new Error('listener failure');
        }
    });
    [a, again, other].forEach((k) => k.start());
    const client = page.installApiClient({ userId: 'u' });
    await page.advance(60);
    await client.reportPlaybackStart({ ItemId: 'i1' });
    await client.reportPlaybackProgress({ ItemId: 'boom' });
    await client.reportPlaybackStopped({ ItemId: 'i1' });
    assert.deepEqual(page.reports.map((r) => r[0] + ' ' + r[1].ItemId), ['start i1', 'progress boom', 'stopped i1']);
    assert.deepEqual(seen.filter((s) => s.startsWith('a ')), ['a start i1', 'a progress boom', 'a stopped i1']);
    assert.deepEqual(seen.filter((s) => s.startsWith('again ')), [], 'a second kit with the same prefix does not wrap again');
    assert.equal(seen.filter((s) => s.startsWith('other ')).length, 3);
});

test('playback.seekTo and playItem use this browser\'s session, which is cached', async () => {
    const { kit, page } = await signedIn();
    kit.playback.onReport(() => {});
    page.respond('GET', '/Sessions', [{ Id: 'other', DeviceId: 'x' }, { Id: 'mine', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/mine/Playing/Seek', null, 204);
    page.respond('POST', '/Sessions/mine/Playing', null, 204);
    await kit.playback.seekTo(150000000.4);
    await kit.playback.seekTo(1);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Sessions').length, 1);
    assert.deepEqual(page.fetchLog.find((f) => f.path === '/Sessions').query, { deviceId: 'device-1' });
    assert.deepEqual(page.fetchLog.find((f) => f.path === '/Sessions/mine/Playing/Seek').query, { SeekPositionTicks: '150000000' });
    const playing = kit.playback.playItem('item-1', { startTicks: 5, mediaSourceId: 'ms', audioStreamIndex: 2 });
    await page.advance(10);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item-1' });
    await playing;
    const play = page.fetchLog.filter((f) => f.path === '/Sessions/mine/Playing');
    assert.equal(play.length, 1);
    assert.deepEqual(play[0].query, { playCommand: 'PlayNow', itemIds: 'item-1', startPositionTicks: '5', mediaSourceId: 'ms', audioStreamIndex: '2' });
});

test('playback.pause and unpause send the Sessions API commands to the session of this browser', async () => {
    const { kit, page } = await signedIn();
    page.respond('GET', '/Sessions', [{ Id: 'mine', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/mine/Playing/Pause', null, 204);
    page.respond('POST', '/Sessions/mine/Playing/Unpause', null, 204);
    await kit.playback.pause();
    await kit.playback.unpause();
    assert.deepEqual(page.fetchLog.filter((f) => f.path.startsWith('/Sessions/mine')).map((f) => f.method + ' ' + f.path), [
        'POST /Sessions/mine/Playing/Pause',
        'POST /Sessions/mine/Playing/Unpause'
    ]);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Sessions').length, 1, 'the session is looked up once');

    page.respond('POST', '/Sessions/mine/Playing/Pause', { title: 'Not Found', status: 404, detail: 'Session not found.' }, 404);
    await assert.rejects(kit.playback.pause(), { message: 'Session not found.' });
});

test('playItem sends the command again when playback does not start, then gives up', async () => {
    const { kit, page } = await signedIn();
    page.respond('GET', '/Sessions', [{ Id: 'mine', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/mine/Playing', null, 204);
    const result = kit.playback.playItem('item-2', {}).then(() => 'started', (e) => e.message);
    await page.advance(8000);
    await page.advance(8000);
    assert.equal(await result, 'Playback did not start. Try pressing play.');
    assert.equal(page.fetchLog.filter((f) => f.path === '/Sessions/mine/Playing').length, 2);
});

// -------------------------------------------------------------------------------------------------- tables

test('the native-class and anchor tables are frozen, and styles go into the configured style element', () => {
    const page = createPage();
    const { createKit, KIT_NATIVE_CLASSES, KIT_ANCHORS } = loadKit(page);
    assert.ok(Object.isFrozen(KIT_NATIVE_CLASSES));
    assert.ok(Object.isFrozen(KIT_NATIVE_CLASSES.cardShape));
    assert.ok(Object.isFrozen(KIT_ANCHORS));
    assert.ok(Object.isFrozen(KIT_ANCHORS.viewShow));
    const admin = createKit(BUILD, '.x{}', { styleId: 'jfkt-admin-styles' });
    admin.start();
    assert.equal(page.document.getElementById('jfkt-admin-styles').textContent, '.x{}');
    assert.equal(page.document.getElementById('jfkt-styles'), null);
});

test('itemHref and imageUrl', async () => {
    const { kit } = await signedIn();
    assert.equal(kit.itemHref('AA-BB'), '#/details?id=aabb&serverId=server-1');
    assert.equal(kit.imageUrl('AA', null), null);
    assert.match(kit.imageUrl('AA', 't', { type: 'Backdrop', maxWidth: 800 }), /Items\/aa\/Images\/Backdrop\?tag=t&maxWidth=800$/);
});

// ------------------------------------------------------------------------------------------------ page parts

test('searchBox: the native input, debounced 300 ms, at once on Enter, only when the trimmed text changed', async () => {
    const { kit, page } = setup();
    const searched = [];
    const input = kit.searchBox({ placeholder: 'Search…', value: ' heat ', maxLength: 100, onSearch: (text) => searched.push(text) });
    assert.equal(input.tagName, 'INPUT');
    assert.equal(input.getAttribute('type'), 'search');
    assert.equal(input.className, 'jfkt-input emby-input jfkt-searchbox');
    assert.equal(input.getAttribute('maxlength'), '100');
    assert.equal(input.getAttribute('aria-label'), 'Search…');
    assert.equal(input.value, ' heat ');
    input.value = 'he';
    page.dispatch(input, 'input');
    await page.advance(200);
    input.value = 'heat ';
    page.dispatch(input, 'input');
    await page.advance(200);
    assert.deepEqual(searched, [], 'nothing before 300 ms of quiet');
    await page.advance(150);
    assert.deepEqual(searched, [], 'unchanged from the initial value, trimmed');
    input.value = 'heat 2';
    page.dispatch(input, 'input');
    await page.advance(310);
    assert.deepEqual(searched, ['heat 2']);
    input.value = 'cold';
    page.dispatch(input, 'input');
    page.key(input, 'Enter');
    assert.deepEqual(searched, ['heat 2', 'cold'], 'Enter fires at once');
    await page.advance(400);
    assert.deepEqual(searched, ['heat 2', 'cold'], 'and the pending timer is dropped');
    input.value = '';
    page.dispatch(input, 'search');
    assert.deepEqual(searched, ['heat 2', 'cold', ''], 'the field cleared');
});

test('searchBox.setSearch: the text set by the caller is remembered, so the same text typed again fires; a pending debounce is dropped', async () => {
    const { kit, page } = setup();
    const searched = [];
    const input = kit.searchBox({ onSearch: (text) => searched.push(text) });
    assert.equal(typeof input.setSearch, 'function');
    input.value = 'zzqx';
    page.dispatch(input, 'input');
    await page.advance(310);
    assert.deepEqual(searched, ['zzqx']);

    // Cleared by the caller (a Clear filters button), not by typing: the memory follows the field.
    input.setSearch('');
    assert.equal(input.value, '');
    input.value = 'zzqx';
    page.dispatch(input, 'input');
    await page.advance(310);
    assert.deepEqual(searched, ['zzqx', 'zzqx'], 'set then the same text fires');
    input.setSearch('');
    input.value = 'zzqx';
    page.key(input, 'Enter');
    assert.deepEqual(searched, ['zzqx', 'zzqx', 'zzqx'], 'and on Enter');

    // A set while a debounce is pending drops it; the text set is what the field now says, and typing it changes nothing.
    input.value = 'cold';
    page.dispatch(input, 'input');
    input.setSearch(' heat ');
    await page.advance(400);
    assert.deepEqual(searched, ['zzqx', 'zzqx', 'zzqx'], 'the pending "cold" never fired');
    assert.equal(input.value, ' heat ');
    page.key(input, 'Enter');
    assert.deepEqual(searched, ['zzqx', 'zzqx', 'zzqx'], 'unchanged from the text set, trimmed');
    input.setSearch(null);
    assert.equal(input.value, '');
});

test('pager: hidden without more, the button while there is more, "Loading…" while busy; the observer presses it off the TV layout', async () => {
    const { kit, page } = setup();
    let loads = 0;
    const pager = kit.pager({ onMore: () => loads++ });
    assert.equal(pager.element.className, 'jfkt-pager hide');
    const button = pager.element.querySelector('button');
    assert.equal(button.textContent, 'Load more');
    pager.update({ hasMore: true });
    assert.equal(pager.element.className, 'jfkt-pager');
    assert.equal(button.className, 'jfkt-button jfkt-pager-more');
    button.click();
    assert.equal(loads, 1);
    pager.update({ busy: true });
    assert.equal(button.disabled, true);
    assert.equal(pager.element.querySelector('[role="status"]').textContent, 'Loading…');
    button.click();
    assert.equal(loads, 1, 'nothing while busy');
    pager.update({ busy: false, hasMore: false });
    assert.equal(pager.element.className, 'jfkt-pager hide');
    assert.equal(button.className, 'jfkt-button jfkt-pager-more hide');
    assert.equal(pager.element.querySelector('[role="status"]').textContent, '');

    // With an IntersectionObserver (the browser has one; the fake page doesn't), scrolling the pager into view loads.
    const observed = [];
    page.context.IntersectionObserver = class {
        constructor(callback) {
            this.callback = callback;
            observed.push(this);
        }

        observe(target) {
            this.target = target;
        }

        disconnect() {
            this.disconnected = true;
        }
    };
    const auto = kit.pager({ onMore: () => loads++ });
    page.document.body.append(auto.element);
    auto.update({ hasMore: true });
    assert.equal(observed.length, 1);
    assert.equal(observed[0].target, auto.element);
    observed[0].callback([{ isIntersecting: false }]);
    assert.equal(loads, 1);
    observed[0].callback([{ isIntersecting: true }]);
    assert.equal(loads, 2);
    auto.element.remove();
    observed[0].callback([{ isIntersecting: true }]);
    assert.equal(loads, 2, 'a removed pager loads nothing and disconnects');
    assert.equal(observed[0].disconnected, true);
    page.document.documentElement.classList.add('layout-tv');
    const tv = kit.pager({ onMore: () => loads++ });
    tv.update({ hasMore: true });
    assert.equal(observed.length, 1, 'no observer on the TV layout: the remote presses the button');
});

test('notice: empty, error (role alert) and info, with an optional action', () => {
    const { kit } = setup();
    let retried = 0;
    const error = kit.notice({ kind: 'error', text: 'Boom', action: { label: 'Retry', onClick: () => retried++ } });
    assert.equal(error.className, 'jfkt-notice jfkt-notice-error');
    assert.equal(error.getAttribute('role'), 'alert');
    assert.equal(error.querySelector('.jfkt-notice-text').textContent, 'Boom');
    error.querySelector('button').click();
    assert.equal(retried, 1);
    const empty = kit.notice({ text: 'Nothing yet.' });
    assert.equal(empty.className, 'jfkt-notice jfkt-notice-empty');
    assert.equal(empty.getAttribute('role'), 'status');
    assert.equal(empty.querySelector('button'), null);
    assert.equal(kit.notice({ kind: 'info', text: 'i' }).className, 'jfkt-notice jfkt-notice-info');
});

test('format.ago: just now, minutes, hours, days, then the date', () => {
    const { kit, page } = setup();
    const now = page.context.Date.now();
    const at = (seconds) => new page.context.Date(now - seconds * 1000).toISOString();
    assert.equal(kit.format.ago(at(5)), 'just now');
    assert.equal(kit.format.ago(at(60)), '1 minute ago');
    assert.equal(kit.format.ago(at(5 * 60)), '5 minutes ago');
    assert.equal(kit.format.ago(at(3 * 3600 + 10)), '3 hours ago');
    assert.equal(kit.format.ago(at(2 * 86400)), '2 days ago');
    assert.equal(kit.format.ago(at(13 * 86400)), '13 days ago');
    assert.equal(kit.format.ago(at(14 * 86400)), new page.context.Date(now - 14 * 86400 * 1000).toLocaleDateString());
    assert.equal(kit.format.ago('nonsense'), '');
});

test('listItem takes a thumb of the plugin\'s own in place of its image box, and lines under the title', async () => {
    const { kit } = await signedIn();
    const thumb = kit.h('div', { class: 'jfkt-clip-thumb' });
    const state = kit.h('div', { class: 'jfkt-clip-state', text: 'Ready' });
    const facts = kit.h('div', { class: 'jfkt-clip-facts', text: '<b>Heat</b> · 0:10' });
    const row = kit.listItem({ id: 'AB', type: 'Movie', title: 'Bank job', subtitle: 'Heat (1995)', href: false }, { thumb, lines: [state, null, facts] });
    assert.equal(row.firstChild, thumb, 'the thumb first, in place of the image box');
    assert.equal(row.querySelector('.listItemImage'), null);
    const body = row.querySelector('.listItemBody');
    assert.deepEqual(body.childNodes.map((n) => n.className), ['listItemBodyText', 'listItemBodyText secondary', 'jfkt-clip-state', 'jfkt-clip-facts'], 'the lines after the title and the subtitle; a missing one left out');
    assert.equal(facts.querySelectorAll('b').length, 0, 'text stays text');
    assert.equal(row.getAttribute('data-id'), 'ab');
    assert.equal(row.querySelectorAll('a').length, 0);
    assert.ok(kit.listItem({ id: 'AB', title: 'x' }, { image: false, thumb: null }).querySelector('.listItemBody'), 'no thumb, no image: the body alone');
});

test('listItem and card with href false link nowhere but keep the item identity', async () => {
    const { kit } = await signedIn();
    const row = kit.listItem({ id: 'AA', type: 'Movie', title: 'Gone', subtitle: 'Unavailable', href: false });
    assert.equal(row.getAttribute('data-id'), 'aa');
    assert.equal(row.querySelectorAll('a').length, 0);
    assert.equal(row.querySelector('.listItemBodyText').textContent, 'Gone');
    const card = kit.card({ id: 'AA', title: 'Gone', href: false });
    assert.equal(card.querySelectorAll('a').length, 0);
    assert.equal(kit.listItem({ id: 'AA', title: 'There' }).querySelector('a').getAttribute('href'), '#/details?id=aa&serverId=server-1');
});

// ------------------------------------------------------------- Jellyfin Enhanced's reviews on the TV layout

// A details page as jellyfin-web 10.11.5 and Jellyfin Enhanced 12.14 draw it: the buttons row (Play, More), the content with
// Jellyfin Enhanced's closed Reviews <details> (its summary, then "Write a Review" outside it) and the plugin's section (a
// hidden link, then See all and a row button).
function tvDetailsPage(page, opts) {
    const o = opts || {};
    const play = page.element('button', { class: 'btnPlay' });
    const more = page.element('button', { class: 'btnMoreCommands' });
    const buttons = page.element('div', { class: 'mainDetailButtons' }, [play, more]);
    const write = page.element('button', { class: 'je-review-btn je-review-write-btn' });
    const own = page.element('button', { class: 'je-review-card', tabindex: '0' });
    const out = page.element('button', { class: 'je-already-out', tabindex: '-1' });
    const summaryLink = page.element('a', { class: 'je-summary-link', href: '#' });
    const summary = page.element('summary', { class: 'sectionTitle' }, [summaryLink]);
    const reviews = page.element('details', { class: 'detailSection tmdb-reviews-section' }, [summary, page.element('div', { class: 'je-review-action-bar' }, [write, own, out])]);
    const hiddenLink = page.element('a', { class: 'jfkt-hidden-link hide', href: '#/a' });
    const seeAll = page.element('a', { class: 'jfkt-see-all', href: '#/b' });
    const rowButton = page.element('button', { class: 'jfkt-row-button' });
    const section = page.element('div', { class: 'jfkt-section' + (o.sectionHidden ? ' hide' : '') }, [hiddenLink, seeAll, rowButton]);
    const content = page.element('div', { class: 'detailSectionContent' }, o.noReviews ? [] : [reviews]);
    const details = page.element('div', { id: 'itemDetailPage' }, [buttons, content, section]);
    page.document.body.append(details);
    return { details, buttons, play, more, reviews, summary, summaryLink, write, own, out, content, section, seeAll, rowButton };
}

function tvJumpOptions(dom, extra) {
    return Object.assign({
        label: 'Things',
        icon: 'list',
        page: () => (dom.details.isConnected ? dom.details : null),
        buttons: (p) => p.querySelector('.mainDetailButtons'),
        before: (row) => row.querySelector('.btnMoreCommands'),
        target: (p) => {
            const s = p.querySelector('.jfkt-section');
            return s && !s.classList.contains('hide') ? s : null;
        }
    }, extra || {});
}

const jumpOf = (dom) => dom.buttons.querySelector('.jfkt-je-jump');

test('je.reviews: Jellyfin Enhanced\'s Reviews section in a scope or the document, null where there is none', async () => {
    const { kit, page } = await signedIn();
    assert.equal(kit.je.reviews(), null, 'no page yet');
    const dom = tvDetailsPage(page);
    assert.equal(kit.je.reviews(), dom.reviews);
    assert.equal(kit.je.reviews(dom.details), dom.reviews);
    assert.equal(kit.je.reviews(dom.section), null, 'a scope that doesn\'t hold it');
    assert.equal(kit.je.reviews('not a node'), dom.reviews, 'anything but an element: the document');
    const plain = page.element('div', { class: 'tmdb-reviews-section' });
    dom.reviews.remove();
    dom.content.append(plain);
    assert.equal(kit.je.reviews(dom.details), null, 'only Jellyfin Enhanced\'s <details>, as the TV jump reads it');
});

test('tvSectionJump: a button before More only on the TV layout, while JE\'s reviews are there and closed and the section is shown', async () => {
    const { kit, page } = await signedIn();
    const dom = tvDetailsPage(page);
    const handle = kit.je.tvSectionJump(tvJumpOptions(dom));
    await page.advance(200);
    assert.equal(jumpOf(dom), null, 'nothing on the desktop layout');
    assert.equal(dom.write.getAttribute('tabindex'), null, 'JE\'s reviews untouched on the desktop layout');

    page.document.documentElement.classList.add('layout-tv');
    kit.rescan();
    await page.advance(200);
    const jump = jumpOf(dom);
    assert.ok(jump, 'the TV layout gets the button');
    assert.equal(handle.button(), jump);
    assert.equal(jump.nextSibling, dom.more, 'right before More');
    assert.equal(jump.className, 'button-flat jfkt-je-jump detailButton emby-button');
    assert.equal(jump.getAttribute('title'), 'Things');
    assert.equal(jump.getAttribute('aria-label'), 'Things');
    assert.equal(jump.querySelector('.detailButton-icon').className, 'material-icons detailButton-icon list');

    dom.section.classList.add('hide');
    kit.rescan();
    await page.advance(200);
    assert.equal(jumpOf(dom), null, 'a hidden section (no bookmarks) has no button');
    dom.section.classList.remove('hide');
    kit.rescan();
    await page.advance(200);
    assert.equal(jumpOf(dom), jump, 'shown again: the same button back, before More');
    assert.equal(jump.nextSibling, dom.more);

    dom.reviews.remove();
    kit.rescan();
    await page.advance(200);
    assert.equal(jumpOf(dom), null, 'no reviews, no button');
    assert.equal(dom.buttons.children.length, 2);
});

test('tvSectionJump: JE drawing its reviews late is seen by the body observer, and the button stays put once in the row', async () => {
    const { kit, page } = await signedIn();
    page.document.documentElement.classList.add('layout-tv');
    const dom = tvDetailsPage(page, { noReviews: true });
    kit.je.tvSectionJump(tvJumpOptions(dom));
    await page.advance(200);
    assert.equal(jumpOf(dom), null);
    dom.content.append(dom.reviews);
    await page.advance(200);
    const jump = jumpOf(dom);
    assert.ok(jump, 'the reviews drawn after the page: the button follows, without a rescan by the plugin');
    assert.equal(dom.write.getAttribute('tabindex'), '-1');

    // Another button inserted before More after ours (Add bookmark): ours isn't moved again (it may have the focus).
    const add = page.element('button', { class: 'btnAdd' });
    dom.buttons.insertBefore(add, dom.more);
    jump.focus();
    await page.advance(200);
    assert.deepEqual(dom.buttons.children.map((c) => (c === jump ? 'jump' : c.className)), ['btnPlay', 'jump', 'btnAdd', 'btnMoreCommands']);
    assert.equal(page.document.activeElement, jump);
});

test('tvSectionJump: the reviews\' controls outside the summary leave the focus order while closed, and get their own tabindex back on toggle', async () => {
    const { kit, page } = await signedIn();
    page.document.documentElement.classList.add('layout-tv');
    const dom = tvDetailsPage(page);
    kit.je.tvSectionJump(tvJumpOptions(dom));
    await page.advance(200);
    assert.equal(dom.write.getAttribute('tabindex'), '-1');
    assert.equal(dom.write.getAttribute('data-jfkit-je-tabindex'), '', 'no tabindex of its own: kept as empty');
    assert.equal(dom.own.getAttribute('tabindex'), '-1');
    assert.equal(dom.own.getAttribute('data-jfkit-je-tabindex'), '0', 'its own tabindex kept');
    assert.equal(dom.out.hasAttribute('data-jfkit-je-tabindex'), false, 'one out of the order already is left alone');
    assert.equal(dom.summaryLink.getAttribute('tabindex'), null, 'the summary is never touched');
    assert.equal(dom.summary.getAttribute('tabindex'), null);

    // Opened (a mouse, Expand reviews by default): put back at once, on the toggle event, without waiting for a scan.
    dom.reviews.setAttribute('open', '');
    page.dispatch(dom.reviews, 'toggle', { bubbles: false });
    assert.equal(dom.write.getAttribute('tabindex'), null);
    assert.equal(dom.write.hasAttribute('data-jfkit-je-tabindex'), false);
    assert.equal(dom.own.getAttribute('tabindex'), '0');
    assert.equal(dom.own.hasAttribute('data-jfkit-je-tabindex'), false);
    assert.equal(dom.out.getAttribute('tabindex'), '-1');
    assert.equal(jumpOf(dom), null, 'open reviews don\'t trap the D-pad: no button');

    dom.reviews.removeAttribute('open');
    page.dispatch(dom.reviews, 'toggle', { bubbles: false });
    assert.equal(dom.write.getAttribute('tabindex'), '-1');
    assert.equal(dom.own.getAttribute('data-jfkit-je-tabindex'), '0');
    assert.ok(jumpOf(dom), 'closed again: the button back');

    // Leaving the TV layout puts everything back.
    page.document.documentElement.classList.remove('layout-tv');
    kit.rescan();
    await page.advance(200);
    assert.equal(dom.write.getAttribute('tabindex'), null);
    assert.equal(dom.own.getAttribute('tabindex'), '0');
    assert.equal(page.document.querySelectorAll('[data-jfkit-je-tabindex]').length, 0);
    assert.equal(jumpOf(dom), null);
});

test('tvSectionJump: pressing the button focuses the section\'s first shown control and scrolls it into view; a plugin may choose it', async () => {
    const { kit, page } = await signedIn();
    page.document.documentElement.classList.add('layout-tv');
    const dom = tvDetailsPage(page);
    kit.je.tvSectionJump(tvJumpOptions(dom));
    await page.advance(200);
    dom.play.focus();
    jumpOf(dom).click();
    assert.equal(page.document.activeElement, dom.seeAll, 'See all, not the hidden link before it');
    const scroll = page.scrolled[page.scrolled.length - 1];
    assert.equal(scroll.element, dom.seeAll);
    assert.deepEqual(plain(scroll.options), { block: 'center' });

    // A section without a control: focusable by script only.
    dom.seeAll.remove();
    dom.rowButton.remove();
    jumpOf(dom).click();
    assert.equal(page.document.activeElement, dom.section);
    assert.equal(dom.section.getAttribute('tabindex'), '-1');

    // The plugin's own choice, and a page whose section went meanwhile (the button goes, the focus stays).
    dom.details.remove();
    const other = tvDetailsPage(page);
    const chosen = kit.je.tvSectionJump(tvJumpOptions(other, { label: 'Other', first: (s) => s.querySelector('.jfkt-row-button') }));
    await page.advance(200);
    chosen.button().click();
    assert.equal(page.document.activeElement, other.rowButton);
    other.section.classList.add('hide');
    chosen.button().click();
    assert.equal(page.document.activeElement, other.rowButton);
    assert.equal(chosen.button(), null);
});

test('tvSectionJump: two kits share the kept tabindex, so one saves it and one puts it back', async () => {
    const first = await signedIn();
    const page = first.page;
    page.document.documentElement.classList.add('layout-tv');
    const dom = tvDetailsPage(page);
    first.kit.je.tvSectionJump(tvJumpOptions(dom));
    const second = first.exports.createKit(Object.assign({}, BUILD, { plugin: 'Other', prefix: 'jfot' }), CSS);
    second.start();
    await page.advance(200);
    second.je.tvSectionJump(tvJumpOptions(dom, { label: 'Other' }));
    await page.advance(200);
    assert.equal(dom.own.getAttribute('data-jfkit-je-tabindex'), '0', 'the second kit didn\'t save the first one\'s -1');
    assert.equal(dom.buttons.querySelectorAll('.jfot-je-jump').length, 1);
    page.document.documentElement.classList.remove('layout-tv');
    first.kit.rescan();
    second.rescan();
    await page.advance(200);
    assert.equal(dom.own.getAttribute('tabindex'), '0');
    assert.equal(dom.write.getAttribute('tabindex'), null);
});

test('tvSectionJump refuses options without a label or a finder', async () => {
    const { kit } = await signedIn();
    assert.throws(() => kit.je.tvSectionJump({ label: 'x', page: () => null, buttons: () => null }), /needs label, page, buttons and target/);
    assert.throws(() => kit.je.tvSectionJump({ page: () => null, buttons: () => null, target: () => null }), /needs label/);
    assert.throws(() => kit.je.tvSectionJump(null), /needs label/);
});

test('tvSectionJump: a section that is there but not shown (hidden by its plugin or a parent) gets no button', async () => {
    const { kit, page } = await signedIn();
    page.document.documentElement.classList.add('layout-tv');
    const dom = tvDetailsPage(page, { sectionHidden: true });
    kit.je.tvSectionJump(tvJumpOptions(dom, { target: (p) => p.querySelector('.jfkt-section') }));
    await page.advance(200);
    assert.equal(jumpOf(dom), null);
    assert.equal(dom.write.getAttribute('tabindex'), '-1', 'the reviews\' trap is still taken away for whatever lies below');
    dom.section.classList.remove('hide');
    kit.rescan();
    await page.advance(200);
    assert.ok(jumpOf(dom));
});
