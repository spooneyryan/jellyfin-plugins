/*
 * Shared web UI kit. kit/build/Kit.props embeds this file in every plugin, and the kit's EmbeddedScript
 * (kit/src/Web/EmbeddedScript.cs) places it after KIT_BUILD and KIT_CSS: inside the plugin's ClientScript IIFE, and in
 * the optional AdminScript ES module. So this file only declares KIT_NATIVE_CLASSES, KIT_ANCHORS, KitApiError and
 * createKit(); nothing runs until the plugin calls createKit(...).start().
 *
 * Plain ES2019 (no optional chaining, no ??, no build step). jellyfin-web names appear only in the two tables below:
 * styling classes in KIT_NATIVE_CLASSES, the anchors the kit itself finds or listens to in
 * KIT_ANCHORS. Kit.Tests' KitScriptConventionTests enforce it.
 */

// <kit-native-classes>
// Native styling classes. The kit writes them on its own nodes so they look native; plugins get them only through the
// kit's markup helpers (kit.card, kit.listItem, kit.list, kit.grid, kit.button, kit.input, kit.textarea, kit.dialog,
// kit.osdButton, kit.detailButton, kit.headerButton, kit.section, kit.setHidden). Theme colours come from these classes (theme.css), never
// from --jf-palette-* surface or text variables.
const KIT_NATIVE_CLASSES = Object.freeze({
    icon: 'material-icons',
    hide: 'hide',
    dialog: 'dialog',
    input: 'emby-input',
    textarea: 'emby-textarea',
    select: 'emby-select-withcolor',
    buttonSubmit: 'button-submit',
    raised: 'raised',
    buttonFlat: 'button-flat',
    embyButton: 'emby-button',
    iconButton: 'paper-icon-button-light',
    autoSize: 'autoSize',
    iconButtonIconLarge: 'xlargePaperIconButton',
    headerButton: 'headerButton',
    headerButtonRight: 'headerButtonRight',
    detailButton: 'detailButton',
    detailButtonContent: 'detailButton-content',
    detailButtonIcon: 'detailButton-icon',
    verticalSection: 'verticalSection',
    detailVerticalSection: 'detailVerticalSection',
    sectionTitle: 'sectionTitle',
    sectionTitleCards: 'sectionTitle-cards',
    paddedRight: 'padded-right',
    card: 'card',
    cardShape: Object.freeze({ portrait: 'portraitCard', backdrop: 'backdropCard', square: 'squareCard' }),
    cardBox: 'cardBox',
    cardBoxPadded: 'cardBox-bottompadded',
    cardScalable: 'cardScalable',
    cardPadder: 'cardPadder',
    cardPadderShape: Object.freeze({ portrait: 'cardPadder-portrait', backdrop: 'cardPadder-backdrop', square: 'cardPadder-square' }),
    cardImageContainer: 'cardImageContainer',
    cardContent: 'cardContent',
    coveredImage: 'coveredImage',
    defaultCardBackground: 'defaultCardBackground',
    cardImageIcon: 'cardImageIcon',
    cardText: 'cardText',
    cardTextCentered: 'cardTextCentered',
    cardTextFirst: 'cardText-first',
    cardTextSecondary: 'cardText-secondary',
    itemsContainer: 'itemsContainer',
    verticalWrap: 'vertical-wrap',
    paperList: 'paperList',
    listItem: 'listItem',
    listItemBorder: 'listItem-border',
    listItemImage: 'listItemImage',
    listItemBody: 'listItemBody',
    listItemBodyText: 'listItemBodyText',
    secondary: 'secondary',
    // Page hosting: page roots, in-page links and the "Library tools" drawer links, shaped like jellyfin-web's own.
    paddedLeft: 'padded-left',
    paddedBottomPage: 'padded-bottom-page',
    linkButton: 'emby-linkbutton',
    buttonLink: 'button-link',
    sidebarHeader: 'sidebarHeader',
    navMenuOption: 'navMenuOption',
    lnkMediaFolder: 'lnkMediaFolder',
    navMenuOptionIcon: 'navMenuOptionIcon',
    navMenuOptionText: 'navMenuOptionText',
    sectionName: 'sectionName',
    // jellyfin-web's focus manager takes INPUT (not range or file), TEXTAREA, SELECT, BUTTON, A and this class; our own
    // control that is none of them (User Ratings' star slider) carries it, or the TV layout's D-pad never reaches it.
    focusable: 'focusable',
    // Action sheet rows: the icon and text of an entry the kit adds to jellyfin-web's item menu, as its own rows
    // have them; the row's own classes are copied from a sibling row (KIT_ANCHORS.actionSheet).
    actionSheetIcon: 'actionsheetMenuItemIcon',
    listItemIcon: 'listItemIcon',
    listItemIconTransparent: 'listItemIcon-transparent',
    actionSheetBody: 'actionsheetListItemBody',
    actionSheetText: 'actionSheetItemText',
    // kit.nativeDialog: a dialogHelper dialog shaped like jellyfin-web's own form dialogs.
    formDialog: 'formDialog',
    formDialogHeader: 'formDialogHeader',
    formDialogHeaderTitle: 'formDialogHeaderTitle',
    formDialogContent: 'formDialogContent',
    smoothScrollY: 'smoothScrollY',
    dialogContentInner: 'dialogContentInner',
    dialogContentCentered: 'dialog-content-centered',
    btnCancel: 'btnCancel'
});
// </kit-native-classes>

// <kit-anchors>
// The jellyfin-web globals, events, classes, storage keys and routes the kit itself relies on. The DOM ones the kit's
// health checks report are declared again, as selectors, in kit/web/anchors.json (names 'kit.<name>';
// tests/health.test.mjs keeps both equal). Plugin anchors are declared in each plugin's Web/anchors.json and reached
// through kit.anchors from its Web/js/10-selectors.js.
const KIT_ANCHORS = Object.freeze({
    apiClient: Object.freeze({ global: 'ApiClient', method: 'getUrl', note: 'absent when ClientScript runs; polled every 25 ms' }),
    playbackReports: Object.freeze({ methods: Object.freeze(['reportPlaybackStart', 'reportPlaybackProgress', 'reportPlaybackStopped']), note: 'ApiClient prototype; the position observer wraps them (bm:bookmarks.js 1.0)' }),
    viewShow: Object.freeze({ event: 'viewshow', note: 'fired on the view after every navigation' }),
    pageShow: Object.freeze({ event: 'pageshow', note: 'fired after viewshow, also for restored views' }),
    layoutTv: Object.freeze({ htmlClass: 'layout-tv', note: 'html class' }),
    layoutMobile: Object.freeze({ htmlClass: 'layout-mobile', note: 'html class' }),
    layoutSetting: Object.freeze({ storageKey: 'layout', experimental: 'experimental', legacy12: Object.freeze(['desktop-legacy', 'mobile-legacy', 'tv']), note: 'the experimental (10.11) or modern (12) layout keeps html.layout-desktop. 10.11: only "experimental" is the experimental layout. 12: the modern layout is the default (no value, auto, desktop, mobile, modern); only desktop-legacy, mobile-legacy and tv are legacy' }),
    detailsRoute: Object.freeze({ hash: '#/details', note: 'item details route, #/details?id=<id>&serverId=<id>' }),
    customElements: Object.freeze({ global: 'CustomElements.upgradeSubtree', note: 'upgrades is="emby-*" elements' }),
    jellyfinEnhanced: Object.freeze({ global: 'JellyfinEnhanced', note: 'present when Jellyfin Enhanced runs' }),
    jeReviews: Object.freeze({ selector: 'details.tmdb-reviews-section', summary: 'summary', controls: 'button, a, input, select, textarea, [tabindex]', savedTabindex: 'data-jfkit-je-tabindex', note: 'Jellyfin Enhanced 12.14\'s Reviews section on a details page: a <details> in .detailSectionContent, closed unless its "Expand reviews by default" is on, with its "Write a Review" button outside the <summary>. Chrome keeps a box for a closed <details>\' content but won\'t focus it, so jellyfin-web\'s TV focus manager picks that button and the D-pad stays put (kit.je.tvSectionJump). Not jellyfin-web\'s: a presence probe, never health-checked; its absence means no reviews on the page. savedTabindex is shared by every plugin\'s kit, unprefixed, so the first saves an element\'s own tabindex and one puts it back' }),
    nativeDialog: Object.freeze({ container: 'dialogContainer', note: 'jellyfin-web\'s own dialogs and action sheets (dialogHelper); while one is open the kit\'s shortcuts stay quiet. A presence probe: its absence means no native dialog is open, so it is never reported missing' }),
    nativeShell: Object.freeze({ global: 'NativeShell', note: 'a native app wrapper (Jellyfin Media Player, Android); ApiClient.appVersion() is then the app\'s version, so health reports send WebVersion null' }),
    // Page hosting.
    viewBeforeShow: Object.freeze({ event: 'viewbeforeshow', note: 'fired on a view before it shows; titles are set here, in the capture phase, before libraryMenu reads data-title on pageshow' }),
    pageBeforeShow: Object.freeze({ event: 'pagebeforeshow', note: 'the second of the four events a React page fires on mount; the fallback route fires all four after an in-place swap' }),
    pageHost: Object.freeze({ attr: 'data-role', value: 'page', title: 'data-title', note: 'every view root is [data-role="page"]; libraryMenu titles the header from its data-title' }),
    fallbackPage: Object.freeze({ id: 'fallbackPage', note: 'the FallbackRoute page, shown for routes jellyfin-web does not know: the fallback host; it stays mounted between two unknown routes' }),
    pluginPagesHost: Object.freeze({ pageClass: 'userPreferencesPage', route: '/userpluginsettings.html', brokenRoute: '/userpluginsettings', param: 'pageUrl', note: 'the Plugin Pages host view; FallbackRoute rewrites the route without .html when the Plugin Pages route patch is missing' }),
    pluginPages: Object.freeze({ global: 'PluginPages', userList: 'PluginPages/User', itemId: 'data-itemid', note: 'a global const of /PluginPages/inject.js (typeof only, not a window property); its drawer links carry data-itemid="<Id>"' }),
    drawer: Object.freeze({ container: 'mainDrawer-scrollContainer', userMenu: 'userMenuOptions', note: '"Library tools" goes right before .userMenuOptions, which exists only while signed in' }),
    appUserMenu: Object.freeze({ id: 'app-user-menu', settings: 'a[href="#/mypreferencesmenu"]', divider: 'hr', icon: '.MuiListItemIcon-root', text: '.MuiListItemText-root', backdrop: '.MuiBackdrop-root', pluginPagesLink: 'a[data-plugin-pages="true"]', note: 'the experimental (10.11) and modern (12, the default) layouts hide the drawer: "Library tools" goes into the app bar user menu, a MUI Menu kept mounted while closed, right after its Settings item (a react-router <a href="#/mypreferencesmenu">), as Plugin Pages 3.x adds its links there' }),
    libraryMenu: Object.freeze({ global: 'LibraryMenu.setTitle', note: 'sets the header title' }),
    // The item "⋮" menu and the selection bar's bulk menu.
    actionSheet: Object.freeze({
        container: 'dialogContainer',
        sheet: 'actionSheet',
        scroller: 'actionSheetScroller',
        item: 'actionSheetMenuItem',
        itemClass: 'listItem listItem-button actionSheetMenuItem',
        selected: 'selected',
        commandAttr: 'data-id',
        itemMarkers: Object.freeze(['refresh', 'edit', 'editimages', 'identify']),
        bulkMarker: 'selectall',
        bulkAdminMarker: 'groupvideos',
        insertAfter: Object.freeze(['refresh', 'moremediainfo', 'identify', 'editlyrics', 'editsubtitles', 'editimages', 'edit']),
        bulkInsertAfter: Object.freeze(['refresh', 'markunplayed', 'markplayed']),
        closeEvent: 'close',
        health: 'kit.actionSheet',
        healthRows: 'kit.actionSheetRows',
        note: 'actionSheet.ts builds a new div.actionSheet inside a div.dialogContainer appended to <body> on every show(), complete before it is attached, with one button.actionSheetMenuItem[data-id=<command>] per row in .actionSheetScroller (each row with the same classes: listItem-focusscale on TV, actionsheet-xlargeFont on mobile). Nothing in it names the item. The item menu is the sheet with an admin-only command (itemMarkers) and no selectall; the bulk menu has selectall, and groupvideos for an admin only. Its dialog-level click listener stores the data-id of the clicked row and closes it through history.back(), so close (not bubbling) comes later; an unknown id rejects the promise of the item menu, which every 10.11.5 caller swallows, and is ignored by the callback of the bulk menu. A sheet that was positioned has inline top/left, clamped once before our row was added'
    }),
    menuTriggers: Object.freeze({
        itemAction: 'itemAction',
        actionAttr: 'data-action',
        menuAction: 'menu',
        commandEvent: 'command',
        menuCommand: 'menu',
        item: Object.freeze({ id: 'data-id', type: 'data-type', isFolder: 'data-isfolder', serverId: 'data-serverid' }),
        cardClass: 'card',
        detailsPage: 'itemDetailPage',
        detailsMore: 'btnMoreCommands',
        detailsVersion: 'selectSource',
        selectionPanel: 'selectionCommandsPanel',
        selectionMenu: 'btnSelectionPanelOptions',
        selectionClose: 'btnCloseSelectionPanel',
        selectionItem: 'itemSelectionPanel',
        selectionCheckbox: 'chkItemSelect',
        health: Object.freeze({ itemMenu: 'kit.itemMenuButton', detailsMore: 'kit.detailsMoreButton', detailsVersion: 'kit.detailsVersion', selectionMenu: 'kit.selectionMenuButton', selectionCheckbox: 'kit.selectionCheckbox', selectionClose: 'kit.selectionClose' }),
        note: 'What opens a menu, recorded in the capture phase since shortcuts.js stops the click at the items container: a click in .itemAction whose data-action (its own, else that of the nearest ancestor) is menu, for the closest [data-id] (a card or a list row, with data-type, data-isfolder and data-serverid); the command CustomEvent with detail.command menu (a right-click on a card, the menu key of a remote), for the closest [data-id] of its target; .btnMoreCommands on a details page, for the version chosen in the .selectSource of that page (a MediaSource id is the item id of its version) or the hash id; .btnSelectionPanelOptions, for the cards of the checked .chkItemSelect boxes. The selection ends with .btnCloseSelectionPanel. Their entries in anchors.json are presence probes for Test-WebAnchors.ps1, never health-checked'
    }),
    dialogHelper: Object.freeze({ global: 'Dashboard.dialogHelper', size: 'small', closeEvent: 'close', note: 'window.Dashboard, kept defined for plugins (src/utils/dashboard.js): createDialog({ size, removeOnClose, scrollY }), open(dialog) and close(dialog); a dialog it opens is history-enabled (Back closes it, never the page) and fires close (not bubbling) after the history pop. kit.nativeDialog falls back to kit.dialog without it' })
});
// </kit-anchors>

/**
 * An API error: the ProblemDetails of the response, or a status of 0 when the request never got an answer.
 * `message` is what the UI shows: the ProblemDetails `detail`, else its `title`, else a generic sentence. `code` is the
 * error's stable code (the ProblemDetails `code` extension), or null.
 */
class KitApiError extends Error {
    constructor(status, message, problem) {
        super(message);
        this.name = 'KitApiError';
        this.status = status;
        this.title = (problem && problem.title) || null;
        this.detail = (problem && problem.detail) || null;
        this.type = (problem && problem.type) || null;
        this.errors = (problem && problem.errors) || null;
        // A stable code a page tells this error apart by, never by its words (PluginException.Code).
        this.code = (problem && typeof problem.code === 'string' && problem.code) || null;
        // The whole ProblemDetails body, its extensions included (a plugin's own Code, Users, ...), or null.
        this.problem = problem && typeof problem === 'object' ? problem : null;
    }

    get isFeatureDisabled() {
        return this.type === 'urn:jellyfin-plugin-kit:feature-disabled';
    }

    get isLocked() {
        return this.type === 'urn:jellyfin-plugin-kit:locked';
    }
}

/**
 * Creates the kit of one plugin.
 * @param {{plugin: string, prefix: string, route: string, version: string, kit: string, jellyfin: string, mvid: string}} build KIT_BUILD
 * @param {string} css KIT_CSS: kit.css for this prefix followed by the plugin's own Web/css files
 * @param {{styleId?: string}} [options] styleId: the id of the style element (default `<prefix>-styles`)
 */
