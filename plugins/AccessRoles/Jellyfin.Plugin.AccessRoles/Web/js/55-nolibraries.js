/*
 * The no-libraries message: Home rebuilds its "Ask an administrator to create a
 * library." text on every show, and only when the user sees no library and no playlist; for a managed user who can open
 * no library, /Me's message takes its place.
 */

const REPLACED_ATTRIBUTE = 'data-jfar-replaced';

function syncNoLibraries() {
    const rules = RULES.rules;
    if (!HEAD.enforced || !rules || !rules.Managed || !rules.NoLibraries) {
        return;
    }

    const text = findAnchor('homeNoLibraries');
    if (!text || text.getAttribute(REPLACED_ATTRIBUTE) === '1') {
        return;
    }

    text.textContent = rules.Message || kit.t('noLibraries');
    text.setAttribute(REPLACED_ATTRIBUTE, '1');
}

function startNoLibraries() {
    kit.observe(syncNoLibraries);
    kit.onView(function (view) {
        if (isAnchor(view, 'homePage')) {
            syncNoLibraries();
        }
    });
    onRulesChanged(syncNoLibraries);
}
