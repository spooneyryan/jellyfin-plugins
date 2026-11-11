// Bookmarks' web UI on the kit, in the kit's fake page. ClientScriptTests runs it with the built ClientScript:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/bookmarks.test.mjs
// The markup expectations began as Bookmarks 1.0's (bm:Web/bookmarks.js, the UI unchanged on the kit), apart from the
// kit's theming classes (the native dialog, emby-input and button-submit classes) and the delete confirmation's stray
// "null" text, which 1.0 showed. Since 1.1.0 the editor has a Name field first and a Note textarea, rows show the
// display title with "Name it" and their badges, the section is "My bookmarks (n)" with the page links the server
// offers, the delete confirmation's "Also delete its clip" follows Capabilities.Pages.Clips, and the player's quick
// bookmark ends in the name dialog (at once with AskForName, else through the toast's "Name it"), with the chapter's
// suggestion, "Always ask for a name", the optional pause and the kit's scopedKeys on every Bookmarks dialog, and the
// OSD buttons follow the server's ShowPlayerButtons.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage, plain } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const MOVIE = { Id: 'item1', Name: 'Long Test Movie', Type: 'Movie', MediaType: 'Video', IsFolder: false, LocationType: 'FileSystem' };
const CAPABILITIES = {
    ClipsEnabled: true,
    CanCreateClips: true,
    MaxClipDurationSeconds: 600,
    DefaultClipMode: 'Accurate',
    IsAdmin: false,
    StoreReadOnly: false,
    MaxNameLength: 100,
    MaxNoteLength: 500,
    Pages: { Bookmarks: false, Clips: false }
};
const CLIP_KEPT_UNTIL_CLIPS_PAGE = 'The clip file stays on the server. You\'ll be able to see, download and delete it on the My Clips page, which arrives in the next update.';

function bookmarks() {
    return [
        { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Note: '<b>webcheck</b>', Clip: null },
        { Id: 'b2', ItemId: 'item1', StartTicks: 300000000, EndTicks: 400000000, Note: null, Clip: { Status: 'Completed', FileName: 'clip.mp4', FileSize: 2048, IsOutdated: false } }
    ];
}

async function start(options) {
    options = options || {};
    const page = createPage();
    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, options.capabilities || {}));
    page.respond('GET', '/Bookmarks', options.list || bookmarks());
    page.respond('GET', '/Sessions', [{ Id: 's1', DeviceId: 'device-1' }]);
    page.respond('POST', '/Sessions/s1/Playing/Seek', null, 204);
    page.respond('POST', '/Sessions/s1/Playing', null, 204);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: { item1: MOVIE } });
    await page.advance(50);
    return page;
}

function detailsPage(page) {
    const buttons = page.element('div', { class: 'mainDetailButtons' }, [page.element('button', { class: 'btnPlay' }), page.element('button', { class: 'btnMoreCommands' })]);
    const content = page.element('div', { class: 'detailPageContent' }, [page.element('div', { class: 'nativeSection' })]);
    const view = page.element('div', { id: 'itemDetailPage', class: 'page' }, [buttons, content]);
    page.document.body.append(view);
    page.context.location.hash = '#/details?id=item1&serverId=server-1';
    page.dispatch(view, 'viewshow');
    return { view, buttons, content };
}

// The player view; with options.slider, its position slider in emby-slider's container, beside the chapter marks' own.
function playerPage(page, options) {
    const buttons = page.element('div', { class: 'buttons' }, [page.element('button', { class: 'btnSubtitles' }), page.element('button', { class: 'btnVideoOsdSettings' })]);
    const slider = options && options.slider ? page.element('input', { type: 'range', class: 'osdPositionSlider mdl-slider' }) : null;
    const bottom = slider
        ? [page.element('div', { class: 'sliderContainer' }, [page.element('div', { class: 'sliderMarkerContainer' }), slider]), buttons]
        : [buttons];
    const osd = page.element('div', { id: 'videoOsdPage', class: 'page' }, [page.element('div', { class: 'videoOsdBottom' }, bottom)]);
    const video = page.element('video');
    video.currentTime = 42;
    page.document.body.append(page.element('div', { class: 'videoPlayerContainer' }, [video]), osd);
    return { osd, buttons, video, slider };
}

function panel(page, title) {
    return page.document.querySelectorAll('.jfbm-backdrop .jfbm-panel').find((p) => p.querySelector('h3').textContent === title) || null;
}

function toasts(page) {
    return page.document.querySelectorAll('.jfbm-toast').map((t) => t.textContent);
}

