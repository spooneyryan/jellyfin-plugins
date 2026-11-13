/*
 * The administrators' banner on the stock Dashboard user pages (ShowManagedNoticeOnUserPages; an admin editing a managed
 * user's Library access, or adding a user): on a managed user's profile, access, parental
 * control and password tabs it names the roles and the locks and says the library ticks are replaced; on Add user it names
 * the default roles. The admin endpoints answer 403 to anyone else, so a non-admin never gets a banner; it is shown also
 * while enforcement is off, and says so.
 */

const BANNER_ATTRIBUTE = 'data-jfar-banner';
const USER_PAGE_ROUTES = Object.freeze(['/dashboard/users/profile', '/dashboard/users/access', '/dashboard/users/parentalcontrol', '/dashboard/users/password']);
const NEW_USER_ROUTE = '/dashboard/users/add';

// Jellyfin 12's user editor: one route per user and tab, /dashboard/users/<userId>/<tab>, with no query.
const USER_PAGE_ROUTE_12 = /^\/dashboard\/users\/([0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12})\/(profile|access|parentalcontrol|password)$/i;

const NOTICES = { key: null, pending: null, text: null };

// The banner's key for the current route: 'user:<id>', 'add', or null.
function noticeKey() {
    const path = normalizePath(window.location.hash);
    if (path === NEW_USER_ROUTE) {
        return 'add';
    }

    const editor12 = USER_PAGE_ROUTE_12.exec(path);
    if (editor12) {
        return 'user:' + normalizeUserId(editor12[1]);
    }

    if (USER_PAGE_ROUTES.indexOf(path) < 0) {
        return null;
    }

    const userId = kit.hashParams().get('userId');
    return userId ? 'user:' + normalizeUserId(userId) : null;
}

function lockNames(locks) {
    return Object.keys(locks || {}).filter(function (name) {
        return locks[name] && locks[name].Locked;
    });
}

// The banner's sentences for a user's Effective answer, or null when Access Roles leaves the user alone.
function userNotice(effective) {
    if (!effective) {
        return null;
    }

    if (effective.State === 'Excluded') {
        return [kit.t('adminExcluded')];
    }

    if (effective.State !== 'Managed' && effective.State !== 'CannotManage' && effective.State !== 'UnknownValue') {
        return null;
    }

    const roles = (effective.Roles || []).map(function (role) {
        return role.Name;
    }).filter(Boolean);
    const lines = [roles.length ? kit.t('adminManaged', { roles: roles.join(', ') }) : kit.t('adminManagedNoRole')];
    if (effective.State === 'Managed') {
        lines.push(kit.t('adminLibraries'));
    }

    const locked = lockNames(effective.Locks);
    if (locked.length) {
        lines.push(kit.t('adminLocked', { sections: sectionTitles(locked) }));
    }

    return lines;
}

// The banner's sentences for Add user: the default roles, or null without any.
function newUserNotice(roles) {
    const defaults = (roles || []).filter(function (role) {
        return role && role.IsDefault;
    }).map(function (role) {
        return role.Name;
    });
    return defaults.length ? [kit.t('adminDefaultRoles', { roles: defaults.join(', ') })] : null;
}

function loadNotice(key) {
    if (key === 'add') {
        return kit.api('GET', 'AccessRoles/Admin/Roles').then(newUserNotice);
    }

    return kit.api('GET', 'AccessRoles/Admin/Users/' + encodeURIComponent(key.substring(5)) + '/Effective').then(userNotice);
}

function placeBanner() {
    const key = NOTICES.key;
    const banners = Array.prototype.slice.call(document.querySelectorAll('[' + BANNER_ATTRIBUTE + ']'));
    const content = key ? findAnchor(key === 'add' ? 'newUserContent' : 'userEditorContent') : null;
    banners.forEach(function (banner) {
        if (!content || !NOTICES.text || banner.getAttribute(BANNER_ATTRIBUTE) !== key || !content.contains(banner)) {
            banner.remove();
        }
    });
    if (!content || !NOTICES.text || content.querySelector('[' + BANNER_ATTRIBUTE + ']')) {
        return;
    }

    const lines = NOTICES.text.slice();
    if (!HEAD.enforced) {
        lines.push(kit.t('adminEnforcementOff'));
    }

    const banner = kit.h('div', { class: kit.cls('admin-banner'), role: 'note', [BANNER_ATTRIBUTE]: key }, lines.map(function (line) {
        return kit.h('p', { text: line });
    }));
    content.insertBefore(banner, content.firstChild);
}

// On every scan: the route's banner, loaded once per route and user (a failed load shows none, and is tried again on the
// next navigation).
function syncNotices() {
    if (!HEAD.userPages) {
        return;
    }

    const key = noticeKey();
    if (key !== NOTICES.key) {
        NOTICES.key = key;
        NOTICES.text = null;
        NOTICES.pending = null;
        if (key) {
            const pending = loadNotice(key).then(function (text) {
                if (NOTICES.pending === pending) {
                    NOTICES.text = text;
                    placeBanner();
                }
            }, function () {
                if (NOTICES.pending === pending) {
                    NOTICES.text = null;
                }
            });
            NOTICES.pending = pending;
        }
    }

    placeBanner();
}

function startNotices() {
    kit.observe(syncNotices);
    kit.onView(syncNotices);
    kit.onNavigate(function () {
        syncNotices();
    });
    kit.user.onChange(function () {
        NOTICES.key = null;
        NOTICES.text = null;
        NOTICES.pending = null;
    });
}
