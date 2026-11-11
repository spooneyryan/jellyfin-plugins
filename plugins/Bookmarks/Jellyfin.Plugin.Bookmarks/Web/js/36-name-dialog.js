// The player's name dialog: the compact dialog a quick bookmark gets its name in, the
// chapter-name suggestion with its rule for Jellyfin's generic names, the "Always ask for a name" preference
// and the optional pause while the dialog is open. Every Bookmarks dialog has the kit's scopedKeys on since 1.1.0:
// a key pressed with the focus anywhere inside one of our dialogs never reaches Jellyfin Enhanced's or
// jellyfin-web's player shortcuts, whatever has the focus (a button or a <select> included).

// Jellyfin names every chapter without a title, every chapter whose title parses as a time and every chapter it makes
// itself (DummyChapterDuration) with its localised ChapterNameValue in the server's UI culture at scan time ("Chapter 3",
// "Kapitel 3"), and rippers embed such names too ("Chapter 01", "Capítulo 5", "00:00:40,000"). The rule: a name is
// generic when it is empty, a time, nothing but numbers, punctuation and symbols, or, stripped of numbers, punctuation,
// symbols and spaces and lower-cased, one of the words of the 86 cultures' "{0}" formats (for a name with a digit) or
// one of the 3 formats without "{0}" (for one without). The two lists are derived from Jellyfin 10.11.5's
// Localization/Core/*.json; a Bookmarks test keeps them equal to its fixture of them (Fixtures/chapter-name-words.json).
const CHAPTER_WORDS_WITH_NUMBER = ['bab', 'bagian', 'bölüm', 'caibidil', 'capitolo', 'capitolul', 'capítol', 'capítulo', 'chapit', 'chapitre',
    'chapter', 'chaptra', 'chibolim', 'chikamu', 'didanedi', 'hoofdstuk', 'hoofstuk', 'jelenet', 'kabanata', 'kafli', 'kapitel',
    'kapitlu', 'kapitola', 'kapittel', 'kapittul', 'kapitulua', 'kappale', 'nodaļa', 'peatükk', 'pennod', 'phâncảnh', 'piece',
    'poglavje', 'poglavlje', 'rozdział', 'sahna', 'scena', 'ĉapitro', 'κεφάλαιο', 'глава', 'дел', 'поглавље', 'раздзел', 'рбүлэг',
    'сцена', 'գլուխ', 'פרק', 'الفصل', 'باب', 'قسمت', 'अध्याय', 'धडा', 'অধ্যায়', 'ਚੈਪਟਰ', 'ବିଭାଗ', 'அத்தியாயம்', 'ಅಧ್ಯಾಯ', 'അധ്യായം',
    'บทที่', 'အခန်း', 'თავი', 'ជំពូក', 'チャプター', '章節', '章节', '第章', '챕터'];
const CHAPTER_WORDS_WITHOUT_NUMBER = ['kapituj', 'باب', 'అధ్యాయం'];
// Times as some rippers write them; Jellyfin itself renames only what TimeSpan.TryParse accepts ("00:00:40,000" stays).
const TIME_LIKE_CHAPTER_NAME = /^\d{1,2}(:\d{2}){1,2}([.,]\d{1,7})?$/;
const CHAPTER_NAME_NOISE = /[\p{N}\p{P}\p{S}\p{Z}\s]+/gu;
const NAME_IT_TOAST_MS = 8000;

// Whether a chapter name is one Jellyfin or a ripper made up, which is never offered as a bookmark name.
// "Bank job 2", "Part 2" and "Chapter 1 - Opening" are real titles; "Chapter 1-2", "Глава 01",
// "(01)00:00:00:000" and "Chapter 1." are not.
function isGenericChapterName(name) {
    if (typeof name !== 'string' || !name.trim()) {
        return true;
    }

    const normalized = name.normalize('NFC').trim();
    if (TIME_LIKE_CHAPTER_NAME.test(normalized)) {
        return true;
    }

    const word = normalized.replace(CHAPTER_NAME_NOISE, '').toLocaleLowerCase('und');
    if (word === '') {
        return true;
    }

    return /\p{N}/u.test(normalized)
        ? CHAPTER_WORDS_WITH_NUMBER.indexOf(word) >= 0
        : CHAPTER_WORDS_WITHOUT_NUMBER.indexOf(word) >= 0;
}