function createKit(build, css, options) {
    const N = KIT_NATIVE_CLASSES;
    const A = KIT_ANCHORS;
    const pfx = build.prefix;
    const settings = { styleId: (options && options.styleId) || pfx + '-styles', toastRaised: null };
    const TICKS_PER_SECOND = 10000000;
    const CLIENT_POLL_MS = 25;
    const CLIENT_WARN_MS = 30000;
    const USER_POLL_LIMIT_MS = 60000;
    const API_TIMEOUT_MS = 30000;
    const SESSION_TTL_MS = 60 * 1000;

    const now = function () {
        return Math.round(performance.now() * 10) / 10;
    };

    // Moments in this document's performance.now() clock, for the start-up checks (scripts/webchecks/kit-boot.json).
    const timing = { script: now(), client: null, user: null, firstApi: null };
    const kit = { build: build, prefix: pfx, timing: timing };
    const cls = function (name) {
        return pfx + '-' + name;
    };

    kit.cls = cls;

    // Generated element ids (form fields, dialog titles). One counter per prefix on window, shared by every kit of that
    // prefix in the document: the ClientScript's and a dashboard AdminScript's (which both run on dashboard pages) never
    // hand out the same id, so label[for] and aria-labelledby always find their own element.
    const idCounter = '__' + pfx + 'KitIds';
    function nextId(name) {
        const n = (Number(window[idCounter]) || 0) + 1;
        window[idCounter] = n;
        return cls(name + '-' + n);
    }

    // ------------------------------------------------------------------ environment

    function client() {
        const c = window[A.apiClient.global];
        return c && typeof c[A.apiClient.method] === 'function' ? c : null;
    }

    kit.client = client;
    kit.env = {
        has: function (path) {
            let value = window;
            const parts = String(path).split('.');
            for (let i = 0; i < parts.length; i++) {
                if (value === null || value === undefined) {
                    return false;
                }

                value = value[parts[i]];
            }

            return value !== null && value !== undefined;
        }
    };

    function report(error) {
        console.warn('[' + build.plugin + ']', error);
    }

    kit.report = report;

    // ---------------------------------------------------------------------- anchors
    // The jellyfin-web anchors of the kit and of the plugin (kit/web/anchors.json and the plugin's Web/anchors.json),
    // which EmbeddedScript passes in as options.anchors: { name, css: [primary, ...fallbacks], required, views }.
    // Plugins find native DOM only through them, from their Web/js/10-selectors.js.

    const anchorTable = {};
    ((options && options.anchors) || []).forEach(function (a) {
        if (a && typeof a.name === 'string' && Array.isArray(a.css) && !Object.prototype.hasOwnProperty.call(anchorTable, a.name)) {
            anchorTable[a.name] = Object.freeze({
                name: a.name,
                css: Object.freeze(a.css.slice()),
                required: !!a.required,
                views: Object.freeze((a.views || []).slice())
            });
        }
    });
    const unknownAnchors = {};

    function declaredAnchor(name) {
        if (Object.prototype.hasOwnProperty.call(anchorTable, name)) {
            return anchorTable[name];
        }

        if (!unknownAnchors[name]) {
            unknownAnchors[name] = true;
            report(new Error('kit.anchors: "' + name + '" is not declared in anchors.json'));
        }

        return null;
    }

    function select(root, selector, all) {
        try {
            return all ? Array.prototype.slice.call(root.querySelectorAll(selector)) : root.querySelector(selector);
        } catch (e) {
            // An invalid selector matches nothing (the anchor then counts as missing).
            return all ? [] : null;
        }
    }

    kit.anchors = {
        names: function () {
            return Object.keys(anchorTable);
        },
        get: function (name) {
            return Object.prototype.hasOwnProperty.call(anchorTable, name) ? anchorTable[name] : null;
        },
        // The first element matching the anchor's primary selector, else its fallbacks in order; null when none does.
        find: function (name, scope) {
            const a = declaredAnchor(name);
            const root = scope || document;
            if (!a || !root) {
                return null;
            }

            for (let i = 0; i < a.css.length; i++) {
                const found = select(root, a.css[i], false);
                if (found) {
                    return found;
                }
            }

            return null;
        },
        // Every element matching the first of the anchor's selectors that matches any; [] when none does.
        all: function (name, scope) {
            const a = declaredAnchor(name);
            const root = scope || document;
            if (!a || !root) {
                return [];
            }

            for (let i = 0; i < a.css.length; i++) {
                const found = select(root, a.css[i], true);
                if (found.length > 0) {
                    return found;
                }
            }

            return [];
        },
        // Which selector matches: 0 the primary, 1 or more a fallback, -1 none.
        match: function (name, scope) {
            const a = declaredAnchor(name);
            const root = scope || document;
            if (!a || !root) {
                return -1;
            }

            for (let i = 0; i < a.css.length; i++) {
                if (select(root, a.css[i], false)) {
                    return i;
                }
            }

            return -1;
        }
    };

    // ----------------------------------------------------------------------- health
    // The health self-check. On each relevant viewshow a feature calls
    // kit.health.expect([...anchors], view). An anchor still missing after a short grace period, while the route is the
    // same, is a miss: the console warns once per page load per anchor and view, and a report is queued. At most one
    // POST /<Route>/Diagnostics/ClientHealth goes out per browser every 10 minutes (the time is kept in localStorage),
    // after the user is known; the server validates it, rate-limits it and logs it once per anchor and web version.
    // kit.health.note() keeps the kit's other problems (no ApiClient, no playback reports), console only.

    const HEALTH_GRACE_MS = 4000;
    const HEALTH_BATCH_MS = 2000;
    const HEALTH_INTERVAL_MS = 10 * 60 * 1000;
    const HEALTH_MAX_MISSING = 32;
    const health = { notes: [], seen: {}, missing: {}, order: [], unsent: {}, pending: {}, lastMiss: {}, warned: {}, timer: null, waitingUser: false, lastReport: 0, sent: 0 };

    // WebVersion is null inside a native app wrapper, where ApiClient.appVersion() is the app's version.
    function webInfo() {
        const c = client();
        const version = c && typeof c.appVersion === 'function' ? String(c.appVersion()) : null;
        return {
            webVersion: kit.env.has(A.nativeShell.global) ? null : version,
            client: c && typeof c.appName === 'function' ? String(c.appName()) : null,
            clientVersion: version
        };
    }

    function webText(info) {
        return info.webVersion ? 'web ' + info.webVersion : 'web unknown, ' + info.client + ' ' + info.clientVersion;
    }

    function lastReportAt() {
        const stored = Number(kit.storage.get('health:lastReport', 0));
        const at = Math.max(isFinite(stored) ? stored : 0, health.lastReport);
        // A time in the future (the clock went back) doesn't block reports for longer than one interval.
        return at > Date.now() + HEALTH_INTERVAL_MS ? 0 : at;
    }

    function scheduleHealthReport(delay) {
        if (health.timer !== null || Object.keys(health.unsent).length === 0) {
            return;
        }

        const wait = Math.max(delay, lastReportAt() + HEALTH_INTERVAL_MS - Date.now());
        health.timer = setTimeout(function () {
            health.timer = null;
            sendHealthReport();
        }, wait);
    }

    function sendHealthReport() {
        const keys = Object.keys(health.unsent).slice(0, HEALTH_MAX_MISSING);
        if (keys.length === 0) {
            return;
        }

        if (!client() || !checkUser()) {
            if (!health.waitingUser) {
                health.waitingUser = true;
                kit.user.ready().then(function () {
                    health.waitingUser = false;
                    scheduleHealthReport(HEALTH_BATCH_MS);
                });
            }

            return;
        }

        if (Date.now() < lastReportAt() + HEALTH_INTERVAL_MS) {
            // Another tab of this browser reported meanwhile.
            scheduleHealthReport(HEALTH_BATCH_MS);
            return;
        }

        const info = webInfo();
        const body = {
            WebVersion: info.webVersion,
            Client: info.client,
            ClientVersion: info.clientVersion,
            Layout: kit.layout.isTv() ? 'Tv' : (kit.layout.isMobile() ? 'Mobile' : 'Desktop'),
            Experimental: kit.layout.isExperimental(),
            PluginVersion: build.version,
            Missing: keys.map(function (key) {
                return { Anchor: health.missing[key].anchor, View: health.missing[key].view, Count: Math.min(1000, health.unsent[key]) };
            })
        };
        health.lastReport = Date.now();
        kit.storage.set('health:lastReport', health.lastReport);
        keys.forEach(function (key) {
            delete health.unsent[key];
        });
        health.sent++;
        // The server answers 204 whatever it did with the report; a failure is not retried (the console warned already).
        kit.api('POST', build.route + '/Diagnostics/ClientHealth', { body: body }).then(null, function () {});
        scheduleHealthReport(HEALTH_BATCH_MS);
    }

    function miss(name, view) {
        const key = name + '|' + view;
        if (!health.missing[key]) {
            health.missing[key] = { anchor: name, view: view, count: 0 };
            health.order.push(key);
        }

        health.missing[key].count++;
        health.unsent[key] = (health.unsent[key] || 0) + 1;
        if (!health.warned[key]) {
            health.warned[key] = true;
            console.warn('[' + pfx + '] web anchor "' + name + '" missing on ' + view + ' (' + webText(webInfo()) + ')');
        }

        scheduleHealthReport(HEALTH_BATCH_MS);
    }

    function routePath() {
        return splitHash().path;
    }

    // entry: an anchor name, or { name, within } to look for it inside another anchor (skipped while that one is missing).
    function anchorPresent(entry) {
        if (!entry.within) {
            return !!kit.anchors.find(entry.name);
        }

        const parent = kit.anchors.find(entry.within);
        return !parent || !!kit.anchors.find(entry.name, parent);
    }

    function expectOne(entry, view, grace) {
        entry = typeof entry === 'string' ? { name: entry } : (entry || {});
        const a = declaredAnchor(entry.name);
        if (!a || (entry.within && !declaredAnchor(entry.within))) {
            return Promise.resolve(false);
        }

        if (a.views.indexOf(view) < 0) {
            report(new Error('kit.health.expect: "' + entry.name + '" is not declared for the view "' + view + '"'));
            return Promise.resolve(false);
        }

        if (anchorPresent(entry)) {
            return Promise.resolve(true);
        }

        const key = entry.name + '|' + view;
        if (health.pending[key]) {
            return health.pending[key];
        }

        // One miss per anchor and view per visit of a route (a new item's details page is a new visit).
        if (health.lastMiss[key] === window.location.hash) {
            return Promise.resolve(false);
        }

        const path = routePath();
        const promise = new Promise(function (resolve) {
            setTimeout(function () {
                delete health.pending[key];
                if (routePath() !== path || anchorPresent(entry)) {
                    // The view is gone, or it rendered the anchor in time.
                    resolve(true);
                    return;
                }

                health.lastMiss[key] = window.location.hash;
                miss(entry.name, view);
                resolve(false);
            }, grace);
        });
        health.pending[key] = promise;
        return promise;
    }

    kit.health = {
        notes: health.notes,
        note: function (kind, detail) {
            health.notes.push({ t: now(), kind: kind, detail: detail });
            if (health.notes.length > 50) {
                health.notes.shift();
            }

            if (!health.seen[kind]) {
                health.seen[kind] = true;
                console.warn('[' + pfx + '] ' + detail);
            }
        },
        // Checks the anchors a feature needs on a view (a name each, or { name, within }); resolves true when all are
        // there, false when one is missing after the grace period (opts.grace, 4 s by default). Never throws.
        expect: function (entries, view, opts) {
            const grace = opts && typeof opts.grace === 'number' ? opts.grace : HEALTH_GRACE_MS;
            try {
                return Promise.all([].concat(entries || []).map(function (entry) {
                    return expectOne(entry, String(view), grace);
                })).then(function (results) {
                    return results.every(Boolean);
                });
            } catch (e) {
                report(e);
                return Promise.resolve(false);
            }
        },
        // This page load's misses: [{ anchor, view, count }].
        missing: function () {
            return health.order.map(function (key) {
                return Object.assign({}, health.missing[key]);
            });
        },
        // { webVersion, client, clientVersion } as a report sends them.
        web: webInfo,
        // How many reports this page load sent.
        reports: function () {
            return health.sent;
        }
    };

    // ---------------------------------------------------------------------- strings

    const strings = {};
    kit.addStrings = function (table) {
        Object.keys(table || {}).forEach(function (key) {
            strings[key] = table[key];
        });
    };
    kit.t = function (key, vars) {
        const template = Object.prototype.hasOwnProperty.call(strings, key) ? strings[key] : key;
        return String(template).replace(/\{(\w+)\}/g, function (match, name) {
            return vars && Object.prototype.hasOwnProperty.call(vars, name) ? String(vars[name]) : match;
        });
    };

    // --------------------------------------------------------------------------- DOM

    // Small DOM builder. Text is always inserted as text nodes, never as HTML.
    function h(tag, props, children) {
        const node = document.createElement(tag);
        Object.keys(props || {}).forEach(function (key) {
            const value = props[key];
            if (value === undefined || value === null || value === false) {
                return;
            }

            if (key === 'class') {
                node.className = value;
            } else if (key === 'text') {
                node.textContent = value;
            } else if (key.indexOf('on') === 0 && typeof value === 'function') {
                node.addEventListener(key.substring(2), value);
            } else if (key === 'value') {
                node.value = value;
            } else {
                node.setAttribute(key, value === true ? '' : value);
            }
        });
        [].concat(children === undefined ? [] : children).forEach(function (child) {
            if (child !== null && child !== undefined && child !== false) {
                node.append(child);
            }
        });
        return node;
    }

    function classes() {
        return Array.prototype.slice.call(arguments).filter(Boolean).join(' ');
    }

    kit.h = h;

    // parent.append() for optional children: null, undefined and false are skipped (append() would add "null" text).
    kit.append = function (parent) {
        Array.prototype.slice.call(arguments, 1).forEach(function (child) {
            if (child !== null && child !== undefined && child !== false) {
                parent.append(child);
            }
        });
        return parent;
    };

    kit.icon = function (name, extraClass) {
        return h('span', { class: classes(N.icon, extraClass, name), 'aria-hidden': 'true' });
    };

    kit.iconButton = function (name, title, onClick) {
        return h('button', {
            type: 'button',
            class: cls('icon-button'),
            title: title,
            'aria-label': title,
            onclick: function (event) {
                event.stopPropagation();
                onClick(event);
            }
        }, kit.icon(name));
    };

    // variant: undefined (neutral), 'primary' (the theme's accent through the native button-submit class) or 'danger'.
    kit.button = function (label, opts) {
        opts = opts || {};
        const variant = opts.variant ? cls('button-' + opts.variant) : null;
        return h('button', {
            type: opts.type || 'button',
            class: classes(cls('button'), variant, opts.variant === 'primary' ? N.buttonSubmit : null, opts.className),
            title: opts.title,
            text: opts.icon ? null : label,
            onclick: opts.onClick
        }, opts.icon ? [kit.icon(opts.icon), h('span', { text: label })] : undefined);
    };

    kit.input = function (props) {
        const attrs = Object.assign({ type: 'text' }, props || {});
        attrs.class = classes(cls('input'), N.input, attrs.class);
        return h('input', attrs);
    };

    // A multi-line text field (a bookmark's note). The native emby-textarea class gives it the theme's field colours, which
    // every 10.11 theme styles together with emby-input's.
    kit.textarea = function (props) {
        const attrs = Object.assign({}, props || {});
        attrs.class = classes(cls('textarea'), N.textarea, attrs.class);
        return h('textarea', attrs);
    };

    kit.select = function (props, choices) {
        const attrs = Object.assign({}, props || {});
        attrs.class = classes(cls('select'), N.select, attrs.class);
        return h('select', attrs, (choices || []).map(function (choice) {
            return h('option', { value: choice.value, text: choice.text });
        }));
    };

    kit.field = function (label, input, extras) {
        const id = nextId('field');
        input.id = id;
        return h('div', { class: cls('field') }, [
            h('label', { for: id, text: label }),
            h('div', { class: cls('field-row') }, [input].concat(extras || []))
        ]);
    };

    kit.setHidden = function (node, hidden) {
        node.classList.toggle(N.hide, !!hidden);
    };

    kit.isHidden = function (node) {
        return node.classList.contains(N.hide);
    };

    // Makes one of our own controls that isn't a button, link or field (a custom slider) a tab stop and a stop
    // of jellyfin-web's TV focus manager: tabindex 0 (unless it has one) and the native class it looks for.
    kit.makeFocusable = function (node) {
        if (!node.hasAttribute('tabindex')) {
            node.setAttribute('tabindex', '0');
        }

        node.classList.add(N.focusable);
        return node;
    };

    kit.upgrade = function (root) {
        if (kit.env.has(A.customElements.global)) {
            window.CustomElements.upgradeSubtree(root);
        }

        return root;
    };

    // A button for the player's on-screen controls, shaped like jellyfin-web's own.
    kit.osdButton = function (opts) {
        return h('button', {
            is: N.iconButton,
            type: 'button',
            class: classes(opts.className, N.autoSize, N.iconButton),
            title: opts.title,
            'aria-label': opts.title,
            onclick: function (event) {
                event.preventDefault();
                event.stopPropagation();
                opts.onClick(event);
            }
        }, h('span', { class: classes(N.iconButtonIconLarge, N.icon, opts.icon), 'aria-hidden': 'true' }));
    };

    // A button for the right side of jellyfin-web's header (next to its search, cast and user buttons), shaped like its
    // own: a paper-icon-button-light with the header's button classes and a Material icon. The click is the plugin's.
    kit.headerButton = function (opts) {
        return h('button', {
            is: N.iconButton,
            type: 'button',
            class: classes(opts.className, N.headerButton, N.headerButtonRight, N.iconButton),
            title: opts.title,
            'aria-label': opts.title,
            onclick: function (event) {
                event.preventDefault();
                opts.onClick(event);
            }
        }, kit.icon(opts.icon));
    };

    // A button for a details page's button row, shaped like jellyfin-web's own.
    kit.detailButton = function (opts) {
        return h('button', {
            is: N.embyButton,
            type: 'button',
            class: classes(N.buttonFlat, opts.className, N.detailButton, N.embyButton),
            title: opts.title,
            'aria-label': opts.title,
            onclick: opts.onClick
        }, h('div', { class: N.detailButtonContent }, h('span', { class: classes(N.icon, N.detailButtonIcon, opts.icon), 'aria-hidden': 'true' })));
    };

    // A details-page section: { section, title, body }. The body gets the plugin's own classes.
    kit.section = function (opts) {
        const title = h('h2', { class: classes(N.sectionTitle, N.sectionTitleCards, N.paddedRight), text: opts.title });
        const body = h('div', { class: classes(opts.bodyClass, N.paddedRight) });
        const attrs = Object.assign({}, opts.attrs || {});
        attrs.class = classes(N.verticalSection, N.detailVerticalSection, opts.className, opts.hidden ? N.hide : null);
        return { section: h('div', attrs, [title, body]), title: title, body: body };
    };

    // ------------------------------------------------------------------ item markup
    // Own card and row markup with native styling classes, never emby-itemscontainer. Item
    // cards and rows carry data-id (N format) and data-type like native ones, so Jellyfin Enhanced's Hidden Content
    // filter, User Ratings' badges and Library Maintenance's action sheet treat them alike; { nativeItemId: false } writes
    // data-<prefix>-item-id instead, for admin tools that must show every item. The kit never touches je-hidden.

    function describe(item) {
        if (item && item.Id !== undefined) {
            return {
                id: item.Id,
                type: item.Type || null,
                isFolder: item.IsFolder === true,
                title: item.Name || '',
                subtitle: item.ProductionYear ? String(item.ProductionYear) : null,
                imageTag: item.ImageTags && item.ImageTags.Primary ? item.ImageTags.Primary : null
            };
        }

        return item || {};
    }

    function normaliseId(id) {
        return String(id).replace(/-/g, '').toLowerCase();
    }

    function identity(attrs, d, opts) {
        if (d.id === undefined || d.id === null) {
            return attrs;
        }

        if (opts && opts.nativeItemId === false) {
            attrs['data-' + pfx + '-item-id'] = normaliseId(d.id);
            if (d.type) {
                attrs['data-' + pfx + '-item-type'] = d.type;
            }
        } else {
            attrs['data-id'] = normaliseId(d.id);
            if (d.type) {
                attrs['data-type'] = d.type;
            }

            if (d.isFolder) {
                attrs['data-isfolder'] = 'true';
            }
        }

        return attrs;
    }

    kit.itemHref = function (id) {
        const c = client();
        const serverId = c && typeof c.serverId === 'function' ? c.serverId() : null;
        return A.detailsRoute.hash + '?id=' + encodeURIComponent(normaliseId(id)) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : '');
    };

    kit.imageUrl = function (id, tag, opts) {
        const c = client();
        if (!c || !tag || typeof c.getScaledImageUrl !== 'function') {
            return null;
        }

        return c.getScaledImageUrl(normaliseId(id), { type: (opts && opts.type) || 'Primary', tag: tag, maxWidth: (opts && opts.maxWidth) || 400 });
    };

    function image(d, fallbackIcon, extraClass) {
        const src = d.imageUrl || (d.id !== undefined ? kit.imageUrl(d.id, d.imageTag) : null);
        return src
            ? h('img', { class: cls(extraClass), src: src, loading: 'lazy', alt: '' })
            : kit.icon(d.icon || fallbackIcon, N.cardImageIcon);
    }

    // The link of a card or row: d.href, else the item's details page; { href: false } means none (an item the viewer
    // can't open, Bookmarks' Unavailable group, keeps its data-id but links nowhere).
    function linkOf(d) {
        if (d.href === false) {
            return null;
        }

        return d.href || (d.id !== undefined && d.id !== null ? kit.itemHref(d.id) : null);
    }

    kit.card = function (item, opts) {
        opts = opts || {};
        const d = describe(item);
        const shape = N.cardShape[opts.shape] ? opts.shape : 'portrait';
        const href = linkOf(d);
        const src = d.imageUrl || (d.id !== undefined ? kit.imageUrl(d.id, d.imageTag) : null);
        const container = h(href ? 'a' : 'div', {
            class: classes(N.cardImageContainer, N.cardContent, N.coveredImage, src ? null : N.defaultCardBackground),
            href: href,
            'aria-label': d.title || null
        }, image(d, opts.icon || 'movie', 'card-image'));
        const texts = [h('div', { class: classes(N.cardText, N.cardTextCentered, N.cardTextFirst) }, h('bdi', { text: d.title || '' }))];
        if (d.subtitle) {
            texts.push(h('div', { class: classes(N.cardText, N.cardTextCentered, N.cardTextSecondary) }, h('bdi', { text: d.subtitle })));
        }

        const attrs = identity({ class: classes(N.card, N.cardShape[shape], cls('card'), opts.className) }, d, opts);
        return h('div', attrs, h('div', { class: classes(N.cardBox, N.cardBoxPadded) }, [
            h('div', { class: N.cardScalable }, [h('div', { class: classes(N.cardPadder, N.cardPadderShape[shape]) }), container])
        ].concat(texts, opts.actions && opts.actions.length ? [h('div', { class: cls('card-actions') }, opts.actions)] : [])));
    };

    // opts (besides nativeItemId, image, icon, actions, className and role): thumb, a node of the plugin's own in place of
    // the kit's image box (Bookmarks' clip row's 16:9 box, which stays out of the native card image classes Jellyfin
    // Enhanced's card tags decorate); lines, nodes of the plugin's own appended to the row's body under the title and the
    // subtitle (a clip row's state, facts and badges), since a plugin never writes the body's native classes itself.
    kit.listItem = function (item, opts) {
        opts = opts || {};
        const d = describe(item);
        const href = linkOf(d);
        const title = href ? h('a', { class: cls('listitem-link'), href: href }, h('bdi', { text: d.title || '' })) : h('bdi', { text: d.title || '' });
        const body = [h('div', { class: N.listItemBodyText }, title)];
        if (d.subtitle) {
            body.push(h('div', { class: classes(N.listItemBodyText, N.secondary) }, h('bdi', { text: d.subtitle })));
        }

        const children = [];
        if (opts.thumb) {
            children.push(opts.thumb);
        } else if (opts.image !== false) {
            children.push(h(href ? 'a' : 'div', { class: classes(N.listItemImage, cls('listitem-image')), href: href, tabindex: href ? '-1' : null, 'aria-hidden': 'true' }, image(d, opts.icon || 'movie', 'listitem-img')));
        }

        children.push(h('div', { class: N.listItemBody }, body.concat(opts.lines || [])));
        if (opts.actions && opts.actions.length) {
            children.push(h('div', { class: cls('listitem-actions') }, opts.actions));
        }

        // A row of a list (role="listitem") unless opts.role says otherwise: false for a row used as a heading
        // inside another list item (Bookmarks' group header), or another role.
        const attrs = identity({ class: classes(N.listItem, N.listItemBorder, cls('listitem'), opts.className), role: opts.role === false ? null : (opts.role || 'listitem') }, d, opts);
        return h('div', attrs, children);
    };

    kit.list = function (children, opts) {
        return h('div', { class: classes(N.paperList, cls('itemlist'), opts && opts.className), role: 'list' }, children || []);
    };

    kit.grid = function (children, opts) {
        return h('div', { class: classes(N.itemsContainer, N.verticalWrap, cls('items'), opts && opts.className) }, children || []);
    };

    // ------------------------------------------------------------------ page parts
    // The toolbar and list parts of a user page (for Bookmarks' My Bookmarks page): a debounced
    // search box, the "Load more" pager and a notice (an empty, error or info line with an optional action).

    const SEARCH_DELAY_MS = 300;
    const PAGER_TEXT = Object.freeze({ more: 'Load more', loading: 'Loading…' });

    // A search field (the native emby-input): opts.onSearch(text) runs with the trimmed text SEARCH_DELAY_MS after the
    // last keystroke, at once on Enter, and only when the text changed since the last call; opts.value, placeholder,
    // maxLength, label (the aria-label), delay. Returns the <input>, with input.setSearch(text): the text set by the
    // caller (a Clear filters button, a state restored), which the memory follows, so the same text typed again is
    // searched again, and which drops a pending debounce.
    kit.searchBox = function (opts) {
        opts = opts || {};
        const delay = typeof opts.delay === 'number' ? opts.delay : SEARCH_DELAY_MS;
        let last = String(opts.value || '').trim();
        let timer = null;
        const input = kit.input({
            type: 'search',
            class: cls('searchbox'),
            value: opts.value || '',
            placeholder: opts.placeholder,
            maxlength: opts.maxLength ? String(opts.maxLength) : null,
            'aria-label': opts.label || opts.placeholder || null,
            autocomplete: 'off',
            spellcheck: 'false'
        });
        const fire = function () {
            clearTimeout(timer);
            timer = null;
            const text = String(input.value || '').trim();
            if (text !== last) {
                last = text;
                if (typeof opts.onSearch === 'function') {
                    opts.onSearch(text);
                }
            }
        };
        input.addEventListener('input', function () {
            clearTimeout(timer);
            timer = setTimeout(fire, delay);
        });
        input.addEventListener('search', fire);
        input.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' && !isComposingKey(event)) {
                event.preventDefault();
                fire();
            }
        });
        input.setSearch = function (text) {
            clearTimeout(timer);
            timer = null;
            input.value = text === undefined || text === null ? '' : String(text);
            last = String(input.value).trim();
        };
        return input;
    };

    // "Load more": a button, pressed for the caller by an IntersectionObserver when it scrolls into view (not on
    // the TV layout, where the remote presses it, and not without the observer). { element, update({ hasMore, busy }),
    // destroy() }: hidden without more, the button disabled and "Loading…" shown while busy. opts.onMore() loads the next
    // page; opts.text names the button.
    kit.pager = function (opts) {
        opts = opts || {};
        const state = { hasMore: false, busy: false };
        let observer = null;
        const more = function () {
            if (state.hasMore && !state.busy && typeof opts.onMore === 'function') {
                opts.onMore();
            }
        };
        const button = kit.button(opts.text || PAGER_TEXT.more, { className: cls('pager-more'), onClick: more });
        const status = h('span', { class: cls('pager-status'), role: 'status' });
        const element = h('div', { class: classes(cls('pager'), N.hide) }, [button, status]);
        const destroy = function () {
            if (observer) {
                observer.disconnect();
                observer = null;
            }
        };
        const update = function (values) {
            values = values || {};
            if (values.hasMore !== undefined) {
                state.hasMore = !!values.hasMore;
            }

            if (values.busy !== undefined) {
                state.busy = !!values.busy;
            }

            kit.setHidden(element, !state.hasMore && !state.busy);
            kit.setHidden(button, !state.hasMore);
            button.disabled = state.busy;
            status.textContent = state.busy ? PAGER_TEXT.loading : '';
            if (state.hasMore && !state.busy && !observer && typeof IntersectionObserver === 'function' && !kit.layout.isTv()) {
                observer = new IntersectionObserver(function (entries) {
                    if (!element.isConnected) {
                        destroy();
                        return;
                    }

                    if (entries.some(function (entry) {
                        return entry.isIntersecting;
                    })) {
                        more();
                    }
                });
                observer.observe(element);
            }
        };
        return { element: element, update: update, destroy: destroy };
    };

    // One sentence for a page's empty, error or info state, with an optional action button: { kind: 'empty' |
    // 'error' | 'info', text, action: { label, onClick } }. An error is role="alert", the others role="status".
    kit.notice = function (opts) {
        opts = opts || {};
        const kind = opts.kind === 'error' || opts.kind === 'info' ? opts.kind : 'empty';
        return h('div', { class: classes(cls('notice'), cls('notice-' + kind)), role: kind === 'error' ? 'alert' : 'status' }, [
            h('span', { class: cls('notice-text'), text: opts.text || '' }),
            opts.action ? kit.button(opts.action.label, { className: cls('notice-action'), onClick: opts.action.onClick }) : null
        ]);
    };

    // ---------------------------------------------------------------------- toasts

    // opts: { action } or { actions: [...] }, each { label, onClick } (a button: the toast goes, then onClick
    // runs) or { label, href } (a link, an emby-linkbutton as kit.pages.link makes, so jellyfin-web's router shows it with
    // no click handler of ours; the toast goes once the click has been handled); timeout (4 s); raised.
    // The toasts' polite live region. kit.start makes it before the first toast (a live region added
    // together with its first message isn't announced by every screen reader); a toast makes it again if it went.
    function toastContainer() {
        let container = document.querySelector('.' + cls('toasts'));
        if (!container && document.body) {
            container = h('div', { class: cls('toasts'), role: 'status', 'aria-live': 'polite' });
            document.body.append(container);
        }

        return container;
    }

    kit.toast = function (message, opts) {
        opts = opts || {};
        const container = toastContainer();

        const raised = opts.raised !== undefined ? !!opts.raised : !!(settings.toastRaised && settings.toastRaised());
        container.classList.toggle(cls('toasts-player'), raised);
        const item = h('div', { class: cls('toast') }, h('span', { text: message }));
        let timer = null;
        const remove = function () {
            clearTimeout(timer);
            item.remove();
        };
        const actions = Array.isArray(opts.actions) ? opts.actions : (opts.action ? [opts.action] : []);
        let links = false;
        actions.forEach(function (action) {
            if (!action || !action.label) {
                return;
            }

            if (action.href) {
                links = true;
                item.append(h('a', {
                    is: N.linkButton,
                    class: cls('toast-action'),
                    href: action.href,
                    onclick: function () {
                        setTimeout(remove, 0);
                    }
                }, action.label));
                return;
            }

            item.append(h('button', {
                type: 'button',
                class: cls('toast-action'),
                text: action.label,
                onclick: function () {
                    remove();
                    if (typeof action.onClick === 'function') {
                        action.onClick();
                    }
                }
            }));
        });
        container.append(item);
        if (links) {
            kit.upgrade(item);
        }

        timer = setTimeout(remove, opts.timeout || 4000);
        return remove;
    };

    // --------------------------------------------------------------------- dialogs
    // Our own overlay: role="dialog", aria-modal, aria-labelledby; Escape closes the topmost one; Tab
    // stays inside; focus returns where it was; key events never reach the player's shortcuts.
    //
    // scopedKeys: Jellyfin Enhanced's capture-phase document listener runs
    // before the panel's own listener and skips only INPUT and TEXTAREA focus, so a key pressed while a button or <select>
    // of ours has the focus would reach its player shortcuts (S on Save opened the subtitle menu). With
    // { scopedKeys: true } the dialog adds a window capture-phase keydown listener that handles only events whose target is
    // inside its panel and stops them there: it is then the dialog's only key handler (nothing of ours sees the event
    // after it): opts.onKey(event) first (return true when handled), else Enter on a button is left to the browser (its
    // click), Escape closes the dialog, Tab stays inside, and every key keeps its default action (typing, <select>
    // arrows). Removed when the dialog closes; events outside the panel are not touched. This is the documented exception
    // to "no capture listener on window or document": it overrides nothing outside our own dialog.
    //
    // A key of an IME composition (isComposing, or keyCode 229) is the composition's (its Enter
    // commits, its Escape cancels), never Save or Skip, and is still kept from the player; a TV remote's or gamepad's Back
    // key closes the dialog as Escape does; and on the TV layout the D-pad moves the focus through the panel, since
    // jellyfin-web's own key navigation (a window bubble listener) never runs inside a scopedKeys dialog.
    //
    // The focus (trap it, and restore it on close; a dialog opened from a row's button
    // left the focus on that button behind the backdrop, so on the TV layout the D-pad and Enter reached the page instead,
    // and Enter opened a second dialog over the first): once the caller has filled the body (a microtask after the call),
    // a dialog the focus isn't inside yet takes it, on opts.initialFocus (an element, or a function answering one) when it
    // can take it, else the body's first focusable control, else the panel's (the header's Close). With scopedKeys, a key
    // pressed while nothing has the focus (its target the page's body: the control that had it was removed or disabled)
    // brings the focus back into the topmost dialog the same way, and goes no further; Escape and Back still close. On
    // close the focus returns to what had it before, or, when that can't take it any more (removed, hidden, disabled), to
    // opts.returnFocus (an element, or a function answering one, asked then).

    const openDialogs = [];

    // The keys TV remotes and gamepads send, as jellyfin-web's keyboardNavigation.js maps them (10.11.5): Back by keyCode
    // (webOS 461, Tizen 10009; Xbox's GamepadB 196) or by name, and the directions by keyCode (Xbox's Navigation* 138-141,
    // GamepadDPad* 203-206 and GamepadLeftThumb* 211-214) next to the arrow keys' own.
    const BACK_KEY_CODES = [196, 461, 10009];
    const BACK_KEY_NAMES = ['Back', 'BrowserBack', 'XF86Back', 'GamepadB'];
    const ARROW_KEY_CODES = {
        37: 'ArrowLeft', 38: 'ArrowUp', 39: 'ArrowRight', 40: 'ArrowDown',
        138: 'ArrowUp', 139: 'ArrowDown', 140: 'ArrowLeft', 141: 'ArrowRight',
        203: 'ArrowUp', 204: 'ArrowDown', 205: 'ArrowLeft', 206: 'ArrowRight',
        211: 'ArrowUp', 212: 'ArrowDown', 213: 'ArrowRight', 214: 'ArrowLeft'
    };
    // An <input> of one of these types is a control, not a text field (jellyfin-web's isInteractiveElement).
    const NON_TEXT_INPUT_TYPES = ['button', 'checkbox', 'color', 'file', 'hidden', 'image', 'radio', 'reset', 'submit'];

    function isComposingKey(event) {
        return event.isComposing === true || event.keyCode === 229;
    }

    function isBackKey(event) {
        return BACK_KEY_CODES.indexOf(event.keyCode) >= 0 || BACK_KEY_NAMES.indexOf(event.key) >= 0;
    }

    // Hisense's VIDAA TVs send their remote's Back as Backspace. jellyfin-web (10.11.5 keyboardNavigation.js) takes Backspace
    // for Back on a browser its browser.js knows for one (tv, hisense and vidaa in the user agent), without exempting a text
    // field. A kit dialog takes it for Back there and on the TV layout, but never while the focus is in something editable
    // (where Backspace deletes a character); on any other browser's desktop or mobile layout Backspace stays as it was, so it
    // can't throw a dialog's edits away from a checkbox or a button.
    function isHisenseTv() {
        const ua = typeof navigator !== 'undefined' && navigator && navigator.userAgent ? String(navigator.userAgent).toLowerCase() : '';
        return ua.indexOf('hisense') >= 0 && ua.indexOf('vidaa') >= 0 && ua.indexOf('tv') >= 0 && ua.indexOf('oculusbrowser') < 0;
    }

    // An <input> of a text type, a <textarea>, a <select>, or contenteditable (the nearest contenteditable attribute decides).
    function isEditable(el) {
        if (!el || !el.tagName) {
            return false;
        }

        if (isTextField(el) || el.tagName === 'SELECT' || el.isContentEditable === true) {
            return true;
        }

        for (let node = el; node && typeof node.getAttribute === 'function'; node = node.parentNode) {
            const value = node.getAttribute('contenteditable');
            if (value !== null && value !== undefined) {
                return String(value).toLowerCase() !== 'false';
            }
        }

        return false;
    }

    // The element a key comes from: the first of the event's composed path, since a field inside an open shadow root (a web
    // component's) reaches a listener outside it with its host as the target, which isn't editable; the
    // target where there is no composed path (an older engine) or it names no element.
    function keyOrigin(event) {
        try {
            const path = typeof event.composedPath === 'function' ? event.composedPath() : null;
            if (path && path.length > 0 && path[0] && path[0].tagName) {
                return path[0];
            }
        } catch (e) {
            // no composed path: the target
        }

        return event.target;
    }

    // The keys that close a dialog: Escape, a remote's Back, and Backspace as above.
    function isDialogBackKey(event) {
        if (event.key === 'Escape' || isBackKey(event)) {
            return true;
        }

        return (event.key === 'Backspace' || (event.keyCode === 8 && !event.key)) && !event.ctrlKey && !event.altKey && !event.metaKey
            && (kit.layout.isTv() || isHisenseTv()) && !isEditable(keyOrigin(event));
    }

    function arrowOf(event) {
        const key = ARROW_KEY_CODES[event.keyCode] || event.key;
        return key === 'ArrowUp' || key === 'ArrowDown' || key === 'ArrowLeft' || key === 'ArrowRight' ? key : null;
    }

    // A text field keeps ArrowLeft and ArrowRight for its caret (jellyfin-web's rule on the TV layout).
    function isTextField(el) {
        if (!el || !el.tagName) {
            return false;
        }

        if (el.tagName === 'TEXTAREA') {
            return true;
        }

        return el.tagName === 'INPUT' && NON_TEXT_INPUT_TYPES.indexOf(String(el.type || el.getAttribute('type') || 'text').toLowerCase()) < 0;
    }

    // A <details>' own <summary> takes the focus too (Library Maintenance's Move: its "Every file" list).
    function focusable(root) {
        const candidates = root.querySelectorAll('button, [href], input, select, textarea, [tabindex], details > summary');
        return Array.prototype.slice.call(candidates).filter(function (el) {
            return !el.disabled && el.getAttribute('tabindex') !== '-1' && el.offsetParent !== null;
        });
    }

    // Whether an element can take the focus now: in the page, a control, not disabled, and shown (the browser gives an
    // element under display:none no box, and won't focus it).
    function canTakeFocus(el) {
        return !!el && el !== document.body && el.isConnected === true && typeof el.focus === 'function' && !el.disabled
            && typeof el.getClientRects === 'function' && el.getClientRects().length > 0;
    }

    function focusTarget(value) {
        try {
            return typeof value === 'function' ? value() : value;
        } catch (e) {
            report(e);
            return null;
        }
    }

    // The focus to the previous or next focusable element of the panel, in order, without wrapping (as jellyfin-web's
    // own focus moves); from outside the panel, to its first or last.
    function moveFocus(panel, forward) {
        const items = focusable(panel);
        const index = items.indexOf(document.activeElement);
        const next = index < 0 ? (forward ? 0 : items.length - 1) : index + (forward ? 1 : -1);
        if (next >= 0 && next < items.length) {
            items[next].focus();
        }
    }

    kit.dialog = function (opts) {
        opts = typeof opts === 'string' ? { title: opts } : (opts || {});
        const titleId = nextId('dialog') + '-title';
        const dismissOnBackdrop = !opts.modal;
        const closeHandlers = [];
        const previousFocus = document.activeElement;
        const body = h('div', { class: cls('panel-body') });
        let closed = false;
        let backdrop = null;
        let scoped = null;
        const close = function () {
            if (closed) {
                return;
            }

            closed = true;
            const index = openDialogs.indexOf(entry);
            if (index >= 0) {
                openDialogs.splice(index, 1);
            }

            if (openDialogs.length === 0) {
                document.removeEventListener('keydown', onDocumentKeyDown, true);
            }

            if (scoped) {
                window.removeEventListener('keydown', scoped, true);
            }

            backdrop.remove();
            closeHandlers.forEach(function (handler) {
                try {
                    handler();
                } catch (e) {
                    report(e);
                }
            });
            restoreFocus();
        };
        // Back to what had the focus before the dialog opened, else to opts.returnFocus (the caller's place for it: what
        // had it was removed, hidden or disabled meanwhile, a row deleted or a bar emptied).
        const restoreFocus = function () {
            const targets = [previousFocus, focusTarget(opts.returnFocus)];
            for (let i = 0; i < targets.length; i++) {
                const target = targets[i];
                if (!canTakeFocus(target)) {
                    continue;
                }

                try {
                    target.focus();
                } catch (e) {
                    // the element can't take focus any more
                }

                if (document.activeElement === target) {
                    return;
                }
            }
        };
        // Into the panel when the focus isn't there (above): opts.initialFocus, the body's first control, the panel's.
        const focusInitial = function () {
            if (closed || panel.contains(document.activeElement)) {
                return;
            }

            const wanted = focusTarget(opts.initialFocus);
            const targets = [wanted && panel.contains(wanted) ? wanted : null].concat(focusable(body), focusable(panel));
            for (let i = 0; i < targets.length; i++) {
                if (canTakeFocus(targets[i])) {
                    targets[i].focus();
                    return;
                }
            }
        };
        const panel = h('div', { class: classes(cls('panel'), N.dialog), role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId }, [
            h('div', { class: cls('panel-header') }, [h('h3', { id: titleId, text: opts.title || '' }), kit.iconButton('close', 'Close', close)]),
            body
        ]);

        panel.addEventListener('keydown', function (event) {
            if (event.key === 'Tab') {
                const items = focusable(panel);
                if (items.length > 0) {
                    const first = items[0];
                    const last = items[items.length - 1];
                    if (event.shiftKey && (document.activeElement === first || !panel.contains(document.activeElement))) {
                        event.preventDefault();
                        last.focus();
                    } else if (!event.shiftKey && document.activeElement === last) {
                        event.preventDefault();
                        first.focus();
                    }
                }
            }

            // Keep typing (space, arrows, B...) away from the player's keyboard shortcuts.
            event.stopPropagation();
        });
        if (opts.scopedKeys) {
            scoped = function (event) {
                const target = event.target;
                if (!target || target === window) {
                    return;
                }

                if (!panel.contains(target)) {
                    // Nothing has the focus (the page's body has the key: what had it was removed or disabled) while this
                    // is the topmost dialog: the key brings the focus back into it and goes no further, so the page behind
                    // never gets it. Escape and Back (isDialogBackKey) are left to close the dialog; a composition's keys to the composition.
                    if ((target === document.body || target === document.documentElement) && openDialogs[openDialogs.length - 1] === entry
                        && !isComposingKey(event) && !isDialogBackKey(event)) {
                        event.preventDefault();
                        event.stopPropagation();
                        focusInitial();
                    }

                    return;
                }

                // A key of an IME composition is the composition's: not onKey's, not Escape, not Tab.
                if (isComposingKey(event)) {
                    event.stopPropagation();
                    return;
                }

                let handled = false;
                if (typeof opts.onKey === 'function') {
                    try {
                        handled = opts.onKey(event) === true;
                    } catch (e) {
                        report(e);
                    }
                }

                if (!handled && isDialogBackKey(event)) {
                    event.preventDefault();
                    close();
                } else if (!handled && event.key === 'Tab') {
                    const items = focusable(panel);
                    if (items.length > 0) {
                        const first = items[0];
                        const last = items[items.length - 1];
                        if (event.shiftKey && (document.activeElement === first || !panel.contains(document.activeElement))) {
                            event.preventDefault();
                            last.focus();
                        } else if (!event.shiftKey && document.activeElement === last) {
                            event.preventDefault();
                            first.focus();
                        }
                    }
                } else if (!handled && kit.layout.isTv()) {
                    // The D-pad: Up and Down (and Left and Right off a text field) move the focus through the panel. The field
                    // is the key's origin, so one inside a web component's open shadow root keeps its caret too.
                    const arrow = arrowOf(event);
                    if (arrow && !((arrow === 'ArrowLeft' || arrow === 'ArrowRight') && isTextField(keyOrigin(event)))) {
                        event.preventDefault();
                        moveFocus(panel, arrow === 'ArrowDown' || arrow === 'ArrowRight');
                    }
                }

                // Nothing after this listener sees the key: not Jellyfin Enhanced, not jellyfin-web, not our own panel.
                event.stopPropagation();
            };
            window.addEventListener('keydown', scoped, true);
        }

        backdrop = h('div', { class: cls('backdrop') }, panel);
        backdrop.addEventListener('mousedown', function (event) {
            if (dismissOnBackdrop && event.target === backdrop) {
                close();
            }
        });
        const entry = { close: close };
        if (openDialogs.length === 0) {
            document.addEventListener('keydown', onDocumentKeyDown, true);
        }

        openDialogs.push(entry);
        document.body.append(backdrop);
        // After the caller's own code (which fills the body, and may focus a control of it itself).
        Promise.resolve().then(focusInitial);

        return {
            body: body,
            panel: panel,
            close: close,
            onClose: function (handler) {
                closeHandlers.push(handler);
            }
        };
    };

    function onDocumentKeyDown(event) {
        if (isDialogBackKey(event) && !isComposingKey(event) && openDialogs.length > 0) {
            event.preventDefault();
            event.stopPropagation();
            openDialogs[openDialogs.length - 1].close();
        }
    }

    kit.dialog.isOpen = function () {
        return openDialogs.length > 0;
    };

    // Resolves true when confirmed, false when cancelled or dismissed; opts.note, a second, quieter line under the
    // message (a warning to read before confirming). A scopedKeys dialog: without it the panel's own
    // listener stops every key in the bubble phase, before jellyfin-web's key navigation (a window bubble listener) sees
    // it, so on the TV layout the D-pad never reached the confirm button.
    kit.confirm = function (opts) {
        return new Promise(function (resolve) {
            let answered = false;
            const dialog = kit.dialog({ title: opts.title, modal: true, scopedKeys: true });
            const answer = function (value) {
                answered = true;
                resolve(value);
                dialog.close();
            };
            dialog.onClose(function () {
                if (!answered) {
                    resolve(false);
                }
            });
            kit.append(dialog.body,
                opts.message ? h('p', { text: opts.message }) : null,
                opts.note ? h('p', { class: cls('confirm-note'), text: opts.note }) : null,
                h('div', { class: cls('actions') }, [
                    kit.button(opts.cancelLabel || 'Cancel', { onClick: function () { answer(false); } }),
                    kit.button(opts.confirmLabel || 'OK', { variant: 'primary', onClick: function () { answer(true); } })
                ]));
        });
    };

    // A dialog of jellyfin-web's own dialogHelper (window.Dashboard.dialogHelper),
    // shaped like its form dialogs: its backdrop, focus scope and TV sizing, and a history entry, so Back closes the
    // dialog and never leaves the page. For flows started from a native action sheet, once the sheet has closed
    // (kit.actionSheet calls onSelect after its close event). The handle is kit.dialog's ({ body, panel, close, onClose })
    // plus native: true; without dialogHelper (another web client) it is a kit.dialog, with native: false. opts: title,
    // size (dialogHelper's: 'small' by default, 'medium', 'fullscreen'), trapFocus (below), autoFocus: false (the
    // caller focuses its own first control; else dialogHelper moves the focus to the first focusable, the
    // header's back button, once its entry animation ends, taking it from wherever the caller put it). Escape closes it
    // only on the TV layout, as jellyfin-web's own dialogs, unless trapFocus is set. Opt-in:
    // returnFocus (an element, or a function answering one, asked then): once the dialog has closed and
    // dialogHelper has put the focus back, when it is nowhere (the page's body, or gone with the dialog), it goes there,
    // as kit.dialog's returnFocus (the menu's trigger: kit.actionSheet's ctx.trigger); escapeClearsText (with
    // trapFocus): Escape in a text field that has text empties it (an input event follows, as typing would) and leaves
    // the dialog open; the next Escape closes it. Without them the focus and Escape are as before.
    // Whether a dialogHelper dialog's container is the last one in the body: no jellyfin-web dialog or sheet is over it.
    function isTopmostContainer(dlg) {
        const container = dlg.parentNode;
        if (!container || container.parentNode !== document.body) {
            return false;
        }

        const containers = Array.prototype.slice.call(document.body.children).filter(function (node) {
            return node.classList && node.classList.contains(A.nativeDialog.container);
        });
        return containers[containers.length - 1] === container;
    }

    // A text field with text in it, which Escape empties first with escapeClearsText (not a checkbox, a button or a select).
    function clearsText(target) {
        const tag = target && target.tagName ? String(target.tagName).toUpperCase() : '';
        const type = tag === 'INPUT' ? String(target.getAttribute('type') || 'text').toLowerCase() : '';
        const text = tag === 'TEXTAREA' || (tag === 'INPUT' && ['text', 'search', 'url', 'email', 'tel', 'number', 'password'].indexOf(type) >= 0);
        return text && !target.disabled && !target.readOnly && String(target.value || '') !== '';
    }

    function dialogHelper() {
        const dashboard = window.Dashboard;
        const helper = dashboard && dashboard.dialogHelper;
        return helper && typeof helper.createDialog === 'function' && typeof helper.open === 'function' && typeof helper.close === 'function' ? helper : null;
    }

    kit.nativeDialog = function (opts) {
        opts = typeof opts === 'string' ? { title: opts } : (opts || {});
        const helper = dialogHelper();
        if (!helper) {
            const fallback = kit.dialog(opts);
            fallback.native = false;
            return fallback;
        }

        const titleId = nextId('native-dialog') + '-title';
        const closeHandlers = [];
        let closed = false;
        const createOptions = { size: opts.size || A.dialogHelper.size, removeOnClose: true, scrollY: false };
        if (opts.autoFocus === false) {
            createOptions.autoFocus = false;
        }

        const dlg = helper.createDialog(createOptions);
        const close = function () {
            if (!closed) {
                helper.close(dlg);
            }
        };
        const body = h('div', { class: classes(N.dialogContentInner, N.dialogContentCentered, cls('native-dialog-body')) });
        dlg.classList.add(N.formDialog, cls('native-dialog'));
        dlg.setAttribute('role', 'dialog');
        dlg.setAttribute('aria-modal', 'true');
        dlg.setAttribute('aria-labelledby', titleId);
        dlg.append(
            h('div', { class: N.formDialogHeader }, [
                h('button', { is: N.iconButton, type: 'button', class: classes(N.btnCancel, N.autoSize), title: 'Close', 'aria-label': 'Close', onclick: close },
                    kit.icon('arrow_back')),
                h('h3', { id: titleId, class: N.formDialogHeaderTitle, text: opts.title || '' })
            ]),
            h('div', { class: classes(N.formDialogContent, N.smoothScrollY) }, body));
        // opts.trapFocus: Tab and Shift+Tab stay inside the dialog, wrapping as kit.dialog's do, and
        // Escape closes it on every layout. dialogHelper has neither (its focus scope moves the TV D-pad only), so Tab
        // past the last control reached the page behind the backdrop. A window capture-phase listener, as dialog's
        // scopedKeys: only Tab and Escape, only for a target inside the dialog (or the page's body, when nothing has the
        // focus), and only while the dialog's container is the last one in the body (a jellyfin-web dialog or sheet opened
        // over it keeps its keys); those two keys then go no further, so Jellyfin Enhanced's capture listener never sees
        // them. A composition's keys stay the composition's. Removed on close.
        const trap = opts.trapFocus ? function (event) {
            const target = event.target;
            if ((event.key !== 'Tab' && event.key !== 'Escape') || isComposingKey(event) || !target || target === window) {
                return;
            }

            const onBody = target === document.body || target === document.documentElement;
            if ((!onBody && !dlg.contains(target)) || !isTopmostContainer(dlg)) {
                return;
            }

            event.stopPropagation();
            event.preventDefault();
            if (event.key === 'Escape') {
                if (opts.escapeClearsText && clearsText(target)) {
                    target.value = '';
                    target.dispatchEvent(new Event('input', { bubbles: true }));
                    return;
                }

                close();
                return;
            }

            const items = focusable(dlg);
            if (items.length === 0) {
                return;
            }

            const index = items.indexOf(document.activeElement);
            const next = event.shiftKey
                ? (index <= 0 ? items.length - 1 : index - 1)
                : (index < 0 || index >= items.length - 1 ? 0 : index + 1);
            items[next].focus();
        } : null;
        if (trap) {
            window.addEventListener('keydown', trap, true);
        }

        dlg.addEventListener(A.dialogHelper.closeEvent, function () {
            if (closed) {
                return;
            }

            closed = true;
            if (trap) {
                window.removeEventListener('keydown', trap, true);
            }

            closeHandlers.forEach(function (handler) {
                try {
                    handler();
                } catch (e) {
                    report(e);
                }
            });
            if (opts.returnFocus) {
                // After dialogHelper's own focus restore (it runs around this event): only when that left it nowhere.
                setTimeout(function () {
                    const active = document.activeElement;
                    if (active && active !== document.body && active !== document.documentElement && active.isConnected && !dlg.contains(active)) {
                        return;
                    }

                    const target = focusTarget(opts.returnFocus);
                    if (canTakeFocus(target)) {
                        try {
                            target.focus();
                        } catch (e) {
                            // the element can't take focus any more
                        }
                    }
                }, 0);
            }
        });
        kit.upgrade(dlg);
        Promise.resolve(helper.open(dlg)).then(null, report);
        return {
            body: body,
            panel: dlg,
            close: close,
            onClose: function (handler) {
                closeHandlers.push(handler);
            },
            native: true
        };
    };

    // ------------------------------------------------------------------------- API
    // fetch(ApiClient.getUrl(path, query)) with the MediaBrowser token header, JSON in and out, a 30 s timeout, and
    // ProblemDetails turned into KitApiError. Never sent before ApiClient.getCurrentUserId() is set.

    let sessionExpiredRaised = false;

    function authorizationHeader(c) {
        const clean = function (value) {
            return String(value === undefined || value === null ? '' : value).replace(/"/g, '');
        };
        return 'MediaBrowser Client="' + clean(c.appName()) +
            '", Device="' + clean(encodeURIComponent(c.deviceName())) +
            '", DeviceId="' + clean(c.deviceId()) +
            '", Version="' + clean(c.appVersion()) +
            '", Token="' + clean(c.accessToken()) + '"';
    }

    // By the object's tag, not instanceof: a File from another frame (or realm) is a Blob too.
    function isBinaryBody(value) {
        const tag = Object.prototype.toString.call(value);
        return tag === '[object Blob]' || tag === '[object File]' || tag === '[object ArrayBuffer]'
            || (typeof ArrayBuffer === 'function' && ArrayBuffer.isView(value));
    }

    kit.api = async function (method, path, opts) {
        opts = opts || {};
        const c = client();
        if (!c || !c.accessToken() || !c.getCurrentUserId()) {
            throw new KitApiError(0, 'You are not signed in.');
        }

        const init = { method: method, headers: { Accept: 'application/json', Authorization: authorizationHeader(c) } };
        if (opts.text !== undefined) {
            // A body sent as it is (a file's JSON, which the server hashes as it came), never parsed and written again. Only
            // a string: String(null) would send "null".
            if (typeof opts.text !== 'string') {
                throw new TypeError('kit.api: text must be a string (a file goes as blob).');
            }

            init.headers['Content-Type'] = opts.contentType || 'application/json';
            init.body = opts.text;
        } else if (opts.blob !== undefined) {
            // A file's bytes as they are (a Blob, such as a file field's File, an ArrayBuffer or a typed array): byte for
            // byte, read by fetch itself, never a string copy of it.
            if (!isBinaryBody(opts.blob)) {
                throw new TypeError('kit.api: blob must be a Blob, an ArrayBuffer or a typed array.');
            }

            init.headers['Content-Type'] = opts.contentType || 'application/json';
            init.body = opts.blob;
        } else if (opts.body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(opts.body);
        }

        const controller = typeof AbortController === 'function' ? new AbortController() : null;
        let timedOut = false;
        const timer = controller ? setTimeout(function () {
            timedOut = true;
            controller.abort();
        }, opts.timeout || API_TIMEOUT_MS) : null;
        const onAbort = function () {
            if (controller) {
                controller.abort();
            }
        };
        if (controller) {
            init.signal = controller.signal;
            if (opts.signal) {
                if (opts.signal.aborted) {
                    controller.abort();
                } else {
                    opts.signal.addEventListener('abort', onAbort);
                }
            }
        }

        if (timing.firstApi === null) {
            timing.firstApi = now();
        }

        let response;
        try {
            response = await fetch(c.getUrl(path, opts.query), init);
        } catch (e) {
            if (timedOut) {
                throw new KitApiError(0, 'The server took too long to answer.');
            }

            if (opts.signal && opts.signal.aborted) {
                throw e;
            }

            throw new KitApiError(0, 'The server could not be reached.');
        } finally {
            clearTimeout(timer);
            if (opts.signal) {
                opts.signal.removeEventListener('abort', onAbort);
            }
        }

        if (response.ok && opts.raw) {
            return response;
        }

        const text = response.status === 204 ? '' : await response.text();
        let data = null;
        if (text) {
            try {
                data = JSON.parse(text);
            } catch (e) {
                data = null;
            }
        }

        if (!response.ok) {
            const problem = data && typeof data === 'object' ? data : null;
            if (response.status === 401 && !sessionExpiredRaised) {
                sessionExpiredRaised = true;
                document.dispatchEvent(new CustomEvent(pfx + ':session-expired', { detail: { path: path } }));
            }

            const fallback = response.status === 401
                ? 'Your session expired. Sign in again.'
                : 'The server returned an error (' + response.status + ').';
            throw new KitApiError(response.status, (problem && (problem.detail || problem.title)) || fallback, problem);
        }

        return data;
    };

    // ------------------------------------------------------------------------ user
    // The signed-in user appears 171-213 ms after window.ApiClient, is null on the login page, and can change without a
    // reload (sign-out, sign-in). User work waits for it and is re-checked on every viewshow.

    const userState = { id: null, listeners: [], waiters: [], cache: null };

    function checkUser() {
        const c = client();
        const id = c && typeof c.getCurrentUserId === 'function' ? c.getCurrentUserId() || null : null;
        if (id === userState.id) {
            return id;
        }

        const previous = userState.id;
        userState.id = id;
        userState.cache = null;
        if (id && timing.user === null) {
            timing.user = now();
        }

        if (id) {
            sessionExpiredRaised = false;
            const waiters = userState.waiters;
            userState.waiters = [];
            waiters.forEach(function (resolve) {
                resolve(id);
            });
        }

        userState.listeners.forEach(function (listener) {
            try {
                listener(id, previous);
            } catch (e) {
                report(e);
            }
        });
        return id;
    }

    kit.user = {
        id: function () {
            return userState.id;
        },
        ready: function () {
            return userState.id ? Promise.resolve(userState.id) : new Promise(function (resolve) {
                userState.waiters.push(resolve);
            });
        },
        onChange: function (listener) {
            userState.listeners.push(listener);
        },
        // The signed-in user's UserDto, cached per user id. A failed request (a network blip) is not cached: the next
        // call asks again.
        current: function () {
            const id = userState.id;
            const c = client();
            if (!id || !c) {
                return Promise.resolve(null);
            }

            if (!userState.cache || userState.cache.id !== id) {
                const promise = new Promise(function (resolve) {
                    resolve(c.getCurrentUser ? c.getCurrentUser() : c.getUser(id));
                });
                const entry = { id: id, promise: promise };
                userState.cache = entry;
                promise.catch(function () {
                    if (userState.cache === entry) {
                        userState.cache = null;
                    }
                });
            }

            return userState.cache.promise;
        },
        // Cosmetic only: it can lag a demotion until the next page load. Controls that call admin endpoints are shown from
        // the plugin's Capabilities.IsAdmin, which the server computes per request.
        isAdmin: function () {
            return kit.user.current().then(function (user) {
                return !!(user && user.Policy && user.Policy.IsAdministrator);
            });
        }
    };

    // --------------------------------------------------------------------- storage

    function localStore() {
        try {
            return window.localStorage || null;
        } catch (e) {
            return null;
        }
    }

    kit.storage = {
        key: function (name) {
            return pfx + ':' + name;
        },
        get: function (name, fallback) {
            try {
                const store = localStore();
                const raw = store ? store.getItem(kit.storage.key(name)) : null;
                return raw === null || raw === undefined ? fallback : JSON.parse(raw);
            } catch (e) {
                return fallback;
            }
        },
        set: function (name, value) {
            try {
                const store = localStore();
                if (!store) {
                    return false;
                }

                store.setItem(kit.storage.key(name), JSON.stringify(value));
                return true;
            } catch (e) {
                return false;
            }
        },
        remove: function (name) {
            try {
                const store = localStore();
                if (store) {
                    store.removeItem(kit.storage.key(name));
                }
            } catch (e) {
                // storage unavailable
            }
        },
        // The names (without the prefix) of this plugin's entries that start with `start` (User Ratings: a member's drafts
        // removed when they sign out); none when the storage can't be read.
        names: function (start) {
            const found = [];
            try {
                const store = localStore();
                const head = kit.storage.key(String(start || ''));
                for (let i = 0; store && i < store.length; i++) {
                    const key = store.key(i);
                    if (typeof key === 'string' && key.indexOf(head) === 0) {
                        found.push(key.substring(pfx.length + 1));
                    }
                }
            } catch (e) {
                // storage unavailable
            }

            return found;
        }
    };

    // ---------------------------------------------------------------------- format

    function pad(value) {
        return String(value).padStart(2, '0');
    }

    kit.format = {
        ticksPerSecond: TICKS_PER_SECOND,
        // 1:02:03, or 2:03 under an hour; precise adds tenths (1:02:03.4).
        time: function (ticks, precise) {
            const totalMs = Math.max(0, Math.round((ticks || 0) / 10000));
            const hours = Math.floor(totalMs / 3600000);
            const minutes = Math.floor(totalMs / 60000) % 60;
            const seconds = Math.floor(totalMs / 1000) % 60;
            let text = hours > 0 ? hours + ':' + pad(minutes) + ':' + pad(seconds) : minutes + ':' + pad(seconds);
            if (precise) {
                text += '.' + (Math.floor(totalMs / 100) % 10);
            }

            return text;
        },
        // Accepts "ss", "m:ss" or "h:mm:ss", with an optional fraction on the last part. Returns ticks, null for empty
        // input, or NaN when the text can't be parsed.
        parseTime: function (text) {
            const value = String(text || '').trim();
            if (!value) {
                return null;
            }

            const parts = value.split(':');
            if (parts.length > 3) {
                return NaN;
            }

            let seconds = 0;
            for (let i = 0; i < parts.length; i++) {
                const pattern = i === parts.length - 1 ? /^\d+(\.\d+)?$/ : /^\d+$/;
                if (!pattern.test(parts[i])) {
                    return NaN;
                }

                seconds = seconds * 60 + Number(parts[i]);
            }

            return Math.round(seconds * TICKS_PER_SECOND);
        },
        size: function (bytes) {
            if (typeof bytes !== 'number') {
                return '';
            }

            const units = ['B', 'KB', 'MB', 'GB', 'TB'];
            let value = bytes;
            let unit = 0;
            while (value >= 1024 && unit < units.length - 1) {
                value /= 1024;
                unit++;
            }

            return (unit === 0 ? value : value.toFixed(value < 10 ? 1 : 0)) + ' ' + units[unit];
        },
        date: function (value, opts) {
            const date = value instanceof Date ? value : new Date(value);
            return isNaN(date.getTime()) ? '' : date.toLocaleDateString(undefined, opts);
        },
        dateTime: function (value, opts) {
            const date = value instanceof Date ? value : new Date(value);
            return isNaN(date.getTime()) ? '' : date.toLocaleString(undefined, opts);
        },
        // How long ago a moment was, in words: "just now", "5 minutes ago", "3 hours ago", "2 days ago", then the date.
        ago: function (value) {
            const date = value instanceof Date ? value : new Date(value);
            if (isNaN(date.getTime())) {
                return '';
            }

            const seconds = Math.round((Date.now() - date.getTime()) / 1000);
            if (seconds < 60) {
                return 'just now';
            }

            const days = Math.floor(seconds / 86400);
            if (days >= 14) {
                return date.toLocaleDateString();
            }

            const units = [['day', 86400], ['hour', 3600], ['minute', 60]];
            for (let i = 0; i < units.length; i++) {
                const count = Math.floor(seconds / units[i][1]);
                if (count >= 1) {
                    return count + ' ' + units[i][0] + (count === 1 ? '' : 's') + ' ago';
                }
            }

            return 'just now';
        },
        // A rating on a five-star scale with half stars: counts for rendering plus a text label.
        stars: function (value, max) {
            const scale = max || 5;
            const rating = Math.max(0, Math.min(scale, Math.round(Number(value || 0) * 2) / 2));
            const full = Math.floor(rating);
            const half = rating - full === 0.5 ? 1 : 0;
            return { value: rating, full: full, half: half, empty: scale - full - half, label: rating + ' out of ' + scale };
        }
    };

    // ---------------------------------------------------------------------- layout

    kit.layout = {
        isTv: function () {
            return document.documentElement.classList.contains(A.layoutTv.htmlClass);
        },
        isMobile: function () {
            return document.documentElement.classList.contains(A.layoutMobile.htmlClass);
        },
        // The layout without the legacy drawer and header: 10.11's experimental layout, chosen by hand, or Jellyfin 12's
        // modern layout, its default (only Desktop, Mobile and TV in Display mode, saved as desktop-legacy, mobile-legacy
        // and tv, are legacy). KIT_BUILD.jellyfin is the Jellyfin line the plugin is built for, the server's.
        isModern: function () {
            try {
                const store = localStore();
                const value = store ? store.getItem(A.layoutSetting.storageKey) : null;
                if (build.jellyfin === '12') {
                    return !kit.layout.isTv() && A.layoutSetting.legacy12.indexOf(value) < 0;
                }

                return value === A.layoutSetting.experimental;
            } catch (e) {
                return false;
            }
        },
        isExperimental: function () {
            return kit.layout.isModern();
        }
    };

    kit.je = {
        present: function () {
            return kit.env.has(A.jellyfinEnhanced.global);
        },
        // Jellyfin Enhanced 12.14's Reviews section (A.jeReviews) inside scope (the document by default), or null: a
        // presence probe, never health-checked, as for kit.je.tvSectionJump below; a page shows it only while JE's reviews
        // are on (User Ratings' Rate button goes where it is).
        reviews: function (scope) {
            const root = scope && typeof scope.querySelector === 'function' ? scope : document;
            return root.querySelector(A.jeReviews.selector);
        }
    };

    // ------------------------------------------------- Jellyfin Enhanced's reviews on the TV layout
    // Jellyfin Enhanced 12.14 draws its Reviews section on a details page as a closed <details> (A.jeReviews) above the
    // plugins' sections. Chrome keeps a box for a closed <details>' content (its "Write a Review" button reports a rect under
    // the summary) but won't focus it, so jellyfin-web's TV focus manager takes that button for the nearest element below the
    // buttons row, or above a section under the reviews, fails to focus it, and the D-pad stays where it is: such a section can
    // be neither reached nor left with the remote (the TV trap). Two narrow fixes, on the TV layout only, while a plugin
    // has registered a jump:
    //   - kit.je.tvSectionJump(opts): a button of the plugin's in the details buttons row while the reviews are there and
    //     closed and the plugin's section is shown; pressing it scrolls to the section and focuses its first control;
    //   - the controls inside a closed reviews <details>, outside its <summary>, get tabindex="-1", which jellyfin-web's
    //     focus manager skips; each one's own tabindex is kept in A.jeReviews.savedTabindex (shared by every plugin's kit, so
    //     one saves it and one puts it back) and put back as soon as the section opens (its toggle event), or once the layout
    //     isn't the TV's. Nothing else of Jellyfin Enhanced's markup is touched.
    // The kit's body observer (kit.observe) sees Jellyfin Enhanced draw the section late; its toggle event is followed at once.

    const tvJumps = [];
    const watchedReviews = typeof WeakSet === 'function' ? new WeakSet() : null;
    let tvJumpScanning = false;

    function callFinder(finder, arg) {
        try {
            return typeof finder === 'function' ? finder(arg) || null : null;
        } catch (e) {
            report(e);
            return null;
        }
    }

    function isClosedReviews(details) {
        return !!details && !details.hasAttribute('open');
    }

    // The controls of a closed reviews section that jellyfin-web's focus manager would consider: not its summary (the
    // section's own title, which it never offers), not one taken out already (by Jellyfin Enhanced, or by another kit).
    function trappedControls(details) {
        const summary = details.querySelector(A.jeReviews.summary);
        return Array.prototype.slice.call(details.querySelectorAll(A.jeReviews.controls)).filter(function (el) {
            return !(summary && (summary === el || summary.contains(el)));
        });
    }

    function releaseControl(el) {
        const saved = el.getAttribute(A.jeReviews.savedTabindex);
        if (saved === null) {
            return;
        }

        el.removeAttribute(A.jeReviews.savedTabindex);
        if (saved === '') {
            el.removeAttribute('tabindex');
        } else {
            el.setAttribute('tabindex', saved);
        }
    }

    function syncReviewsFocus(tv) {
        Array.prototype.slice.call(document.querySelectorAll('[' + A.jeReviews.savedTabindex + ']')).forEach(function (el) {
            const details = el.closest(A.jeReviews.selector);
            if (!tv || !isClosedReviews(details)) {
                releaseControl(el);
            }
        });
        Array.prototype.slice.call(document.querySelectorAll(A.jeReviews.selector)).forEach(function (details) {
            if (watchedReviews && !watchedReviews.has(details)) {
                watchedReviews.add(details);
                details.addEventListener('toggle', function () {
                    syncTvJumps();
                });
            }

            if (!tv || !isClosedReviews(details)) {
                return;
            }

            trappedControls(details).forEach(function (el) {
                if (el.hasAttribute(A.jeReviews.savedTabindex) || el.getAttribute('tabindex') === '-1') {
                    return;
                }

                el.setAttribute(A.jeReviews.savedTabindex, el.hasAttribute('tabindex') ? el.getAttribute('tabindex') : '');
                el.setAttribute('tabindex', '-1');
            });
        });
    }

    // The control a jump lands on: the plugin's own choice, else the section's first focusable control that is shown, else
    // the section itself (focusable by script only).
    function jumpTarget(jump, section) {
        const chosen = callFinder(jump.opts.first, section);
        if (canTakeFocus(chosen)) {
            return chosen;
        }

        const first = focusable(section).filter(canTakeFocus)[0];
        if (first) {
            return first;
        }

        if (!section.hasAttribute('tabindex')) {
            section.setAttribute('tabindex', '-1');
        }

        return section;
    }

    function runJump(jump) {
        const page = callFinder(jump.opts.page);
        const section = page ? callFinder(jump.opts.target, page) : null;
        if (!section || !canTakeFocus(section)) {
            syncTvJumps();
            return;
        }

        const control = jumpTarget(jump, section);
        control.focus();
        if (typeof control.scrollIntoView === 'function') {
            control.scrollIntoView({ block: 'center' });
        }
    }

    function removeJump(jump) {
        if (jump.button && jump.button.parentNode) {
            jump.button.remove();
        }
    }

    function syncTvJump(jump, tv) {
        const page = tv ? callFinder(jump.opts.page) : null;
        const reviews = page ? page.querySelector(A.jeReviews.selector) : null;
        const section = reviews && isClosedReviews(reviews) ? callFinder(jump.opts.target, page) : null;
        const buttons = section && canTakeFocus(section) ? callFinder(jump.opts.buttons, page) : null;
        if (!buttons) {
            removeJump(jump);
            return;
        }

        if (!jump.button) {
            jump.button = kit.detailButton({
                className: classes(pfx + '-je-jump', jump.opts.className),
                icon: jump.opts.icon || 'arrow_downward',
                title: jump.opts.label,
                onClick: function (event) {
                    if (event && typeof event.preventDefault === 'function') {
                        event.preventDefault();
                    }

                    runJump(jump);
                }
            });
        }

        // Placed once per row: a button already in it stays where it is (moving it would drop the focus it may have).
        if (jump.button.parentNode !== buttons) {
            const before = callFinder(jump.opts.before, buttons);
            buttons.insertBefore(jump.button, before && before.parentNode === buttons ? before : null);
            kit.upgrade(jump.button);
        }
    }

    function syncTvJumps() {
        if (tvJumpScanning || tvJumps.length === 0) {
            return;
        }

        tvJumpScanning = true;
        try {
            const tv = kit.layout.isTv();
            syncReviewsFocus(tv);
            tvJumps.forEach(function (jump) {
                syncTvJump(jump, tv);
            });
        } finally {
            tvJumpScanning = false;
        }
    }

    // opts: { label, icon, className, page() → the shown details page or null, buttons(page) → its buttons row,
    // before(buttons) → the button to go before (or null: at the end), target(page) → the plugin's section, null while it
    // isn't there; first(section) → the control to focus (optional) }. Returns { sync(), button() }.
    kit.je.tvSectionJump = function (opts) {
        if (!opts || typeof opts.page !== 'function' || typeof opts.buttons !== 'function' || typeof opts.target !== 'function' || !opts.label) {
            throw new Error('kit.je.tvSectionJump needs label, page, buttons and target');
        }

        const jump = { opts: opts, button: null };
        tvJumps.push(jump);
        if (tvJumps.length === 1) {
            kit.observe(syncTvJumps);
        } else {
            syncTvJumps();
        }

        return {
            sync: syncTvJumps,
            button: function () {
                return jump.button && jump.button.isConnected ? jump.button : null;
            }
        };
    };

    // -------------------------------------------------------------------- shortcuts
    // Keyboard shortcuts next to Jellyfin Enhanced and jellyfin-web. One notation,
    // shared with the kit's ShortcutCombo (kit/testdata/shortcuts/combos.json holds the cases both pass): the modifiers
    // Shift+, Meta+, Ctrl+, Alt+ in that order, then the key, an ASCII letter upper-cased ('Shift+B', '/', 'ArrowLeft',
    // 'Space'). It is how Jellyfin Enhanced builds the combination of a key press and compares it whole with its configured
    // keys (js/enhanced/events.js), so 'Shift+B' is not 'B'; the one difference is the space bar ('Space' here, ' ' there).
    //
    // One document bubble-phase keydown listener serves every binding of this kit: JE's capture-phase player keys never
    // arrive (it stops them), keys JE or another plugin handled arrive defaultPrevented and are skipped, and so are key
    // repeats, keys typed into a field, and keys pressed while one of our dialogs or a jellyfin-web dialog is open. The
    // first binding whose combination matches handles the press (once per keydown) and the event is defaultPrevented, so
    // the next bubble listener, Bookmarks 1.0's for example, skips it too. Never a capture listener on window or document:
    // one added at script load runs before JE's and would act on JE's own keys as well.

    const MODIFIERS = ['Shift', 'Meta', 'Ctrl', 'Alt'];
    const MODIFIER_ALIASES = {
        shift: 'Shift', meta: 'Meta', cmd: 'Meta', command: 'Meta', win: 'Meta', windows: 'Meta', super: 'Meta',
        ctrl: 'Ctrl', control: 'Ctrl', alt: 'Alt', option: 'Alt', opt: 'Alt'
    };
    const NAMED_KEYS = ['Enter', 'Escape', 'Space', 'Tab', 'Backspace', 'Delete', 'Insert', 'Home', 'End', 'PageUp', 'PageDown',
        'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ContextMenu', 'PrintScreen', 'ScrollLock', 'Pause',
        'MediaPlayPause', 'MediaPlay', 'MediaPause', 'MediaStop', 'MediaTrackNext', 'MediaTrackPrevious',
        'MediaFastForward', 'MediaRewind', 'AudioVolumeUp', 'AudioVolumeDown', 'AudioVolumeMute',
        'BrowserBack', 'BrowserForward', 'BrowserHome', 'BrowserSearch'];
    const KEY_ALIASES = {
        esc: 'Escape', 'return': 'Enter', spacebar: 'Space', up: 'ArrowUp', down: 'ArrowDown', left: 'ArrowLeft', right: 'ArrowRight',
        del: 'Delete', ins: 'Insert', pgup: 'PageUp', pgdn: 'PageDown', 'page up': 'PageUp', 'page down': 'PageDown'
    };
    const namedKeys = {};
    NAMED_KEYS.forEach(function (name) {
        namedKeys[name.toLowerCase()] = name;
    });
    for (let f = 1; f <= 24; f++) {
        namedKeys['f' + f] = 'F' + f;
    }

    Object.keys(KEY_ALIASES).forEach(function (alias) {
        namedKeys[alias] = KEY_ALIASES[alias];
    });

    function textElements(text) {
        return Array.from(text).length;
    }

    function normalizeKey(key) {
        if (key.length === 0) {
            return null;
        }

        if (textElements(key) === 1 && key.length <= 2) {
            if (/^[\s\u0000-\u001f\u007f]$/.test(key)) {
                return null;
            }

            return /^[a-zA-Z]$/.test(key) ? key.toUpperCase() : key;
        }

        const lower = key.toLowerCase();
        if (Object.prototype.hasOwnProperty.call(MODIFIER_ALIASES, lower)) {
            return null;
        }

        return Object.prototype.hasOwnProperty.call(namedKeys, lower) ? namedKeys[lower] : null;
    }

    function compose(modifiers, key) {
        return MODIFIERS.filter(function (m) {
            return modifiers.indexOf(m) >= 0;
        }).map(function (m) {
            return m + '+';
        }).join('') + key;
    }

    const bindings = [];
    let shortcutsListening = false;

    function isTextTarget(target) {
        if (!target || target === document || target === window) {
            return false;
        }

        const tag = target.tagName;
        return target.isContentEditable === true || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
    }

    // The key's origin (keyOrigin): a field inside a web component's open shadow root reaches this listener with its host as
    // the target, which isn't a field, so a shortcut fired while someone typed there.
    function onShortcutKeyDown(event) {
        if (event.defaultPrevented || event.repeat || bindings.length === 0 || isTextTarget(keyOrigin(event))) {
            return;
        }

        if (kit.dialog.isOpen() || document.querySelector('.' + A.nativeDialog.container)) {
            return;
        }

        const combo = kit.shortcuts.comboOf(event);
        if (!combo) {
            return;
        }

        for (let i = 0; i < bindings.length; i++) {
            const binding = bindings[i];
            if (binding.combo !== combo) {
                continue;
            }

            let wanted = true;
            try {
                wanted = typeof binding.when !== 'function' || binding.when(event) !== false;
            } catch (e) {
                report(e);
                wanted = false;
            }

            if (!wanted) {
                continue;
            }

            event.preventDefault();
            try {
                binding.handler(event);
            } catch (e) {
                report(e);
            }

            return;
        }
    }

    kit.shortcuts = {
        // The canonical combination of what someone typed ('shift+b', 'Cmd+K', 'Esc', 'page down'): '' for nothing, null
        // for text that isn't one (a modifier alone, two keys, an unknown name, more than 40 characters).
        normalize: function (text) {
            const trimmed = String(text === null || text === undefined ? '' : text).trim();
            if (trimmed.length === 0) {
                return '';
            }

            if (trimmed.length > 40) {
                return null;
            }

            const modifiers = [];
            let rest = trimmed;
            for (;;) {
                const plus = rest.indexOf('+');
                if (plus <= 0) {
                    break;
                }

                const name = rest.substring(0, plus).trim().toLowerCase();
                if (!Object.prototype.hasOwnProperty.call(MODIFIER_ALIASES, name)) {
                    break;
                }

                if (modifiers.indexOf(MODIFIER_ALIASES[name]) < 0) {
                    modifiers.push(MODIFIER_ALIASES[name]);
                }

                rest = rest.substring(plus + 1).replace(/^\s+/, '');
            }

            const key = normalizeKey(rest.replace(/\s+$/, ''));
            return key === null ? null : compose(modifiers, key);
        },
        // The combination of a keydown event, as Jellyfin Enhanced builds it (events.js), with ' ' as 'Space'.
        comboOf: function (event) {
            const key = event.key === ' ' ? 'Space' : String(event.key || '');
            if (!key) {
                return '';
            }

            const modifiers = [];
            if (event.shiftKey) {
                modifiers.push('Shift');
            }

            if (event.metaKey) {
                modifiers.push('Meta');
            }

            if (event.ctrlKey) {
                modifiers.push('Ctrl');
            }

            if (event.altKey) {
                modifiers.push('Alt');
            }

            return compose(modifiers, /^[a-zA-Z]$/.test(key) ? key.toUpperCase() : key);
        },
        // Whether a keydown is a TV remote's or gamepad's Back key (the rule that closes a dialog, less a Hisense TV's
        // Backspace, which a text field keeps), for a control of ours outside a dialog that cancels on Escape (Bookmarks'
        // inline rename).
        isBackKey: function (event) {
            return !!event && isBackKey(event);
        },
        // The direction of a keydown as jellyfin-web's keyboard navigation reads it ('ArrowLeft', 'ArrowUp',
        // 'ArrowRight', 'ArrowDown'), the TV remotes' and gamepads' D-pad key codes included; null for any other key. For
        // a control of ours that takes some arrows itself and leaves the others to jellyfin-web (User Ratings' slider).
        arrowOf: function (event) {
            return event ? arrowOf(event) : null;
        },
        // The key of a combination, without its modifiers; null for ''.
        keyOf: function (combo) {
            let rest = String(combo || '');
            if (!rest) {
                return null;
            }

            MODIFIERS.forEach(function (m) {
                if (rest.length > m.length + 1 && rest.indexOf(m + '+') === 0) {
                    rest = rest.substring(m.length + 1);
                }
            });
            return rest;
        },
        // Whether a keyboard can send the combination's key: one character or a named key ('Enter', 'F5'); not 'Disabled'.
        isPressable: function (combo) {
            const key = kit.shortcuts.keyOf(combo);
            if (key === null) {
                return false;
            }

            return textElements(key) === 1 || NAMED_KEYS.indexOf(key) >= 0 || /^F([1-9]|1[0-9]|2[0-4])$/.test(key);
        },
        // The reservation entries of a combination in a list the server sent (Capabilities.ReservedShortcuts: strings),
        // for a warning next to a user's own override; [] when the combination is free.
        reserved: function (combo, list) {
            const normalized = kit.shortcuts.normalize(combo);
            if (!normalized || !Array.isArray(list)) {
                return [];
            }

            return list.filter(function (entry) {
                const value = entry && typeof entry === 'object' ? entry.Combo : entry;
                return value === normalized;
            });
        },
        // Binds a handler to a combination (in any spelling; '' or an invalid one binds nothing until update()). Options:
        // when(event) returning false leaves the press to others (not on the player, say). Returns the binding, whose
        // update(combo) follows a settings change and unbind() removes it.
        bind: function (combo, handler, options) {
            if (typeof handler !== 'function') {
                throw new TypeError('kit.shortcuts.bind needs a handler');
            }

            const binding = {
                combo: kit.shortcuts.normalize(combo) || '',
                handler: handler,
                when: options && options.when,
                update: function (next) {
                    binding.combo = kit.shortcuts.normalize(next) || '';
                    return binding;
                },
                unbind: function () {
                    const index = bindings.indexOf(binding);
                    if (index >= 0) {
                        bindings.splice(index, 1);
                    }
                }
            };
            bindings.push(binding);
            if (!shortcutsListening) {
                shortcutsListening = true;
                document.addEventListener('keydown', onShortcutKeyDown, false);
            }

            return binding;
        },
        bindings: function () {
            return bindings.map(function (binding) {
                return binding.combo;
            });
        }
    };

    kit.hashParams = function () {
        const hash = window.location.hash || '';
        const q = hash.indexOf('?');
        return new URLSearchParams(q >= 0 ? hash.substring(q + 1) : '');
    };

    // ------------------------------------------------------------------- observers
    // One body MutationObserver per plugin, throttled (150 ms, then the next animation frame), running the registered
    // scanners, each in its own try/catch. Scanners run only once ApiClient and the signed-in user exist.

    const scanners = [];
    let scanScheduled = false;

    function runScanners() {
        scanScheduled = false;
        if (!client() || !checkUser()) {
            return;
        }

        scanners.forEach(function (scanner) {
            try {
                const result = scanner();
                if (result && typeof result.then === 'function') {
                    result.then(null, report);
                }
            } catch (e) {
                report(e);
            }
        });
    }

    function scheduleScan() {
        if (scanScheduled) {
            return;
        }

        scanScheduled = true;
        setTimeout(function () {
            if (typeof requestAnimationFrame === 'function' && document.visibilityState !== 'hidden') {
                requestAnimationFrame(runScanners);
            } else {
                runScanners();
            }
        }, 150);
    }

    kit.observe = function (scanner) {
        scanners.push(scanner);
        if (started) {
            scheduleScan();
        }
    };
    kit.rescan = scheduleScan;

    kit.onView = function (listener, opts) {
        const events = (opts && opts.events) || [A.viewShow.event];
        events.forEach(function (name) {
            document.addEventListener(name, function (event) {
                try {
                    listener(event.target, event.detail, event.type);
                } catch (e) {
                    report(e);
                }
            });
        });
    };

    // pushState/replaceState/popstate/hashchange. The wrappers always call through: Jellyfin Enhanced and Custom Tabs
    // wrap them too.
    const navigationListeners = [];
    let historyWrapped = false;

    function notifyNavigation(kind) {
        navigationListeners.forEach(function (listener) {
            try {
                listener(window.location, kind);
            } catch (e) {
                report(e);
            }
        });
    }

    kit.onNavigate = function (listener) {
        navigationListeners.push(listener);
        if (historyWrapped) {
            return;
        }

        historyWrapped = true;
        ['pushState', 'replaceState'].forEach(function (name) {
            const original = window.history[name];
            if (typeof original !== 'function') {
                return;
            }

            window.history[name] = function () {
                const result = original.apply(this, arguments);
                notifyNavigation(name);
                return result;
            };
        });
        window.addEventListener('popstate', function () {
            notifyNavigation('popstate');
        });
        window.addEventListener('hashchange', function () {
            notifyNavigation('hashchange');
        });
    };

    // ------------------------------------------------------------------ action sheet
    // Entries in jellyfin-web's item "⋮" menu and in the selection bar's bulk menu, for admins. The
    // sheet never says which item it is for, so the trigger is recorded as it happens, by document
    // capture-phase click and command listeners (shortcuts.js stops the click at the items container): a card's or a
    // list row's menu button, a right-click or a remote's menu key (the command event), a details page's "More" (the
    // version chosen there, else the page's item), or the selection bar's menu button (in selection mode the only click
    // trigger: a card's click selects it there). One record at a time, kept 8 s;
    // any other click outside a dialog drops it, so menus opened from elsewhere (now playing, sort, filter, the player's
    // audio and subtitles) never get an entry. These listeners only read: they never stop, prevent or change an event,
    // which keeps them clear of the kit's rule against capture listeners (a rule about keys).
    //
    // A body childList observer, installed with the first entry once the kit has started, takes each new
    // .dialogContainer > .actionSheet once. It is the item menu when it has one of the admin-only commands refresh, edit,
    // editimages or identify and no selectall, and the bulk menu when it has selectall and the admin-only groupvideos; so
    // a non-admin's menus never qualify, and kit.user.isAdmin() is asked as well (defence in depth; the trigger warmed
    // it). Each entry whose when(ctx) answers true gets a <button is="emby-button"> with the classes of the sheet's own
    // rows (listItem-focusscale on TV, actionsheet-xlargeFont on mobile) and data-id "<prefix>-<id>", after "Refresh
    // metadata" (or the next command of KIT_ANCHORS.actionSheet.insertAfter there, else last), once; then the sheet's
    // inline top and left, clamped by jellyfin-web before the row was there, are clamped again.
    //
    // The entry's click is left alone: the sheet stores its data-id and closes itself (the item menu's promise rejects
    // on the unknown command and every 10.11.5 caller swallows it; the bulk menu's callback ignores it and keeps the
    // selection). onSelect(ctx) runs after the sheet's close event, which follows its history.back(), or after 1.5 s, so
    // a dialog it opens never races that back(); open it with kit.nativeDialog. It runs once a sheet: the
    // first click on one of our rows is the choice, and a second one while the sheet closes does nothing.
    //
    // ctx: { itemIds, surface ('card', 'list', 'details' or 'bulk'), isBulk, sheet, itemType, isFolder, serverId,
    // commands, exitSelection, trigger }. trigger is the element that opened the menu: the
    // card's or row's menu button, the details page's More, the selection bar's menu button, or the item a command
    // event came from; a dialog onSelect opens can give the focus back to it on close (kit.nativeDialog's returnFocus).
    // itemType and isFolder are the trigger's data-type and data-isfolder, null without them
    // (the details page, a bare command event, the bulk menu), so when(ctx) can leave out item kinds with no server call;
    // commands are the sheet's own command ids (moremediainfo on a details page: the item has media sources);
    // exitSelection() ends the selection (the bulk menu only, a no-op elsewhere). Health: a recorded trigger that no
    // sheet followed within 8 s is a miss of kit.actionSheet, a sheet without rows one of kit.actionSheetRows (view
    // menus).

    const SHEET_TTL_MS = 8000;
    const SHEET_CLOSE_WAIT_MS = 1500;
    const SHEET_VIEW = 'menus';
    const SHEET_ID = /^[a-z0-9][a-z0-9-]{0,31}$/;
    const AS = A.actionSheet;
    const MT = A.menuTriggers;
    const sheetEntries = [];
    const sheetState = { pending: null, expiry: null, installed: false, seen: [] };

    function sheetCommandSelector(command) {
        return '.' + AS.item + '[' + AS.commandAttr + '="' + command + '"]';
    }

    function sheetCommands(scroller) {
        return select(scroller, '.' + AS.item + '[' + AS.commandAttr + ']', true).map(function (row) {
            return row.getAttribute(AS.commandAttr);
        });
    }

    function isElement(node) {
        return !!node && node.nodeType === 1 && typeof node.closest === 'function';
    }

    function triggerRecord(host, source, trigger) {
        const id = host ? host.getAttribute(MT.item.id) : null;
        if (!id) {
            return null;
        }

        const folder = host.getAttribute(MT.item.isFolder);
        return {
            kind: 'item',
            source: source,
            surface: host.classList.contains(MT.cardClass) ? 'card' : 'list',
            itemIds: [id],
            itemType: host.getAttribute(MT.item.type) || null,
            isFolder: folder === 'true' ? true : (folder === 'false' ? false : null),
            serverId: host.getAttribute(MT.item.serverId) || null,
            trigger: trigger || host
        };
    }

    // As shortcuts.js's onClick: the .itemAction, its data-action or else its nearest ancestor's, 'menu', the closest
    // [data-id].
    function menuHost(target) {
        const root = target.closest('.' + MT.itemAction);
        if (!root) {
            return null;
        }

        const action = root.hasAttribute(MT.actionAttr) ? root : root.closest('[' + MT.actionAttr + ']');
        return action && action.getAttribute(MT.actionAttr) === MT.menuAction ? root.closest('[' + MT.item.id + ']') : null;
    }

    function detailsRecord(button) {
        const page = button.closest('.' + MT.detailsPage);
        const version = page ? select(page, '.' + MT.detailsVersion) : null;
        const id = (version && version.value) || kit.hashParams().get('id');
        const c = client();
        return id ? {
            kind: 'item',
            source: 'details',
            surface: 'details',
            itemIds: [id],
            itemType: null,
            isFolder: null,
            serverId: kit.hashParams().get('serverId') || (c && typeof c.serverId === 'function' ? c.serverId() : null),
            trigger: button
        } : null;
    }

    function recordTrigger(record) {
        if (sheetState.expiry !== null) {
            clearTimeout(sheetState.expiry);
            sheetState.expiry = null;
        }

        sheetState.pending = record;
        if (!record) {
            return;
        }

        record.at = now();
        kit.user.isAdmin().then(null, function () {});
        sheetState.expiry = setTimeout(function () {
            sheetState.expiry = null;
            if (sheetState.pending === record) {
                sheetState.pending = null;
                miss(AS.health, SHEET_VIEW);
            }
        }, SHEET_TTL_MS);
    }

    function onTriggerClick(event) {
        const target = event.target;
        if (!isElement(target) || target.closest('.' + AS.container)) {
            // A click in a dialog or a sheet (a row, the metadata editor's own menu button) is that dialog's.
            return;
        }

        const more = target.closest('.' + MT.detailsMore);
        if (more) {
            recordTrigger(detailsRecord(more));
        } else if (target.closest('.' + MT.selectionMenu)) {
            recordTrigger({ kind: 'bulk', source: 'click', surface: 'bulk', itemIds: [], itemType: null, isFolder: null, serverId: null, trigger: target.closest('.' + MT.selectionMenu) });
        } else if (select(document, '.' + MT.selectionPanel)) {
            // In selection mode a click on a card, its "⋮" among it, selects the card and no sheet follows:
            // recorded, it was a miss of kit.actionSheet 8 s later. Only the selection menu above records then;
            // the click drops a record, as any other click.
            recordTrigger(null);
        } else {
            recordTrigger(triggerRecord(menuHost(target), 'click', target.closest('.' + MT.itemAction)));
        }
    }

    function onTriggerCommand(event) {
        const target = event.target;
        if (!event.detail || event.detail.command !== MT.menuCommand || !isElement(target) || target.closest('.' + AS.container)) {
            return;
        }

        const root = target.closest('.' + MT.itemAction) || target;
        recordTrigger(triggerRecord(root.closest('[' + MT.item.id + ']'), 'command', root));
    }

    function selectedIds() {
        const ids = [];
        select(document, '.' + MT.selectionItem + ' .' + MT.selectionCheckbox, true).forEach(function (box) {
            const host = box.checked ? box.closest('[' + MT.item.id + ']') : null;
            const id = host ? host.getAttribute(MT.item.id) : null;
            if (id && ids.indexOf(id) < 0) {
                ids.push(id);
            }
        });
        return ids;
    }

    function exitSelection() {
        const button = select(document, '.' + MT.selectionPanel + ' .' + MT.selectionClose);
        if (button) {
            button.click();
        }
    }

    // jellyfin-web's clamp again, after our rows made the sheet taller or wider (actionSheet.ts: the overflow and 20 px
    // back, at least 10 px from the top and the left); offsetHeight and offsetWidth, since the entry animation's
    // transform distorts getBoundingClientRect.
    function keepSheetOnScreen(sheet) {
        [['top', 'offsetHeight', 'innerHeight'], ['left', 'offsetWidth', 'innerWidth']].forEach(function (axis) {
            const start = parseFloat(sheet.style[axis[0]]);
            const size = Number(sheet[axis[1]]);
            const room = Number(window[axis[2]]);
            if (!isFinite(start) || !(size > 0) || !(room > 0)) {
                return;
            }

            const over = start + size - room;
            if (over > 0) {
                sheet.style[axis[0]] = Math.max(start - over - 20, 10) + 'px';
            }
        });
    }

    function afterSheetClose(sheet, run) {
        let done = false;
        const once = function () {
            if (done) {
                return;
            }

            done = true;
            sheet.removeEventListener(AS.closeEvent, once);
            try {
                const result = run();
                if (result && typeof result.then === 'function') {
                    result.then(null, report);
                }
            } catch (e) {
                report(e);
            }
        };
        sheet.addEventListener(AS.closeEvent, once);
        setTimeout(once, SHEET_CLOSE_WAIT_MS);
    }

    function sheetRow(scroller, command, text, icon) {
        const sibling = select(scroller, '.' + AS.item);
        const own = sibling ? String(sibling.getAttribute('class') || '').split(/\s+/).filter(function (name) {
            return name && name !== AS.selected;
        }).join(' ') : '';
        const attrs = { is: N.embyButton, type: 'button', class: own || AS.itemClass };
        attrs[AS.commandAttr] = command;
        return h('button', attrs, [
            h('span', { class: classes(N.actionSheetIcon, N.listItemIcon, N.listItemIconTransparent, N.icon, icon), 'aria-hidden': 'true' }),
            h('div', { class: classes(N.listItemBody, N.actionSheetBody) }, h('div', { class: classes(N.listItemBodyText, N.actionSheetText), text: text }))
        ]);
    }

    function firstRow(scroller, commands) {
        for (let i = 0; i < commands.length; i++) {
            const row = select(scroller, sheetCommandSelector(commands[i]));
            if (row) {
                return row;
            }
        }

        return null;
    }

    function fillSheet(sheet, scroller, record, commands) {
        const isBulk = record.kind === 'bulk';
        const ctx = Object.freeze({
            itemIds: Object.freeze(isBulk ? selectedIds() : record.itemIds.slice()),
            surface: record.surface,
            isBulk: isBulk,
            sheet: sheet,
            itemType: record.itemType,
            isFolder: record.isFolder,
            serverId: record.serverId,
            commands: Object.freeze(commands.slice()),
            exitSelection: isBulk ? exitSelection : function () {},
            trigger: record.trigger || null
        });
        let after = firstRow(scroller, isBulk ? AS.bulkInsertAfter : AS.insertAfter);
        let added = 0;
        // One choice a sheet: a second click while the sheet closes (its rows stay ~100 ms), on the same
        // row or another of ours, would run a second onSelect after the same close event.
        let chosen = false;
        sheetEntries.slice().forEach(function (entry) {
            const command = cls(entry.id);
            if (select(scroller, sheetCommandSelector(command))) {
                return;
            }

            let wanted = false;
            let text = '';
            try {
                wanted = entry.when ? !!entry.when(ctx) : !isBulk;
                text = wanted ? String(typeof entry.text === 'function' ? entry.text(ctx) : entry.text) : '';
            } catch (e) {
                report(e);
                wanted = false;
            }

            if (!wanted) {
                return;
            }

            const row = sheetRow(scroller, command, text, entry.icon);
            if (after && after.parentNode) {
                after.parentNode.insertBefore(row, after.nextSibling);
            } else {
                scroller.append(row);
            }

            after = row;
            added++;
            kit.upgrade(row);
            // Not stopped: the sheet's own listener, next on the way up, stores our data-id and closes the sheet.
            row.addEventListener('click', function () {
                if (chosen) {
                    return;
                }

                chosen = true;
                afterSheetClose(sheet, function () {
                    return entry.onSelect(ctx);
                });
            });
        });
        if (added > 0) {
            keepSheetOnScreen(sheet);
        }
    }

    function onSheet(sheet) {
        const record = sheetState.pending;
        recordTrigger(null);
        if (!record || now() - record.at > SHEET_TTL_MS || sheetEntries.length === 0) {
            return;
        }

        const scroller = select(sheet, '.' + AS.scroller);
        const commands = scroller ? sheetCommands(scroller) : [];
        if (commands.length === 0) {
            miss(AS.healthRows, SHEET_VIEW);
            return;
        }

        const has = function (command) {
            return commands.indexOf(command) >= 0;
        };
        const qualifies = record.kind === 'bulk'
            // Jellyfin 12 offers the admin-only groupvideos only when the first selected item is a video, so it can't tell an
            // admin's bulk menu; kit.user.isAdmin() below does.
            ? has(AS.bulkMarker)
            : !has(AS.bulkMarker) && AS.itemMarkers.some(has);
        if (!qualifies) {
            return;
        }

        kit.user.isAdmin().then(function (admin) {
            if (admin && sheet.isConnected) {
                fillSheet(sheet, scroller, record, commands);
            }
        }, report);
    }

    // The body's direct children: each sheet is taken once, also while its container stays there closing (~100 ms).
    function scanSheets() {
        sheetState.seen = sheetState.seen.filter(function (sheet) {
            return sheet.isConnected;
        });
        Array.prototype.slice.call(document.body.children).forEach(function (container) {
            if (!container.classList.contains(AS.container)) {
                return;
            }

            Array.prototype.slice.call(container.children).forEach(function (sheet) {
                if (sheet.classList.contains(AS.sheet) && sheetState.seen.indexOf(sheet) < 0) {
                    sheetState.seen.push(sheet);
                    try {
                        onSheet(sheet);
                    } catch (e) {
                        report(e);
                    }
                }
            });
        });
    }

    function installActionSheet() {
        if (sheetState.installed || !started || !document.body || sheetEntries.length === 0) {
            return;
        }

        sheetState.installed = true;
        document.addEventListener('click', onTriggerClick, true);
        document.addEventListener(MT.commandEvent, onTriggerCommand, true);
        new MutationObserver(scanSheets).observe(document.body, { childList: true });
    }

    kit.actionSheet = {
        // { id, icon, text, when(ctx), onSelect(ctx) }: id is the plugin's own (a-z, 0-9 and dashes; data-id
        // "<prefix>-<id>"); text a string or a function of ctx; when(ctx) defaults to the item menu only (!ctx.isBulk).
        // Returns { remove() }.
        addItem: function (opts) {
            opts = opts || {};
            if (!SHEET_ID.test(String(opts.id)) || typeof opts.onSelect !== 'function' || (opts.when && typeof opts.when !== 'function')) {
                throw new Error('kit action sheet, addItem: needs an id of a-z, 0-9 and dashes and an onSelect function');
            }

            if (sheetEntries.some(function (entry) { return entry.id === opts.id; })) {
                throw new Error('kit action sheet, addItem: "' + opts.id + '" is added already');
            }

            const entry = { id: opts.id, icon: opts.icon || '', text: opts.text === undefined ? opts.id : opts.text, when: opts.when || null, onSelect: opts.onSelect };
            sheetEntries.push(entry);
            installActionSheet();
            return {
                remove: function () {
                    const index = sheetEntries.indexOf(entry);
                    if (index >= 0) {
                        sheetEntries.splice(index, 1);
                    }
                }
            };
        },
        // The entries added, by their data-id ("<prefix>-<id>").
        items: function () {
            return sheetEntries.map(function (entry) {
                return cls(entry.id);
            });
        }
    };

    // -------------------------------------------------------------------- playback
    // Sessions API commands for this browser's own session (there is no global playbackManager) and the
    // playback reports the web client sends, observed by wrapping ApiClient's report methods.

    const playback = { listeners: [], hooked: false, session: null };

    function hookPlaybackReports(c) {
        if (playback.hooked || playback.listeners.length === 0) {
            return;
        }

        const proto = Object.getPrototypeOf(c);
        const flag = '__' + pfx + 'PlaybackHooked';
        if (!proto || proto[flag]) {
            playback.hooked = true;
            return;
        }

        proto[flag] = true;
        playback.hooked = true;
        const kinds = ['start', 'progress', 'stopped'];
        A.playbackReports.methods.forEach(function (name, index) {
            const original = proto[name];
            if (typeof original !== 'function') {
                kit.health.note('playbackReports', 'ApiClient.' + name + ' is missing; playback positions are unavailable');
                return;
            }

            proto[name] = function () {
                const info = arguments[0];
                try {
                    playback.listeners.forEach(function (listener) {
                        listener(kinds[index], info);
                    });
                } catch (e) {
                    report(e);
                }

                return original.apply(this, arguments);
            };
        });
    }

    kit.playback = {
        // listener(kind, info): kind 'start' | 'progress' | 'stopped', info the PlaybackProgressInfo the client sends.
        onReport: function (listener) {
            playback.listeners.push(listener);
            const c = client();
            if (c) {
                hookPlaybackReports(c);
            }

            return function () {
                const index = playback.listeners.indexOf(listener);
                if (index >= 0) {
                    playback.listeners.splice(index, 1);
                }
            };
        },
        // The session of this browser, used to seek and to start playback through the Sessions API.
        sessionId: async function () {
            const c = client();
            if (playback.session && playback.session.deviceId === c.deviceId() && Date.now() - playback.session.at < SESSION_TTL_MS) {
                return playback.session.id;
            }

            const sessions = await kit.api('GET', 'Sessions', { query: { deviceId: c.deviceId() } }) || [];
            const own = sessions.filter(function (s) {
                return s.DeviceId === c.deviceId();
            })[0] || sessions[0];
            if (!own) {
                throw new KitApiError(0, 'This player session could not be found.');
            }

            playback.session = { id: own.Id, deviceId: c.deviceId(), at: Date.now() };
            return own.Id;
        },
        seekTo: async function (ticks) {
            const sessionId = await kit.playback.sessionId();
            await kit.api('POST', 'Sessions/' + sessionId + '/Playing/Seek', { query: { SeekPositionTicks: Math.round(ticks) } });
        },
        // Pauses and resumes this browser's own playback (Bookmarks: the optional pause while a name dialog is
        // open). Both are no-ops for jellyfin-web when playback is already in that state.
        pause: async function () {
            const sessionId = await kit.playback.sessionId();
            await kit.api('POST', 'Sessions/' + sessionId + '/Playing/Pause');
        },
        unpause: async function () {
            const sessionId = await kit.playback.sessionId();
            await kit.api('POST', 'Sessions/' + sessionId + '/Playing/Unpause');
        },
        // Resolves true once the client reports playback of the item, false after timeoutMs.
        waitForPlayback: function (itemId, timeoutMs) {
            return new Promise(function (resolve) {
                let timer = null;
                const stop = kit.playback.onReport(function (kind, info) {
                    if (kind !== 'stopped' && info && info.ItemId === itemId) {
                        finish(true);
                    }
                });
                const finish = function (value) {
                    clearTimeout(timer);
                    stop();
                    resolve(value);
                };
                timer = setTimeout(function () {
                    finish(false);
                }, timeoutMs);
            });
        },
        // jellyfin-web sometimes drops a play command that arrives right after a video ended, so the command is sent
        // again if playback doesn't start (Bookmarks 1.0).
        playItem: async function (itemId, opts) {
            opts = opts || {};
            const query = { playCommand: 'PlayNow', itemIds: itemId, startPositionTicks: opts.startTicks || 0 };
            if (opts.mediaSourceId) {
                query.mediaSourceId = opts.mediaSourceId;
            }

            if (typeof opts.audioStreamIndex === 'number') {
                query.audioStreamIndex = opts.audioStreamIndex;
            }

            const sessionId = await kit.playback.sessionId();
            for (let attempt = 0; attempt < 2; attempt++) {
                const started = kit.playback.waitForPlayback(itemId, opts.timeoutMs || 8000);
                await kit.api('POST', 'Sessions/' + sessionId + '/Playing', { query: query });
                if (await started) {
                    return;
                }
            }

            throw new KitApiError(0, 'Playback did not start. Try pressing play.');
        }
    };

    // ------------------------------------------------------------------------- pages
    // User pages. A plugin registers its pages with kit.pages.register(); each one
    // shows in one of two hosts:
    //   - The fallback route (the default, PageHostMode=FallbackOnly): #/<prefix>-<key>, which jellyfin-web doesn't
    //     know, so its FallbackRoute page shows. The kit adds the page root to it, and the pre-hide rule of the plugin's head fragment
    //     and of the kit's own styles hides "Page not found" while <html> has the class <prefix>-route, and (the
    //     kit's) while this plugin claims the page, so leaving for another unknown route shows our page until the
    //     hand-back rather than "Page not found" under our title.
    //   - Plugin Pages (Auto/PluginPagesOnly, when it lists the page): #/userpluginsettings.html?pageUrl=/<Route>/Pages/<key>,
    //     whose host view inserts the shell the server sends; the kit mounts the shell's page root.
    // The head fragment's JSON state (FallbackRouteHeadFragment.Render) says which pages the server knows, which are turned
    // off, PageHostMode and ShowFallbackNav, so none of it waits for an API call. Links go into the shared "Library tools"
    // drawer section and are plain emby-linkbutton links without a click handler of ours.

    const PAGE_KEY = /^[a-z0-9][a-z0-9-]{0,39}$/;
    const PAGE_ID_SUFFIX = /^[A-Z][A-Za-z0-9]*Page$/;
    const PAGE_ICON = /^[a-z][a-z0-9_]{0,39}$/;
    // The sentences of the server's shells (KitPageMarkup), for the fallback host; a Kit.Tests test keeps them equal.
    const PAGE_TEXT = Object.freeze({
        turnedOff: ' is turned off by your server admin.',
        unknown: 'This page is not available on this server.',
        failed: 'This page could not be shown. Reload to try again.'
    });
    // The DOM contract between our plugins' kits: the shared "Library tools" section, and the claim on
    // FallbackRoute's page, so that one plugin never restores the title while another plugin's page is showing.
    const SHARED = Object.freeze({
        navClass: 'jfkit-nav',
        navAttr: 'data-jfkit-nav',
        navPlugin: 'data-jfkit-plugin',
        navOrder: 'data-order',
        navHeader: 'Library tools',
        claim: 'data-jfkit-claim',
        originalTitle: 'data-jfkit-orig-title'
    });
    const HAND_BACK_MS = 1000;
    const DRAWER_GRACE_MS = 1000;
    // After a popstate within Plugin Pages' host: time for jellyfin-web's view manager to restore or load a view for the
    // location (its effect runs a few milliseconds after the event) before the kit renders the page shown there itself.
    const PLUGIN_PAGES_POP_MS = 100;
    const shellPath = build.route + '/Pages/';
    const pageAttr = function (name) {
        return 'data-' + pfx + '-' + name;
    };

    const pages = {
        list: [],
        byKey: {},
        ignored: [],
        started: false,
        hosts: {},
        hostsKnown: false,
        selecting: false,
        listed: null,
        lastHash: null,
        swapping: false,
        // The last viewbeforeshow on #fallbackPage that React's Page fired (not ours): { view, hash }, until the next
        // location change.
        shown: null,
        drawerMenu: null,
        drawerSeenAt: 0,
        renders: {},
        generations: new WeakMap(),
        head: undefined
    };

    // The head fragment's state: { pages: [keys], off: [keys], mode, nav }, or null without one (read once per load).
    function pageHead() {
        if (pages.head === undefined) {
            pages.head = null;
            const node = document.querySelector('script[type="application/json"][' + pageAttr('head') + '][' + pageAttr('pages') + ']');
            if (node) {
                try {
                    const state = JSON.parse(node.textContent);
                    pages.head = state && typeof state === 'object' ? state : null;
                } catch (e) {
                    report(e);
                }
            }
        }

        return pages.head;
    }

    function hostMode() {
        const head = pageHead();
        const mode = head && head.mode;
        return mode === 'Auto' || mode === 'PluginPagesOnly' ? mode : 'FallbackOnly';
    }

    function navShown() {
        const head = pageHead();
        return !head || head.nav !== false;
    }

    function isTurnedOff(key) {
        const head = pageHead();
        return !!head && Array.isArray(head.off) && head.off.indexOf(key) >= 0;
    }

    function pluginPagesId(page) {
        return 'Jellyfin.Plugin.' + build.route + '.' + page.idSuffix;
    }

    // PluginPages is a global const declared by /PluginPages/inject.js, not a window property: only typeof
    // sees it. Asked once the user is known, when every deferred script has run.
    function pluginPagesPresent() {
        return typeof PluginPages !== 'undefined';
    }

    function splitHash() {
        const hash = String(window.location.hash || '').replace(/^#!?/, '');
        const q = hash.indexOf('?');
        return { path: q >= 0 ? hash.substring(0, q) : hash, query: q >= 0 ? hash.substring(q + 1) : '' };
    }

    function pageParams() {
        const out = {};
        new URLSearchParams(splitHash().query).forEach(function (value, name) {
            if (name !== A.pluginPagesHost.param) {
                out[name] = value;
            }
        });
        return out;
    }

    function keyFromPageUrl(pageUrl) {
        const prefix = '/' + shellPath;
        if (!pageUrl || pageUrl.indexOf(prefix) !== 0) {
            return null;
        }

        const key = pageUrl.substring(prefix.length).split(/[?#&/]/)[0];
        return pages.byKey[key] ? key : null;
    }

    // Our fallback route, or a Plugin Pages link FallbackRoute rewrote to /userpluginsettings (no .html) because Plugin Pages'
    // route is missing: removed, or its bundle patch broken by a jellyfin-web update.
    function fallbackKey() {
        const hash = splitHash();
        const own = '/' + pfx + '-';
        if (hash.path.indexOf(own) === 0) {
            const key = hash.path.substring(own.length);
            return pages.byKey[key] ? key : null;
        }

        return hash.path === A.pluginPagesHost.brokenRoute ? keyFromPageUrl(new URLSearchParams(hash.query).get(A.pluginPagesHost.param)) : null;
    }

    function pluginPagesKey() {
        const hash = splitHash();
        return hash.path === A.pluginPagesHost.route ? keyFromPageUrl(new URLSearchParams(hash.query).get(A.pluginPagesHost.param)) : null;
    }

    function pageQuery(extra) {
        const query = new URLSearchParams();
        Object.keys(extra || {}).forEach(function (name) {
            if (extra[name] !== undefined && extra[name] !== null) {
                query.append(name, String(extra[name]));
            }
        });
        return query.toString();
    }

    function hostHref(key, host, text) {
        if (host === 'PluginPages') {
            return '#' + A.pluginPagesHost.route + '?' + A.pluginPagesHost.param + '=' + encodeURIComponent('/' + shellPath + key) + (text ? '&' + text : '');
        }

        return '#/' + pfx + '-' + key + (text ? '?' + text : '');
    }

    function pageHref(key, extra) {
        return hostHref(key, pages.hosts[key], pageQuery(extra));
    }

    // The hash parameters of the page shown now, changed without a render (a page keeps its state in the hash,
    // so a reload, a deep link and Back show the same list): the page changes what it shows itself, and the kit renders
    // it again only when the location changes otherwise (Back, Forward, a link). The history entry is pushed, so Back
    // returns to the previous state, or with { replace: true } replaced (a search typed, a page loaded). The location
    // keeps its host: the fallback route, or the Plugin Pages link the page is shown in. jellyfin-web's router isn't told (it
    // follows popstate, as for any hash change), and the entry keeps the router's own history state. Nothing is done,
    // and false returned, when the page isn't the one shown or the hash wouldn't change.
    function setPageParams(key, extra, opts) {
        const page = pages.byKey[key];
        const inFallback = fallbackKey() === key;
        if (!page || (!inFallback && pluginPagesKey() !== key)) {
            return false;
        }

        const hash = hostHref(key, inFallback ? 'Fallback' : 'PluginPages', pageQuery(extra));
        if (window.location.hash === hash) {
            return false;
        }

        // The location moves on, so the last viewbeforeshow React fired no longer stands for it: a popstate back to
        // that hash is a navigation of its own, which the kit renders.
        pages.shown = null;
        pages.lastHash = hash;
        window.history[opts && opts.replace ? 'replaceState' : 'pushState'](window.history.state, '', hash);
        pages.lastHash = window.location.hash;
        return true;
    }

    function setHeaderTitle(title) {
        if (kit.env.has(A.libraryMenu.global)) {
            try {
                window.LibraryMenu.setTitle(title);
            } catch (e) {
                report(e);
            }
        }
    }

    function childWith(parent, attribute) {
        const children = parent ? parent.children : [];
        for (let i = 0; i < children.length; i++) {
            if (children[i].hasAttribute(attribute)) {
                return children[i];
            }
        }

        return null;
    }

    function childWithClass(parent, name) {
        const children = parent ? parent.children : [];
        for (let i = 0; i < children.length; i++) {
            if (children[i].classList.contains(name)) {
                return children[i];
            }
        }

        return null;
    }

    function viewOf(node) {
        return node && typeof node.closest === 'function' ? node.closest('[' + A.pageHost.attr + '="' + A.pageHost.value + '"]') : null;
    }

    function hostKind(view) {
        if (!view) {
            return 'none';
        }

        if (view.id === A.fallbackPage.id) {
            return 'Fallback';
        }

        return view.classList.contains(A.pluginPagesHost.pageClass) ? 'PluginPages' : 'other';
    }

    function isShown(node) {
        return !!node && !node.classList.contains(N.hide) && node.getClientRects().length > 0;
    }

    // A page root on screen: neither it nor a view around it hidden (jellyfin-web hides a view it keeps with the class
    // "hide"), and laid out.
    function rootShown(root) {
        for (let node = root; node && node.nodeType === 1; node = node.parentNode) {
            if (node.classList.contains(N.hide)) {
                return false;
            }
        }

        return root.getClientRects().length > 0;
    }

    // Fallback route: our page root in FallbackRoute's page, which keeps its own children (React leaves ours alone).
    function claim(view, key) {
        const page = pages.byKey[key];
        if (!view.hasAttribute(SHARED.originalTitle)) {
            view.setAttribute(SHARED.originalTitle, view.getAttribute(A.pageHost.title) || '');
        }

        view.setAttribute(SHARED.claim, pfx);
        let root = childWith(view, pageAttr('page'));
        if (root && root.getAttribute(pageAttr('page')) !== key) {
            root.remove();
            root = null;
        }

        if (!root) {
            root = h('div', { class: classes(cls('page'), N.paddedLeft, N.paddedRight, N.paddedBottomPage) });
            root.setAttribute(pageAttr('page'), key);
            view.append(root);
        }

        view.setAttribute(A.pageHost.title, page.title);
        return root;
    }

    // The route left ours while FallbackRoute's page stayed (another unknown route): after about 1 s, remove our root and,
    // if no other plugin claimed the page meanwhile, give it back its title. Until then our
    // claim keeps our page, and our title, showing; another plugin's claim hides it at once by that plugin's route class.
    // The original title is the page's own: the head fragment records it before it sets ours (FallbackRouteHeadFragment).
    function handBack(view) {
        if (!view.isConnected || fallbackKey()) {
            return;
        }

        const root = childWith(view, pageAttr('page'));
        if (root) {
            root.remove();
        }

        if (view.getAttribute(SHARED.claim) === pfx) {
            const original = view.getAttribute(SHARED.originalTitle);
            view.removeAttribute(SHARED.claim);
            view.removeAttribute(SHARED.originalTitle);
            if (original) {
                view.setAttribute(A.pageHost.title, original);
                setHeaderTitle(original);
            }
        }
    }

    // What React's Page does on mount. FallbackRoute stays mounted between two unknown routes, so it doesn't; and
    // appRouter.show() waits for the next viewshow, so without these the next navigation hangs.
    function dispatchShow(view) {
        const detail = { isRestored: false, options: { enableMediaControl: true, supportsThemeMedia: false } };
        pages.swapping = true;
        try {
            [A.viewBeforeShow.event, A.pageBeforeShow.event, A.viewShow.event, A.pageShow.event].forEach(function (type) {
                view.dispatchEvent(new CustomEvent(type, { bubbles: true, cancelable: false, detail: detail }));
            });
        } finally {
            pages.swapping = false;
        }
    }

    function onLocationChange(kind) {
        const key = fallbackKey();
        document.documentElement.classList.toggle(cls('route'), !!key);
        if (key) {
            // FallbackRoute's page must mount for our route; without it the page can't show.
            kit.health.expect(['kit.f1Host'], 'pages');
        }

        const hash = window.location.hash;
        if (hash === pages.lastHash) {
            return;
        }

        pages.lastHash = hash;
        const shown = pages.shown;
        pages.shown = null;
        const pluginPages = key ? null : pluginPagesKey();
        if (pluginPages && kind === 'popstate') {
            // Back or Forward within Plugin Pages' host over the entries a page's setParams made.
            // jellyfin-web's router never heard of those entries, so a popstate that lands on the location it still
            // holds (Back to the state the view was loaded with) changes nothing for it: no page events, and the page
            // shown there renders again from the hash here. One that lands elsewhere is a navigation to the router: its
            // view manager restores the view cached for that exact URL, or loads a new one (ViewManagerPage, by
            // pathname and search), with the page events that render; so the kit waits a moment and leaves it to them
            // when a host view showed for this location or the page's view is hidden by then.
            setTimeout(function () {
                if (window.location.hash !== hash || pluginPagesKey() !== pluginPages) {
                    return;
                }

                const showedFor = function (s) {
                    return !!s && s.hash === hash;
                };
                const root = shownPluginPagesRoot(pluginPages);
                if (root && root.hasAttribute(pageAttr('mounted')) && !showedFor(shown) && !showedFor(pages.shown)) {
                    renderPage(root, 'navigation');
                }
            }, PLUGIN_PAGES_POP_MS);
            return;
        }

        const view = document.getElementById(A.fallbackPage.id);
        if (!view) {
            // Another page shows now, or FallbackRoute mounts shortly: viewbeforeshow and pageshow handle that.
            return;
        }

        if (key) {
            const root = claim(view, key);
            if (kind === 'init') {
                // The script came after the page showed (a slow ClientScript): mount now.
                if (!root.hasAttribute(pageAttr('mounted'))) {
                    attachPage(root, 'mount');
                }
            } else if (!shown || shown.view !== view || shown.hash !== hash) {
                // Unless FallbackRoute mounted for this very navigation before we heard of it (Back or Forward from a native
                // page: jellyfin-web's popstate listener runs first) and React's Page already fired the page events.
                Promise.resolve().then(function () {
                    if (view.isConnected && fallbackKey() === key) {
                        dispatchShow(view);
                    }
                });
            }
        } else if (childWith(view, pageAttr('page'))) {
            setTimeout(function () {
                handBack(view);
            }, HAND_BACK_MS);
        }
    }

    // Before libraryMenu's pageshow handler reads data-title: the title from the first frame.
    function onViewBeforeShow(event) {
        const view = event.target;
        if (!view || !view.classList) {
            return;
        }

        if (view.id === A.fallbackPage.id) {
            if (!pages.swapping) {
                pages.shown = { view: view, hash: window.location.hash };
            }

            const key = fallbackKey();
            if (key) {
                claim(view, key);
            }
        } else if (view.classList.contains(A.pluginPagesHost.pageClass)) {
            const key = pluginPagesKey();
            if (key) {
                view.setAttribute(A.pageHost.title, pages.byKey[key].title);
                // React's page events stand for this location (a popstate into the host renders through them).
                pages.shown = { view: view, hash: window.location.hash };
            }
        }
    }

    // Our page root in the Plugin Pages host view shown now (the one not hidden), if any.
    function shownPluginPagesRoot(key) {
        const hosts = document.querySelectorAll('.' + A.pluginPagesHost.pageClass + ':not(.' + N.hide + ')');
        for (let i = 0; i < hosts.length; i++) {
            const root = hosts[i].querySelector('[' + pageAttr('page') + '="' + key + '"]');
            if (root && isShown(hosts[i])) {
                return root;
            }
        }

        return null;
    }

    // Cached views don't re-run scripts, so every show (a restored view, a fallback-route swap) renders again.
    function onPageShow(event) {
        const view = event.target;
        if (!view || typeof view.querySelector !== 'function') {
            return;
        }

        const root = view.id === A.fallbackPage.id ? childWith(view, pageAttr('page')) : view.querySelector('[' + pageAttr('page') + ']');
        const page = root ? pages.byKey[root.getAttribute(pageAttr('page'))] : null;
        if (!page || kit.isHidden(root)) {
            return;
        }

        if (!root.hasAttribute(pageAttr('mounted'))) {
            attachPage(root, 'mount');
        } else {
            const detail = event.detail || {};
            renderPage(root, pages.swapping ? 'swap' : (detail.isRestored ? 'restored' : 'show'));
        }

        setTimeout(function () {
            if (isShown(view)) {
                setHeaderTitle(page.title);
            }
        }, 0);
    }

    function attachPage(root, reason) {
        const page = pages.byKey[root.getAttribute(pageAttr('page'))];
        if (!page) {
            return;
        }

        root.setAttribute(pageAttr('mounted'), '');
        const view = viewOf(root);
        if (view && view.getAttribute(A.pageHost.title) !== page.title) {
            view.setAttribute(A.pageHost.title, page.title);
        }

        if (isShown(view)) {
            setHeaderTitle(page.title);
        }

        renderPage(root, reason);
    }

    // In the fallback host the server's shell says whether the page is on: 'on', 'off' (known but turned off) or
    // 'unknown' (404). Plugin Pages' host fetched the shell itself.
    function verify(key, host) {
        if (host !== 'Fallback') {
            return Promise.resolve('on');
        }

        return kit.api('GET', shellPath + encodeURIComponent(key), { raw: true }).then(function (response) {
            return response.text().then(function (text) {
                return text.indexOf(pageAttr('page') + '="' + key + '"') >= 0 ? 'on' : 'off';
            });
        }, function (e) {
            // Anything but a 404 renders the page, whose own requests show the problem.
            return e && e.status === 404 ? 'unknown' : 'on';
        });
    }

    function renderPage(root, reason) {
        const key = root.getAttribute(pageAttr('page'));
        const page = pages.byKey[key];
        const host = hostKind(viewOf(root));
        const count = (pages.renders[key] || 0) + 1;
        pages.renders[key] = count;
        root.setAttribute(pageAttr('render'), String(count));
        root.setAttribute(pageAttr('reason'), reason);
        root.setAttribute(pageAttr('host'), host);
        let body = childWithClass(root, cls('page-body'));
        if (!body) {
            body = h('div', { class: cls('page-body') });
            root.append(body);
        }

        body.replaceChildren();
        const generation = {};
        pages.generations.set(root, generation);
        const context = {
            key: key,
            title: page.title,
            host: host,
            reason: reason,
            render: count,
            params: pageParams(),
            isCurrent: function () {
                return pages.generations.get(root) === generation && root.isConnected;
            },
            // Current and on screen. Plugin Pages' host is a view jellyfin-web keeps when another page shows, hidden
            // with the class "hide", and the kit renders it again from its page events when it shows again; until then
            // isCurrent() stays true. So a page's own repeating work (a poll) asks this and stops when it is false.
            isShown: function () {
                return context.isCurrent() && rootShown(root);
            }
        };
        kit.user.ready().then(function () {
            return context.isCurrent() ? verify(key, host) : null;
        }).then(function (verdict) {
            if (verdict === null || !context.isCurrent()) {
                return null;
            }

            root.setAttribute(pageAttr('state'), verdict);
            if (verdict !== 'on') {
                body.append(h('p', { class: cls('page-note'), text: verdict === 'off' ? page.title + PAGE_TEXT.turnedOff : PAGE_TEXT.unknown }));
                return null;
            }

            return page.render(body, context.params, context);
        }).then(function () {
            if (context.isCurrent()) {
                kit.upgrade(body);
            }
        }, function (e) {
            report(e);
            if (context.isCurrent()) {
                body.append(h('p', { class: cls('page-note'), text: PAGE_TEXT.failed }));
            }
        });
    }

    // Every DOM change: claim FallbackRoute's page for our route, and mount the shell Plugin Pages' host inserted. It runs
    // unthrottled, beside the kit's throttled observer, because both must happen before the next paint (the shell's
    // "needs the web UI" note shows until the mount). So it is kept cheap: off our pages it returns
    // after one look at the hash, and it only looks inside #fallbackPage and the shown Plugin Pages host, never through
    // home rows, library grids or the player. Fallback-route roots are mounted by their pageshow (or at start), so a
    // swap renders once.
    function scanPages() {
        const key = fallbackKey();
        if (key) {
            const view = document.getElementById(A.fallbackPage.id);
            const root = childWith(view, pageAttr('page'));
            if (view && (!root || root.getAttribute(pageAttr('page')) !== key || view.getAttribute(SHARED.claim) !== pfx)) {
                const claimed = claim(view, key);
                if (!claimed.hasAttribute(pageAttr('mounted')) && isShown(view)) {
                    attachPage(claimed, 'mount');
                }
            }

            return;
        }

        if (!pluginPagesKey()) {
            return;
        }

        const hosts = document.querySelectorAll('.' + A.pluginPagesHost.pageClass + ':not(.' + N.hide + ')');
        for (let i = 0; i < hosts.length; i++) {
            const roots = hosts[i].querySelectorAll('[' + pageAttr('page') + ']:not([' + pageAttr('mounted') + '])');
            for (let j = 0; j < roots.length; j++) {
                if (pages.byKey[roots[j].getAttribute(pageAttr('page'))]) {
                    attachPage(roots[j], 'mount');
                }
            }
        }
    }

    // Auto/PluginPagesOnly: the Plugin Pages route for pages it lists, once per page load after the user is known.
    // FallbackOnly: the fallback route for every page, with no request.
    function selectHosts() {
        if (pages.hostsKnown || pages.selecting) {
            return;
        }

        const settle = function (listed) {
            pages.listed = listed;
            pages.list.forEach(function (page) {
                pages.hosts[page.key] = listed && listed.indexOf(pluginPagesId(page)) >= 0 ? 'PluginPages' : 'Fallback';
            });
            pages.hostsKnown = true;
            scheduleScan();
        };
        if (hostMode() === 'FallbackOnly') {
            settle(null);
            return;
        }

        if (!userState.id) {
            return;
        }

        if (!pluginPagesPresent()) {
            settle(null);
            return;
        }

        pages.selecting = true;
        kit.api('GET', A.pluginPages.userList).then(function (result) {
            settle(((result && result.Items) || []).map(function (item) {
                return item && item.Id;
            }));
        }, function (e) {
            report(e);
            settle(null);
        }).then(function () {
            pages.selecting = false;
        });
    }

    // --------------------------------------------------------------------------- nav
    // The shared "Library tools" drawer section: created by the first of our plugins right before
    // .userMenuOptions (above "User"; Plugin Pages' section goes below it), links ordered by data-order, rebuilt after
    // every drawer rebuild, removed when empty. Links are upgraded emby-linkbuttons, which call appRouter.show()
    // themselves; a click handler of ours would show twice.

    function drawerParts() {
        const container = document.querySelector('.' + A.drawer.container);
        const menu = childWithClass(container, A.drawer.userMenu);
        return container && menu ? { container: container, menu: menu } : null;
    }

    function wantedNav(parts) {
        if (!parts || !navShown() || !userState.id || !pages.hostsKnown) {
            return [];
        }

        const mode = hostMode();
        if (parts.menu !== pages.drawerMenu) {
            // A new drawer (first build, or a rebuild after signing in): give Plugin Pages its chance to add its links.
            pages.drawerMenu = parts.menu;
            pages.drawerSeenAt = now();
            if (mode === 'Auto') {
                setTimeout(scheduleScan, DRAWER_GRACE_MS + 50);
            }
        }

        const settled = now() - pages.drawerSeenAt >= DRAWER_GRACE_MS;
        return pages.list.filter(function (page) {
            if (isTurnedOff(page.key)) {
                return false;
            }

            if (pages.hosts[page.key] !== 'PluginPages') {
                return true;
            }

            // Auto decides by the drawer: Plugin Pages 2.4.7 never re-adds its section after a sign-in without a reload.
            // PluginPagesOnly leaves listed pages to Plugin Pages.
            return mode === 'Auto' && settled && !parts.container.querySelector('a[' + A.pluginPages.itemId + '="' + pluginPagesId(page) + '"]');
        });
    }

    function insertByOrder(section, link, order) {
        const children = section.children;
        for (let i = 0; i < children.length; i++) {
            if (children[i].hasAttribute(SHARED.navPlugin) && Number(children[i].getAttribute(SHARED.navOrder)) > order) {
                section.insertBefore(link, children[i]);
                return;
            }
        }

        section.append(link);
    }

    function ensureNav() {
        selectHosts();
        ensureDrawerNav();
        ensureMenuNav();
    }

    function ensureDrawerNav() {
        const parts = drawerParts();
        const wanted = wantedNav(parts);
        const keys = wanted.map(function (page) {
            return page.key;
        });
        let section = parts ? childWith(parts.container, SHARED.navAttr) : null;
        if (section) {
            const ours = section.querySelectorAll('a[' + SHARED.navPlugin + '="' + pfx + '"]');
            for (let i = 0; i < ours.length; i++) {
                if (keys.indexOf(ours[i].getAttribute(pageAttr('nav'))) < 0) {
                    ours[i].remove();
                }
            }

            if (!section.querySelector('a')) {
                section.remove();
                section = null;
            }
        }

        if (!parts || wanted.length === 0) {
            return;
        }

        if (!section) {
            section = h('div', { class: SHARED.navClass }, h('h3', { class: N.sidebarHeader, text: SHARED.navHeader }));
            section.setAttribute(SHARED.navAttr, '1');
            parts.container.insertBefore(section, parts.menu);
        }

        let added = false;
        wanted.forEach(function (page) {
            const href = pageHref(page.key);
            let link = section.querySelector('a[' + pageAttr('nav') + '="' + page.key + '"]');
            if (link) {
                if (link.getAttribute('href') !== href) {
                    link.setAttribute('href', href);
                }

                return;
            }

            link = h('a', { is: N.linkButton, class: classes(N.navMenuOption, N.lnkMediaFolder), href: href }, [
                h('span', { class: classes(N.icon, N.navMenuOptionIcon, page.icon), 'aria-hidden': 'true' }),
                h('span', { class: classes(N.sectionName, N.navMenuOptionText), text: page.title })
            ]);
            link.setAttribute(SHARED.navPlugin, pfx);
            link.setAttribute(SHARED.navOrder, String(page.order));
            link.setAttribute(pageAttr('nav'), page.key);
            insertByOrder(section, link, page.order);
            added = true;
        });
        if (added) {
            kit.upgrade(section);
        }
    }

    // The experimental (10.11) and modern (12, the default) layouts hide the drawer, so the same links also go into the
    // app bar's user menu (#app-user-menu, kept mounted while closed), after a divider right below Settings, as Plugin
    // Pages 3.x adds its own there. They are copies of the Settings item, without React's handlers: a click follows the
    // href (the hash router shows the page) and closes the menu through its backdrop. Shared by our plugins: a divider
    // with SHARED.navAttr, then every plugin's links by data-order.
    function menuParts() {
        const menu = document.getElementById(A.appUserMenu.id);
        const settings = menu ? menu.querySelector(A.appUserMenu.settings) : null;
        return settings && settings.parentNode ? { menu: menu, list: settings.parentNode, settings: settings } : null;
    }

    function wantedMenuNav(parts) {
        if (!parts || !navShown() || !userState.id || !pages.hostsKnown) {
            return [];
        }

        const mode = hostMode();
        return pages.list.filter(function (page) {
            if (isTurnedOff(page.key)) {
                return false;
            }

            if (pages.hosts[page.key] !== 'PluginPages') {
                return true;
            }

            // Plugin Pages' menu links carry no id: one with the page's title is the page.
            return mode === 'Auto' && !Array.prototype.some.call(parts.list.querySelectorAll(A.appUserMenu.pluginPagesLink), function (a) {
                return (a.textContent || '').trim() === page.title;
            });
        });
    }

    function menuLinks(divider) {
        const links = [];
        for (let node = divider ? divider.nextElementSibling : null; node && node.hasAttribute(SHARED.navPlugin); node = node.nextElementSibling) {
            links.push(node);
        }

        return links;
    }

    function closeUserMenu(menu) {
        const backdrop = menu.querySelector(A.appUserMenu.backdrop);
        if (backdrop) {
            backdrop.click();
        } else {
            menu.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        }
    }

    function menuLink(parts, page) {
        const link = parts.settings.cloneNode(true);
        link.removeAttribute('id');
        link.classList.remove('Mui-focusVisible', 'Mui-selected');
        link.setAttribute('href', pageHref(page.key));
        link.setAttribute('tabindex', '-1');
        const icon = link.querySelector(A.appUserMenu.icon);
        if (icon) {
            icon.replaceChildren(h('span', { class: classes(N.icon, page.icon), 'aria-hidden': 'true' }));
        }

        const text = link.querySelector(A.appUserMenu.text);
        const label = text ? (text.firstElementChild || text) : link;
        label.textContent = page.title;
        link.setAttribute(SHARED.navPlugin, pfx);
        link.setAttribute(SHARED.navOrder, String(page.order));
        link.setAttribute(pageAttr('menu'), page.key);
        link.addEventListener('click', function () {
            closeUserMenu(parts.menu);
        });
        return link;
    }

    function ensureMenuNav() {
        const parts = menuParts();
        const wanted = wantedMenuNav(parts);
        const keys = wanted.map(function (page) {
            return page.key;
        });
        let divider = parts ? childWith(parts.list, SHARED.navAttr) : null;
        if (divider) {
            menuLinks(divider).forEach(function (link) {
                if (link.getAttribute(SHARED.navPlugin) === pfx && keys.indexOf(link.getAttribute(pageAttr('menu'))) < 0) {
                    link.remove();
                }
            });
            if (menuLinks(divider).length === 0) {
                divider.remove();
                divider = null;
            }
        }

        if (!parts || wanted.length === 0) {
            return;
        }

        if (!divider) {
            const native = parts.list.querySelector(A.appUserMenu.divider);
            divider = native ? native.cloneNode(false) : h('hr', {});
            divider.setAttribute(SHARED.navAttr, '1');
            parts.settings.after(divider);
        }

        wanted.forEach(function (page) {
            const links = menuLinks(divider);
            const href = pageHref(page.key);
            const existing = links.filter(function (link) {
                return link.getAttribute(SHARED.navPlugin) === pfx && link.getAttribute(pageAttr('menu')) === page.key;
            })[0];
            if (existing) {
                if (existing.getAttribute('href') !== href) {
                    existing.setAttribute('href', href);
                }

                return;
            }

            const link = menuLink(parts, page);
            const before = links.filter(function (other) {
                return Number(other.getAttribute(SHARED.navOrder)) <= page.order;
            });
            (before.length ? before[before.length - 1] : divider).after(link);
        });
    }

    function startPages() {
        if (pages.started || pages.list.length === 0 || !document.body) {
            return;
        }

        pages.started = true;
        // FallbackRoute's own content stays hidden while the route is ours and while we claim the page (until the
        // hand-back). A claim by another plugin turns our second rule off, and that plugin's route class hides our root.
        const others = ' > :not([' + pageAttr('page') + '])';
        (document.head || document.documentElement).append(h('style', {
            id: cls('page-styles'),
            text: 'html.' + cls('route') + ' #' + A.fallbackPage.id + others + ',#' + A.fallbackPage.id + '[' + SHARED.claim + '="' + pfx + '"]' + others
                + '{display:none !important}'
        }));
        document.addEventListener(A.viewBeforeShow.event, onViewBeforeShow, true);
        document.addEventListener(A.pageShow.event, onPageShow);
        kit.onNavigate(function (location, kind) {
            onLocationChange(kind);
        });
        new MutationObserver(function () {
            try {
                scanPages();
            } catch (e) {
                report(e);
            }
        }).observe(document.body, { childList: true, subtree: true });
        kit.observe(ensureNav);
        kit.user.onChange(function (id) {
            if (id) {
                selectHosts();
            }
        });
        selectHosts();
        onLocationChange('init');
        scanPages();
    }

    kit.pages = {
        // { key, title, icon, idSuffix, order, render(body, params, context) }: key, title and icon as the server's
        // KitPage. render is called on every show with the page's emptied body element, the hash parameters and
        // { key, title, host, reason, render, params, isCurrent(), isShown() }; it may return a promise. isCurrent() is
        // false once the page was rendered again or its root left the document; isShown() also while its view is hidden
        // (Plugin Pages' host, left for another page). A page the server's head fragment doesn't list is ignored (returns
        // false).
        register: function (definition) {
            const d = definition || {};
            const problems = [];
            if (!PAGE_KEY.test(String(d.key))) {
                problems.push('key');
            }

            if (typeof d.title !== 'string' || !d.title.trim()) {
                problems.push('title');
            }

            if (!PAGE_ICON.test(String(d.icon))) {
                problems.push('icon');
            }

            if (!PAGE_ID_SUFFIX.test(String(d.idSuffix))) {
                problems.push('idSuffix');
            }

            if (typeof d.render !== 'function') {
                problems.push('render');
            }

            if (problems.length > 0 || pages.byKey[d.key]) {
                report(new Error('kit.pages.register: invalid or duplicate page ' + String(d.key) + (problems.length ? ' (' + problems.join(', ') + ')' : '')));
                return false;
            }

            const head = pageHead();
            if (head && (!Array.isArray(head.pages) || head.pages.indexOf(d.key) < 0)) {
                pages.ignored.push(d.key);
                return false;
            }

            const page = {
                key: d.key,
                title: d.title,
                icon: d.icon,
                idSuffix: d.idSuffix,
                order: typeof d.order === 'number' ? d.order : 100 + pages.list.length,
                render: d.render
            };
            pages.list.push(page);
            pages.byKey[page.key] = page;
            if (started) {
                startPages();
            }

            return true;
        },
        // The page's link for the chosen host, with extra hash parameters.
        href: pageHref,
        // The shown page's hash parameters changed in place, without a render (above): pushed, or replaced with
        // { replace: true }. False when the page isn't shown or nothing would change.
        setParams: setPageParams,
        // A plain in-page link to a page: an emby-linkbutton without a click handler of ours. Page bodies are upgraded
        // after render; elsewhere call kit.upgrade().
        link: function (key, extra, text, attrs) {
            const link = h('a', Object.assign({ is: N.linkButton, class: classes(N.buttonLink, cls('page-link')), href: pageHref(key, extra) }, attrs || {}), text);
            link.setAttribute(pageAttr('link'), key);
            return link;
        },
        host: function (key) {
            return pages.hosts[key] || 'Fallback';
        },
        // Whether the server's head fragment lists the page (any page, without a key); false without a fragment.
        announced: function (key) {
            const head = pageHead();
            return !!head && Array.isArray(head.pages) && (key === undefined ? head.pages.length > 0 : head.pages.indexOf(key) >= 0);
        },
        // Whether the head fragment lists the page as on (its KitPage.IsEnabled): what an entry point outside the nav
        // (a header button) asks before it shows. False without a fragment, for a page it doesn't list, or one turned off.
        enabled: function (key) {
            return kit.pages.announced(key) && !isTurnedOff(key);
        },
        params: pageParams,
        state: function () {
            return {
                keys: pages.list.map(function (page) {
                    return page.key;
                }),
                ignored: pages.ignored.slice(),
                mode: hostMode(),
                nav: navShown(),
                hosts: Object.assign({}, pages.hosts),
                hostsKnown: pages.hostsKnown,
                listed: pages.listed ? pages.listed.slice() : null,
                renders: Object.assign({}, pages.renders)
            };
        }
    };
    kit.nav = {
        refresh: function () {
            scheduleScan();
        },
        section: function () {
            const parts = drawerParts();
            return parts ? childWith(parts.container, SHARED.navAttr) : null;
        }
    };

    // ----------------------------------------------------------------------- start

    const clientHooks = [];
    let started = false;

    kit.onClient = function (hook) {
        clientHooks.push(hook);
        const c = client();
        if (started && c && timing.client !== null) {
            hook(c);
        }
    };

    kit.configure = function (values) {
        Object.keys(values || {}).forEach(function (key) {
            settings[key] = values[key];
        });
        return kit;
    };

    // Puts KIT_CSS into <style id="<prefix>-styles"> (or the configured id) once. start() calls it; a dashboard module,
    // which doesn't start the kit, calls it from mount().
    function injectStyles() {
        if (!css || document.getElementById(settings.styleId)) {
            return;
        }

        (document.head || document.documentElement).append(h('style', { id: settings.styleId, text: css }));
    }

    kit.injectStyles = injectStyles;

    function onClient(c) {
        timing.client = now();
        hookPlaybackReports(c);
        clientHooks.forEach(function (hook) {
            try {
                hook(c);
            } catch (e) {
                report(e);
            }
        });

        // The user follows 171-213 ms later; poll for it at the same rate for a while, then rely on viewshow.
        const userDeadline = now() + USER_POLL_LIMIT_MS;
        const pollUser = function () {
            if (checkUser()) {
                scheduleScan();
                return;
            }

            if (now() < userDeadline) {
                setTimeout(pollUser, CLIENT_POLL_MS);
            }
        };
        pollUser();
        scheduleScan();
    }

    // Never touches ApiClient synchronously: it doesn't exist yet when ClientScript runs.
    function pollClient() {
        let warned = false;
        const tick = function () {
            const c = client();
            if (c) {
                onClient(c);
                return;
            }

            const waited = now() - timing.script;
            if (!warned && waited > CLIENT_WARN_MS) {
                warned = true;
                kit.health.note('apiClient', 'window.ApiClient did not appear within 30 s; the ' + build.plugin + ' web UI is waiting for it');
            }

            setTimeout(tick, warned ? 1000 : CLIENT_POLL_MS);
        };
        setTimeout(tick, 0);
    }

    kit.start = function () {
        if (started) {
            return kit;
        }

        started = true;
        const begin = function () {
            injectStyles();
            toastContainer();
            new MutationObserver(scheduleScan).observe(document.body, { childList: true, subtree: true });
            document.addEventListener(A.viewShow.event, function () {
                checkUser();
                scheduleScan();
            });
            window.addEventListener('hashchange', scheduleScan);
            pollClient();
            startPages();
            installActionSheet();
        };
        if (document.body) {
            begin();
        } else {
            document.addEventListener('DOMContentLoaded', begin);
        }

        return kit;
    };

    return kit;
}
