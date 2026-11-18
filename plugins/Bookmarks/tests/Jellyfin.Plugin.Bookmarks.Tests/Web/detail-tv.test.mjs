// Bookmarks' hook of the kit's Jellyfin Enhanced TV mitigation (kit.je.tvSectionJump, for Jellyfin Enhanced 12.14's TV
// trap) on the details page, in the kit's fake page with the built ClientScript. PageHostingTests
// runs it:
//   KIT_CLIENT_SCRIPT=<file> KIT_PREFIX=jfbm node --test plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Web/detail-tv.test.mjs
// On the TV layout, while Jellyfin Enhanced's closed Reviews <details> is on the page and the Bookmarks row (since
// 1.6.0 / 2.4.0; "My bookmarks" before) or the Clips row is shown, a "Bookmarks" button sits in the buttons row right
// before More (after Add bookmark); OK on it focuses the first row's first control. Nothing on the desktop layout, nor
// while the video has neither bookmarks nor clips (both rows hidden). The kit's own tests
// (kit/web/tests/kit.test.mjs) cover the rest: the toggle, the tabindex kept and put back, a late drawing.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

const file = process.env.KIT_CLIENT_SCRIPT;
const skip = !file ? 'KIT_CLIENT_SCRIPT is not set' : false;
const source = skip ? '' : fs.readFileSync(file, 'utf8');

const MOVIE = { Id: 'item1', Name: 'Tagged Movie', Type: 'Movie', MediaType: 'Video', IsFolder: false, LocationType: 'FileSystem' };
const CAPABILITIES = {
    ClipsEnabled: true,
    CanCreateClips: true,
    MaxClipDurationSeconds: 600,
    DefaultClipMode: 'Accurate',
    IsAdmin: false,
    StoreReadOnly: false,
    MaxNameLength: 100,
    MaxNoteLength: 500,
    Pages: { Bookmarks: true, Clips: true }
};
const BOOKMARKS = [{ Id: 'b1', ItemId: 'item1', StartTicks: 100000000, EndTicks: 200000000, Name: 'Bank job', Note: null, Clip: null }];

async function start(options) {
    const o = options || {};
    const page = createPage();
    if (o.tv) {
        page.document.documentElement.classList.add('layout-tv');
    }

    page.respond('GET', '/Bookmarks/Capabilities', CAPABILITIES);
    page.respond('GET', '/Bookmarks', o.list || BOOKMARKS);
    page.respond('GET', '/Bookmarks/Clips', { Items: o.clips || [], TotalRecordCount: (o.clips || []).length, StartIndex: 0 });
    page.respond('GET', '/Bookmarks/Clips/Media', []);
    page.run(source, file);
    await page.advance(50);
    page.installApiClient({ userId: 'user-1', items: { item1: MOVIE } });
    await page.advance(50);
    return page;
}

// jellyfin-web 10.11.5's details view with Jellyfin Enhanced 12.14's closed Reviews section in its content.
function detailsPage(page) {
    const more = page.element('button', { class: 'btnMoreCommands' });
    const buttons = page.element('div', { class: 'mainDetailButtons' }, [page.element('button', { class: 'btnPlay' }), more]);
    const write = page.element('button', { class: 'je-review-btn je-review-write-btn' });
    const reviews = page.element('details', { class: 'detailSection tmdb-reviews-section' }, [page.element('summary', { class: 'sectionTitle' }), page.element('div', { class: 'je-review-action-bar' }, [write])]);
    const content = page.element('div', { class: 'detailPageContent' }, [page.element('div', { class: 'detailSectionContent' }, [reviews])]);
    const view = page.element('div', { id: 'itemDetailPage', class: 'page' }, [buttons, content]);
    page.document.body.append(view);
    page.context.location.hash = '#/details?id=item1&serverId=server-1';
    page.dispatch(view, 'viewshow');
    return { view, buttons, more, reviews, write, content };
}