// The chapter covering a position: the last one starting at or before it; null before the first.
function chapterAt(chapters, ticks) {
    let found = null;
    (chapters || []).forEach(function (chapter) {
        if (chapter && typeof chapter.StartPositionTicks === 'number' && chapter.StartPositionTicks <= ticks
            && (!found || chapter.StartPositionTicks >= found.StartPositionTicks)) {
            found = chapter;
        }
    });
    return found;
}

// The name to suggest for a position: the covering chapter's title when it is a real one, else '' (the dialog opens with
// an empty field; on a server with 15-minute dummy chapters that is the usual case).
function suggestedChapterName(chapters, ticks) {
    const chapter = chapterAt(chapters, ticks);
    return chapter && !isGenericChapterName(chapter.Name) ? chapter.Name.normalize('NFC').trim() : '';
}

// The video through ApiClient.getItem (GET /Items/{id}, scoped to the user), read once per video for the session:
// the chapters the suggestion comes from and the runtime the player's timeline marks are placed by (41-timeline.js). A
// failed read is null and is tried again next time.
function readItem(itemId) {
    let cached = state.items.get(itemId);
    if (!cached) {
        cached = Promise.resolve().then(function () {
            return kit.client().getItem(kit.user.id(), itemId);
        }).then(function (item) {
            return item || null;
        }, function (e) {
            report(e);
            state.items.delete(itemId);
            return null;
        });
        state.items.set(itemId, cached);
    }

    return cached;
}

// The video's chapters (Chapters is always there, [] without any); a failed read is no suggestion.
async function getChapters(itemId) {
    const item = await readItem(itemId);
    return item && Array.isArray(item.Chapters) ? item.Chapters : [];
}

// The suggestion for a bookmark's position, or '' while the server's SuggestChapterNames is off (no lookup then).
async function chapterSuggestion(itemId, ticks, capabilities) {
    if (capabilities && capabilities.SuggestChapterNames === false) {
        return '';
    }

    return suggestedChapterName(await getChapters(itemId), ticks);
}

// The dialog's "Always ask for a name": the user's own AskForName, saved with
// POST /Bookmarks/Preferences, which replaces both preferences, so the pause is read from the server first
// (GET /Bookmarks/Preferences) and sent back as it is there, never as this page last saw it (the capabilities can be
// five minutes old, or the fallback of a failed read; another device may have changed the pause meanwhile); the
// capabilities this page holds follow both (the server resolves them the same way on its next answer).
async function setAlwaysAsk(capabilities, value) {
    const preferences = await api('GET', 'Bookmarks/Preferences');
    const pause = !!preferences && preferences.PauseWhileNaming === true;
    await api('POST', 'Bookmarks/Preferences', { AskForName: !!value, PauseWhileNaming: pause });
    [capabilities, state.capabilities].forEach(function (c) {
        if (c) {
            c.AskForName = !!value;
            c.AskForNameIsDefault = false;
            c.PauseWhileNaming = pause;
        }
    });
}

// Whether this browser's video is playing: the video element when the player has one, else the last playback report.
function isPlaying() {
    const video = findVideoElement();
    if (video) {
        return !video.paused && !video.ended;
    }

    return !!state.playback && !state.playback.isPaused;
}

// How long the undo of a pause waits for the client's report of it before deciding.
const PAUSE_LANDS_MS = 1000;
// Each pause sent; an undo whose pause a later one superseded (a new dialog's) leaves the resume to that one.
let pauseGeneration = 0;