test('the details page gets the Add bookmark button before the more button and the My bookmarks section first', { skip }, async () => {
    const page = await start();
    const { buttons, content } = detailsPage(page);
    await page.advance(400);

    const add = buttons.querySelector('.btnBookmarkItem');
    assert.ok(add, 'Add bookmark button');
    assert.equal(add.nextElementSibling.className, 'btnMoreCommands');
    assert.equal(add.getAttribute('is'), 'emby-button');
    assert.equal(add.className, 'button-flat btnBookmarkItem detailButton emby-button');
    assert.equal(add.getAttribute('title'), 'Add bookmark');
    assert.equal(add.firstChild.firstChild.className, 'material-icons detailButton-icon bookmark_add');

    const section = content.firstElementChild;
    assert.equal(section.className, 'verticalSection detailVerticalSection jfbm-section');
    assert.equal(section.getAttribute('data-item-id'), 'item1');
    assert.equal(section.querySelector('h2').textContent, 'My bookmarks (2)');
    assert.equal(section.querySelector('h2').className, 'sectionTitle sectionTitle-cards padded-right');
    const links = section.querySelector('.jfbm-section-links');
    assert.equal(links.className, 'jfbm-section-links hide', 'no page links: the server offers neither page');
    assert.equal(links.children.length, 0);
    const rows = section.querySelectorAll('.jfbm-list.padded-right > .jfbm-row');
    assert.deepEqual(rows.map((r) => r.getAttribute('data-id')), ['b1', 'b2']);
    assert.equal(section.querySelector('.jfbm-list').getAttribute('role'), 'list', 'a list of list items');
    assert.deepEqual(rows.map((r) => r.getAttribute('role')), ['listitem', 'listitem']);
    assert.equal(rows[0].querySelector('.jfbm-time-button').textContent, '0:15');
    assert.equal(rows[0].querySelector('.jfbm-row-title').textContent, '<b>webcheck</b>');
    assert.equal(rows[0].querySelectorAll('b').length, 0, 'notes are text, never HTML');
    assert.equal(rows[0].querySelector('.jfbm-row-subtitle'), null, 'the note is the title, so no second line');
    assert.equal(rows[0].querySelector('.jfbm-name-it').textContent, 'Name it');
    assert.equal(rows[1].querySelector('.jfbm-time-button').textContent, '0:30 – 0:40');
    assert.equal(rows[1].querySelector('.jfbm-row-title').textContent, 'Range 0:30–0:40');
    assert.equal(rows[1].querySelector('.jfbm-name-it').textContent, 'Name it');
    assert.equal(section.querySelectorAll('.jfbm-badge').length, 0);
    assert.deepEqual(rows[1].querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Make the clip again', 'Edit', 'Delete']);
    assert.deepEqual(rows[0].querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Edit', 'Delete']);
    const ready = rows[1].querySelector('.jfbm-clip.jfbm-clip-ready');
    assert.equal(ready.querySelector('span:not(.material-icons)').textContent, 'Clip ready, 2.0 KB');
    assert.match(ready.querySelector('a.jfbm-link').getAttribute('href'), /\/Bookmarks\/b2\/Clip\/File\?ApiKey=test-token&download=true$/);
    assert.deepEqual(ready.querySelectorAll('button.jfbm-link').map((b) => b.textContent), ['Preview', 'Delete clip']);
    assert.ok(page.fetchLog.every((f) => f.userId === 'user-1'));
    assert.deepEqual(page.fetchLog.map((f) => f.method + ' ' + f.path).sort(), ['GET /Bookmarks', 'GET /Bookmarks/Capabilities']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('an item without bookmarks keeps the section hidden; a folder gets no UI', { skip }, async () => {
    const page = await start({ list: [] });
    const { content, buttons } = detailsPage(page);
    await page.advance(400);
    assert.equal(content.firstElementChild.className, 'verticalSection detailVerticalSection jfbm-section hide');
    assert.ok(buttons.querySelector('.btnBookmarkItem'));

    const folderPage = await start();
    folderPage.context.ApiClient.getItem = () => Promise.resolve({ Id: 'item1', Type: 'Series', MediaType: 'Video', IsFolder: true });
    const folder = detailsPage(folderPage);
    await folderPage.advance(400);
    assert.equal(folder.content.querySelector('.jfbm-section'), null);
    assert.equal(folder.buttons.querySelector('.btnBookmarkItem'), null);
});

test('the editor: New bookmark, Name first with the focus, the Note a textarea, the time format check, then POST and a toast', { skip }, async () => {
    const page = await start();
    const { buttons } = detailsPage(page);
    await page.advance(400);
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b3', Clip: null }, entry.body), 201);

    buttons.querySelector('.btnBookmarkItem').click();
    await page.advance(10);
    const editor = panel(page, 'New bookmark');
    assert.ok(editor);
    assert.equal(editor.className, 'jfbm-panel dialog');
    assert.equal(editor.getAttribute('aria-labelledby'), editor.querySelector('h3').id);
    assert.deepEqual(editor.querySelectorAll('.jfbm-field > label').map((l) => l.textContent), ['Name', 'Start', 'End', 'Note'], 'the fields\' order');
    const [nameInput, startInput, endInput] = editor.querySelectorAll('input[type="text"]');
    const noteInput = editor.querySelector('textarea');
    assert.equal(page.document.activeElement, nameInput, 'Name has the focus');
    assert.equal(nameInput.className, 'jfbm-input emby-input');
    assert.equal(nameInput.getAttribute('maxlength'), '100');
    assert.equal(nameInput.value, '');
    assert.equal(nameInput.getAttribute('placeholder'), 'Optional');
    assert.equal(startInput.className, 'jfbm-input emby-input');
    assert.equal(startInput.value, '0:00.0');
    assert.equal(endInput.getAttribute('placeholder'), 'Optional');
    assert.equal(noteInput.className, 'jfbm-textarea emby-textarea');
    assert.equal(noteInput.getAttribute('maxlength'), '500');
    assert.equal(noteInput.getAttribute('rows'), '3');
    assert.equal(editor.querySelectorAll('label').find((l) => l.textContent === 'Note').getAttribute('for'), noteInput.id);
    assert.deepEqual(editor.querySelectorAll('.jfbm-actions button').map((b) => b.textContent + '|' + b.className), [
        'Cancel|jfbm-button',
        'Save|jfbm-button',
        'Save & make clip|jfbm-button jfbm-button-primary button-submit'
    ]);
    assert.equal(editor.querySelector('.jfbm-actions select').className, 'jfbm-select emby-select-withcolor');

    const form = editor.querySelector('form');
    startInput.value = 'abc';
    page.dispatch(form, 'submit');
    await page.advance(10);
    assert.equal(editor.querySelector('.jfbm-error').textContent, 'Enter the start as h:mm:ss, for example 1:02:03.5.');

    startInput.value = '0:07';
    endInput.value = '0:05';
    page.dispatch(form, 'submit');
    await page.advance(10);
    assert.equal(editor.querySelector('.jfbm-error').textContent, 'The end must be after the start.');

    endInput.value = '';
    nameInput.value = '  Bank job ';
    noteInput.value = 'created\nsecond line';
    page.dispatch(form, 'submit');
    await page.advance(50);
    const post = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks');
    assert.deepEqual(post.body, { ItemId: 'item1', StartTicks: 70000000, EndTicks: null, Name: 'Bank job', Note: 'created\nsecond line', MediaSourceId: null, AudioStreamIndex: null });
    assert.equal(panel(page, 'New bookmark'), null, 'the editor closed');
    assert.deepEqual(toasts(page), ['Bookmark added at 0:07.']);
    assert.equal(page.document.querySelector('.jfbm-toasts').classList.contains('jfbm-toasts-player'), false);
});

test('the editor says under Note what a clip file\'s name is made of (never the note, since 1.2.0), while clips can be made or the bookmark has one', { skip }, async () => {
    const hint = 'A clip\'s file name is made from the video\'s name, this bookmark\'s name and its range; the note isn\'t part of it.';
    const page = await start();
    const { buttons } = detailsPage(page);
    await page.advance(400);
    buttons.querySelector('.btnBookmarkItem').click();
    await page.advance(10);
    const editor = panel(page, 'New bookmark');
    const line = editor.querySelector('.jfbm-editor-clip-name-hint');
    assert.ok(line, 'the hint');
    assert.equal(line.textContent, hint);
    assert.ok(line.classList.contains('jfbm-hint'));
    const note = editor.querySelectorAll('.jfbm-field').find((f) => f.querySelector('label').textContent === 'Note');
    assert.ok(note.nextSibling === line, 'right under Note');

    // No clips for this user and none on the bookmark: no hint.
    const noClips = await start({ capabilities: { CanCreateClips: false } });
    const other = detailsPage(noClips);
    await noClips.advance(400);
    other.buttons.querySelector('.btnBookmarkItem').click();
    await noClips.advance(10);
    assert.equal(panel(noClips, 'New bookmark').querySelector('.jfbm-editor-clip-name-hint'), null);
    assert.equal(page.errors.length + noClips.errors.length, 0, page.errors.concat(noClips.errors).join('\n'));
});

test('a server error shows its ProblemDetails detail in the editor, without Reload', { skip }, async () => {
    const page = await start();
    const { buttons } = detailsPage(page);
    await page.advance(400);
    page.respond('POST', '/Bookmarks', { title: 'Bad Request', status: 400, detail: 'The start is past the end of the video.' }, 400);
    buttons.querySelector('.btnBookmarkItem').click();
    await page.advance(10);
    const editor = panel(page, 'New bookmark');
    editor.querySelectorAll('input[type="text"]')[1].value = '9:59:00';
    page.dispatch(editor.querySelector('form'), 'submit');
    await page.advance(50);
    assert.equal(editor.querySelector('.jfbm-error').textContent, 'The start is past the end of the video.');
    assert.ok(editor.querySelector('.jfbm-reload').classList.contains('hide'), 'Reload only for a 409');
    assert.ok(editor.isConnected);
});

function deleteButton(confirm) {
    return confirm.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Delete');
}

function untick(page, box, checked) {
    box.checked = checked;
    page.dispatch(box, 'change');
}

test('the delete confirmation (1.1.0): no stray "null" text without a clip; "Also delete its clip" ticked, the next-update hint when unticked', { skip }, async () => {
    const page = await start();
    const { content } = detailsPage(page);
    await page.advance(400);
    page.respond('DELETE', /^\/Bookmarks\/b\d$/, null, 204);

    const [first, second] = content.querySelectorAll('.jfbm-row');
    first.querySelector('button[title="Delete"]').click();
    await page.advance(10);
    let confirm = panel(page, 'Delete bookmark?');
    const body = confirm.querySelector('.jfbm-panel-body');
    assert.equal(body.textContent.includes('null'), false);
    assert.equal(body.querySelector('p').textContent, '"<b>webcheck</b>" at 0:15 will be deleted.');
    assert.equal(body.querySelector('.jfbm-checkbox'), null);
    assert.equal(body.querySelector('.jfbm-delete-hint'), null);
    page.key(page.document.body, 'Escape');
    assert.equal(panel(page, 'Delete bookmark?'), null);

    // In 1.1.0 (Capabilities.Pages.Clips false) the box is ticked, as in 1.0; unticked, the dialog says the
    // clip stays and where it will be listed.
    second.querySelector('button[title="Delete"]').click();
    await page.advance(10);
    confirm = panel(page, 'Delete bookmark?');
    assert.equal(confirm.querySelector('p').textContent, 'Range 0:30–0:40 will be deleted.', 'an untitled bookmark by its display title');
    assert.equal(confirm.querySelector('.jfbm-checkbox span').textContent, 'Also delete its clip (clip.mp4)');
    const box = confirm.querySelector('.jfbm-checkbox input[type="checkbox"]');
    const hint = confirm.querySelector('.jfbm-delete-hint');
    assert.equal(box.checked, true, 'ticked in 1.1.0');
    assert.equal(hint.textContent, CLIP_KEPT_UNTIL_CLIPS_PAGE);
    assert.equal(hint.getAttribute('role'), 'status');
    assert.ok(hint.classList.contains('hide'), 'no hint while ticked');
    untick(page, box, false);
    assert.equal(hint.classList.contains('hide'), false, 'unticked: the next-update hint');
    untick(page, box, true);
    assert.ok(hint.classList.contains('hide'));
    deleteButton(confirm).click();
    await page.advance(50);
    let deleted = page.fetchLog.filter((f) => f.method === 'DELETE');
    assert.equal(deleted[0].path, '/Bookmarks/b2');
    assert.deepEqual(deleted[0].query, { deleteClip: 'true' });
    assert.deepEqual(toasts(page), ['Bookmark deleted.']);

    // Unticked, the clip is kept (a tombstone).
    content.querySelectorAll('.jfbm-row')[1].querySelector('button[title="Delete"]').click();
    await page.advance(10);
    confirm = panel(page, 'Delete bookmark?');
    untick(page, confirm.querySelector('.jfbm-checkbox input'), false);
    deleteButton(confirm).click();
    await page.advance(50);
    deleted = page.fetchLog.filter((f) => f.method === 'DELETE');
    assert.deepEqual(deleted[1].query, { deleteClip: 'false' });
    assert.equal(page.document.body.textContent.includes('My Clips'), false, 'no My Clips link or copy anywhere once the dialog closed');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the delete confirmation once My Clips is there (Pages.Clips): "Also delete its clip" unticked, "The clip stays in My Clips."', { skip }, async () => {
    const page = await start({ capabilities: { Pages: { Bookmarks: true, Clips: true } } });
    const { content } = detailsPage(page);
    await page.advance(400);
    page.respond('DELETE', /^\/Bookmarks\/b\d$/, null, 204);

    content.querySelectorAll('.jfbm-row')[1].querySelector('button[title="Delete"]').click();
    await page.advance(10);
    const confirm = panel(page, 'Delete bookmark?');
    const box = confirm.querySelector('.jfbm-checkbox input');
    const hint = confirm.querySelector('.jfbm-delete-hint');
    assert.equal(box.checked, false, 'unticked from 1.2.0 on');
    assert.equal(hint.textContent, 'The clip stays in My Clips.');
    assert.equal(hint.classList.contains('hide'), false);
    untick(page, box, true);
    assert.ok(hint.classList.contains('hide'));
    untick(page, box, false);
    deleteButton(confirm).click();
    await page.advance(50);
    assert.deepEqual(page.fetchLog.find((f) => f.method === 'DELETE').query, { deleteClip: 'false' });
});

test('the delete confirmation without a clip file sends the box\'s default: a clip that finishes while it is open goes too in 1.1.0, stays once My Clips is there', { skip }, async () => {
    for (const clipsPage of [false, true]) {
        const making = { Id: 'b1', ItemId: 'item1', StartTicks: 10000000, EndTicks: 1780000000, Name: 'race', Note: null, Clip: { Status: 'Running', Progress: 40 } };
        const noClip = { Id: 'b2', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: null, Note: null, Clip: null };
        const page = await start({ list: [making, noClip], capabilities: { Pages: { Bookmarks: false, Clips: clipsPage } } });
        page.respond('GET', '/Bookmarks/b1', making);
        const { content } = detailsPage(page);
        await page.advance(400);
        page.respond('DELETE', /^\/Bookmarks\/b\d$/, null, 204);

        content.querySelectorAll('.jfbm-row')[0].querySelector('button[title="Delete"]').click();
        await page.advance(10);
        const confirm = panel(page, 'Delete bookmark?');
        assert.equal(confirm.querySelector('p').textContent, '"race" at 0:01 will be deleted.');
        assert.equal(confirm.querySelector('.jfbm-checkbox'), null, 'no clip file yet: no box');

        // The clip finishes while the dialog is open (the row's watcher reads it); the dialog still shows no box.
        const done = Object.assign({}, making, { Clip: { Status: 'Completed', FileName: 'race.mp4', FileSize: 4096, IsOutdated: false } });
        page.respond('GET', '/Bookmarks/b1', done);
        page.respond('GET', '/Bookmarks', [done, noClip]);
        await page.advance(2100);
        assert.equal(panel(page, 'Delete bookmark?'), confirm, 'still open');
        assert.equal(confirm.querySelector('.jfbm-checkbox'), null);
        deleteButton(confirm).click();
        await page.advance(50);
        const expected = { deleteClip: clipsPage ? 'false' : 'true' };
        let deleted = page.fetchLog.filter((f) => f.method === 'DELETE');
        assert.equal(deleted[0].path, '/Bookmarks/b1');
        assert.deepEqual(deleted[0].query, expected, clipsPage ? 'kept, and listed in My Clips' : 'deleted with the bookmark, as the box would have been ticked');

        // A bookmark that never had a clip sends the same default.
        content.querySelectorAll('.jfbm-row')[1].querySelector('button[title="Delete"]').click();
        await page.advance(10);
        deleteButton(panel(page, 'Delete bookmark?')).click();
        await page.advance(50);
        deleted = page.fetchLog.filter((f) => f.method === 'DELETE');
        assert.equal(deleted[1].path, '/Bookmarks/b2');
        assert.deepEqual(deleted[1].query, expected);
        assert.equal(page.errors.length, 0, page.errors.join('\n'));
    }
});

test('the display title: the name, else the note\'s first line cut to 80 characters, else the time; a name\'s note as a second line', { skip }, async () => {
    const longLine = 'A'.repeat(78) + '😀 tail beyond eighty';
    const page = await start({
        list: [
            { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: '<i>Bank job</i>', Note: '\n  Watch the reflection  \nsecond line', Clip: null },
            { Id: 'b2', ItemId: 'item1', StartTicks: 160000000, EndTicks: null, Name: null, Note: '\r\n' + longLine + '\r\nmore', Clip: null },
            { Id: 'b3', ItemId: 'item1', StartTicks: 170000000, EndTicks: null, Name: null, Note: ' \n\t\n ', Clip: null },
            { Id: 'b4', ItemId: 'item1', StartTicks: 36000000000, EndTicks: 37230000000, Name: null, Note: null, Clip: null },
            { Id: 'b5', ItemId: 'item1', StartTicks: 180000000, EndTicks: null, Name: 'Short', Note: 'x'.repeat(120), Clip: null }
        ]
    });
    const { content } = detailsPage(page);
    await page.advance(400);

    const rows = content.querySelectorAll('.jfbm-row');
    const titles = rows.map((r) => r.querySelector('.jfbm-row-title').textContent);
    assert.deepEqual(titles, [
        '<i>Bank job</i>',
        'A'.repeat(78) + '😀…',
        'Bookmark at 0:17',
        'Range 1:00:00–1:02:03',
        'Short'
    ]);
    assert.equal(rows[0].querySelectorAll('i').length, 0, 'names are text, never HTML');
    assert.equal(Array.from(titles[1]).length, 80, 'cut by code points: the emoji at the cut stays whole (it would be half a surrogate pair by UTF-16 units)');
    assert.equal(rows[0].querySelector('.jfbm-row-subtitle').textContent, 'Watch the reflection');
    assert.equal(rows[0].querySelector('.jfbm-row-subtitle').getAttribute('title'), '\n  Watch the reflection  \nsecond line', 'the whole note on hover');
    assert.equal(rows[4].querySelector('.jfbm-row-subtitle').textContent, 'x'.repeat(79) + '…');
    assert.deepEqual(rows.map((r) => !!r.querySelector('.jfbm-row-subtitle')), [true, false, false, false, true]);
    assert.deepEqual(rows.map((r) => !!r.querySelector('.jfbm-name-it')), [false, true, true, true, false], 'Name it on unnamed rows only');

    rows[2].querySelector('button[title="Delete"]').click();
    await page.advance(10);
    assert.equal(panel(page, 'Delete bookmark?').querySelector('.jfbm-panel-body p').textContent, 'Bookmark at 0:17 will be deleted.');
    page.key(page.document.body, 'Escape');
    rows[0].querySelector('button[title="Delete"]').click();
    await page.advance(10);
    assert.equal(panel(page, 'Delete bookmark?').querySelector('.jfbm-panel-body p').textContent, '"<i>Bank job</i>" at 0:15 will be deleted.');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Name it opens the editor on the bookmark with Name focused; saving sends the name with the UpdatedAt it read', { skip }, async () => {
    const page = await start({
        list: [{ Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: null, Note: 'the note', UpdatedAt: '2026-09-30T08:00:00.1234567Z', Clip: null }]
    });
    const { content } = detailsPage(page);
    await page.advance(400);
    page.respond('POST', '/Bookmarks/b1', (entry) => Object.assign({ Id: 'b1', ItemId: 'item1', UpdatedAt: '2026-09-30T08:06:00.0000000Z', Clip: null }, entry.body));

    content.querySelector('.jfbm-row .jfbm-name-it').click();
    await page.advance(10);
    const editor = panel(page, 'Edit bookmark');
    const nameInput = editor.querySelectorAll('input[type="text"]')[0];
    assert.equal(page.document.activeElement, nameInput);
    assert.equal(nameInput.value, '');
    assert.equal(editor.querySelector('textarea').value, 'the note');
    nameInput.value = 'Vault';
    page.dispatch(editor.querySelector('form'), 'submit');
    await page.advance(50);
    const saved = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b1');
    assert.deepEqual(saved.body, { StartTicks: 150000000, EndTicks: null, Name: 'Vault', Note: 'the note', IfUpdatedAt: '2026-09-30T08:00:00.1234567Z' });
    assert.equal(panel(page, 'Edit bookmark'), null);
    assert.equal(page.fetchLog.filter((f) => f.method === 'GET' && f.path === '/Bookmarks').length, 2, 'the section was refreshed');
});

test('the editor keeps the name it shows (an update replaces every field) with the UpdatedAt it read; a 409 offers Reload', { skip }, async () => {
    // POST /Bookmarks/{id} has replace semantics and IfUpdatedAt makes a change made elsewhere meanwhile a 409 instead
    // of a lost update; since 1.1.0 the editor's Name field holds the name, and Reload opens the editor again on the
    // bookmark as the server has it now.
    const page = await start({
        list: [
            { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: 'Bank job', Note: 'the note', UpdatedAt: '2026-09-30T08:00:00.1234567Z', Clip: null },
            { Id: 'b2', ItemId: 'item1', StartTicks: 300000000, EndTicks: null, Note: 'only a note', UpdatedAt: '2026-09-30T08:05:00.0000000Z', Clip: null }
        ]
    });
    const { content } = detailsPage(page);
    await page.advance(400);
    page.respond('POST', '/Bookmarks/b1', { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    page.respond('POST', '/Bookmarks/b2', (entry) => Object.assign({ Id: 'b2', ItemId: 'item1', UpdatedAt: '2026-09-30T08:06:00.0000000Z', Clip: null }, entry.body));
    page.respond('GET', '/Bookmarks/b1', { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: 'Other tab', Note: 'the note', UpdatedAt: '2026-09-30T08:07:00.0000000Z', Clip: null });

    const [named, unnamed] = content.querySelectorAll('.jfbm-row');
    named.querySelector('button[title="Edit"]').click();
    await page.advance(10);
    let editor = panel(page, 'Edit bookmark');
    assert.equal(editor.querySelectorAll('input[type="text"]')[0].value, 'Bank job');
    let noteInput = editor.querySelector('textarea');
    assert.equal(noteInput.value, 'the note');
    noteInput.value = 'a new note';
    page.dispatch(editor.querySelector('form'), 'submit');
    await page.advance(50);

    const conflict = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b1');
    assert.deepEqual(conflict.body, { StartTicks: 150000000, EndTicks: null, Name: 'Bank job', Note: 'a new note', IfUpdatedAt: '2026-09-30T08:00:00.1234567Z' });
    assert.equal(editor.querySelector('.jfbm-error').textContent, 'Changed elsewhere. Reload to see the latest.');
    const reload = editor.querySelector('.jfbm-reload');
    assert.equal(reload.textContent, 'Reload');
    assert.equal(reload.classList.contains('hide'), false, 'Reload next to the 409');
    assert.ok(editor.isConnected, 'the editor stays open');

    reload.click();
    await page.advance(50);
    assert.equal(editor.isConnected, false, 'the stale editor closed');
    editor = panel(page, 'Edit bookmark');
    assert.equal(editor.querySelectorAll('input[type="text"]')[0].value, 'Other tab', 'the editor again, as the server has it now');
    assert.equal(editor.querySelector('textarea').value, 'the note');
    page.key(page.document.body, 'Escape');

    unnamed.querySelector('button[title="Edit"]').click();
    await page.advance(10);
    editor = panel(page, 'Edit bookmark');
    noteInput = editor.querySelector('textarea');
    noteInput.value = 'still unnamed';
    page.dispatch(editor.querySelector('form'), 'submit');
    await page.advance(50);

    const saved = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b2');
    assert.deepEqual(saved.body, { StartTicks: 300000000, EndTicks: null, Name: null, Note: 'still unnamed', IfUpdatedAt: '2026-09-30T08:05:00.0000000Z' });
    assert.equal(panel(page, 'Edit bookmark'), null, 'the editor closed');
    assert.ok(toasts(page).includes('Bookmark saved.'));
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Reload after a 409 on a bookmark deleted meanwhile closes the editor with a toast', { skip }, async () => {
    const page = await start({
        list: [{ Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: 'Bank job', Note: null, UpdatedAt: '2026-09-30T08:00:00.0000000Z', Clip: null }]
    });
    const { content } = detailsPage(page);
    await page.advance(400);
    page.respond('POST', '/Bookmarks/b1', { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    page.respond('GET', '/Bookmarks/b1', { title: 'Not Found', status: 404, detail: 'Bookmark not found.' }, 404);
    content.querySelector('.jfbm-row button[title="Edit"]').click();
    await page.advance(10);
    const editor = panel(page, 'Edit bookmark');
    page.dispatch(editor.querySelector('form'), 'submit');
    await page.advance(50);
    editor.querySelector('.jfbm-reload').click();
    await page.advance(50);
    assert.equal(panel(page, 'Edit bookmark'), null);
    assert.ok(toasts(page).includes('This bookmark was deleted elsewhere.'));
});

test('badges: Not in library (greyed, no play, no clip), Unavailable (no play, no clip), their editors without Save & make clip, and Relinked with Looks right', { skip }, async () => {
    const clip = { Status: 'Completed', FileName: 'clip.mp4', FileSize: 2048, IsOutdated: false };
    const page = await start({
        list: [
            { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: 250000000, Name: 'Gone', Note: null, Detached: true, Relinked: false, Clip: clip },
            { Id: 'b2', ItemId: 'item1', StartTicks: 160000000, EndTicks: 260000000, Name: 'Hidden', Note: null, Detached: false, ItemAvailable: false, Clip: null },
            { Id: 'b3', ItemId: 'item1', StartTicks: 170000000, EndTicks: null, Name: 'Moved', Note: null, Detached: false, Relinked: true, UpdatedAt: '2026-09-30T08:00:00.0000000Z', Clip: null },
            { Id: 'b4', ItemId: 'item1', StartTicks: 180000000, EndTicks: 280000000, Name: null, Note: null, Detached: false, Relinked: false, ItemAvailable: true, Clip: null }
        ]
    });
    const { content } = detailsPage(page);
    await page.advance(400);
    page.respond('POST', '/Bookmarks/b3/Name', (entry) => ({ Id: 'b3', ItemId: 'item1', Name: entry.body.Name, Relinked: false }));

    const [gone, hidden, moved, fine] = content.querySelectorAll('.jfbm-row');
    const badges = (row) => row.querySelectorAll('.jfbm-badge').map((b) => b.className + '|' + b.textContent);
    assert.equal(gone.className, 'jfbm-row jfbm-row-detached');
    assert.deepEqual(badges(gone), ['jfbm-badge jfbm-badge-missing|Not in library']);
    assert.match(gone.querySelector('.jfbm-badge').getAttribute('title'), /isn't in the library any more/);
    assert.equal(gone.querySelector('.jfbm-time-button').disabled, true, 'no play');
    assert.equal(gone.querySelector('.jfbm-time-button').getAttribute('title'), 'Not in library');
    assert.deepEqual(gone.querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Edit', 'Delete'], 'no clip of a missing video');
    assert.ok(gone.querySelector('.jfbm-clip-ready'), 'its clip file can still be downloaded');

    assert.equal(hidden.className, 'jfbm-row');
    assert.deepEqual(badges(hidden), ['jfbm-badge jfbm-badge-unavailable|Unavailable']);
    assert.equal(hidden.querySelector('.jfbm-time-button').disabled, true);
    assert.equal(hidden.querySelector('.jfbm-time-button').getAttribute('title'), 'Unavailable');
    assert.deepEqual(hidden.querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Edit', 'Delete']);

    assert.deepEqual(badges(moved), ['jfbm-badge jfbm-badge-relinked|Relinked, check position']);
    assert.equal(moved.querySelector('.jfbm-time-button').disabled, false, 'a relinked bookmark still plays');
    const looksRight = moved.querySelector('.jfbm-looks-right');
    assert.equal(looksRight.textContent, 'Looks right');

    assert.deepEqual(badges(fine), []);
    assert.equal(fine.querySelector('.jfbm-time-button').disabled, false);
    assert.deepEqual(fine.querySelectorAll('.jfbm-row-actions button').map((b) => b.getAttribute('title')), ['Make a clip', 'Edit', 'Delete']);

    // The editor follows the row: no Save & make clip (nor the clip mode) for a Not in library or Unavailable bookmark,
    // whose Save is the primary button; the others keep it.
    const editorActions = async (row) => {
        row.querySelector('button[title="Edit"]').click();
        await page.advance(10);
        const editor = panel(page, 'Edit bookmark');
        const actions = {
            buttons: editor.querySelectorAll('.jfbm-actions button').map((b) => b.textContent + (b.classList.contains('jfbm-button-primary') ? ' (primary)' : '')),
            mode: !!editor.querySelector('.jfbm-actions select')
        };
        page.key(page.document.body, 'Escape');
        assert.equal(panel(page, 'Edit bookmark'), null);
        return actions;
    };
    const saveOnly = { buttons: ['Delete', 'Cancel', 'Save (primary)'], mode: false };
    assert.deepEqual(await editorActions(gone), saveOnly, 'Not in library: no Save & make clip');
    assert.deepEqual(await editorActions(hidden), saveOnly, 'Unavailable: no Save & make clip');
    assert.deepEqual(await editorActions(fine), { buttons: ['Delete', 'Cancel', 'Save', 'Save & make clip (primary)'], mode: true });
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST').length, 0);

    const listsBefore = page.fetchLog.filter((f) => f.method === 'GET' && f.path === '/Bookmarks').length;
    looksRight.click();
    await page.advance(50);
    const confirmed = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b3/Name');
    assert.deepEqual(confirmed.body, { Name: 'Moved', IfUpdatedAt: '2026-09-30T08:00:00.0000000Z' }, 'the rename with the unchanged name');
    assert.equal(page.fetchLog.filter((f) => f.method === 'GET' && f.path === '/Bookmarks').length, listsBefore + 1, 'the section was refreshed');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('Looks right answered 409 shows the sentence with Reload, which refreshes the section', { skip }, async () => {
    const page = await start({
        list: [{ Id: 'b3', ItemId: 'item1', StartTicks: 170000000, EndTicks: null, Name: null, Note: null, Relinked: true, UpdatedAt: '2026-09-30T08:00:00.0000000Z', Clip: null }]
    });
    const { content } = detailsPage(page);
    await page.advance(400);
    page.respond('POST', '/Bookmarks/b3/Name', { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    content.querySelector('.jfbm-looks-right').click();
    await page.advance(50);
    assert.deepEqual(page.fetchLog.find((f) => f.path === '/Bookmarks/b3/Name').body, { Name: null, IfUpdatedAt: '2026-09-30T08:00:00.0000000Z' });
    assert.deepEqual(toasts(page), ['Changed elsewhere. Reload to see the latest.Reload']);
    const lists = page.fetchLog.filter((f) => f.method === 'GET' && f.path === '/Bookmarks').length;
    page.document.querySelector('.jfbm-toast-action').click();
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'GET' && f.path === '/Bookmarks').length, lists + 1);
});

test('the section\'s page links follow Capabilities.Pages: See all to My Bookmarks for this video, My Clips only while it is there', { skip }, async () => {
    const onlyBookmarks = await start({ capabilities: { Pages: { Bookmarks: true, Clips: false } } });
    detailsPage(onlyBookmarks);
    await onlyBookmarks.advance(400);
    let links = onlyBookmarks.document.querySelector('.jfbm-section .jfbm-section-links');
    assert.equal(links.classList.contains('hide'), false);
    assert.deepEqual(links.querySelectorAll('a').map((a) => a.textContent + '|' + a.getAttribute('href') + '|' + a.getAttribute('data-jfbm-link')), ['See all|#/jfbm-bookmarks?item=item1|bookmarks']);
    assert.equal(links.querySelector('a').getAttribute('is'), 'emby-linkbutton');
    assert.equal(onlyBookmarks.document.body.textContent.includes('My Clips'), false, 'no My Clips link while Pages.Clips is false');

    const both = await start({ capabilities: { Pages: { Bookmarks: true, Clips: true } } });
    detailsPage(both);
    await both.advance(400);
    links = both.document.querySelector('.jfbm-section .jfbm-section-links');
    assert.deepEqual(links.querySelectorAll('a').map((a) => a.textContent + '|' + a.getAttribute('href')), ['See all|#/jfbm-bookmarks?item=item1', 'My Clips|#/jfbm-clips']);

    const older = await start({ capabilities: { Pages: undefined } });
    detailsPage(older);
    await older.advance(400);
    links = older.document.querySelector('.jfbm-section .jfbm-section-links');
    assert.ok(links.classList.contains('hide'), 'without Pages (an older answer) no links');
    assert.equal(links.children.length, 0);
});

test('the player: four buttons before the subtitles button, Shift+B bookmarks the current position, toasts sit above the controls', { skip }, async () => {
    const page = await start();
    const { buttons, video } = playerPage(page);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 100000000, MediaSourceId: 'ms1', AudioStreamIndex: 1, IsPaused: false });
    await page.advance(400);
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b9', Clip: null }, entry.body), 201);

    const inserted = buttons.children.slice(0, 4);
    assert.deepEqual(inserted.map((b) => b.className), [
        'btnBookmarkAdd autoSize paper-icon-button-light',
        'btnBookmarkRange autoSize paper-icon-button-light',
        'btnBookmarkEnd autoSize paper-icon-button-light',
        'btnBookmarkList autoSize paper-icon-button-light'
    ]);
    assert.deepEqual(inserted.map((b) => b.getAttribute('title')), ['Bookmark this moment (Shift+B)', 'Mark the start of a range', 'Set end time', 'Bookmarks']);
    assert.equal(inserted[0].firstChild.className, 'xlargePaperIconButton material-icons bookmark_add');
    assert.equal(inserted[2].firstChild.className, 'xlargePaperIconButton material-icons last_page');
    assert.equal(buttons.children[4].className, 'btnSubtitles');

    // Plain B is Jellyfin Enhanced's (the kit's policy): the configured combination, Shift+B by default, is matched whole.
    video.currentTime = 44.5;
    assert.equal(page.key(page.document.body, 'b'), true, 'plain B is left alone');
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST').length, 0);
    const event = page.key(page.document.body, 'B', { shiftKey: true });
    assert.equal(event, false, 'Shift+B is handled (default prevented)');
    await page.advance(50);
    const post = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks');
    assert.deepEqual(post.body, { ItemId: 'item1', StartTicks: 125000000, MediaSourceId: 'ms1', AudioStreamIndex: 1 });
    assert.deepEqual(toasts(page), ['Bookmarked at 0:12.Name it'], 'the toast offers Name it (1.0: Edit)');
    assert.ok(page.document.querySelector('.jfbm-toasts').classList.contains('jfbm-toasts-player'));
    await page.advance(7000);
    assert.equal(toasts(page).length, 1, 'the toast stays 8 s, not the usual 4');
    await page.advance(1100);
    assert.equal(toasts(page).length, 0);
});

test('Shift+B is left alone in text fields, with other modifiers, when already handled, while a dialog is open and off the player', { skip }, async () => {
    const page = await start();
    playerPage(page);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 0 });
    await page.advance(400);
    const input = page.element('input');
    page.document.body.append(input);
    assert.equal(page.key(input, 'B', { shiftKey: true }), true);
    assert.equal(page.key(page.document.body, 'B', { shiftKey: true, ctrlKey: true }), true);
    const prevented = new page.Event('keydown', { bubbles: true, key: 'B', shiftKey: true });
    prevented.preventDefault();
    page.document.body.dispatchEvent(prevented);
    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    assert.ok(panel(page, 'Bookmarks'));
    assert.equal(page.key(page.document.querySelector('.jfbm-panel'), 'B', { shiftKey: true }), true);
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST').length, 0);

    // Off the player (the OSD page hidden) the binding declines, so the key keeps its default and reaches others.
    page.key(page.document.body, 'Escape');
    page.document.querySelector('#videoOsdPage').classList.add('hide');
    assert.equal(page.key(page.document.body, 'B', { shiftKey: true }), true);
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST').length, 0);
});

test('the bookmark shortcut follows the server\'s setting and the user\'s own device override; the tooltip says which', { skip }, async () => {
    const page = await start({ capabilities: { Shortcuts: { BookmarkNow: 'Ctrl+Shift+K' } } });
    const { buttons } = playerPage(page);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 100000000, MediaSourceId: 'ms1', AudioStreamIndex: 1 });
    await page.advance(400);
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b9', Clip: null }, entry.body), 201);

    assert.equal(buttons.children[0].getAttribute('title'), 'Bookmark this moment (Shift+Ctrl+K)');
    assert.equal(page.key(page.document.body, 'B', { shiftKey: true }), true, 'the default no longer applies');
    assert.equal(page.key(page.document.body, 'K', { shiftKey: true, ctrlKey: true }), false);
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST').length, 1);

    // The user's own override on this device (localStorage jfbm:shortcut:bookmarkNow) wins; '' turns it off.
    page.context.localStorage.setItem('jfbm:shortcut:bookmarkNow', JSON.stringify('alt+b'));
    page.dispatch(page.document.querySelector('#videoOsdPage'), 'viewshow');
    await page.advance(400);
    assert.equal(buttons.children[0].getAttribute('title'), 'Bookmark this moment (Alt+B)');
    assert.equal(page.key(page.document.body, 'K', { shiftKey: true, ctrlKey: true }), true);
    assert.equal(page.key(page.document.body, 'b', { altKey: true }), false);
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST').length, 2);

    page.context.localStorage.setItem('jfbm:shortcut:bookmarkNow', JSON.stringify(''));
    page.dispatch(page.document.querySelector('#videoOsdPage'), 'viewshow');
    await page.advance(400);
    assert.equal(buttons.children[0].getAttribute('title'), 'Bookmark this moment');
    assert.equal(page.key(page.document.body, 'b', { altKey: true }), true);
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST').length, 2);
});

test('the player list jumps with a Sessions API seek, and the range button opens the editor with the range', { skip }, async () => {
    const page = await start();
    const { video } = playerPage(page);
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 200000000 });
    await page.advance(400);

    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    const list = panel(page, 'Bookmarks');
    assert.equal(list.querySelector('.jfbm-list-toolbar .jfbm-button').textContent, 'New bookmark');
    const rows = list.querySelectorAll('.jfbm-row');
    assert.equal(rows.length, 2);
    assert.equal(rows[0].querySelector('.jfbm-time-button').getAttribute('title'), 'Jump here');
    rows[0].querySelector('.jfbm-time-button').click();
    await page.advance(50);
    assert.equal(panel(page, 'Bookmarks'), null);
    const seek = page.fetchLog.find((f) => f.path === '/Sessions/s1/Playing/Seek');
    assert.deepEqual(seek.query, { SeekPositionTicks: '150000000' });

    const range = page.document.querySelector('.btnBookmarkRange');
    range.click();
    await page.advance(10);
    assert.ok(range.classList.contains('jfbm-active'));
    assert.equal(range.getAttribute('title'), 'Mark the end of the range (started at 0:20)');
    assert.deepEqual(toasts(page), ['Range starts at 0:20. Press the scissors again at the end.Cancel']);
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 350000000 });
    video.currentTime = 42;
    range.click();
    await page.advance(10);
    const editor = panel(page, 'New bookmark');
    const inputs = editor.querySelectorAll('input[type="text"]');
    assert.deepEqual(inputs.map((i) => i.value), ['', '0:20.0', '0:35.0']);
    assert.equal(editor.querySelector('textarea').value, '');
    assert.equal(page.document.activeElement, inputs[0], 'the range flow opens the editor with Name focused');
    assert.equal(editor.querySelector('.jfbm-hint').textContent, 'Range length 0:15.0');
    assert.equal(range.classList.contains('jfbm-active'), false);

    // Save & make clip with a name: the clip file and title use it (the server's part).
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b9', UpdatedAt: '2026-09-30T08:00:00.0000000Z', Clip: null }, entry.body), 201);
    page.respond('POST', '/Bookmarks/b9/Clip', { Id: 'b9', ItemId: 'item1', StartTicks: 200000000, EndTicks: 350000000, Name: 'Bank job', Clip: { Status: 'Queued' } });
    inputs[0].value = 'Bank job';
    editor.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Save & make clip').click();
    await page.advance(50);
    const created = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks');
    assert.deepEqual(plain(created.body), { ItemId: 'item1', StartTicks: 200000000, EndTicks: 350000000, Name: 'Bank job', Note: null, MediaSourceId: null, AudioStreamIndex: null });
    assert.deepEqual(page.fetchLog.find((f) => f.path === '/Bookmarks/b9/Clip').body, { Mode: 'Accurate' });
    assert.equal(panel(page, 'New bookmark'), null);
    assert.ok(toasts(page).includes('Making the clip. You can keep watching.'));
});

test('the player is open while any of its views is shown: an earlier, hidden one may come first in the document', { skip }, async () => {
    // Played again and again from My Bookmarks, the web client keeps up to three player views (#videoOsdPage), the
    // earlier ones hidden, in page slots whose order the shown one doesn't follow.
    const page = await start({ list: endList() });
    const earlier = page.element('div', { id: 'videoOsdPage', class: 'page hide' }, [page.element('div', { class: 'videoOsdBottom' }, [page.element('div', { class: 'buttons' })])]);
    page.document.body.append(earlier);
    const { osd, buttons } = playerPage(page);
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 350000000 });
    await page.advance(400);
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b9', Clip: null }, entry.body), 201);

    buttons.querySelector('.btnBookmarkEnd').click();
    await page.advance(50);
    assert.ok(panel(page, 'Set end time to 0:35'), 'the position is known');
    assert.deepEqual(toasts(page), []);
    page.key(page.document.body, 'Escape');
    await page.advance(50);

    assert.equal(page.key(page.document.body, 'B', { shiftKey: true }), false, 'the shortcut is bound');
    await page.advance(50);
    assert.equal(page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks').body.StartTicks, 350000000);
    assert.ok(page.document.querySelector('.jfbm-toasts').classList.contains('jfbm-toasts-player'), 'its toasts raised above the controls');

    // Every view hidden: off the player again.
    osd.classList.add('hide');
    buttons.querySelector('.btnBookmarkEnd').click();
    await page.advance(50);
    assert.ok(toasts(page).includes('Start playing a video to set a bookmark\'s end time.'));
    assert.equal(page.key(page.document.body, 'B', { shiftKey: true }), true);
});

// Set end time: b1 a named point at 0:15, b2 the range 0:30-0:40, b3 a point at 0:50; playing at 0:35.
function endList() {
    const list = bookmarks();
    Object.assign(list[0], { Name: 'Bank job', UpdatedAt: '2026-10-29T07:00:00.0000000Z' });
    return list.concat([{ Id: 'b3', ItemId: 'item1', StartTicks: 500000000, EndTicks: null, Name: 'Later', Note: null, Clip: null }]);
}

async function endChooser(options) {
    const page = await start(Object.assign({ list: endList() }, options || {}));
    playerPage(page);
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 350000000 });
    await page.advance(400);
    page.document.querySelector('.btnBookmarkEnd').click();
    await page.advance(50);
    return page;
}

function endItems(chooser) {
    return chooser.querySelectorAll('.jfbm-end-item').map((i) => i.getAttribute('data-id') + '|' + i.querySelector('.jfbm-end-item-title').textContent
        + '|' + i.querySelector('.jfbm-end-item-detail').textContent + (i.disabled ? '|disabled' : ''));
}

test('Set end time lists the video\'s bookmarks, the nearest start before the position first, and ends the chosen one there', { skip }, async () => {
    const page = await endChooser();
    const chooser = panel(page, 'Set end time to 0:35');
    assert.ok(chooser, 'the chooser is titled with the position at the click');
    assert.equal(chooser.querySelector('.jfbm-end-hint').textContent, 'Choose the bookmark that ends here.');
    assert.deepEqual(endItems(chooser), [
        'b2|Range 0:30–0:40|From 0:30, 0:05 long (ends at 0:40 now)',
        'b1|Bank job|From 0:15, 0:20 long',
        'b3|Later|Starts at 0:50, after this moment|disabled'
    ]);
    assert.equal(chooser.querySelector('.jfbm-row-menu').getAttribute('role'), 'menu');
    const items = chooser.querySelectorAll('.jfbm-end-item');
    assert.equal(page.document.activeElement, items[0], 'the nearest bookmark before the position has the focus');

    // The row menu's keys: Down to the next, wrapping past the one that can't be chosen.
    assert.equal(page.key(items[0], 'ArrowDown'), false);
    assert.equal(page.document.activeElement, items[1]);
    page.key(items[1], 'ArrowDown');
    assert.equal(page.document.activeElement, items[0]);

    // Every field sent again with the UpdatedAt it was read with (an update replaces every field).
    page.respond('POST', '/Bookmarks/b1', (entry) => Object.assign({ Id: 'b1', ItemId: 'item1', UpdatedAt: '2026-10-29T07:00:01.0000000Z', Clip: null }, entry.body));
    page.respond('POST', '/Bookmarks/b1/Clip', { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: 350000000, Name: 'Bank job', Clip: { Status: 'Queued' } });
    items[1].click();
    await page.advance(50);
    const update = page.fetchLog.find((f) => f.method === 'POST' && f.path === '/Bookmarks/b1');
    assert.deepEqual(plain(update.body), { StartTicks: 150000000, EndTicks: 350000000, Name: 'Bank job', Note: '<b>webcheck</b>', IfUpdatedAt: '2026-10-29T07:00:00.0000000Z' });
    assert.equal(panel(page, 'Set end time to 0:35'), null);
    assert.deepEqual(toasts(page), ['"Bank job" now ends at 0:35.Make a clip']);
    page.document.querySelector('.jfbm-toast-action').click();
    await page.advance(50);
    assert.deepEqual(page.fetchLog.find((f) => f.path === '/Bookmarks/b1/Clip').body, { Mode: 'Accurate' });
});

test('Set end time: a range past the clip limit is said and gets no Make a clip; without clips no Make a clip either', { skip }, async () => {
    const limited = await endChooser({ capabilities: { MaxClipDurationSeconds: 10 } });
    const chooser = panel(limited, 'Set end time to 0:35');
    assert.deepEqual(endItems(chooser).slice(0, 2), [
        'b2|Range 0:30–0:40|From 0:30, 0:05 long (ends at 0:40 now)',
        'b1|Bank job|From 0:15, 0:20 long, longer than the 0:10 clip limit'
    ]);
    limited.respond('POST', '/Bookmarks/b1', (entry) => Object.assign({ Id: 'b1', ItemId: 'item1', Clip: null }, entry.body));
    chooser.querySelectorAll('.jfbm-end-item')[1].click();
    await limited.advance(50);
    assert.deepEqual(toasts(limited), ['"Bank job" now ends at 0:35.']);

    const noClips = await endChooser({ capabilities: { CanCreateClips: false } });
    assert.equal(endItems(panel(noClips, 'Set end time to 0:35'))[1], 'b1|Bank job|From 0:15, 0:20 long', 'no clip limit without clips');
    noClips.respond('POST', '/Bookmarks/b2', (entry) => Object.assign({ Id: 'b2', ItemId: 'item1', Clip: null }, entry.body));
    panel(noClips, 'Set end time to 0:35').querySelectorAll('.jfbm-end-item')[0].click();
    await noClips.advance(50);
    assert.deepEqual(toasts(noClips), ['"Range 0:30–0:40" now ends at 0:35.']);
});

test('Set end time: a bookmark changed or deleted elsewhere reads the list again and says so; a refusal keeps the chooser', { skip }, async () => {
    const page = await endChooser();
    const chooser = panel(page, 'Set end time to 0:35');
    page.respond('POST', '/Bookmarks/b1', { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    const latest = endList();
    latest[0].Name = 'Bank job (renamed)';
    page.respond('GET', '/Bookmarks', latest);
    chooser.querySelectorAll('.jfbm-end-item')[1].click();
    await page.advance(50);
    assert.equal(chooser.querySelector('[role="alert"]').textContent, 'That bookmark was changed elsewhere. The list shows it as it is now; choose again.');
    assert.equal(endItems(chooser)[1], 'b1|Bank job (renamed)|From 0:15, 0:20 long');
    assert.equal(chooser.querySelectorAll('.jfbm-end-item')[1].disabled, false, 'it can be chosen again');

    page.respond('POST', '/Bookmarks/b2', { title: 'Bad Request', status: 400, detail: 'The end must be after the start.' }, 400);
    chooser.querySelectorAll('.jfbm-end-item')[0].click();
    await page.advance(50);
    assert.ok(panel(page, 'Set end time to 0:35'), 'still open');
    assert.equal(chooser.querySelector('[role="alert"]').textContent, 'The end must be after the start.');

    page.respond('POST', '/Bookmarks/b2', { title: 'Not Found', status: 404, detail: 'Bookmark not found.' }, 404);
    page.respond('GET', '/Bookmarks', []);
    chooser.querySelectorAll('.jfbm-end-item')[0].click();
    await page.advance(50);
    assert.equal(panel(page, 'Set end time to 0:35'), null, 'no bookmark left closes it');
    assert.deepEqual(toasts(page), ['That bookmark was deleted elsewhere.']);
});

test('Set end time without playback or without bookmarks only says so', { skip }, async () => {
    const idle = await start();
    playerPage(idle);
    await idle.advance(400);
    idle.document.querySelector('.btnBookmarkEnd').click();
    await idle.advance(50);
    assert.deepEqual(toasts(idle), ['Start playing a video to set a bookmark\'s end time.']);

    const empty = await endChooser({ list: [] });
    assert.equal(empty.document.querySelector('.jfbm-backdrop'), null);
    assert.deepEqual(toasts(empty), ['This video has no bookmarks yet. Bookmark a moment first, then set its end time here.']);
});

test('Play from here on the details page starts playback through the Sessions API, sending again if it does not start', { skip }, async () => {
    const page = await start();
    const { content } = detailsPage(page);
    await page.advance(400);
    content.querySelector('.jfbm-row .jfbm-time-button').click();
    await page.advance(8100);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 150000000 });
    await page.advance(50);
    const plays = page.fetchLog.filter((f) => f.path === '/Sessions/s1/Playing');
    assert.equal(plays.length, 2);
    assert.deepEqual(plays[0].query, { playCommand: 'PlayNow', itemIds: 'item1', startPositionTicks: '150000000' });
    assert.deepEqual(toasts(page), []);
});

test('playback stopped clears a pending range; no request ever goes out without the user', { skip }, async () => {
    const page = createPage();
    page.run(source, file);
    page.installApiClient({ userId: null, items: { item1: MOVIE } });
    detailsPage(page);
    playerPage(page);
    await page.advance(1000);
    assert.equal(page.fetchLog.length, 0);
    assert.equal(page.document.querySelector('.btnBookmarkItem'), null, 'no user work before the user');
    page.clientState.userId = 'user-1';
    page.respond('GET', '/Bookmarks/Capabilities', CAPABILITIES);
    page.respond('GET', '/Bookmarks', bookmarks());
    await page.advance(500);
    assert.ok(page.document.querySelector('.btnBookmarkItem'));
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 0 });
    page.document.querySelector('.btnBookmarkRange').click();
    assert.ok(page.document.querySelector('.btnBookmarkRange').classList.contains('jfbm-active'));
    await page.context.ApiClient.reportPlaybackStopped({ ItemId: 'item1' });
    assert.equal(page.document.querySelector('.btnBookmarkRange').classList.contains('jfbm-active'), false);
    assert.ok(page.fetchLog.every((f) => f.userId === 'user-1'), JSON.stringify(plain(page.fetchLog)));
});

