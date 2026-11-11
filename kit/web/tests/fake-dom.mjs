// A small fake browser page for the kit's JavaScript tests (Node 22, node:test; no dependencies). It implements just the
// DOM, events, timers and globals kit.js and plugin scripts use, inside a vm context:
//   - nodes, elements, text, append/insertBefore/remove/replaceChildren, attributes, classList, textContent, focus,
//     matches/closest, getClientRects (empty when detached or hidden by the class "hide") and scrollIntoView (recorded
//     in page.scrolled);
//   - querySelector(All) for compound selectors (tag, #id, .class, [attr], [attr="v"], :not(simple)) with descendant
//     and child combinators and selector lists;
//   - events with capture and bubble phases, CustomEvent, MutationObserver (childList, and attributes with an optional
//     attributeFilter; batched per microtask);
//   - a manual clock driving performance.now(), setTimeout/setInterval and requestAnimationFrame;
//   - fetch with scripted responses and a log (each entry records the fake ApiClient's user id at the time), location,
//     history, localStorage, CSS.escape and a fake ApiClient.
import fs from 'node:fs';
import vm from 'node:vm';

class FakeEvent {
    constructor(type, init) {
        init = init || {};
        this.type = type;
        this.bubbles = !!init.bubbles;
        this.cancelable = init.cancelable !== false;
        this.detail = init.detail === undefined ? null : init.detail;
        this.defaultPrevented = false;
        this.target = null;
        this.currentTarget = null;
        this.eventPhase = 0;
        this._stop = false;
        this._stopNow = false;
        ['key', 'code', 'keyCode', 'shiftKey', 'ctrlKey', 'altKey', 'metaKey', 'repeat', 'isComposing'].forEach((k) => {
            if (init[k] !== undefined) {
                this[k] = init[k];
            }
        });
    }

    preventDefault() {
        if (this.cancelable) {
            this.defaultPrevented = true;
        }
    }

    stopPropagation() {
        this._stop = true;
    }

    stopImmediatePropagation() {
        this._stop = true;
        this._stopNow = true;
    }
}

class FakeCustomEvent extends FakeEvent {}

class FakeEventTarget {
    constructor() {
        this._listeners = [];
    }

    addEventListener(type, fn, options) {
        if (typeof fn !== 'function') {
            return;
        }

        const capture = typeof options === 'boolean' ? options : !!(options && options.capture);
        if (!this._listeners.some((l) => l.type === type && l.fn === fn && l.capture === capture)) {
            this._listeners.push({ type, fn, capture });
        }
    }

    removeEventListener(type, fn, options) {
        const capture = typeof options === 'boolean' ? options : !!(options && options.capture);
        this._listeners = this._listeners.filter((l) => !(l.type === type && l.fn === fn && l.capture === capture));
    }

    _fire(event, phase) {
        event.currentTarget = this;
        event.eventPhase = phase;
        for (const l of this._listeners.slice()) {
            if (l.type !== event.type || (phase === 1 && !l.capture) || (phase === 3 && l.capture)) {
                continue;
            }

            l.fn.call(this, event);
            if (event._stopNow) {
                break;
            }
        }
    }

    dispatchEvent(event) {
        event.target = this;
        const path = [];
        for (let node = this._parentTarget(); node; node = node._parentTarget()) {
            path.push(node);
        }

        for (let i = path.length - 1; i >= 0 && !event._stop; i--) {
            path[i]._fire(event, 1);
        }

        if (!event._stop) {
            this._fire(event, 2);
        }

        if (event.bubbles) {
            for (let i = 0; i < path.length && !event._stop; i++) {
                path[i]._fire(event, 3);
            }
        }

        return !event.defaultPrevented;
    }

    _parentTarget() {
        return null;
    }
}

class FakeNode extends FakeEventTarget {
    constructor(doc, nodeType) {
        super();
        // Not enumerable, so util.inspect never climbs from a node to its parents, the document and the window: a failed
        // assert.equal between two nodes prints and diffs both sides to depth 1000 (node:assert ignores custom inspect),
        // and through these links that was the whole page, which once took a test process past 20 GB. Reads and writes
        // are as before; an assignment keeps the property not enumerable.
        Object.defineProperty(this, 'ownerDocument', { value: doc, writable: true, enumerable: false, configurable: true });
        this.nodeType = nodeType;
        Object.defineProperty(this, 'parentNode', { value: null, writable: true, enumerable: false, configurable: true });
        this.childNodes = [];
    }

