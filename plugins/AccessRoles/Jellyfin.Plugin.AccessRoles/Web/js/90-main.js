/*
 * Start-up. The guard starts at once (the startup check must come before the router's first update); the
 * rules follow the kit's signed-in user, /Me once it is known.
 */

function onKept(sections) {
    if (!HEAD.enforced || HEAD.notice === 'Silent' || sections.length === 0) {
        return;
    }

    kit.toast(kit.t('kept', { sections: sectionTitles(sections) }), { timeout: 6000 });
}

(function start() {
    // Read by the webchecks and the tests; nothing on the server trusts any of it.
    kit.accessRoles = Object.freeze({
        head: HEAD,
        rules: function () {
            return { userId: RULES.userId, source: RULES.source, rules: RULES.rules, locked: lockedSections() };
        },
        log: function () {
            return GUARD.log.slice();
        },
        normalizePath: normalizePath,
        blockedSection: blockedSection,
        headClasses: headClasses,
        hideRules: HIDE_RULES,
        pendingAnchors: PENDING_ANCHORS,
        blockingAnchors: BLOCKING_ANCHORS,
        hiddenEntryPoints: hiddenEntryPoints
    });

    kit.start();
    try {
        if (HEAD.enforced) {
            // The head script's jfar-pending waits for this user's /Me; 3 s at most if no user ever shows (a stale token).
            // The head lifts it itself after 3 s (should this script never run); from here this script owns it.
            if (window.__jfarHead && window.__jfarHead.pendingTimer) {
                clearTimeout(window.__jfarHead.pendingTimer);
                window.__jfarHead.pendingTimer = null;
            }

            if (document.documentElement.classList.contains(PENDING_CLASS)) {
                RULES.pendingTimer = setTimeout(function () {
                    setPending(false);
                }, ME_TIMEOUT_MS);
            }

            // Until the kit knows the user, the head's cached answer stands in.
            if (HEAD.userId) {
                RULES.userId = HEAD.userId;
                RULES.rules = cachedRules(HEAD.userId);
                RULES.source = RULES.rules ? 'cache' : 'none';
            }

            startGuard();
            startMenus();
            startNoLibraries();
            watchKeptSaves(onKept);
            kit.onView(function () {
                retryMe();
            });
        } else {
            // Enforcement off: nothing is hidden; only the administrators' banner.
            document.documentElement.classList.remove(PENDING_CLASS);
        }

        startNotices();
        kit.user.onChange(function (id) {
            userChanged(id);
        });
    } catch (e) {
        // Never break the page: the server enforces whatever the web shows.
        document.documentElement.classList.remove(PENDING_CLASS);
        document.documentElement.classList.remove(BLOCKING_CLASS);
        kit.report(e);
    }
}());