// ----------------------------------------------------------------------------------------------------- the name dialog

const CHAPTERS = [
    { StartPositionTicks: 0, Name: 'Opening' },
    { StartPositionTicks: 100000000, Name: 'Bank job' },
    { StartPositionTicks: 200000000, Name: 'Chapter 3' }
];
const CHAPTERED_MOVIE = Object.assign({ Chapters: CHAPTERS }, MOVIE);
const PLAYER_CAPABILITIES = { Shortcuts: { BookmarkNow: 'Shift+B' }, ShowPlayerButtons: true, AskForName: false, AskForNameIsDefault: true, PauseWhileNaming: false, SuggestChapterNames: true };

// A player with the chaptered movie playing at 0:12 (inside "Bank job"); the quick bookmarks are b9, b10, ...
async function playing(options) {
    options = options || {};
    const page = await start({ capabilities: Object.assign({}, PLAYER_CAPABILITIES, options.capabilities || {}), list: options.list || [] });
    page.lookups = [];
    page.context.ApiClient.getItem = (userId, id) => {
        page.lookups.push(id);
        return Promise.resolve(id === 'item1' ? (options.item || CHAPTERED_MOVIE) : null);
    };
    const player = playerPage(page, { slider: options.slider });
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 100000000, MediaSourceId: 'ms1', AudioStreamIndex: 1, IsPaused: false });
    await page.advance(400);
    let created = 0;
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b' + (9 + created++), ItemName: 'Long Test Movie', UpdatedAt: '2026-10-06T08:00:00.0000000Z', Clip: null }, entry.body), 201);
    page.respond('POST', /^\/Bookmarks\/b\d+\/Name$/, (entry) => ({ Id: entry.path.split('/')[2], ItemId: 'item1', StartTicks: 125000000, Name: entry.body.Name, UpdatedAt: '2026-10-06T08:00:01.0000000Z', Clip: null }));
    page.respond('POST', '/Bookmarks/Preferences', null, 204);
    page.respond('POST', '/Sessions/s1/Playing/Pause', null, 204);
    page.respond('POST', '/Sessions/s1/Playing/Unpause', null, 204);
    player.video.currentTime = 44.5;
    return Object.assign({ page }, player);
}

