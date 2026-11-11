// Access Roles' admin page: the AdminScript module's shared words and helpers. The module is the kit
// (above) and these files, in name order; 90-mount.js declares mount(view, params). Every value from the server is written
// as text, through kit.h's text and textContent (never as HTML), and no file touches the page until mount is called.

// The locks in the API's order, with what the role editor and the per-user panel call them.
const AR_SETTINGS_SECTIONS = ['Profile', 'DisplayAccount', 'DisplayDevice', 'Home', 'Playback', 'Subtitles', 'Controls', 'QuickConnect', 'EnhancedPanel'];
const AR_ALL_LOCKS = AR_SETTINGS_SECTIONS.concat(['AdminSurfaces', 'UserDirectory']);
// The sections whose settings live in the browser or the app: the web hides them, the server has nothing to enforce.
const AR_HIDDEN_ONLY = ['DisplayDevice', 'Controls'];
const AR_LOCK_LABELS = {
    Profile: 'Profile (password, picture, name)',
    DisplayAccount: 'Display (account)',
    DisplayDevice: 'Display (this device)',
    Home: 'Home screen',
    Playback: 'Playback',
    Subtitles: 'Subtitles',
    Controls: 'Controls',
    QuickConnect: 'Quick Connect',
    EnhancedPanel: 'Jellyfin Enhanced settings',
    AdminSurfaces: 'Dashboard data and server calls',
    UserDirectory: 'Other users'
};

// The presets of a new role (the server's AccessPresets).
const AR_PRESETS = {
    Kid: {
        locks: ['Profile', 'DisplayAccount', 'Home', 'Controls', 'QuickConnect', 'EnhancedPanel', 'AdminSurfaces', 'UserDirectory'],
        mode: 'Only',
        text: 'Locks the profile, the account\'s display settings, the home screen, controls, Quick Connect and Jellyfin Enhanced\'s settings, blocks the Dashboard\'s data and hides other users. Grants only the libraries you pick.'
    },
    Guest: {
        locks: AR_SETTINGS_SECTIONS.concat(['AdminSurfaces', 'UserDirectory']),
        mode: 'Only',
        text: 'Locks all nine settings sections, blocks the Dashboard\'s data and hides other users. Grants only the libraries you pick.'
    },
    Standard: {
        locks: [],
        mode: 'NotManaged',
        text: 'No locks; leaves library access as it is. A starting point for a role of your own.'
    }
};

const AR_ROLE_MODES = [
    { value: 'NotManaged', text: 'Not managed' },
    { value: 'All', text: 'All libraries (including future ones)' },
    { value: 'AllExcept', text: 'All except…' },
    { value: 'Only', text: 'Only…' }
];
const AR_USER_MODES = [
    { value: 'Inherit', text: 'Inherit from roles' },
    { value: 'All', text: 'All libraries' },
    { value: 'Only', text: 'Only…' }
];
const AR_STATES = {
    NotManaged: 'Not managed',
    Managed: 'Managed',
    Excluded: 'Excluded (management paused)',
    CannotManage: 'Can\'t be managed',
    UnknownValue: 'Set by a newer version'
};
const AR_CHOICES = [
    { value: 'Restore', text: 'Restore original access' },
    { value: 'Keep', text: 'Keep current access' },
    { value: 'GrantAll', text: 'Grant all libraries' }
];

// The page's own classes: jfar-page-<name> (jfar-admin-banner is the web client's administrators' banner, Web/css).
function arClass(name) {
    return KIT_BUILD.prefix + '-page-' + name;
}

function arEl(tag, props, children) {
    return kit.h(tag, props, children);
}

function arClear(node) {
    while (node && node.firstChild) {
        node.removeChild(node.firstChild);
    }

    return node;
}

function arShow(node, shown) {
    if (shown) {
        node.removeAttribute('hidden');
    } else {
        node.setAttribute('hidden', '');
    }
}