    _parentTarget() {
        if (this.parentNode) {
            return this.parentNode;
        }

        return this.nodeType === 9 ? this._windowTarget || null : null;
    }

    get children() {
        return this.childNodes.filter((n) => n.nodeType === 1);
    }

    get firstChild() {
        return this.childNodes[0] || null;
    }

    get lastChild() {
        return this.childNodes[this.childNodes.length - 1] || null;
    }

    get firstElementChild() {
        return this.children[0] || null;
    }

    get nextSibling() {
        if (!this.parentNode) {
            return null;
        }

        const siblings = this.parentNode.childNodes;
        return siblings[siblings.indexOf(this) + 1] || null;
    }

    get nextElementSibling() {
        for (let n = this.nextSibling; n; n = n.nextSibling) {
            if (n.nodeType === 1) {
                return n;
            }
        }

        return null;
    }

    get isConnected() {
        let node = this;
        while (node.parentNode) {
            node = node.parentNode;
        }

        return node.nodeType === 9;
    }

    contains(other) {
        for (let n = other; n; n = n.parentNode) {
            if (n === this) {
                return true;
            }
        }

        return false;
    }

    _adopt(node) {
        // As the real append(): anything that isn't a node becomes text, so append(null) adds "null".
        if (node === null || node === undefined || typeof node !== 'object' || typeof node.nodeType !== 'number') {
            return this.ownerDocument.createTextNode(String(node));
        }

        if (node.parentNode) {
            node.parentNode.removeChild(node);
        }

        return node;
    }

    appendChild(node) {
        return this.insertBefore(node, null);
    }

    insertBefore(node, reference) {
        const child = this._adopt(node);
        const index = reference ? this.childNodes.indexOf(reference) : -1;
        if (index >= 0) {
            this.childNodes.splice(index, 0, child);
        } else {
            this.childNodes.push(child);
        }

        child.parentNode = this;
        this.ownerDocument._mutated(this);
        return child;
    }

    append(...nodes) {
        nodes.forEach((n) => this.appendChild(n));
    }

    removeChild(node) {
        const index = this.childNodes.indexOf(node);
        if (index >= 0) {
            this.childNodes.splice(index, 1);
            node.parentNode = null;
            this.ownerDocument._mutated(this);
        }

        return node;
    }

    remove() {
        if (this.parentNode) {
            this.parentNode.removeChild(this);
        }
    }

    replaceChildren(...nodes) {
        this.childNodes.slice().forEach((n) => this.removeChild(n));
        this.append(...nodes);
    }

    get textContent() {
        return this.childNodes.map((n) => n.textContent).join('');
    }

    set textContent(value) {
        this.childNodes.slice().forEach((n) => this.removeChild(n));
        if (value !== '' && value !== null && value !== undefined) {
            this.appendChild(this.ownerDocument.createTextNode(String(value)));
        }
    }

    querySelectorAll(selector) {
        const matchers = parseSelectorList(selector);
        const out = [];
        const walk = (node) => {
            for (const child of node.childNodes) {
                if (child.nodeType === 1) {
                    if (matchers.some((m) => m(child, this))) {
                        out.push(child);
                    }

                    walk(child);
                }
            }
        };
        walk(this);
        return out;
    }

    querySelector(selector) {
        return this.querySelectorAll(selector)[0] || null;
    }
}

class FakeText extends FakeNode {
    constructor(doc, data) {
        super(doc, 3);
        this.data = String(data);
    }

    get textContent() {
        return this.data;
    }

    set textContent(value) {
        this.data = String(value);
    }
}

class FakeClassList {
    constructor(element) {
        this._el = element;
    }

    _items() {
        return (this._el.getAttribute('class') || '').split(/\s+/).filter(Boolean);
    }

    contains(name) {
        return this._items().indexOf(name) >= 0;
    }

    add(...names) {
        const items = this._items();
        names.forEach((n) => {
            if (items.indexOf(n) < 0) {
                items.push(n);
            }
        });
        this._el.setAttribute('class', items.join(' '));
    }