function nameDialog(page) {
    return panel(page, 'Name this bookmark');
}

function nameInput(dialog) {
    return dialog.querySelector('input[type="text"]');
}

function shiftB(page) {
    return page.key(page.document.body, 'B', { shiftKey: true });
}

function nameRequests(page) {
    return page.fetchLog.filter((f) => f.method === 'POST' && /^\/Bookmarks\/b\d+\/Name$/.test(f.path));
}

test('the name dialog from the toast\'s Name it: the chapter\'s title suggested and selected, Save names the bookmark with the UpdatedAt it was made with', { skip }, async () => {
    const { page } = await playing();
    shiftB(page);
    await page.advance(50);
    assert.equal(nameDialog(page), null, 'AskForName off: no dialog, the toast');
    assert.deepEqual(toasts(page), ['Bookmarked at 0:12.Name it']);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Items/item1').length, 0, 'no chapter lookup before Name it');

    page.document.querySelector('.jfbm-toast-action').click();
    await page.advance(50);
    const dialog = nameDialog(page);
    assert.ok(dialog, 'the name dialog');
    assert.equal(dialog.className, 'jfbm-panel dialog');
    assert.equal(toasts(page).length, 0, 'the toast went with its action');
    assert.equal(dialog.querySelector('.jfbm-name-where').textContent, 'Bookmarked at 0:12 in Long Test Movie.');
    const input = nameInput(dialog);
    assert.equal(input.value, 'Bank job', 'the chapter covering 0:12 (the last starting at or before it)');
    assert.equal(page.document.activeElement, input, 'focused');
    assert.deepEqual([input.selectionStart, input.selectionEnd], [0, 8], 'fully selected, so typing replaces it');
    assert.equal(input.getAttribute('maxlength'), '100');
    assert.equal(input.className, 'jfbm-input emby-input');
    assert.deepEqual(dialog.querySelectorAll('.jfbm-field > label').map((l) => l.textContent), ['Name']);
    const ask = dialog.querySelector('.jfbm-always-ask input[type="checkbox"]');
    assert.equal(ask.checked, false, 'mirrors AskForName');
    assert.equal(dialog.querySelector('.jfbm-always-ask span').textContent, 'Always ask for a name');
    assert.deepEqual(dialog.querySelectorAll('.jfbm-actions button').map((b) => b.textContent + '|' + b.className), [
        'More…|jfbm-link jfbm-name-more',
        'Skip|jfbm-button',
        'Save|jfbm-button jfbm-button-primary button-submit'
    ]);

    input.value = 'Vault';
    assert.equal(page.key(input, 'Enter'), false, 'Enter in the field saves (prevented: no second submission)');
    await page.advance(50);
    const [named] = nameRequests(page);
    assert.equal(named.path, '/Bookmarks/b9/Name');
    assert.deepEqual(named.body, { Name: 'Vault', IfUpdatedAt: '2026-10-06T08:00:00.0000000Z' });
    assert.equal(nameDialog(page), null, 'closed');
    assert.deepEqual(toasts(page), ['Named "Vault".'], 'the toast says the name');
    assert.equal(page.fetchLog.filter((f) => f.path === '/Sessions/s1/Playing/Pause').length, 0, 'no pause without the preference');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('AskForName on: Shift+B opens the dialog at once; Escape skips; Skip and an empty Save send nothing; a 409 stays in the dialog', { skip }, async () => {
    const { page, video } = await playing({ capabilities: { AskForName: true, AskForNameIsDefault: false } });
    assert.equal(shiftB(page), false);
    await page.advance(50);
    let dialog = nameDialog(page);
    assert.ok(dialog, 'the dialog at once');
    assert.equal(toasts(page).length, 0, 'no toast');
    assert.equal(nameInput(dialog).value, 'Bank job');
    assert.equal(video.paused, undefined, 'playback untouched');
    assert.equal(page.key(nameInput(dialog), 'Escape'), false, 'Escape (the kit\'s scoped listener) skips');
    assert.equal(nameDialog(page), null);
    assert.equal(nameRequests(page).length, 0, 'the bookmark stays unnamed');

    shiftB(page);
    await page.advance(50);
    dialog = nameDialog(page);
    dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Skip').click();
    assert.equal(nameDialog(page), null);
    assert.equal(nameRequests(page).length, 0);

    shiftB(page);
    await page.advance(50);
    dialog = nameDialog(page);
    nameInput(dialog).value = '   ';
    page.dispatch(dialog.querySelector('form'), 'submit');
    await page.advance(20);
    assert.equal(nameDialog(page), null, 'an empty name is a Skip');
    assert.equal(nameRequests(page).length, 0);

    // A 409 (changed elsewhere since the bookmark was made): the dialog reads the bookmark again, shows the name it has
    // now, selected, and says so; Save then carries the latest UpdatedAt (it once kept the stale one, so every
    // further Save was another 409 under "Reload to see the latest" with no Reload offered).
    page.respond('POST', /^\/Bookmarks\/b\d+\/Name$/, { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    page.respond('GET', '/Bookmarks/b12', { Id: 'b12', ItemId: 'item1', ItemName: 'Long Test Movie', StartTicks: 130000000, Name: 'Bank job 2', UpdatedAt: '2026-10-06T08:05:00.0000000Z', Clip: null });
    shiftB(page);
    await page.advance(50);
    dialog = nameDialog(page);
    page.dispatch(dialog.querySelector('form'), 'submit');
    await page.advance(50);
    assert.ok(dialog.isConnected, 'the dialog stays open');
    assert.equal(dialog.querySelector('.jfbm-error').textContent, 'Changed elsewhere: it is named "Bank job 2" now. Save replaces that.');
    assert.equal(nameInput(dialog).value, 'Bank job 2', 'the name it has now');
    assert.deepEqual([nameInput(dialog).selectionStart, nameInput(dialog).selectionEnd], [0, 10], 'selected, so typing replaces it');
    assert.equal(page.document.activeElement, nameInput(dialog));
    assert.equal(dialog.querySelector('.jfbm-name-where').textContent, 'Bookmarked at 0:13 in Long Test Movie.', 'the time as it is now');
    assert.equal(dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Save').disabled, false, 'ready for another try');
    page.respond('POST', /^\/Bookmarks\/b\d+\/Name$/, (entry) => ({ Id: 'b12', ItemId: 'item1', StartTicks: 130000000, Name: entry.body.Name, UpdatedAt: '2026-10-06T08:06:00.0000000Z', Clip: null }));
    nameInput(dialog).value = 'Vault';
    page.dispatch(dialog.querySelector('form'), 'submit');
    await page.advance(50);
    const retried = nameRequests(page);
    assert.equal(retried.length, 2);
    assert.deepEqual(retried[1].body, { Name: 'Vault', IfUpdatedAt: '2026-10-06T08:05:00.0000000Z' }, 'the latest UpdatedAt');
    assert.equal(nameDialog(page), null, 'saved');
    assert.ok(toasts(page).includes('Named "Vault".'));

    // One deleted meanwhile (the read answers 404): the dialog closes with a toast, as the editor's Reload does.
    page.respond('POST', /^\/Bookmarks\/b\d+\/Name$/, { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    page.respond('GET', '/Bookmarks/b13', { title: 'Not Found', status: 404, detail: 'Bookmark not found.' }, 404);
    shiftB(page);
    await page.advance(50);
    dialog = nameDialog(page);
    page.dispatch(dialog.querySelector('form'), 'submit');
    await page.advance(50);
    assert.equal(nameDialog(page), null, 'closed');
    assert.ok(toasts(page).includes('This bookmark was deleted elsewhere.'));

    // The read itself refused: the server's sentence stands.
    page.respond('GET', '/Bookmarks/b14', { title: 'Service Unavailable', status: 503, detail: 'The server is busy.' }, 503);
    shiftB(page);
    await page.advance(50);
    dialog = nameDialog(page);
    page.dispatch(dialog.querySelector('form'), 'submit');
    await page.advance(50);
    assert.ok(dialog.isConnected);
    assert.equal(dialog.querySelector('.jfbm-error').textContent, 'Changed elsewhere. Reload to see the latest.');
    page.key(page.document.body, 'Escape');

    // More… after a 409 opens the editor on the bookmark as read again, not on the stale one.
    page.respond('GET', '/Bookmarks/b15', { Id: 'b15', ItemId: 'item1', StartTicks: 125000000, Name: 'Chase', Note: 'later', UpdatedAt: '2026-10-06T08:07:00.0000000Z', Clip: null });
    shiftB(page);
    await page.advance(50);
    dialog = nameDialog(page);
    page.dispatch(dialog.querySelector('form'), 'submit');
    await page.advance(50);
    dialog.querySelector('.jfbm-name-more').click();
    await page.advance(50);
    const editor = panel(page, 'Edit bookmark');
    assert.equal(editor.querySelectorAll('input[type="text"]')[0].value, 'Chase', 'the editor on the latest');
    assert.equal(editor.querySelector('textarea').value, 'later');
    page.key(page.document.body, 'Escape');
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks').length, 7);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Items/item1').length, 0, 'getItem, not a request of ours');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a double activation (two Shift+B presses, two clicks of the button) makes one bookmark, and one dialog with AskForName; a refused create frees the next', { skip }, async () => {
    const { page } = await playing();
    const posts = () => page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks').length;
    assert.equal(shiftB(page), false);
    assert.equal(shiftB(page), false, 'the binding fires again (the kit skips only a held key\'s repeats); the guard ignores it');
    await page.advance(50);
    assert.equal(posts(), 1, 'one request');
    assert.deepEqual(toasts(page), ['Bookmarked at 0:12.Name it'], 'one toast');

    const button = page.document.querySelector('.btnBookmarkAdd');
    button.click();
    button.click();
    await page.advance(50);
    assert.equal(posts(), 2, 'the button: one more, not two');

    shiftB(page);
    await page.advance(50);
    assert.equal(posts(), 3, 'a press after the first settled makes the next');

    // With AskForName: one dialog, not two stacked; under the open dialog the button does nothing, as the shortcut.
    const ask = await playing({ capabilities: { AskForName: true } });
    const askPosts = () => ask.page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks').length;
    const add = ask.page.document.querySelector('.btnBookmarkAdd');
    add.click();
    add.click();
    await ask.page.advance(50);
    assert.equal(askPosts(), 1);
    assert.equal(ask.page.document.querySelectorAll('.jfbm-backdrop').length, 1, 'one dialog');
    add.click();
    shiftB(ask.page);
    await ask.page.advance(50);
    assert.equal(askPosts(), 1, 'nothing while the dialog is open');
    ask.page.key(ask.page.document.body, 'Escape');
    await ask.page.advance(50);
    add.click();
    await ask.page.advance(50);
    assert.equal(askPosts(), 2, 'free again once it closed');
    assert.equal(ask.page.document.querySelectorAll('.jfbm-backdrop').length, 1);
    ask.page.key(ask.page.document.body, 'Escape');

    // A refused create frees the next activation too.
    const refused = await playing();
    refused.page.respond('POST', '/Bookmarks', { title: 'Service Unavailable', status: 503, detail: 'The bookmark data is read-only.' }, 503);
    shiftB(refused.page);
    await refused.page.advance(50);
    assert.ok(toasts(refused.page).includes('The bookmark data is read-only.'));
    shiftB(refused.page);
    await refused.page.advance(50);
    assert.equal(refused.page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks').length, 2, 'the next press is a new request');
    assert.equal(page.errors.length + ask.page.errors.length + refused.page.errors.length, 0);
});

test('"Always ask for a name" reads the preferences and saves AskForName with the pause as the server has it; the next Shift+B follows it; a refused read or save puts the box back', { skip }, async () => {
    const { page } = await playing({ capabilities: { AskForName: true, AskForNameIsDefault: true, PauseWhileNaming: true } });
    // The pause was turned off on another device since this page read its capabilities (the dialog once
    // posted the page's copy, up to five minutes old, which turned it back on).
    page.respond('GET', '/Bookmarks/Preferences', { AskForName: null, PauseWhileNaming: false });
    shiftB(page);
    await page.advance(50);
    let dialog = nameDialog(page);
    const pauses = () => page.fetchLog.filter((f) => f.path === '/Sessions/s1/Playing/Pause').length;
    assert.equal(pauses(), 1, 'this dialog paused, by the page\'s copy');
    const box = dialog.querySelector('.jfbm-always-ask input[type="checkbox"]');
    assert.equal(box.checked, true, 'the server default is on');
    untick(page, box, false);
    await page.advance(20);
    const saved = page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Preferences');
    assert.equal(saved.length, 1);
    assert.deepEqual(saved[0].body, { AskForName: false, PauseWhileNaming: false }, 'POST replaces both: the pause as the server has it');
    const read = page.fetchLog.find((f) => f.method === 'GET' && f.path === '/Bookmarks/Preferences');
    assert.ok(read && page.fetchLog.indexOf(read) < page.fetchLog.indexOf(saved[0]), 'read first');
    assert.equal(box.disabled, false);
    page.key(page.document.body, 'Escape');
    await page.advance(50);

    shiftB(page);
    await page.advance(50);
    assert.equal(nameDialog(page), null, 'unticked: the next bookmark only toasts');
    assert.deepEqual(toasts(page), ['Bookmarked at 0:12.Name it']);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Bookmarks/Capabilities').length, 1, 'no capabilities request: the page\'s copy was updated');

    page.document.querySelector('.jfbm-toast-action').click();
    await page.advance(50);
    dialog = nameDialog(page);
    assert.equal(pauses(), 1, 'the page\'s copy of the pause followed the server too: this dialog did not pause');
    const again = dialog.querySelector('.jfbm-always-ask input[type="checkbox"]');
    assert.equal(again.checked, false);
    page.respond('POST', '/Bookmarks/Preferences', { title: 'Service Unavailable', status: 503, detail: 'The bookmark data is read-only.' }, 503);
    untick(page, again, true);
    await page.advance(20);
    assert.equal(again.checked, false, 'put back');
    assert.ok(toasts(page).includes('The bookmark data is read-only.'));

    // A refused read: nothing is posted, the box is put back.
    page.respond('GET', '/Bookmarks/Preferences', { title: 'Service Unavailable', status: 503, detail: 'The server is busy.' }, 503);
    untick(page, again, true);
    await page.advance(20);
    assert.equal(again.checked, false, 'put back');
    assert.ok(toasts(page).includes('The server is busy.'));
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Preferences').length, 2, 'no post without the read');
    page.key(page.document.body, 'Escape');
    await page.advance(50);
    shiftB(page);
    await page.advance(50);
    assert.equal(nameDialog(page), null, 'still not asking');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the suggestion: a generic chapter name or no chapter yet is none, SuggestChapterNames off looks nothing up, chapters are read once per video and a failed read is none', { skip }, async () => {
    const generic = { Id: 'item1', Name: 'Long Test Movie', Type: 'Movie', MediaType: 'Video', Chapters: [{ StartPositionTicks: 0, Name: 'Chapter 1' }, { StartPositionTicks: 100000000, Name: 'Chapter 2' }] };
    const { page } = await playing({ capabilities: { AskForName: true }, item: generic });
    let lookups = 0;
    const getItem = page.context.ApiClient.getItem;
    page.context.ApiClient.getItem = (userId, id) => {
        lookups++;
        assert.equal(userId, 'user-1', 'scoped to the user');
        return getItem(userId, id);
    };
    shiftB(page);
    await page.advance(50);
    assert.equal(nameInput(nameDialog(page)).value, '', 'Jellyfin\'s "Chapter 2" is never suggested');
    page.key(page.document.body, 'Escape');
    assert.equal(lookups, 1);

    shiftB(page);
    await page.advance(50);
    assert.equal(nameInput(nameDialog(page)).value, '');
    page.key(page.document.body, 'Escape');
    assert.equal(lookups, 1, 'the chapters are read once per video for the session');

    // Before the first chapter: none.
    page.context.ApiClient.getItem = (userId, id) => Promise.resolve({ Id: 'item2', Chapters: [{ StartPositionTicks: 500000000, Name: 'Late start' }] });
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item2', PositionTicks: 100000000 });
    shiftB(page);
    await page.advance(50);
    assert.equal(nameInput(nameDialog(page)).value, '');
    page.key(page.document.body, 'Escape');

    // A failed read: none, and the next bookmark asks again.
    let failed = 0;
    page.context.ApiClient.getItem = () => {
        failed++;
        return Promise.reject(new Error('offline'));
    };
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item3', PositionTicks: 100000000 });
    shiftB(page);
    await page.advance(50);
    assert.equal(nameInput(nameDialog(page)).value, '');
    page.key(page.document.body, 'Escape');
    shiftB(page);
    await page.advance(50);
    assert.equal(nameInput(nameDialog(page)).value, '');
    page.key(page.document.body, 'Escape');
    assert.equal(failed, 2);

    // SuggestChapterNames off: no lookup at all.
    const off = await playing({ capabilities: { AskForName: true, SuggestChapterNames: false } });
    let offLookups = 0;
    off.page.context.ApiClient.getItem = () => {
        offLookups++;
        return Promise.resolve(CHAPTERED_MOVIE);
    };
    shiftB(off.page);
    await off.page.advance(50);
    assert.equal(nameInput(nameDialog(off.page)).value, '');
    assert.equal(offLookups, 0);
    off.page.key(off.page.document.body, 'Escape');
    assert.equal(page.errors.length + off.page.errors.length, 0);
});

test('Pause while naming: the dialog pauses a playing video through the Sessions API and resumes it on close, not when the user resumed it or it was paused already; More… keeps the pause for the editor', { skip }, async () => {
    const { page, video } = await playing({ capabilities: { AskForName: true, PauseWhileNaming: true } });
    const pauses = () => page.fetchLog.filter((f) => f.path === '/Sessions/s1/Playing/Pause').length;
    const unpauses = () => page.fetchLog.filter((f) => f.path === '/Sessions/s1/Playing/Unpause').length;
    video.paused = false;

    shiftB(page);
    await page.advance(50);
    assert.ok(nameDialog(page));
    assert.equal(pauses(), 1, 'paused when the dialog opened');
    video.paused = true;
    page.key(page.document.body, 'Escape');
    await page.advance(50);
    assert.equal(unpauses(), 1, 'resumed on close');

    // The user resumed meanwhile: left playing.
    video.paused = false;
    shiftB(page);
    await page.advance(50);
    assert.equal(pauses(), 2);
    video.paused = false;
    page.key(page.document.body, 'Escape');
    await page.advance(50);
    assert.equal(unpauses(), 1, 'not resumed again');

    // Paused already: nothing sent either way.
    video.paused = true;
    shiftB(page);
    await page.advance(50);
    assert.equal(pauses(), 2);
    page.key(page.document.body, 'Escape');
    await page.advance(50);
    assert.equal(unpauses(), 1);

    // More…: the editor takes over; playback resumes when it closes, and not before: a Reload (after a 409) hands the
    // pause on to the editor it opens again (it once resumed under the reloaded editor).
    video.paused = false;
    shiftB(page);
    await page.advance(50);
    assert.equal(pauses(), 3);
    video.paused = true;
    nameDialog(page).querySelector('.jfbm-name-more').click();
    await page.advance(50);
    assert.equal(nameDialog(page), null);
    let editor = panel(page, 'Edit bookmark');
    assert.ok(editor, 'the full editor on the bookmark');
    assert.equal(page.document.activeElement, editor.querySelectorAll('input[type="text"]')[0], 'Name focused');
    assert.equal(unpauses(), 1, 'still paused under the editor');
    page.respond('POST', '/Bookmarks/b12', { title: 'Conflict', status: 409, detail: 'Changed elsewhere. Reload to see the latest.' }, 409);
    page.respond('GET', '/Bookmarks/b12', { Id: 'b12', ItemId: 'item1', StartTicks: 125000000, Name: 'Elsewhere', UpdatedAt: '2026-10-06T08:09:00.0000000Z', Clip: null });
    page.dispatch(editor.querySelector('form'), 'submit');
    await page.advance(50);
    editor.querySelector('.jfbm-reload').click();
    await page.advance(50);
    assert.equal(editor.isConnected, false, 'the editor closed');
    editor = panel(page, 'Edit bookmark');
    assert.equal(editor.querySelectorAll('input[type="text"]')[0].value, 'Elsewhere', 'and opened again on the latest');
    assert.equal(unpauses(), 1, 'still paused under the reloaded editor');
    page.key(page.document.body, 'Escape');
    await page.advance(50);
    assert.equal(panel(page, 'Edit bookmark'), null);
    assert.equal(unpauses(), 2, 'resumed when the editor closed');

    // A refused pause: nothing to undo.
    page.respond('POST', '/Sessions/s1/Playing/Pause', { title: 'Not Found', status: 404, detail: 'Session not found.' }, 404);
    video.paused = false;
    shiftB(page);
    await page.advance(50);
    video.paused = true;
    page.key(page.document.body, 'Escape');
    await page.advance(50);
    assert.equal(unpauses(), 2);

    // The command lands at the client over the websocket after the HTTP answer: an Escape before it
    // lands waits for the client's report of the pause, then resumes; without one within a second, a video still
    // playing is left alone (the user resumed it, or the pause never came).
    page.respond('POST', '/Sessions/s1/Playing/Pause', null, 204);
    video.paused = false;
    shiftB(page);
    await page.advance(50);
    assert.equal(pauses(), 5);
    page.key(page.document.body, 'Escape');
    await page.advance(50);
    assert.equal(unpauses(), 2, 'not yet: the pause has not landed');
    video.paused = true;
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 130000000, IsPaused: true });
    await page.advance(20);
    assert.equal(unpauses(), 3, 'resumed once the client reported the pause');

    video.paused = false;
    shiftB(page);
    await page.advance(50);
    assert.equal(pauses(), 6);
    page.key(page.document.body, 'Escape');
    await page.advance(1100);
    assert.equal(unpauses(), 3, 'no report within a second and still playing: left alone');

    // The pause had landed (the report came) before the dialog closed: resumed at once.
    video.paused = false;
    shiftB(page);
    await page.advance(50);
    assert.equal(pauses(), 7);
    video.paused = true;
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 130000000, IsPaused: true });
    page.key(page.document.body, 'Escape');
    await page.advance(10);
    assert.equal(unpauses(), 4);

    // A video that stopped under the dialog is never resumed.
    video.paused = false;
    shiftB(page);
    await page.advance(50);
    assert.equal(pauses(), 8);
    await page.context.ApiClient.reportPlaybackStopped({ ItemId: 'item1' });
    video.paused = true;
    page.key(page.document.body, 'Escape');
    await page.advance(1100);
    assert.equal(unpauses(), 4);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

// Jellyfin Enhanced's document capture-phase listener as measured in a sandbox, and jellyfin-web's OSD bubble listener:
// what each saw of the keys pressed.
function installPlayerListeners(page) {
    const seen = { je: [], osd: [] };
    page.document.addEventListener('keydown', (event) => {
        seen.je.push(event.key);
    }, true);
    page.document.addEventListener('keydown', (event) => {
        seen.osd.push(event.key);
    }, false);
    return seen;
}

test('scopedKeys: keys pressed inside the name dialog, the editor, the list and the delete confirmation never reach Jellyfin Enhanced\'s or the OSD\'s listeners, whatever has the focus', { skip }, async () => {
    const { page } = await playing({ capabilities: { AskForName: true }, list: [{ Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Note: 'one', Clip: null }] });
    const seen = installPlayerListeners(page);
    shiftB(page);
    await page.advance(50);
    const dialog = nameDialog(page);
    assert.deepEqual(seen.je, ['B'], 'the shortcut itself reached them, as it must');
    const input = nameInput(dialog);
    for (const key of ['B', 'a', '2', 'S', ' ']) {
        assert.equal(page.key(input, key), true, key + ' keeps its default action (typing)');
    }

    const save = dialog.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Save');
    save.focus();
    for (const key of ['S', 'R', 'O', '2', 'M', 'F', ' ', 'ArrowLeft']) {
        assert.equal(page.key(save, key), true, key + ' on Save keeps its default action');
    }

    const box = dialog.querySelector('.jfbm-always-ask input');
    box.focus();
    page.key(box, ' ');
    assert.deepEqual(seen.je, ['B'], 'Jellyfin Enhanced saw none of them');
    assert.deepEqual(seen.osd, ['B'], 'nor the OSD');
    assert.equal(page.key(save, 'Escape'), false);
    assert.equal(nameDialog(page), null);
    assert.deepEqual(seen.je, ['B']);

    // The editor (the range flow), the player's list and the delete confirmation: the same.
    const range = page.document.querySelector('.btnBookmarkRange');
    range.click();
    await page.context.ApiClient.reportPlaybackProgress({ ItemId: 'item1', PositionTicks: 300000000 });
    range.click();
    await page.advance(20);
    const editor = panel(page, 'New bookmark');
    const editorSave = editor.querySelectorAll('.jfbm-actions button').find((b) => b.textContent === 'Save');
    editorSave.focus();
    assert.equal(page.key(editorSave, 'O'), true);
    assert.equal(page.key(editor.querySelector('select'), 'ArrowDown'), true);
    assert.equal(page.key(editor.querySelectorAll('input[type="text"]')[0], 'Enter'), true, 'Enter in a field is the form\'s own submission (not prevented by us)');
    page.key(editorSave, 'Escape');
    assert.equal(panel(page, 'New bookmark'), null);

    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    const list = panel(page, 'Bookmarks');
    const jump = list.querySelector('.jfbm-time-button');
    jump.focus();
    assert.equal(page.key(jump, 'S'), true);
    list.querySelector('button[title="Delete"]').click();
    await page.advance(20);
    const confirm = panel(page, 'Delete bookmark?');
    const cancel = confirm.querySelectorAll('.jfbm-actions button')[0];
    cancel.focus();
    assert.equal(page.key(cancel, 'O'), true);
    page.key(cancel, 'Escape');
    assert.equal(panel(page, 'Delete bookmark?'), null);
    page.key(jump, 'Escape');
    assert.equal(page.document.querySelector('.jfbm-backdrop'), null);
    assert.deepEqual(seen.je, ['B'], 'none of the keys inside any of our dialogs reached Jellyfin Enhanced');
    assert.deepEqual(seen.osd, ['B']);

    // Outside our dialogs keys go where they went.
    page.key(page.document.body, 'S');
    assert.deepEqual(seen.je, ['B', 'S']);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('ShowPlayerButtons off: no buttons in the player while Shift+B still bookmarks; the buttons go when the setting changes', { skip }, async () => {
    const { page, buttons } = await playing({ capabilities: { ShowPlayerButtons: false } });
    assert.equal(buttons.querySelector('.btnBookmarkAdd'), null, 'no bookmark button');
    assert.deepEqual(buttons.children.map((b) => b.className), ['btnSubtitles', 'btnVideoOsdSettings'], 'jellyfin-web\'s own untouched');
    assert.equal(shiftB(page), false);
    await page.advance(50);
    assert.equal(page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks').length, 1, 'the shortcut works');
    assert.deepEqual(toasts(page), ['Bookmarked at 0:12.Name it']);
    assert.equal(page.fetchLog.filter((f) => f.path === '/Bookmarks/Capabilities').length, 1, 'the capabilities were read once');

    // On, then off at the next read (an admin changed the setting): the buttons are removed.
    const on = await playing();
    assert.ok(on.buttons.querySelector('.btnBookmarkAdd'));
    on.page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, PLAYER_CAPABILITIES, { ShowPlayerButtons: false }));
    await on.page.advance(5 * 60 * 1000 + 1);
    on.page.dispatch(on.osd, 'viewshow');
    await on.page.advance(500);
    assert.equal(on.buttons.querySelector('.btnBookmarkAdd'), null, 'gone');
    assert.equal(on.buttons.querySelector('.btnBookmarkRange'), null);
    assert.equal(on.buttons.querySelector('.btnBookmarkList'), null);
    assert.equal(on.buttons.children.length, 2);
    assert.equal(page.errors.length + on.page.errors.length, 0);
});

test('a failed capabilities read is kept ten seconds, not five minutes, and a session expiry forgets the capabilities', { skip }, async () => {
    const page = createPage();
    page.respond('GET', '/Bookmarks/Capabilities', { title: 'Service Unavailable', status: 503, detail: 'Starting up.' }, 503);
    page.respond('GET', '/Bookmarks', []);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: { item1: MOVIE } });
    await page.advance(50);
    const { osd } = playerPage(page);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 100000000 });
    await page.advance(400);
    const reads = () => page.fetchLog.filter((f) => f.path === '/Bookmarks/Capabilities').length;
    assert.equal(reads(), 1, 'one request, shared by the scanners');
    assert.ok(page.document.querySelector('.btnBookmarkAdd'), 'the fallback shows the buttons');

    await page.advance(5000);
    page.dispatch(osd, 'viewshow');
    await page.advance(400);
    assert.equal(reads(), 1, 'the fallback is kept for a while');

    page.respond('GET', '/Bookmarks/Capabilities', Object.assign({}, CAPABILITIES, PLAYER_CAPABILITIES, { ShowPlayerButtons: false }));
    await page.advance(5000);
    page.dispatch(osd, 'viewshow');
    await page.advance(400);
    assert.equal(reads(), 2, 'asked again after ten seconds');
    assert.equal(page.document.querySelector('.btnBookmarkAdd'), null, 'and followed the answer');

    // A good answer is kept the full five minutes.
    await page.advance(60 * 1000);
    page.dispatch(osd, 'viewshow');
    await page.advance(400);
    assert.equal(reads(), 2);

    // A session that expired (the kit's event on a 401): the capabilities are forgotten; the next read is a fresh one.
    page.dispatch(page.document, 'jfbm:session-expired');
    page.dispatch(osd, 'viewshow');
    await page.advance(400);
    assert.equal(reads(), 3);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the player list\'s empty text names the combination in effect', { skip }, async () => {
    const { page } = await playing();
    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    assert.equal(panel(page, 'Bookmarks').querySelector('.jfbm-empty').textContent, 'No bookmarks yet. Press Shift+B or the bookmark button while watching.');
    page.key(page.document.body, 'Escape');

    page.context.localStorage.setItem('jfbm:shortcut:bookmarkNow', JSON.stringify(''));
    page.document.querySelector('.btnBookmarkList').click();
    await page.advance(50);
    assert.equal(panel(page, 'Bookmarks').querySelector('.jfbm-empty').textContent, 'No bookmarks yet. Press the bookmark button while watching.');
    page.key(page.document.body, 'Escape');
});

// -------------------------------------------------------------------------------------------------- anchors and health

function healthPosts(page) {
    return page.fetchLog.filter((f) => f.method === 'POST' && f.path === '/Bookmarks/Diagnostics/ClientHealth');
}

test('with every anchor there, the health check stays quiet: no warning and no report', { skip }, async () => {
    const page = await start();
    page.respond('POST', '/Bookmarks/Diagnostics/ClientHealth', null, 204);
    detailsPage(page);
    await page.advance(15000);
    const osd = playerPage(page, { slider: true });
    page.context.location.hash = '#/video';
    page.dispatch(osd.osd, 'viewshow');
    await page.advance(15000);
    assert.deepEqual(plain(page.context.__jfkit.jfbm.health.missing()), []);
    assert.equal(healthPosts(page).length, 0);
    assert.deepEqual(page.warnings, []);
});

test('a missing button row (detailButtons): no Add bookmark button, the section still works, one warning and one report', { skip }, async () => {
    const page = await start();
    page.respond('POST', '/Bookmarks/Diagnostics/ClientHealth', null, 204);
    const content = page.element('div', { class: 'detailPageContent' });
    const view = page.element('div', { id: 'itemDetailPage', class: 'page' }, [page.element('div', { class: 'mainDetailButtonsRenamed' }), content]);
    page.document.body.append(view);
    page.context.location.hash = '#/details?id=item1&serverId=server-1';
    page.dispatch(view, 'viewshow');
    await page.advance(400);
    assert.equal(page.document.querySelector('.btnBookmarkItem'), null, 'the feature that needs the row is off');
    assert.equal(content.firstElementChild.querySelector('h2').textContent, 'My bookmarks (2)', 'the section doesn\'t need it');

    await page.advance(4000);
    assert.deepEqual(page.warnings, [
        '[jfbm] web anchor "detailButtons" missing on details (web 10.11.5)',
        '[jfbm] web anchor "detailButtonsBefore" missing on details (web 10.11.5)'
    ]);
    await page.advance(2500);
    const sent = healthPosts(page);
    assert.equal(sent.length, 1);
    assert.deepEqual(plain(sent[0].body), {
        WebVersion: '10.11.5',
        Client: 'Jellyfin Web',
        ClientVersion: '10.11.5',
        Layout: 'Desktop',
        Experimental: false,
        PluginVersion: '1.2.2.0',
        Missing: [{ Anchor: 'detailButtons', View: 'details', Count: 1 }, { Anchor: 'detailButtonsBefore', View: 'details', Count: 1 }]
    });

    // Another visit warns no more and waits for the ten minutes.
    page.context.location.hash = '#/details?id=item2&serverId=server-1';
    page.dispatch(view, 'viewshow');
    await page.advance(7000);
    assert.equal(page.warnings.length, 2);
    assert.equal(healthPosts(page).length, 1);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('a missing OSD button row (osdButtons): no player buttons, but B still bookmarks, and the player view is reported with its slider', { skip }, async () => {
    const page = await start();
    page.respond('POST', '/Bookmarks/Diagnostics/ClientHealth', null, 204);
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b9', Clip: null }, entry.body), 201);
    const osd = page.element('div', { id: 'videoOsdPage', class: 'page' }, [page.element('div', { class: 'videoOsdBottomRenamed' }, [page.element('div', { class: 'buttons' })])]);
    page.document.body.append(osd);
    page.context.location.hash = '#/video';
    page.dispatch(osd, 'viewshow');
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 100000000 });
    await page.advance(400);
    assert.equal(page.document.querySelector('.btnBookmarkAdd'), null);

    page.key(page.document.body, 'B', { shiftKey: true });
    await page.advance(50);
    assert.ok(page.fetchLog.some((f) => f.method === 'POST' && f.path === '/Bookmarks'), 'Shift+B still works');

    await page.advance(7000);
    assert.deepEqual(page.warnings, [
        '[jfbm] web anchor "osdButtons" missing on player (web 10.11.5)',
        '[jfbm] web anchor "osdPositionSlider" missing on player (web 10.11.5)'
    ], 'the renamed OSD has no slider either: no timeline marks');
    assert.deepEqual(plain(healthPosts(page)[0].body.Missing), [{ Anchor: 'osdButtons', View: 'player', Count: 1 }, { Anchor: 'osdPositionSlider', View: 'player', Count: 1 }]);
});

// The player's timeline marks (41-timeline.js): the video's bookmarks on the position slider. TIMELINE_MOVIE plays its
// first version, 80 s long, so b1 (0:15) is at 18.75 % and b2 (0:30-0:40) from 37.5 % to 50 %.
const TIMELINE_MOVIE = Object.assign({}, MOVIE, { RunTimeTicks: 1000000000, MediaSources: [{ Id: 'ms1', RunTimeTicks: 800000000 }, { Id: 'ms2', RunTimeTicks: 1000000000 }] });

function timelineLayer(slider) {
    return slider.parentNode.querySelector(':scope > .jfbm-timeline');
}

function timelineMarks(slider) {
    const layer = timelineLayer(slider);
    return layer ? layer.children.map((m) => [m.className, m.getAttribute('data-id'), m.style.left, m.style.right || null, m.getAttribute('title')]) : null;
}

function timelineReads(page) {
    return page.fetchLog.filter((f) => f.method === 'GET' && f.path === '/Bookmarks').map((f) => f.query.itemId);
}

test('the timeline marks the playing video\'s bookmarks on the position slider: a tick at each start, a range\'s band, the titles as tooltips, a click plays from there', { skip }, async () => {
    const { page, slider } = await playing({ slider: true, list: bookmarks(), item: TIMELINE_MOVIE });

    const layer = timelineLayer(slider);
    assert.ok(layer, 'a layer of our own');
    assert.equal(slider.parentNode.children.slice(-1)[0], layer, 'after the slider, not in the chapter marks\' container (the slider rewrites that)');
    assert.equal(slider.parentNode.querySelector('.sliderMarkerContainer').children.length, 0);
    assert.equal(layer.getAttribute('aria-hidden'), 'true');
    assert.deepEqual(timelineMarks(slider), [
        ['jfbm-timeline-range', 'b2', '37.500%', '50.000%', null],
        ['jfbm-timeline-mark', 'b1', '18.750%', null, '<b>webcheck</b> (0:15)'],
        ['jfbm-timeline-mark jfbm-timeline-mark-range', 'b2', '37.500%', null, 'Range 0:30–0:40']
    ], 'placed by the playing version\'s runtime (ms1, 80 s), the bands under the ticks');
    assert.deepEqual(timelineReads(page), ['item1']);
    assert.deepEqual(page.lookups, ['item1']);

    // Scans redraw nothing while nothing changed: the ones its own drawing causes, and every later one.
    const tick = layer.querySelector('.jfbm-timeline-mark');
    page.document.body.append(page.element('div'));
    await page.advance(400);
    assert.equal(layer.querySelector('.jfbm-timeline-mark'), tick);
    assert.deepEqual(timelineReads(page), ['item1']);

    tick.click();
    await page.advance(50);
    const seek = page.fetchLog.find((f) => f.path === '/Sessions/s1/Playing/Seek');
    assert.deepEqual(seek.query, { SeekPositionTicks: '150000000' });
    assert.equal(page.errors.length + page.warnings.length, 0, page.errors.concat(page.warnings).join('\n'));
});

test('the timeline follows the bookmarks: a new one is marked at once, a new playback reads them again, another video has its own, and the marks go when playback stops', { skip }, async () => {
    const list = [];
    const { page, slider } = await playing({ slider: true, list: [], item: TIMELINE_MOVIE });
    page.respond('GET', '/Bookmarks', () => list);
    page.respond('POST', '/Bookmarks', (entry) => {
        const created = Object.assign({ Id: 'b9', ItemId: 'item1', EndTicks: null, Clip: null }, entry.body);
        list.push(created);
        return created;
    }, 201);
    assert.deepEqual(timelineMarks(slider), [], 'no bookmarks yet: an empty layer');

    // Shift+B at 0:12.5 (42 s on the video's clock at the report, 44.5 s now).
    page.key(page.document.body, 'B', { shiftKey: true });
    await page.advance(400);
    assert.deepEqual(timelineMarks(slider).map((m) => m[1] + ' ' + m[2] + ' ' + m[4]), ['b9 15.625% Bookmark at 0:12'], 'read again after the change');

    // A change made elsewhere shows at the next playback.
    list.push(...bookmarks());
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 0, MediaSourceId: 'ms2' });
    await page.advance(400);
    assert.deepEqual(timelineMarks(slider).map((m) => m[1] + ' ' + m[2]), ['b2 30.000%', 'b9 12.500%', 'b1 15.000%', 'b2 30.000%'], 'ms2 is 100 s long');
    assert.deepEqual(timelineReads(page), ['item1', 'item1', 'item1']);

    // Another video, without versions: its own runtime.
    page.context.ApiClient.getItem = (userId, id) => Promise.resolve({ Id: id, RunTimeTicks: 600000000 });
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item2', PositionTicks: 0 });
    await page.advance(400);
    assert.deepEqual(timelineReads(page), ['item1', 'item1', 'item1', 'item2']);
    assert.deepEqual(timelineMarks(slider).map((m) => m[1] + ' ' + m[2]), ['b2 50.000%', 'b9 20.833%', 'b1 25.000%', 'b2 50.000%']);

    await page.context.ApiClient.reportPlaybackStopped({ ItemId: 'item2', PositionTicks: 0 });
    await page.advance(400);
    assert.deepEqual(timelineMarks(slider), []);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});