// The pause while a dialog is open, when the video is playing: sends the Pause command and answers
// the function that undoes it when the dialog closes (null when nothing was sent, or the command was refused). The
// command lands at the client over the websocket after the HTTP answer, so the undo first waits for the client's own
// report of the pause (a playback report with IsPaused, or the video element paused), or PAUSE_LANDS_MS, before it
// decides: a video paused then is resumed, one the user set playing again meanwhile is left alone, and an Escape
// pressed in that window never leaves a late pause behind. A video that stopped is never resumed.
function pausePlayback() {
    if (!isPlaying()) {
        return Promise.resolve(null);
    }

    const generation = ++pauseGeneration;
    let landed = false;
    let stopped = false;
    let onLanded = null;
    const stop = kit.playback.onReport(function (kind, info) {
        if (kind === 'stopped') {
            stopped = true;
        }

        if (kind === 'stopped' || (info && info.IsPaused === true)) {
            landed = true;
            if (onLanded) {
                onLanded();
            }
        }
    });
    return kit.playback.pause().then(function () {
        return function () {
            const wait = landed || !isPlaying()
                ? Promise.resolve()
                : new Promise(function (resolve) {
                    const timer = setTimeout(resolve, PAUSE_LANDS_MS);
                    onLanded = function () {
                        clearTimeout(timer);
                        resolve();
                    };
                });
            return wait.then(function () {
                stop();
                if (!stopped && generation === pauseGeneration && !isPlaying()) {
                    return kit.playback.unpause();
                }

                return undefined;
            });
        };
    }, function (e) {
        stop();
        report(e);
        return null;
    });
}