    remove(...names) {
        this._el.setAttribute('class', this._items().filter((n) => names.indexOf(n) < 0).join(' '));
    }

    toggle(name, force) {
        const has = this.contains(name);
        const want = force === undefined ? !has : !!force;
        if (want && !has) {
            this.add(name);
        } else if (!want && has) {
            this.remove(name);
        }

        return want;
    }

    get length() {
        return this._items().length;
    }
}

class FakeElement extends FakeNode {
    constructor(doc, tagName) {
        super(doc, 1);
        this.tagName = String(tagName).toUpperCase();
        this.attributes = [];
        this.classList = new FakeClassList(this);
        this.style = {};
        this.disabled = false;
        this.checked = false;
        this.isContentEditable = false;
        this._value = null;
    }

    getAttribute(name) {
        const a = this.attributes.find((x) => x.name === name);
        return a ? a.value : null;
    }

    hasAttribute(name) {
        return this.attributes.some((x) => x.name === name);
    }

    setAttribute(name, value) {
        const a = this.attributes.find((x) => x.name === name);
        if (a) {
            a.value = String(value);
        } else {
            this.attributes.push({ name: String(name), value: String(value) });
        }

        if (name === 'checked') {
            this.checked = true;
        }

        if (name === 'disabled') {
            this.disabled = true;
        }

        this._attributeChanged(name);
    }

    removeAttribute(name) {
        const had = this.hasAttribute(name);
        this.attributes = this.attributes.filter((x) => x.name !== name);
        if (had) {
            this._attributeChanged(name);
        }
    }

    _attributeChanged(name) {
        if (this.ownerDocument && this.ownerDocument._attributeMutated) {
            this.ownerDocument._attributeMutated(this, String(name));
        }
    }

    get id() {
        return this.getAttribute('id') || '';
    }

    set id(value) {
        this.setAttribute('id', value);
    }

    get title() {
        return this.getAttribute('title') || '';
    }

    set title(value) {
        this.setAttribute('title', value);
    }

    get className() {
        return this.getAttribute('class') || '';
    }

    set className(value) {
        this.setAttribute('class', value);
    }

    get value() {
        return this._value !== null ? this._value : (this.getAttribute('value') || (this.tagName === 'SELECT' && this.children[0] ? this.children[0].value : ''));
    }

    set value(v) {
        this._value = String(v);
    }

    get offsetParent() {
        return this.isConnected ? this.ownerDocument.body : null;
    }

    focus() {
        this.ownerDocument.activeElement = this;
    }

    // A text field's select(): the whole value selected (selectionStart and selectionEnd, as tests read them).
    select() {
        this.selectionStart = 0;
        this.selectionEnd = String(this.value).length;
    }

    blur() {
        if (this.ownerDocument.activeElement === this) {
            this.ownerDocument.activeElement = this.ownerDocument.body;
        }
    }

    click() {
        this.dispatchEvent(new FakeEvent('click', { bubbles: true }));
    }

    // Recorded on the page (page.scrolled), so a test can count the scrolls a script asks for.
    scrollIntoView(options) {
        this.ownerDocument._scrolled.push({ element: this, options: options === undefined ? null : options });
    }

    matches(selector) {
        return parseSelectorList(selector).some((m) => m(this, null));
    }

    closest(selector) {
        const matchers = parseSelectorList(selector);
        for (let node = this; node && node.nodeType === 1; node = node.parentNode) {
            if (matchers.some((m) => m(node, null))) {
                return node;
            }
        }

        return null;
    }

    // A box when connected and not hidden by the class "hide", the hidden attribute or style.display = 'none'.
    getClientRects() {
        for (let node = this; node; node = node.parentNode) {
            if (node.nodeType === 1 && (node.classList.contains('hide') || node.hasAttribute('hidden') || node.style.display === 'none')) {
                return [];
            }
        }

        return this.isConnected ? [{ width: 1, height: 1 }] : [];
    }