test('TV layout with JE\'s closed reviews: a Bookmarks button after Add bookmark, before More, and OK on it focuses the Bookmarks row\'s first control', { skip }, async () => {
    const page = await start({ tv: true });
    const dom = detailsPage(page);
    await page.advance(1000);
    const section = dom.view.querySelector('.jfbm-section');
    assert.ok(section && !section.classList.contains('hide'), 'the Bookmarks row shown');
    const card = section.querySelector('.jfbm-bookmark-card');
    assert.equal(card.className, 'card overflowBackdropCard show-focus jfbm-rowcard jfbm-bookmark-card focusable', 'a TV card, the focus stop itself');
    assert.equal(card.getAttribute('tabindex'), '0');
    assert.equal(card.querySelectorAll('button').length, 0, 'no buttons inside it: OK plays, the menu key opens its menu');
    const jump = dom.buttons.querySelector('.jfbm-je-jump');
    assert.ok(jump, 'the Bookmarks button');
    assert.equal(jump.getAttribute('title'), 'Bookmarks');
    assert.equal(jump.querySelector('.detailButton-icon').classList.contains('bookmarks'), true);
    assert.equal(jump.nextSibling, dom.more, 'right before More');
    const row = dom.buttons.children;
    assert.equal(row[row.indexOf(jump) - 1].classList.contains('btnBookmarkItem'), true, 'after Add bookmark');
    assert.equal(dom.write.getAttribute('tabindex'), '-1', 'the reviews\' hidden button out of the focus order');

    jump.click();
    const first = section.querySelectorAll('a, button').find((e) => e.getClientRects().length > 0);
    assert.equal(page.document.activeElement, first, 'the row\'s first shown control (Manage, in its heading)');
    assert.equal(first.textContent, 'Manage');
});

test('no Bookmarks button on the desktop layout, nor while the video has no bookmarks', { skip }, async () => {
    const desktop = await start();
    const dom = detailsPage(desktop);
    await desktop.advance(1000);
    assert.ok(dom.view.querySelector('.jfbm-section:not(.hide)'));
    assert.equal(dom.buttons.querySelector('.jfbm-je-jump'), null);
    assert.equal(dom.write.getAttribute('tabindex'), null, 'JE\'s reviews untouched');

    const empty = await start({ tv: true, list: [] });
    const none = detailsPage(empty);
    await empty.advance(1000);
    assert.ok(none.view.querySelector('.jfbm-section.hide'), 'the section hidden: no bookmarks');
    assert.ok(none.buttons.querySelector('.btnBookmarkItem'), 'Add bookmark is there');
    assert.equal(none.buttons.querySelector('.jfbm-je-jump'), null);
    assert.equal(none.write.getAttribute('tabindex'), '-1', 'the trap is still taken away for what lies below');
});

test('TV layout with clips but no bookmarks: the Bookmarks button goes to the Clips row', { skip }, async () => {
    const clip = {
        Id: 'b9', BookmarkDeleted: true, Name: 'Kept', ItemId: 'item1', ItemName: 'Tagged Movie', ItemType: 'Movie', ItemAvailable: true,
        BookmarkStartTicks: 100000000, BookmarkEndTicks: 200000000, UpdatedAt: '2026-11-01T10:00:00Z',
        Clip: { Status: 'Completed', State: 'Ready', StartTicks: 100000000, EndTicks: 200000000, FileName: 'Kept.mp4', FileSize: 10, FileExists: true }
    };
    const page = await start({ tv: true, list: [], clips: [clip] });
    const dom = detailsPage(page);
    await page.advance(1000);
    assert.ok(dom.view.querySelector('.jfbm-section.hide'), 'no bookmarks: the Bookmarks row hidden');
    const clips = dom.view.querySelector('.jfbm-detail-clips');
    assert.equal(clips.classList.contains('hide'), false);
    const jump = dom.buttons.querySelector('.jfbm-je-jump');
    assert.ok(jump, 'the Bookmarks button, for the Clips row');
    jump.click();
    assert.equal(page.document.activeElement.textContent, 'My Clips', 'the Clips row\'s first control');
    assert.ok(clips.contains(page.document.activeElement));
});