// GUIDs arrive without dashes (Jellyfin's JSON) and may be typed with them: compared lower-case without dashes.
function arId(id) {
    return String(id === undefined || id === null ? '' : id).replace(/-/g, '').toLowerCase();
}

function arSameId(a, b) {
    return arId(a) === arId(b);
}

function arHasId(list, id) {
    return (list || []).some(function (item) {
        return arSameId(item, id);
    });
}

function arNames(items) {
    return (items || []).map(function (item) {
        return item && item.Name !== undefined ? item.Name : item;
    }).join(', ');
}

// A name an admin typed (a role's, a user's), isolated from the text around it, so a right-to-left name can't reorder the
// sentence it sits in: <bdi> is dir="auto" and unicode-bidi: isolate.
function arName(text) {
    return arEl('bdi', { class: arClass('name'), text: String(text === undefined || text === null ? '' : text) });
}

// Names (or { Name } items) as isolated names with ", " between them, for a sentence's children.
function arNameNodes(items) {
    const nodes = [];
    (items || []).forEach(function (item, index) {
        if (index > 0) {
            nodes.push(', ');
        }

        nodes.push(arName(item && item.Name !== undefined ? item.Name : item));
    });
    return nodes;
}

// A list edited from was to mine, put over now ("Reload and keep my changes"): what the admin took out
// stays out, what they added is added, and the rest is as it is now.
function arRebaseList(was, mine, now) {
    was = was || [];
    mine = mine || [];
    const removed = was.filter(function (id) {
        return !arHasId(mine, id);
    });
    const result = (now || []).filter(function (id) {
        return !arHasId(removed, id);
    });
    mine.forEach(function (id) {
        if (!arHasId(was, id) && !arHasId(result, id)) {
            result.push(id);
        }
    });
    return result;
}

function arDate(value) {
    const date = new Date(value);
    return value && !isNaN(date.getTime()) ? date.toLocaleString() : '';
}

function arErrorText(error, fallback) {
    return (error && error.message) || fallback || 'The server could not be reached.';
}

// A 409 Changed (the kit's write tokens): what the request's tokens say the admin read has changed since.
function arChanged(error) {
    return !!error && error.status === 409 && !!error.problem && error.problem.Code === 'Changed';
}

function arChangedText(error) {
    const users = (error && error.problem && error.problem.Users) || [];
    return 'This changed after you opened it' + (users.length ? ' (' + arNames(users) + ')' : '')
        + ', in another tab or by another administrator, so nothing was saved.';
}

function arLockLabel(name) {
    return AR_LOCK_LABELS[name] || name;
}

function arModeLabel(modes, value) {
    const found = modes.filter(function (m) {
        return m.value === value;
    })[0];
    return found ? found.text : String(value || '');
}

// A banner of the page: warning (amber) or error (red), role="alert".
function arBanner(text, kind) {
    return arEl('p', { class: arClass('banner') + (kind === 'error' ? ' ' + arClass('banner-error') : ''), role: 'alert', text: text });
}

function arHint(text) {
    return arEl('p', { class: arClass('hint'), text: text });
}

function arHeading(level, text) {
    return arEl('h' + level, { class: arClass('heading'), text: text });
}

function arList(items) {
    return arEl('ul', { class: arClass('list') }, items.map(function (item) {
        return arEl('li', item && item.nodeType === 1 ? null : { text: String(item) }, item && item.nodeType === 1 ? item : undefined);
    }));
}

// Rows of [label, value] as a two-column table; a value may be a node.
function arTable(rows) {
    return arEl('table', { class: arClass('table') }, rows.map(function (row) {
        const cell = arEl('td');
        if (row[1] && row[1].nodeType === 1) {
            cell.appendChild(row[1]);
        } else {
            cell.textContent = row[1] === undefined || row[1] === null ? '' : String(row[1]);
        }

        return arEl('tr', null, [arEl('th', { text: row[0] }), cell]);
    }));
}

