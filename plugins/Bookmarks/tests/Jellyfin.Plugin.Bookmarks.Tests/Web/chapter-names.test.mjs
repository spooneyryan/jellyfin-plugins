// The name dialog's chapter suggestion on the built ClientScript, in the kit's fake page: every case of
// chapter-names.json (the sandbox names, the generic shapes seen on a server in use, and real titles)
// goes through the whole flow, a quick bookmark with AskForName on at a position inside a one-chapter video, and the
// dialog's field must hold the title (a real one) or nothing (a generic one). ChapterSuggestionTests runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/chapter-names.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');
const fixture = JSON.parse(fs.readFileSync(new URL('./chapter-names.json', import.meta.url), 'utf8'));

const CAPABILITIES = {
    ClipsEnabled: false, CanCreateClips: false, MaxClipDurationSeconds: 0, DefaultClipMode: 'Accurate', IsAdmin: false, StoreReadOnly: false,
    MaxNameLength: 100, MaxNoteLength: 500, Shortcuts: { BookmarkNow: 'Shift+B' }, ReservedShortcuts: [], ShowPlayerButtons: true,
    AskForName: true, AskForNameIsDefault: false, PauseWhileNaming: false, SuggestChapterNames: true, Pages: { Bookmarks: false, Clips: false }
};

test('the fixture holds the generated word lists and its names', () => {
    assert.equal(fixture.words.withNumber.length, 67, 'the 86 cultures with a {0} format reduce to 67 words');
    assert.equal(fixture.words.withoutNumber.length, 3);
    assert.ok(fixture.cases.length >= 40);
    for (const name of ['Opening', 'Bank job', 'Chase', 'Chapter 4', 'Capítulo 5', '00:00:40,000', 'Chapter 1-2', 'Глава 01', '(01)00:00:00:000', 'Chapter 1.']) {
        assert.ok(fixture.cases.some((c) => c.name === name), name);
    }
});

test('every case: a real title is suggested, a generic name leaves the field empty', { skip }, async () => {
    const page = createPage();
    const items = {};
    page.respond('GET', '/Bookmarks/Capabilities', CAPABILITIES);
    page.respond('GET', '/Bookmarks', []);
    page.respond('POST', '/Bookmarks', (entry) => Object.assign({ Id: 'b-' + entry.body.ItemId, UpdatedAt: '2026-10-06T08:00:00.0000000Z', Clip: null }, entry.body), 201);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items });
    const buttons = page.element('div', { class: 'buttons' }, [page.element('button', { class: 'btnSubtitles' })]);
    const osd = page.element('div', { id: 'videoOsdPage', class: 'page' }, [page.element('div', { class: 'videoOsdBottom' }, [buttons])]);
    const video = page.element('video');
    video.currentTime = 5;
    page.document.body.append(page.element('div', { class: 'videoPlayerContainer' }, [video]), osd);
    await page.advance(400);

    const results = [];
    for (let i = 0; i < fixture.cases.length; i++) {
        const c = fixture.cases[i];
        const itemId = 'c' + i;
        items[itemId] = { Id: itemId, Name: 'Case ' + i, Type: 'Movie', MediaType: 'Video', Chapters: [{ StartPositionTicks: 0, Name: c.name }, { StartPositionTicks: 600000000, Name: 'Later' }] };
        await page.context.ApiClient.reportPlaybackStart({ ItemId: itemId, PositionTicks: 50000000 });
        assert.equal(page.key(page.document.body, 'B', { shiftKey: true }), false, 'Shift+B handled for ' + JSON.stringify(c.name));
        await page.advance(50);
        const dialog = page.document.querySelector('.jfbm-backdrop .jfbm-panel');
        assert.ok(dialog, 'the name dialog opened for ' + JSON.stringify(c.name));
        const input = dialog.querySelector('input[type="text"]');
        const expected = c.generic ? '' : c.name.normalize('NFC').trim();
        results.push({ name: c.name, generic: c.generic, got: input.value });
        assert.equal(input.value, expected, JSON.stringify(c.name) + ': ' + c.note);
        assert.equal(page.document.activeElement, input);
        page.key(input, 'Escape');
        assert.equal(page.document.querySelector('.jfbm-backdrop'), null, 'Escape skipped');
        await page.advance(20);
    }

    assert.equal(results.length, fixture.cases.length);
    assert.equal(page.fetchLog.filter((f) => f.path.endsWith('/Name')).length, 0, 'nothing was named');
    assert.equal(page.errors.length, 0, page.errors.join('\n'));
});