test('the timeline marks only shown player views: an earlier, hidden one first in the document is left alone, and without a slider nothing is read', { skip }, async () => {
    const page = await start({ capabilities: PLAYER_CAPABILITIES, list: bookmarks() });
    page.lookups = [];
    page.context.ApiClient.getItem = (userId, id) => {
        page.lookups.push(id);
        return Promise.resolve(TIMELINE_MOVIE);
    };
    const earlier = playerPage(page, { slider: true });
    earlier.osd.classList.add('hide');
    const shown = playerPage(page, { slider: true });
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item1', PositionTicks: 0, MediaSourceId: 'ms1' });
    await page.advance(400);
    assert.equal(timelineLayer(earlier.slider), null);
    assert.equal(timelineMarks(shown.slider).length, 3);

    // The web client shows the earlier view again: it is marked on its viewshow.
    shown.osd.classList.add('hide');
    earlier.osd.classList.remove('hide');
    page.dispatch(earlier.osd, 'viewshow');
    await page.advance(400);
    assert.equal(timelineMarks(earlier.slider).length, 3);
    assert.deepEqual(timelineReads(page), ['item1'], 'the same video: read once');
    assert.deepEqual(page.lookups, ['item1']);

    // A player without a slider (a jellyfin-web that renamed it) reads nothing for the timeline.
    const bare = await playing({ list: bookmarks(), item: TIMELINE_MOVIE });
    assert.deepEqual(timelineReads(bare.page), []);
    assert.deepEqual(bare.page.lookups, []);
    assert.equal(page.errors.length + bare.page.errors.length, 0);
});