function arBadge(text, kind) {
    return arEl('span', { class: arClass('badge') + (kind ? ' ' + arClass('badge-' + kind) : ''), text: text });
}

// A checkbox with its label. Enter toggles it too: a TV remote's OK sends Enter, which does nothing on a checkbox by
// itself (jellyfin-web's own emby-checkbox does the same). opts: { checked, disabled, hint, onChange }.
function arCheckbox(label, opts) {
    opts = opts || {};
    const input = arEl('input', { type: 'checkbox', class: arClass('check-input') });
    input.checked = !!opts.checked;
    input.disabled = !!opts.disabled;
    if (opts.onChange) {
        input.addEventListener('change', function () {
            opts.onChange(input.checked);
        });
    }

    const element = arEl('label', { class: arClass('check') }, [
        input,
        arEl('span', { class: arClass('check-text') }, [
            arEl('span', { dir: 'auto', text: label }),
            opts.hint ? arEl('span', { class: arClass('check-hint'), text: opts.hint }) : null
        ])
    ]);
    return { element: element, input: input };
}

// Radio buttons in a fieldset; Enter checks the focused one (the TV remote's OK). Answers { element, value(), set(value) }.
let arRadioGroups = 0;
function arRadios(legend, choices, value, onChange) {
    arRadioGroups++;
    const name = KIT_BUILD.prefix + '-page-radio-' + arRadioGroups;
    const inputs = [];
    const element = arEl('fieldset', { class: arClass('radios') }, [arEl('legend', { text: legend })].concat(choices.map(function (choice) {
        const input = arEl('input', { type: 'radio', name: name, value: choice.value, class: arClass('check-input') });
        // The attribute too, so a selector (a webcheck's input[value=Keep]) finds the choice.
        input.setAttribute('value', choice.value);
        input.checked = choice.value === value;
        input.addEventListener('change', function () {
            if (input.checked && onChange) {
                onChange(choice.value);
            }
        });
        inputs.push(input);
        return arEl('label', { class: arClass('check') }, [input, arEl('span', { class: arClass('check-text') }, arEl('span', { text: choice.text }))]);
    })));
    return {
        element: element,
        value: function () {
            const checked = inputs.filter(function (i) {
                return i.checked;
            })[0];
            return checked ? checked.value : null;
        },
        set: function (wanted) {
            inputs.forEach(function (i) {
                i.checked = i.value === wanted;
            });
        }
    };
}

// The TV remote's OK (Enter) on a checkbox or radio of ours: checks or toggles it and tells its listeners. Answers whether
// it handled the key, for a dialog's onKey and the page's own keydown listener.
function arEnterKey(event) {
    const target = event.target;
    if (event.key !== 'Enter' || !target || target.tagName !== 'INPUT' || target.disabled) {
        return false;
    }

    const type = String(target.type || target.getAttribute('type') || '').toLowerCase();
    if (type !== 'checkbox' && type !== 'radio') {
        return false;
    }

    if (event.preventDefault) {
        event.preventDefault();
    }

    target.checked = type === 'radio' ? true : !target.checked;
    target.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
}

// A select with its label (kit.field), its choices { value, text }; a value it has no choice for is kept as one.
function arSelect(label, choices, value, onChange) {
    const known = choices.some(function (c) {
        return c.value === value;
    });
    const select = kit.select({}, known || value === null || value === undefined ? choices : choices.concat([{ value: value, text: String(value) }]));
    if (value !== null && value !== undefined) {
        select.value = value;
    }

    if (onChange) {
        select.addEventListener('change', function () {
            onChange(select.value);
        });
    }

    return { element: kit.field(label, select), select: select };
}

function arButton(label, onClick, opts) {
    opts = opts || {};
    const button = kit.button(label, { variant: opts.variant, className: opts.className, onClick: onClick });
    if (opts.disabled) {
        button.disabled = true;
    }

    if (opts.title) {
        button.title = opts.title;
    }

    return button;
}

