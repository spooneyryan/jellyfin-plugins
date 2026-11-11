/*
 * The signed-in user's rules and <html>'s classes (the first load in a new browser included): the
 * cached answer at once, then /Me; every answer cached, Managed false too; re-checked when the user changes, since a
 * sign-out doesn't reload the page in jellyfin-web. jfar-pending waits ME_TIMEOUT_MS for /Me, the request as long as the
 * kit's timeout: a late answer is applied and cached; a /Me that failed is asked again on a later view, ME_RETRY_MS apart
 * at most.
 */

const LOCK_CLASS_PREFIX = 'jfar-lock-';
const ALL_SETTINGS_CLASS = 'jfar-lock-allsettings';
const PENDING_CLASS = 'jfar-pending';
const BLOCKING_CLASS = 'jfar-blocking';

const RULES = {
    userId: null,
    rules: null,
    source: 'none',
    generation: 0,
    pendingTimer: null,
    listeners: [],
    asking: false,
    failed: false,
    askedAt: 0
};

// A lock's class: jfar-lock- and the lowercase name (Profile gives jfar-lock-profile).
function lockClass(name) {
    return LOCK_CLASS_PREFIX + String(name).toLowerCase().replace(/[^a-z]/g, '');
}

// The classes the head script sets for a user (Web/head.js; a test keeps both equal): the cached locks of a managed user,
// and jfar-pending for a user known to be managed or, without a cached answer, while the server manages anyone.
function headClasses(flags, userId, cached) {
    const result = [];
    if (!flags || flags.enforced === false || !userId) {
        return result;
    }

    if (cached && cached.Managed) {
        (cached.Locked || []).forEach(function (name) {
            result.push(lockClass(name));
        });
        if (cached.AllSettingsLocked) {
            result.push(ALL_SETTINGS_CLASS);
        }
    }

    if (flags.firstLoad !== 'ShowUntilKnown' && ((cached && cached.Managed) || (!cached && flags.anyManaged !== false))) {
        result.push(PENDING_CLASS);
    }

    return result;
}

// The lowercase names of the sections locked for the signed-in user now (none while enforcement is off).
function lockedSections() {
    const rules = RULES.rules;
    if (!HEAD.enforced || !rules || !rules.Managed) {
        return [];
    }

    return (rules.Locked || []).map(function (name) {
        return String(name).toLowerCase().replace(/[^a-z]/g, '');
    });
}

function isLocked(section) {
    return lockedSections().indexOf(section) >= 0;
}

function allSettingsLocked() {
    return HEAD.enforced && !!RULES.rules && !!RULES.rules.Managed && !!RULES.rules.AllSettingsLocked;
}

function setPending(on) {
    document.documentElement.classList.toggle(PENDING_CLASS, !!on);
    if (!on && RULES.pendingTimer) {
        clearTimeout(RULES.pendingTimer);
        RULES.pendingTimer = null;
    }
}

// Puts <html>'s lock classes in step with the rules (the pending class is setPending's).
function applyClasses() {
    const root = document.documentElement;
    const locked = lockedSections();
    String(root.getAttribute('class') || '').split(' ').forEach(function (name) {
        if (name.indexOf(LOCK_CLASS_PREFIX) === 0 && name !== ALL_SETTINGS_CLASS && locked.indexOf(name.substring(LOCK_CLASS_PREFIX.length)) < 0) {
            root.classList.remove(name);
        }
    });
    locked.forEach(function (section) {
        root.classList.add(LOCK_CLASS_PREFIX + section);
    });
    root.classList.toggle(ALL_SETTINGS_CLASS, allSettingsLocked());
}

// Something that runs whenever the rules change (the guard's check, the menu, the page notes).
function onRulesChanged(listener) {
    RULES.listeners.push(listener);
}

function rulesChanged(reason) {
    try {
        applyClasses();
    } catch (e) {
        kit.report(e);
    }

    RULES.listeners.forEach(function (listener) {
        try {
            listener(reason);
        } catch (e) {
            kit.report(e);
        }
    });
}

// The signed-in user changed (the first one known, a sign-in or a user switch without a reload, or a sign-out).
function userChanged(id) {
    const generation = ++RULES.generation;
    RULES.userId = normalizeUserId(id);
    const cached = cachedRules(RULES.userId);
    RULES.rules = cached;
    RULES.source = cached ? 'cache' : 'none';
    RULES.asking = false;
    RULES.failed = false;
    setPending(false);
    if (!RULES.userId || !HEAD.enforced) {
        rulesChanged('user');
        return;
    }

    // The head script set jfar-pending only for the user index.html was loaded for; a sign-in without a reload sets it here.
    if (headClasses(HEAD, RULES.userId, cached).indexOf(PENDING_CLASS) >= 0) {
        setPending(true);
        RULES.pendingTimer = setTimeout(function () {
            if (generation === RULES.generation) {
                setPending(false);
            }
        }, ME_TIMEOUT_MS);
    }

    rulesChanged('user');
    askMe(generation);
}

// One /Me for the user of this generation: its answer, however late, is applied and cached; a failure keeps the cached
// rules (or none) and lets a later view ask again.
function askMe(generation) {
    RULES.asking = true;
    RULES.askedAt = Date.now();
    fetchMe().then(function (me) {
        if (generation !== RULES.generation) {
            return;
        }

        RULES.asking = false;
        if (!me || normalizeUserId(me.UserId) !== RULES.userId) {
            return;
        }

        RULES.failed = false;
        cacheRules(me);
        RULES.rules = me;
        RULES.source = 'me';
        setPending(false);
        rulesChanged('me');
    }, function () {
        // The server enforces anyway (the browser is offline or /Me failed): the cached rules stay, nothing else hides.
        if (generation === RULES.generation) {
            RULES.asking = false;
            RULES.failed = true;
            RULES.source = RULES.rules ? 'cache' : 'failed';
            setPending(false);
            rulesChanged('failed');
        }
    });
}

// A view after a /Me that failed asks again, ME_RETRY_MS after the last try at the soonest.
function retryMe() {
    if (!HEAD.enforced || !RULES.userId || !RULES.failed || RULES.asking || Date.now() - RULES.askedAt < ME_RETRY_MS) {
        return;
    }

    askMe(RULES.generation);
}
