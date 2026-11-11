// The kit's fake page set up as jellyfin-web 10.11.5 for Access Roles' web guard tests (accessroles.test.mjs,
// head.test.mjs): the head fragment's flags and script, the browser's storage, a router event bus (window.Events), a
// location with replace(), /AccessRoles/Me, and the built ClientScript. WebTests runs both files with
//   KIT_CLIENT_SCRIPT=<built ClientScript> KIT_PREFIX=jfar JFAR_HEAD_SCRIPT=<AccessRolesHead.Script>
import fs from 'node:fs';
import { createPage } from '../../../../../kit/web/tests/fake-dom.mjs';

export const clientFile = process.env.KIT_CLIENT_SCRIPT;
export const headFile = process.env.JFAR_HEAD_SCRIPT;
export const skip = !clientFile || !headFile ? 'KIT_CLIENT_SCRIPT and JFAR_HEAD_SCRIPT are not set' : false;
const clientSource = skip ? '' : fs.readFileSync(clientFile, 'utf8');
export const headSource = skip ? '' : fs.readFileSync(headFile, 'utf8');

export const USER = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
export const OTHER = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
export const DEFAULT_HEAD = Object.freeze({ anyManaged: true, enforced: true, firstLoad: 'HideUntilKnown', notice: 'Toast', userPages: true });

export function me(locked, extra) {
    return Object.assign({ UserId: USER, Managed: true, RulesVersion: 'v1', Locked: locked, AllSettingsLocked: false, NoLibraries: false, Message: null, NotEnforced: [] }, extra || {});
}

export const EVERY_SECTION = ['Profile', 'DisplayAccount', 'DisplayDevice', 'Home', 'Playback', 'Subtitles', 'Controls', 'QuickConnect', 'EnhancedPanel'];

// A page before any script: flags, storage, location, router events.
export function blankPage(options) {
    options = options || {};
    const page = createPage();
    const context = page.context;
    if (options.head !== null) {
        context.__jfarHead = Object.assign({}, DEFAULT_HEAD, options.head || {});
    }

    if (options.credentials !== false) {
        const userId = options.credentialsUser || USER;
        context.localStorage.setItem('jellyfin_credentials', JSON.stringify({ Servers: [{ Id: 'server-1', UserId: userId, AccessToken: 'token', DateLastAccessed: 5 }] }));
    }

    Object.entries(options.cache || {}).forEach(([userId, rules]) => {
        context.localStorage.setItem('jfar:rules:' + userId, JSON.stringify(rules));
    });
    page.replaced = [];
    context.location.hash = options.hash || '#/home';
    context.location.replace = (url) => {
        page.replaced.push(String(url));
        context.location.hash = String(url).substring(String(url).indexOf('#'));
    };
    const handlers = [];
    // jellyfin-web's event bus, assigned by its bundle: after the head script, before the ClientScript (as on 10.11.5).
    page.installEvents = () => {
        context.Events = {
            on(target, name, fn) {
                handlers.push({ target, name, fn });
            }
        };
    };
    page.historyUpdate = (state) => handlers.filter((h) => h.name === 'HISTORY_UPDATE').forEach((h) => h.fn({ type: 'HISTORY_UPDATE' }, state));
    page.historyHandlers = handlers;
    return page;
}

// The head script, as index.html runs it.
export function runHead(page) {
    page.run(headSource, 'head-script.js');
    return page.context.__jfarHead;
}

// A page with the head script and the ClientScript run, then the signed-in user (unless user: null).
export async function start(options) {
    options = options || {};
    const page = blankPage(options);
    if (options.me !== undefined) {
        page.respond('GET', '/AccessRoles/Me', options.me, options.meStatus || 200, options.meExtra);
    }

    (options.responses || []).forEach((r) => page.respond(r.method, r.path, r.body, r.status, r.extra));
    if (options.beforeScripts) {
        options.beforeScripts(page);
    }

    if (options.runHead !== false) {
        runHead(page);
    }

    page.installEvents();
    page.run(clientSource, 'client-script.js');
    await page.advance(10);
    if (options.user !== null) {
        page.installApiClient({ userId: options.user || USER, admin: !!options.admin });
        await page.advance(60);
    }

    return page;
}

export function html(page) {
    return page.document.documentElement.classList;
}

export function guard(page) {
    return page.context.__jfkit.jfar.instance.accessRoles;
}

export function toasts(page) {
    return page.document.querySelectorAll('.jfar-toast').map((t) => t.textContent);
}

export function view(page, id, classes, children) {
    const element = page.element('div', { id, class: ['page'].concat(classes || []).join(' '), 'data-role': 'page' }, children || []);
    page.document.body.append(element);
    return element;
}