function arActions(buttons) {
    return arEl('div', { class: arClass('actions') }, buttons);
}

// Runs an action with its button disabled, so a second press can't send it twice. A dialog the action opened and closed
// meanwhile found the button disabled and couldn't give the focus back to it: the button takes it again when nothing has
// it (on the TV layout the D-pad has nowhere to start from otherwise). enabled() (optional) says whether the button is on
// afterwards (a preview's Save after a 409 isn't). An error is the action's to show; it never
// reaches the console (the page shows it).
function arBusy(button, action, enabled) {
    if (button.disabled) {
        return Promise.resolve(null);
    }

    button.disabled = true;
    const done = function () {
        button.disabled = enabled ? !enabled() : false;
        const active = document.activeElement;
        if (button.isConnected && !button.disabled && (!active || active === document.body || !active.isConnected)) {
            button.focus();
        }
    };
    return Promise.resolve().then(action).then(function (value) {
        done();
        return value;
    }, function () {
        done();
        return null;
    });
}

// The error line of a dialog or a section: empty until an error, role="alert".
function arErrorLine() {
    return arEl('p', { class: arClass('error'), role: 'alert' });
}

function arDialog(title, opts) {
    return kit.dialog(Object.assign({ title: title, scopedKeys: true, onKey: arEnterKey }, opts || {}));
}

// A rule name of the settings filter's (the settings locks' and the server-wide rules) as the rules it names: the filter
// times an action with more than one server-wide rule, and counts a failure of its rules, under their names joined with
// + (ServerHardening.RuleNames), so
// "LibraryVisibility+ItemIds" is two rules. Answers the parts, trimmed; a name with no + is its only part.
function arRuleParts(name) {
    return String(name === undefined || name === null ? '' : name).split('+').map(function (part) {
        return part.trim();
    }).filter(function (part) {
        return part.length > 0;
    });
}

// A rule name shown as its rules, " + " between them, each a code element whose data-rule is the rule; the name as the
// server has it in data-rule-name and the title (what the Audit tab's rule filter and the Diagnostics tab's search find).
function arRuleNode(name) {
    const raw = String(name === undefined || name === null ? '' : name);
    const node = arEl('span', { class: arClass('rule'), title: raw, 'data-rule-name': raw });
    arRuleParts(raw).forEach(function (part, index) {
        if (index > 0) {
            node.appendChild(document.createTextNode(' + '));
        }

        node.appendChild(arEl('code', { class: arClass('rule-part'), 'data-rule': part, text: part }));
    });
    return node;
}

// Whether a rule name is what an admin searched for, ignoring case: one of its rules, or the name as it is ("A+B", spaces
// around + or not); an empty search finds every rule.
function arRuleMatches(name, search) {
    const wanted = arRuleParts(search).join('+').toLowerCase();
    if (!wanted) {
        return true;
    }

    return arRuleParts(name).join('+').toLowerCase() === wanted || arRuleParts(name).some(function (part) {
        return part.toLowerCase() === wanted;
    });
}

// A chip of the Overview's status: kind ok | warning | error | info, its children the text and isolated names.
function arChip(id, kind, children) {
    return arEl('li', { class: arClass('chip') + ' ' + arClass('chip-' + kind), 'data-chip': id, 'data-kind': kind }, children);
}

// Hands the browser a file to save (the audit export): a link to the blob, clicked and taken away again.
function arDownload(blob, fileName) {
    if (typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function') {
        throw new Error('This browser can\'t save the file.');
    }

    const url = URL.createObjectURL(blob);
    const link = arEl('a', { href: url, download: fileName, class: arClass('download'), hidden: true });
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(function () {
        URL.revokeObjectURL(url);
    }, 0);
}

// A count with its noun: "1 user", "3 users".
function arCount(count, one, many) {
    return count + ' ' + (count === 1 ? one : (many || one + 's'));
}