    // Tests only: a readable view of the subtree.
    get outerHTML() {
        const attrs = this.attributes.map((a) => ' ' + a.name + '="' + a.value.replace(/"/g, '&quot;') + '"').join('');
        const inner = this.childNodes.map((n) => (n.nodeType === 3 ? n.data : n.outerHTML)).join('');
        return '<' + this.tagName.toLowerCase() + attrs + '>' + inner + '</' + this.tagName.toLowerCase() + '>';
    }
}

class FakeDocument extends FakeNode {
    constructor(window) {
        super(null, 9);
        this.ownerDocument = this;
        this.defaultView = window;
        this.readyState = 'complete';
        this.visibilityState = 'visible';
        this._observers = [];
        this.documentElement = new FakeElement(this, 'html');
        this.head = new FakeElement(this, 'head');
        this.body = new FakeElement(this, 'body');
        this.appendChild(this.documentElement);
        this.documentElement.appendChild(this.head);
        this.documentElement.appendChild(this.body);
        this.activeElement = this.body;
        this._scrolled = [];
    }

    createElement(tag) {
        return new FakeElement(this, tag);
    }

    createTextNode(text) {
        return new FakeText(this, text);
    }

    getElementById(id) {
        return this.querySelectorAll('[id]').find((e) => e.id === id) || null;
    }

    _mutated(target) {
        this._observers.forEach((o) => o._notify(target));
    }

    _attributeMutated(target, name) {
        this._observers.forEach((o) => o._notifyAttribute(target, name));
    }
}

class FakeMutationObserver {
    constructor(doc, callback) {
        this._doc = doc;
        this._callback = callback;
        this._targets = [];
        this._pending = false;
    }

    observe(target, options) {
        const o = options || {};
        // Without options (older callers) it watches the children, as before.
        this._targets.push({
            target,
            subtree: !!o.subtree,
            childList: !!o.childList || !o.attributes,
            attributes: !!o.attributes || Array.isArray(o.attributeFilter),
            filter: Array.isArray(o.attributeFilter) ? o.attributeFilter.slice() : null
        });
        if (this._doc._observers.indexOf(this) < 0) {
            this._doc._observers.push(this);
        }
    }

    disconnect() {
        this._targets = [];
        this._doc._observers = this._doc._observers.filter((o) => o !== this);
    }

    _notify(node) {
        this._fire(this._targets.some((t) => t.childList && (t.target === node || (t.subtree && t.target.contains(node)))));
    }

    _notifyAttribute(node, name) {
        this._fire(this._targets.some((t) => t.attributes && (!t.filter || t.filter.indexOf(name) >= 0)
            && (t.target === node || (t.subtree && t.target.contains(node)))));
    }

    _fire(hit) {
        if (hit && !this._pending) {
            this._pending = true;
            queueMicrotask(() => {
                this._pending = false;
                this._callback([], this);
            });
        }
    }
}

// ------------------------------------------------------------------------------------------ selectors

function parseCompound(text) {
    const tests = [];
    const re = /(\*|[a-zA-Z][\w-]*)|#([\w-]+)|\.([\w-]+)|\[([\w-]+)(?:=(?:"([^"]*)"|'([^']*)'|([^\]]*)))?\]|:not\(([^)]*)\)/g;
    let m;
    let consumed = 0;
    while ((m = re.exec(text)) !== null) {
        if (m.index !== consumed) {
            throw new Error('fake-dom: unsupported selector ' + text);
        }

        consumed = re.lastIndex;
        if (m[1]) {
            const any = m[1] === '*';
            const tag = m[1].toUpperCase();
            tests.push((e) => any || e.tagName === tag);
        } else if (m[2]) {
            const id = m[2];
            tests.push((e) => e.id === id);
        } else if (m[3]) {
            const c = m[3];
            tests.push((e) => e.classList.contains(c));
        } else if (m[4]) {
            const name = m[4];
            const value = m[5] !== undefined ? m[5] : (m[6] !== undefined ? m[6] : m[7]);
            tests.push((e) => e.hasAttribute(name) && (value === undefined || e.getAttribute(name) === value));
        } else if (m[8] !== undefined) {
            const inner = parseCompound(m[8].trim());
            tests.push((e) => !inner(e));
        }
    }

    if (consumed !== text.length) {
        throw new Error('fake-dom: unsupported selector ' + text);
    }

    return (e) => tests.every((t) => t(e));
}

function parseSelector(selector) {
    const tokens = selector.trim().replace(/\s*>\s*/g, ' > ').split(/\s+/);
    const parts = [];
    let combinator = ' ';
    for (const token of tokens) {
        if (token === '>') {
            combinator = '>';
            continue;
        }

        parts.push({ test: token === ':scope' ? null : parseCompound(token), combinator, scope: token === ':scope' });
        combinator = ' ';
    }

    return (element, scopeRoot) => {
        const matchFrom = (node, index) => {
            const part = parts[index];
            const ok = part.scope ? node === scopeRoot : part.test(node);
            if (!ok) {
                return false;
            }

            if (index === 0) {
                return true;
            }

            const combinatorBefore = part.combinator;
            let parent = node.parentNode;
            if (combinatorBefore === '>') {
                return !!parent && parent.nodeType === 1 && matchFrom(parent, index - 1);
            }

            for (; parent && parent.nodeType === 1; parent = parent.parentNode) {
                if (matchFrom(parent, index - 1)) {
                    return true;
                }
            }

            return false;
        };
        return matchFrom(element, parts.length - 1);
    };
}

function parseSelectorList(list) {
    return list.split(',').map((s) => parseSelector(s));
}

// ----------------------------------------------------------------------------------------------- page

export function createPage(options) {
    options = options || {};
    const clock = { now: options.start || 0, timers: [], seq: 0 };
    const context = {};
    context.window = context;
    context.self = context;
    const document = new FakeDocument(context);
    const windowTarget = new FakeEventTarget();
    document._windowTarget = windowTarget;
    context.document = document;
    context.addEventListener = windowTarget.addEventListener.bind(windowTarget);
    context.removeEventListener = windowTarget.removeEventListener.bind(windowTarget);
    context.dispatchEvent = windowTarget.dispatchEvent.bind(windowTarget);
    context.console = options.console || { log() {}, info() {}, debug() {}, warn: (...a) => page.warnings.push(a.map(String).join(' ')), error: (...a) => page.errors.push(a.map(String).join(' ')) };
    context.performance = { now: () => clock.now };
    const addTimer = (fn, ms, repeat) => {
        const id = ++clock.seq;
        clock.timers.push({ id, at: clock.now + Math.max(0, Number(ms) || 0), fn, repeat: repeat ? Math.max(1, Number(ms) || 0) : 0 });
        return id;
    };
    context.setTimeout = (fn, ms) => addTimer(fn, ms, false);
    context.setInterval = (fn, ms) => addTimer(fn, ms, true);
    context.clearTimeout = (id) => {
        clock.timers = clock.timers.filter((t) => t.id !== id);
    };
    context.clearInterval = context.clearTimeout;
    context.requestAnimationFrame = (fn) => addTimer(() => fn(clock.now), 16, false);
    context.queueMicrotask = queueMicrotask;
    context.Event = FakeEvent;
    context.CustomEvent = FakeCustomEvent;
    context.KeyboardEvent = FakeEvent;
    context.MutationObserver = function (callback) {
        return new FakeMutationObserver(document, callback);
    };
    context.AbortController = AbortController;
    context.URL = URL;
    context.URLSearchParams = URLSearchParams;
    // Date.now() and new Date() follow the manual clock, from the real time the page was created (wall-clock waits such
    // as the health report's ten minutes pass with page.advance).
    const epoch = Date.now();
    context.Date = class FakeDate extends Date {
        constructor(...args) {
            if (args.length === 0) {
                super(epoch + clock.now);
            } else {
                super(...args);
            }
        }

        static now() {
            return epoch + clock.now;
        }
    };
    // CSS.escape as browsers have it (CSSOM): every character that isn't a letter, digit, hyphen or underscore is
    // backslash-escaped (a leading digit as a code point), so a value can be put in an attribute selector.
    context.CSS = {
        escape: (value) => String(value).replace(/^(\d)/, (d) => '\\3' + d + ' ').replace(/[^a-zA-Z0-9_ -￿-]/g, (c) => '\\' + c)
    };
    const storage = new Map();
    context.localStorage = {
        getItem: (k) => (storage.has(k) ? storage.get(k) : null),
        setItem: (k, v) => storage.set(k, String(v)),
        removeItem: (k) => storage.delete(k),
        clear: () => storage.clear(),
        key: (i) => (i >= 0 && i < storage.size ? Array.from(storage.keys())[i] : null),
        get length() {
            return storage.size;
        }
    };
    const location = { href: 'http://127.0.0.1:8097/web/index.html#/home', hash: '#/home' };
    context.location = location;
    context.history = {
        length: 1,
        pushState(state, title, url) {
            this.length++;
            if (url && String(url).indexOf('#') >= 0) {
                location.hash = String(url).substring(String(url).indexOf('#'));
            }
        },
        replaceState(state, title, url) {
            if (url && String(url).indexOf('#') >= 0) {
                location.hash = String(url).substring(String(url).indexOf('#'));
            }
        }
    };

    const page = {
        context,
        window: context,
        document,
        clock,
        warnings: [],
        errors: [],
        fetchLog: [],
        responses: [],
        scrolled: document._scrolled,
        Event: FakeEvent,
        CustomEvent: FakeCustomEvent
    };

    // fetch: the first scripted response whose method and path pattern match, else 404.
    context.fetch = (url, init) => {
        const method = (init && init.method) || 'GET';
        const parsed = new URL(String(url), 'http://127.0.0.1:8097');
        const client = context.ApiClient;
        const entry = {
            t: clock.now,
            method,
            path: parsed.pathname,
            query: Object.fromEntries(parsed.searchParams.entries()),
            headers: (init && init.headers) || {},
            body: init && init.body !== undefined ? parseBody(init.body) : undefined,
            rawBody: init ? init.body : undefined,
            userId: client && client.getCurrentUserId ? client.getCurrentUserId() : null
        };
        page.fetchLog.push(entry);
        const match = page.responses.find((r) => r.method === method && (r.path instanceof RegExp ? r.path.test(entry.path) : r.path === entry.path));
        if (init && init.signal && init.signal.aborted) {
            return Promise.reject(new Error('aborted'));
        }

        if (match && match.hang) {
            return new Promise((resolve, reject) => {
                if (init && init.signal) {
                    init.signal.addEventListener('abort', () => reject(new Error('aborted')));
                }
            });
        }

        if (match && match.networkError) {
            return Promise.reject(new TypeError('Failed to fetch'));
        }

        // A status may be a function of the request too (a test's server that decides, as User Ratings' preconditions do);
        // it runs before the body's.
        const status = match ? ((typeof match.status === 'function' ? match.status(entry) : match.status) || 200) : 404;
        const body = match ? (typeof match.body === 'function' ? match.body(entry) : match.body) : undefined;
        const text = body === undefined || body === null ? '' : (typeof body === 'string' ? body : JSON.stringify(body));
        return Promise.resolve({
            ok: status >= 200 && status < 300,
            status,
            // A response's other headers, from respond()'s extra { headers: { Name: value } } (matched ignoring case).
            headers: { get: (name) => (name.toLowerCase() === 'content-type' ? (match && match.contentType) || 'application/json' : headerOf(match, name)) },
            text: () => Promise.resolve(text),
            json: () => Promise.resolve(JSON.parse(text))
        });
    };

    const headerOf = (match, name) => {
        const found = match && match.headers ? Object.keys(match.headers).find((k) => k.toLowerCase() === name.toLowerCase()) : undefined;
        return found === undefined ? null : String(match.headers[found]);
    };

    page.respond = (method, path, body, status, extra) => {
        page.responses.unshift(Object.assign({ method, path, body, status }, extra || {}));
    };

    // A jellyfin-web ApiClient stand-in with a prototype, as the real one (the kit wraps prototype methods).
    page.installApiClient = (values) => {
        values = values || {};
        const state = { userId: values.userId === undefined ? null : values.userId, token: values.token || 'test-token' };
        function FakeApiClient() {}
        FakeApiClient.prototype.getUrl = function (path, query) {
            const q = query ? '?' + new URLSearchParams(Object.entries(query).filter(([, v]) => v !== undefined && v !== null).map(([k, v]) => [k, String(v)])).toString() : '';
            return 'http://127.0.0.1:8097/' + String(path).replace(/^\//, '') + (q === '?' ? '' : q);
        };
        FakeApiClient.prototype.accessToken = function () {
            return state.token;
        };
        FakeApiClient.prototype.getCurrentUserId = function () {
            return state.userId;
        };
        FakeApiClient.prototype.appName = () => 'Jellyfin Web';
        FakeApiClient.prototype.appVersion = () => '10.11.5';
        FakeApiClient.prototype.deviceName = () => 'Chrome';
        FakeApiClient.prototype.deviceId = () => 'device-1';
        FakeApiClient.prototype.serverId = () => 'server-1';
        FakeApiClient.prototype.getScaledImageUrl = (id, o) => 'http://127.0.0.1:8097/Items/' + id + '/Images/' + o.type + '?tag=' + o.tag + '&maxWidth=' + o.maxWidth;
        FakeApiClient.prototype.getItem = (userId, id) => Promise.resolve(values.items && values.items[id] ? values.items[id] : null);
        FakeApiClient.prototype.getCurrentUser = () => Promise.resolve(values.user || { Id: state.userId, Policy: { IsAdministrator: !!values.admin } });
        FakeApiClient.prototype.reportPlaybackStart = function (info) {
            page.reports.push(['start', info]);
            return Promise.resolve();
        };
        FakeApiClient.prototype.reportPlaybackProgress = function (info) {
            page.reports.push(['progress', info]);
            return Promise.resolve();
        };
        FakeApiClient.prototype.reportPlaybackStopped = function (info) {
            page.reports.push(['stopped', info]);
            return Promise.resolve();
        };
        const client = new FakeApiClient();
        context.ApiClient = client;
        page.apiClientAt = clock.now;
        page.clientState = state;
        return client;
    };
    page.reports = [];

    // Moves the clock forward, running every timer that falls due (including ones they schedule), in time order.
    page.advance = async (ms) => {
        const end = clock.now + ms;
        for (;;) {
            await flushMicrotasks();
            const due = clock.timers.filter((t) => t.at <= end).sort((a, b) => a.at - b.at || a.id - b.id)[0];
            if (!due) {
                break;
            }

            clock.now = Math.max(clock.now, due.at);
            if (due.repeat) {
                due.at += due.repeat;
            } else {
                clock.timers = clock.timers.filter((t) => t !== due);
            }

            due.fn();
        }

        clock.now = end;
        await flushMicrotasks();
    };
    page.flush = flushMicrotasks;

    page.run = (code, filename) => vm.runInContext(code, vmContext, { filename: filename || 'script.js' });
    page.dispatch = (target, type, init) => target.dispatchEvent(new FakeEvent(type, Object.assign({ bubbles: true }, init || {})));
    page.key = (target, key, init) => page.dispatch(target, 'keydown', Object.assign({ key, cancelable: true }, init || {}));
    page.element = (tag, attrs, children) => {
        const e = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => e.setAttribute(k, v));
        (children || []).forEach((c) => e.append(c));
        return e;
    };

    const vmContext = vm.createContext(context);
    return page;
}

// A request's body as JSON when it is JSON, else the text itself (kit.api's text option sends a body as it is).
function parseBody(body) {
    try {
        return JSON.parse(body);
    } catch (e) {
        return body;
    }
}

async function flushMicrotasks() {
    for (let i = 0; i < 20; i++) {
        await new Promise((resolve) => setImmediate(resolve));
    }
}

// Plain JSON copy of a value from the page's realm, for deepStrictEqual.
export function plain(value) {
    return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

// kit/web/kit.js, loaded into a page: its declarations (createKit, KitApiError and the two tables).
export const KIT_JS = new URL('../kit.js', import.meta.url);

export function loadKit(page) {
    const source = fs.readFileSync(KIT_JS, 'utf8');
    page.run(source + String.fromCharCode(10) + ';globalThis.__kitExports = { createKit, KitApiError, KIT_NATIVE_CLASSES, KIT_ANCHORS };', 'kit.js');
    return page.context.__kitExports;
}

// kit/web/anchors.json in the shape EmbeddedScript gives createKit (options.anchors): the kit's own anchors, plus
// optional plugin anchors ({ name, css, required, views }).
export const KIT_ANCHORS_JSON = new URL('../anchors.json', import.meta.url);

export function anchorDeclarations(pluginAnchors) {
    const kitAnchors = JSON.parse(fs.readFileSync(KIT_ANCHORS_JSON, 'utf8')).anchors;
    return kitAnchors.concat(pluginAnchors || []).map((a) => ({ name: a.name, css: a.css, required: a.required, views: a.views }));
}