test('the timeline leaves out what it can\'t place: no runtime, a start past the end; a range past the end stops there; a failed read is tried again after 30 s', { skip }, async () => {
    const list = [
        { Id: 'b1', ItemId: 'item1', StartTicks: 150000000, EndTicks: null, Name: 'Inside', Clip: null },
        { Id: 'b2', ItemId: 'item1', StartTicks: 700000000, EndTicks: 900000000, Name: 'Over the end', Clip: null },
        { Id: 'b3', ItemId: 'item1', StartTicks: 850000000, EndTicks: null, Name: 'Past the end', Clip: null }
    ];
    const { page, slider } = await playing({ slider: true, list, item: TIMELINE_MOVIE });
    assert.deepEqual(timelineMarks(slider), [
        ['jfbm-timeline-range', 'b2', '87.500%', '0.000%', null],
        ['jfbm-timeline-mark', 'b1', '18.750%', null, 'Inside (0:15)'],
        ['jfbm-timeline-mark jfbm-timeline-mark-range', 'b2', '87.500%', null, 'Over the end (1:10–1:30)']
    ]);

    // A video without a runtime (live TV): no marks.
    page.context.ApiClient.getItem = (userId, id) => Promise.resolve({ Id: id });
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item2', PositionTicks: 0 });
    await page.advance(400);
    assert.deepEqual(timelineMarks(slider), []);

    // A failed read: no marks, a warning, and no new request at every scan; the next try is 30 s later.
    page.context.ApiClient.getItem = (userId, id) => Promise.resolve(TIMELINE_MOVIE);
    page.respond('GET', '/Bookmarks', { title: 'Down' }, 500);
    await page.context.ApiClient.reportPlaybackStart({ ItemId: 'item3', PositionTicks: 0, MediaSourceId: 'ms1' });
    await page.advance(400);
    assert.deepEqual(timelineMarks(slider), []);
    assert.equal(page.warnings.length, 1, page.warnings.join('\n'));
    const failedReads = timelineReads(page).length;
    page.document.body.append(page.element('div'));
    await page.advance(10000);
    page.document.body.append(page.element('div'));
    await page.advance(400);
    assert.equal(timelineReads(page).length, failedReads);

    page.respond('GET', '/Bookmarks', list);
    await page.advance(20000);
    page.document.body.append(page.element('div'));
    await page.advance(400);
    assert.equal(timelineReads(page).length, failedReads + 1);
    assert.equal(timelineMarks(slider).length, 3);
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});