// The name dialog on a bookmark just made: one field, focused and holding the suggestion selected, so
// typing replaces it; Save (Enter) names the bookmark through POST /Bookmarks/{id}/Name, Skip (Escape) leaves it
// unnamed, "More…" opens the full editor on it; "Always ask for a name" is the user's AskForName. With the user's
// PauseWhileNaming on, playback pauses while the dialog is open and resumes when it closes, unless they resumed it
// themselves meanwhile; "More…" keeps the pause until the editor closes. A 409 on Save (changed elsewhere since the
// bookmark was made) reads the bookmark again, so the next Save and More… carry the latest.
// options: { bookmark, suggestion, capabilities }.
function openNameDialog(options) {
    let bookmark = options.bookmark;
    const capabilities = options.capabilities || {};
    let saving = false;
    let handedToEditor = false;
    const dialog = kit.dialog({
        title: 'Name this bookmark',
        modal: true,
        scopedKeys: true,
        onKey: function (event) {
            // Enter in the field or on the checkbox saves; on a button it is that button's click, which the browser
            // makes. Escape (the kit closes: Skip) and Tab (the kit's trap) are the kit's.
            if (event.key === 'Enter' && event.target && event.target.tagName !== 'BUTTON') {
                event.preventDefault();
                save();
                return true;
            }

            return false;
        }
    });

    // The pause, only when the video is playing; what to undo on close waits for its answer and for
    // the pause to land (pausePlayback).
    const paused = capabilities.PauseWhileNaming === true ? pausePlayback() : Promise.resolve(null);
    const resume = function () {
        paused.then(function (undo) {
            return undo ? undo() : undefined;
        }).then(null, report);
    };

    const input = kit.input({
        value: options.suggestion || '',
        maxlength: String(capabilities.MaxNameLength || 100),
        autocomplete: 'off',
        placeholder: 'Optional'
    });
    const askBox = h('input', { type: 'checkbox', class: 'jfbm-always-ask-box', checked: capabilities.AskForName === true });
    askBox.addEventListener('change', function () {
        const wanted = askBox.checked;
        askBox.disabled = true;
        setAlwaysAsk(capabilities, wanted).then(function () {
            askBox.disabled = false;
        }, function (e) {
            askBox.disabled = false;
            askBox.checked = !wanted;
            toast(e.message);
        });
    });
    const error = h('div', { class: kit.cls('error'), role: 'alert' });
    const where = h('p', { class: 'jfbm-name-where' });
    const describe = function () {
        where.textContent = 'Bookmarked at ' + formatTime(bookmark.StartTicks) + (bookmark.ItemName ? ' in ' + bookmark.ItemName : '') + '.';
    };
    describe();
    const buttons = [];
    const setBusy = function (busy) {
        buttons.forEach(function (button) {
            button.disabled = busy;
        });
    };

    // A 409 (the bookmark changed elsewhere since it was made or last read): the bookmark is read again,
    // so the next Save and More… carry its latest UpdatedAt instead of another 409 each time, the field shows the name
    // it has now, selected, and the sentence says so; one deleted meanwhile closes the dialog, as the editor's Reload
    // does. When the read itself fails, the server's sentence stands.
    const reloadAfterConflict = async function (conflict) {
        let latest;
        try {
            latest = await api('GET', 'Bookmarks/' + bookmark.Id);
        } catch (e) {
            if (e.status === 404) {
                dialog.close();
                notifyChanged(bookmark.ItemId);
                toast('This bookmark was deleted elsewhere.');
            } else {
                error.textContent = conflict.message;
            }

            return;
        }

        bookmark = latest;
        describe();
        input.value = latest.Name || '';
        error.textContent = latest.Name
            ? 'Changed elsewhere: it is named "' + latest.Name + '" now. Save replaces that.'
            : 'Changed elsewhere: it has no name now. Save names it.';
        input.focus();
        if (typeof input.select === 'function') {
            input.select();
        }
    };

    const save = async function () {
        if (saving) {
            return;
        }

        const name = input.value.trim();
        if (!name) {
            // Nothing to name it: the same as Skip.
            dialog.close();
            return;
        }

        saving = true;
        setBusy(true);
        error.textContent = '';
        try {
            // The rename with the UpdatedAt the bookmark was made with: a change made elsewhere meanwhile is a
            // 409 with its sentence here, never a lost update.
            const named = await api('POST', 'Bookmarks/' + bookmark.Id + '/Name', { Name: name, IfUpdatedAt: bookmark.UpdatedAt || null });
            notifyChanged(named.ItemId || bookmark.ItemId);
            toast('Named "' + (named.Name || name) + '".');
            dialog.close();
        } catch (e) {
            saving = false;
            setBusy(false);
            if (e.status === CONFLICT_STATUS) {
                await reloadAfterConflict(e);
            } else {
                error.textContent = e.message;
            }
        }
    };

    const moreLink = linkButton('More…', function () {
        handedToEditor = true;
        dialog.close();
        openEditor({ bookmark: bookmark, onClose: resume });
    }, { class: kit.cls('link') + ' jfbm-name-more', title: 'The full editor: times, a note, a clip' });
    const skipButton = kit.button('Skip', { onClick: dialog.close });
    const saveButton = kit.button('Save', { variant: 'primary', type: 'submit' });
    buttons.push(skipButton, saveButton);

    const form = h('form', {
        class: 'jfbm-form jfbm-name-dialog',
        onsubmit: function (event) {
            event.preventDefault();
            save();
        }
    }, [
        where,
        kit.field('Name', input),
        h('label', { class: kit.cls('checkbox') + ' jfbm-always-ask' }, [askBox, h('span', { text: 'Always ask for a name' })]),
        error,
        h('div', { class: kit.cls('actions') }, [moreLink, skipButton, saveButton])
    ]);
    dialog.body.append(form);
    dialog.onClose(function () {
        if (!handedToEditor) {
            resume();
        }
    });
    input.focus();
    if (typeof input.select === 'function') {
        input.select();
    }

    return dialog;
}

// "Name it" on a bookmark made without the dialog (the toast's action): the dialog with the chapter's
// suggestion for the bookmark's position.
async function nameBookmark(bookmark) {
    const capabilities = await getCapabilities();
    const suggestion = await chapterSuggestion(bookmark.ItemId, bookmark.StartTicks, capabilities);
    openNameDialog({ bookmark: bookmark, suggestion: suggestion, capabilities: capabilities });
}
