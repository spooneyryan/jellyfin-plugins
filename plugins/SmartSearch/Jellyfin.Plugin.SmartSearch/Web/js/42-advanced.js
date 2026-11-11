// The advanced form: Basic | Advanced, and the rule tree (35-tree.js) drawn and edited.
//
// A group reads "[All of | Any of | None of | Not all of] the following" (its match and negation in one select), then
// its rules and groups, then Add rule and Add group (each off at the server's limits: 50 rules, groups 4 deep, 50
// children); a nested group also offers Duplicate, Ungroup (its children move up) and Remove. A rule reads
// "[Field] [Condition] [value]" with Duplicate and Remove: the fields grouped as the server groups them (a field that
// doesn't apply to the item types searched is offered but marked, and refused when searched), the conditions its field
// takes, and an editor for the condition's value: text, a number, a date, a range of two, an amount of days, weeks,
// months or years, yes or no, buttons for a field's few choices or the user's libraries, and chips with suggestions for
// values (genres, tags, studios, parental ratings and containers from GET /SmartSearch/Values, shows from
// GET /SmartSearch/Series by name, years typed). Every control has a label; a rule is a group named "Rule n".
//
// The whole tree is drawn again after a change of its shape (a rule added, removed, moved to another field or
// condition), and the focus goes back to the control it was on, or to the nearest one left. Typing changes the tree in
// place. Enter in a text field searches. Before a search or a save the rules are checked here (ssCheckTree), and the
// server's refusals are shown under the rule they name, by their path; the first one marked takes the focus.
//
// Users' text (values, show and library names) is always text: chips and suggestions in a <bdi>, control characters as
// U+FFFD.

// ------------------------------------------------------------------------------------------------ basic and advanced

function ssBuildModeSwitch(page) {
    const h = kit.h;
    page.modeButtons = [['basic', SS_TEXT.modeBasic], ['advanced', SS_TEXT.modeAdvanced]].map(function (mode) {
        return h('button', {
            type: 'button',
            class: 'jfss-segment jfss-mode',
            'data-jfss-mode': mode[0],
            'aria-pressed': page.mode === mode[0] ? 'true' : 'false',
            text: mode[1],
            onclick: function () {
                ssSetMode(page, mode[0]);
            }
        });
    });
    const labelId = ssNextId('mode');
    page.modeNote = h('span', { class: 'jfss-mode-note', role: 'status' });
    page.modeSwitch = h('div', { class: kit.cls('field') + ' jfss-mode-field' }, [
        h('span', { class: 'jfss-label', id: labelId, text: SS_TEXT.modeLabel }),
        h('div', { class: 'jfss-segments jfss-modes', role: 'group', 'aria-labelledby': labelId }, page.modeButtons),
        page.modeNote
    ]);
    return page.modeSwitch;
}

function ssBuildAdvanced(page) {
    page.advanced = kit.h('div', { class: 'jfss-advanced' });
    return page.advanced;
}

// The query the form shows now could be the basic form's: then Basic may be chosen.
function ssBasicStateOf(page) {
    const built = ssBuildQuery(ssReadForm(page), page.kept);
    return ssStateFromQuery(built.query, {
        kinds: page.catalog.kinds,
        sortKeys: page.catalog.sortKeys,
        libraryIds: page.libraries.map(function (l) {
            return l.Id;
        })
    });
}

function ssSyncMode(page) {
    if (!page.modeButtons) {
        return;
    }

    page.modeButtons.forEach(function (button) {
        button.setAttribute('aria-pressed', button.getAttribute('data-jfss-mode') === page.mode ? 'true' : 'false');
    });
    const basicOk = page.mode === 'basic' || !!ssBasicStateOf(page);
    page.modeButtons[0].disabled = !basicOk;
    page.modeNote.textContent = basicOk ? '' : SS_TEXT.basicUnavailable;
    kit.setHidden(page.basicFields, page.locked || page.mode !== 'basic');
    kit.setHidden(page.advanced, page.locked || page.mode !== 'advanced');
}

// Basic to Advanced: the basic fields become rules of an All group. Advanced to Basic: only when the rules are what the
// basic form shows (otherwise Basic is off, with a note).
function ssSetMode(page, mode) {
    if (mode === page.mode) {
        return;
    }

    if (mode === 'advanced') {
        const where = ssBuildQuery(ssReadForm(page), null).query.Where;
        page.mode = 'advanced';
        page.tree = ssTreeFromWhere(where, page.catalog);
        ssShowFieldErrors(page, {});
        ssSyncMode(page);
        ssRenderTree(page, page.tree.children.length ? { id: page.tree.children[0].id, part: 'field' } : { id: page.tree.id, part: 'add-rule' });
    } else {
        const basic = ssBasicStateOf(page);
        if (!basic) {
            ssSyncMode(page);
            return;
        }

        page.mode = 'basic';
        page.titleInput.value = basic.q;
        page.fromInput.value = basic.from;
        page.toInput.value = basic.to;
        page.genres = basic.genres.slice();
        ssRenderChips(page);
        ssShowTreeErrors(page, {});
        ssSyncMode(page);
        page.titleInput.focus();
    }

    ssSyncActions(page);
}

// ------------------------------------------------------------------------------------------------ drawing the tree

// The tree drawn again; focus: { id, part } of the control to focus afterwards (when it is there).
function ssRenderTree(page, focus) {
    if (!page.advanced) {
        return;
    }

    page.advanced.replaceChildren(ssRenderGroup(page, page.tree, 1, 0));
    kit.upgrade(page.advanced);
    if (page.treeErrors) {
        ssShowTreeErrors(page, page.treeErrors, true);
    }

    if (focus) {
        ssFocusNode(page, focus);
    }
}

function ssFocusNode(page, focus) {
    const node = page.advanced.querySelector('[data-jfss-focus="' + focus.id + ':' + focus.part + '"]')
        || page.advanced.querySelector('[data-jfss-node="' + focus.id + '"] [data-jfss-focus]');
    if (node && typeof node.focus === 'function') {
        node.focus();
    }
}

function ssFocusKey(node, id, part) {
    node.setAttribute('data-jfss-focus', id + ':' + part);
    return node;
}

// A change of the tree's shape: drawn again, the focus on `focus`, the actions brought up to date.
function ssTreeChanged(page, focus) {
    page.treeErrors = null;
    ssRenderTree(page, focus);
    ssSyncActions(page);
}

function ssMatchValue(group) {
    if (group.negate) {
        return group.match === 'Any' ? 'None' : 'NotAll';
    }

    return group.match;
}

function ssMatchText(group) {
    return { All: SS_TEXT.matchAll, Any: SS_TEXT.matchAny, None: SS_TEXT.matchNone, NotAll: SS_TEXT.matchNotAll }[ssMatchValue(group)];
}

function ssGroupLabel(group, depth) {
    return depth === 1 ? SS_TEXT.rulesLabel + ', ' + ssMatchText(group) : ssText('groupLabel', { match: ssMatchText(group) });
}

function ssRenderGroup(page, group, depth) {
    const h = kit.h;
    const root = depth === 1;
    const box = h('div', { class: 'jfss-group' + (root ? ' jfss-group-root' : ''), role: 'group', 'data-jfss-node': group.id, 'aria-label': ssGroupLabel(group, depth) });
    const match = ssFocusKey(kit.select({ class: 'jfss-match', 'aria-label': SS_TEXT.matchLabel }, [
        { value: 'All', text: SS_TEXT.matchAll },
        { value: 'Any', text: SS_TEXT.matchAny },
        { value: 'None', text: SS_TEXT.matchNone },
        { value: 'NotAll', text: SS_TEXT.matchNotAll }
    ]), group.id, 'match');
    match.value = ssMatchValue(group);
    match.addEventListener('change', function () {
        const v = match.value;
        group.match = v === 'Any' || v === 'None' ? 'Any' : 'All';
        group.negate = v === 'None' || v === 'NotAll';
        box.setAttribute('aria-label', ssGroupLabel(group, depth));
        ssSyncActions(page);
    });
    const addRule = ssFocusKey(kit.button(SS_TEXT.addRule, { icon: 'add', className: 'jfss-add-rule', onClick: function () {
        const rule = ssAddRule(page.tree, group.id, page.catalog, ssPressedKinds(page));
        if (rule) {
            ssTreeChanged(page, { id: rule.id, part: 'field' });
        }
    } }), group.id, 'add-rule');
    addRule.disabled = !ssCanAddRule(page.tree, group.id);
    if (addRule.disabled) {
        addRule.title = SS_TEXT.limitRules;
    }

    const addGroup = ssFocusKey(kit.button(SS_TEXT.addGroup, { icon: 'playlist_add', className: 'jfss-add-group', onClick: function () {
        const added = ssAddGroup(page.tree, group.id, page.catalog, ssPressedKinds(page));
        if (added) {
            ssTreeChanged(page, { id: added.id, part: 'match' });
        }
    } }), group.id, 'add-group');
    addGroup.disabled = !ssCanAddGroup(page.tree, group.id);
    if (addGroup.disabled) {
        addGroup.title = depth >= SS_MAX_DEPTH ? SS_TEXT.limitDepth : SS_TEXT.limitRules;
    }

    const tools = [];
    if (!root) {
        tools.push(ssNodeAction(page, group, 'content_copy', SS_TEXT.duplicateGroup, 'jfss-duplicate-node', 'duplicate', ssCanDuplicate(page.tree, group.id), function () {
            const copy = ssDuplicateNode(page.tree, group.id);
            return copy ? { id: copy.id, part: 'match' } : null;
        }));
        tools.push(ssNodeAction(page, group, 'format_indent_decrease', SS_TEXT.unwrapGroup, 'jfss-unwrap', 'unwrap', ssCanUnwrap(page.tree, group.id), function () {
            const moved = ssUnwrapGroup(page.tree, group.id);
            return moved && moved.length ? { id: moved[0].id, part: moved[0].type === 'group' ? 'match' : 'field' } : ssNeighbourFocus(page, group.id);
        }));
        tools.push(ssNodeAction(page, group, 'delete', SS_TEXT.removeGroup, 'jfss-remove-node', 'remove', true, function () {
            const next = ssNeighbourFocus(page, group.id);
            ssRemoveNode(page.tree, group.id);
            return next;
        }));
    }

    const children = h('div', { class: 'jfss-children', role: 'list' });
    let n = 0;
    group.children.forEach(function (child) {
        const item = h('div', { class: 'jfss-child', role: 'listitem' });
        if (child.type === 'group') {
            item.append(ssRenderGroup(page, child, depth + 1));
        } else {
            n += 1;
            item.append(ssRenderRule(page, child, n));
        }

        children.append(item);
    });
    box.append(
        h('div', { class: 'jfss-group-head' }, [match].concat(tools.length ? [h('span', { class: 'jfss-node-tools' }, tools)] : [])),
        ssNodeError(group.id),
        group.children.length ? children : h('p', { class: 'jfss-group-empty', text: root ? SS_TEXT.groupEmptyNote : SS_TEXT.groupEmpty }),
        h('div', { class: 'jfss-group-add' }, [addRule, addGroup]));
    return box;
}

// An icon button acting on a node: the change made, then the tree drawn again with the focus where `run` says.
function ssNodeAction(page, node, icon, label, cls, part, enabled, run) {
    const button = kit.iconButton(icon, label, function () {
        const focus = run();
        ssTreeChanged(page, focus);
    });
    button.classList.add('jfss-node-action', cls);
    button.disabled = !enabled;
    return ssFocusKey(button, node.id, part);
}

// Where the focus goes when a node is removed: the next node, else the one before, else its group's Add rule.
function ssNeighbourFocus(page, id) {
    const at = ssFindNode(page.tree, id);
    if (!at || !at.parent) {
        return null;
    }

    const sibling = at.parent.children[at.index + 1] || at.parent.children[at.index - 1];
    if (sibling) {
        return { id: sibling.id, part: sibling.type === 'group' ? 'match' : 'field' };
    }

    return { id: at.parent.id, part: 'add-rule' };
}

function ssNodeError(id) {
    return kit.h('div', { class: kit.cls('error') + ' jfss-node-error', 'data-jfss-node-error': id, role: 'alert' });
}

// The field select: the server's groups as option groups; a field for none of the kinds searched, or not available, is
// marked (still offered, so a rule can be built before its kinds are chosen); a locked rule's field shows as it is.
function ssFieldSelect(page, rule) {
    const h = kit.h;
    const kinds = ssPressedKinds(page);
    const select = ssFocusKey(kit.select({ class: 'jfss-rule-field', 'aria-label': SS_TEXT.fieldLabel }, []), rule.id, 'field');
    const known = !!ssField(page.catalog, rule.field);
    page.catalog.groups.forEach(function (group) {
        const options = page.catalog.fields.filter(function (f) {
            return f.Group === group;
        }).map(function (f) {
            let text = f.Label;
            if (f.Available === false) {
                text = ssText('fieldNotAvailable', { field: f.Label });
            } else if (!ssFieldFits(f, kinds)) {
                text = ssText('fieldNotForKinds', { field: f.Label });
            }

            return h('option', { value: f.Id, text: rule.locked && f.Id === rule.field ? ssText('fieldLockedLabel', { field: f.Label }) : text });
        });
        select.append(h('optgroup', { label: group }, options));
    });
    if (!known) {
        select.append(h('option', { value: rule.field, text: ssText('fieldLockedLabel', { field: ssDisplayText(rule.field || '?') }) }));
    }

    select.value = rule.field;
    select.disabled = !!rule.locked;
    select.addEventListener('change', function () {
        ssSetRuleField(rule, page.catalog, select.value);
        ssTreeChanged(page, { id: rule.id, part: 'field' });
    });
    return select;
}

function ssOpSelect(page, rule, field) {
    const select = ssFocusKey(kit.select({ class: 'jfss-rule-op', 'aria-label': SS_TEXT.opLabel }, (field ? field.Operators : [rule.op]).map(function (op) {
        return { value: op, text: ssOpLabel(field, op) };
    })), rule.id, 'op');
    select.value = rule.op;
    select.disabled = !!rule.locked;
    select.addEventListener('change', function () {
        ssSetRuleOp(rule, page.catalog, select.value);
        ssTreeChanged(page, { id: rule.id, part: 'op' });
    });
    return select;
}

function ssRenderRule(page, rule, n) {
    const h = kit.h;
    const field = rule.locked ? null : ssField(page.catalog, rule.field);
    const box = h('div', { class: 'jfss-rule' + (rule.locked ? ' jfss-rule-locked' : ''), role: 'group', 'data-jfss-node': rule.id, 'aria-label': ssText('ruleLabel', { n: n }) });
    const fieldSelect = ssFieldSelect(page, rule);
    const parts = [fieldSelect, ssOpSelect(page, rule, field || ssField(page.catalog, rule.field))];
    if (field) {
        const editor = ssRuleEditor(page, rule, field);
        if (editor) {
            parts.push(h('div', { class: 'jfss-rule-editor' }, editor));
        }

        parts.push.apply(parts, ssRuleOptions(page, rule, field));
    }

    const tools = h('span', { class: 'jfss-node-tools' }, [
        ssNodeAction(page, rule, 'content_copy', SS_TEXT.duplicateRule, 'jfss-duplicate-node', 'duplicate', ssCanDuplicate(page.tree, rule.id), function () {
            const copy = ssDuplicateNode(page.tree, rule.id);
            return copy ? { id: copy.id, part: 'field' } : null;
        }),
        ssNodeAction(page, rule, 'delete', SS_TEXT.removeRule, 'jfss-remove-node', 'remove', true, function () {
            const next = ssNeighbourFocus(page, rule.id);
            ssRemoveNode(page.tree, rule.id);
            return next;
        })
    ]);
    const note = rule.locked ? SS_TEXT.ruleLocked : (field && field.Note ? field.Note : '');
    const noteNode = note ? h('p', { class: 'jfss-rule-note', id: ssNextId('note'), text: note }) : null;
    if (noteNode) {
        fieldSelect.setAttribute('aria-describedby', noteNode.id);
    }

    box.append.apply(box, [h('div', { class: 'jfss-rule-line' }, parts.concat([tools])), noteNode, ssNodeError(rule.id)].filter(Boolean));
    return box;
}

// ------------------------------------------------------------------------------------------------ value editors

function ssRuleEditor(page, rule, field) {
    const shape = ssOpShape(rule.op);
    if (shape === 'none') {
        return null;
    }

    if (shape === 'value') {
        if (field.Type === 'Bool') {
            return ssBoolSelect(page, rule);
        }

        if (field.ValueSource && field.ElementKind === 'String') {
            return ssPicker(page, rule, field, { single: true });
        }

        return ssScalarInput(page, rule, field, 'value', SS_TEXT.valueLabel);
    }

    if (shape === 'range') {
        return [ssScalarInput(page, rule, field, 'from', SS_TEXT.fromLabel), kit.h('span', { class: 'jfss-dash', 'aria-hidden': 'true', text: '–' }), ssScalarInput(page, rule, field, 'to', SS_TEXT.toLabel)];
    }

    if (shape === 'relative') {
        return ssRelativeEditor(page, rule);
    }

    if (field.ElementKind === 'Guid') {
        return ssLibraryToggles(page, rule);
    }

    if (Array.isArray(field.Choices) && field.Choices.length) {
        return ssChoiceToggles(page, rule, field);
    }

    return ssPicker(page, rule, field, {});
}

// A text, number or date field for Value, From or To.
function ssScalarInput(page, rule, field, part, label) {
    const value = rule.input[part];
    const props = { class: 'jfss-rule-input jfss-rule-' + part, 'aria-label': label, autocomplete: 'off', spellcheck: 'false', value: value };
    if (field.Type === 'Number') {
        Object.assign(props, { type: 'number', inputmode: field.Integer ? 'numeric' : 'decimal', step: field.Integer ? '1' : 'any' });
        if (typeof field.Min === 'number') {
            props.min = String(field.Min);
        }

        if (typeof field.Max === 'number') {
            props.max = String(field.Max);
        }
    } else if (field.Type === 'Date' && (!value || SS_DATE_DAY.test(value))) {
        Object.assign(props, { type: 'date', min: SS_YEAR_MIN + '-01-01', max: SS_YEAR_MAX + '-12-31' });
    } else {
        Object.assign(props, { type: 'text', maxlength: String(SS_TEXT_MAX) });
    }

    const input = ssFocusKey(kit.input(props), rule.id, part);
    input.setAttribute('data-jfss-part', part);
    const read = function () {
        rule.input[part] = input.value;
        ssSyncActions(page);
    };
    input.addEventListener('input', read);
    input.addEventListener('change', read);
    ssOnEnter(input, function () {
        rule.input[part] = input.value;
        ssSubmit(page);
    });
    return input;
}

function ssBoolSelect(page, rule) {
    const select = ssFocusKey(kit.select({ class: 'jfss-rule-bool', 'aria-label': SS_TEXT.valueLabel }, [{ value: 'true', text: SS_TEXT.yes }, { value: 'false', text: SS_TEXT.no }]), rule.id, 'value');
    select.setAttribute('data-jfss-part', 'value');
    select.value = rule.bool ? 'true' : 'false';
    select.addEventListener('change', function () {
        rule.bool = select.value === 'true';
        ssSyncActions(page);
    });
    return select;
}

function ssRelativeEditor(page, rule) {
    const amount = ssFocusKey(kit.input({ type: 'number', class: 'jfss-rule-input jfss-rule-amount', 'aria-label': SS_TEXT.amountLabel, inputmode: 'numeric', min: '1', step: '1', value: rule.input.amount }), rule.id, 'amount');
    amount.setAttribute('data-jfss-part', 'amount');
    amount.addEventListener('input', function () {
        rule.input.amount = amount.value;
        ssSyncActions(page);
    });
    ssOnEnter(amount, function () {
        rule.input.amount = amount.value;
        ssSubmit(page);
    });
    const unit = ssFocusKey(kit.select({ class: 'jfss-rule-unit', 'aria-label': SS_TEXT.unitLabel }, SS_UNITS.map(function (u) {
        return { value: u, text: SS_UNIT_LABELS[u] };
    })), rule.id, 'unit');
    unit.value = rule.unit;
    unit.addEventListener('change', function () {
        rule.unit = unit.value;
        ssSyncActions(page);
    });
    return [amount, unit];
}

// Toggle buttons in a group: one per choice, pressed when chosen.
function ssToggleGroup(page, rule, part, items, isOn, toggle) {
    const h = kit.h;
    const buttons = items.map(function (item, i) {
        const button = h('button', {
            type: 'button',
            class: 'jfss-segment jfss-choice',
            'data-jfss-value': item.value,
            'aria-pressed': isOn(item.value) ? 'true' : 'false',
            onclick: function () {
                toggle(item.value);
                button.setAttribute('aria-pressed', isOn(item.value) ? 'true' : 'false');
                ssSyncActions(page);
            }
        }, h('bdi', { text: ssDisplayText(item.text) }));
        return i === 0 ? ssFocusKey(button, rule.id, part) : button;
    });
    const group = h('div', { class: 'jfss-segments jfss-choices', role: 'group', 'aria-label': SS_TEXT.valuesLabel, 'data-jfss-part': 'values' }, buttons);
    return group;
}

function ssSameValue(a, b) {
    return String(a).toLowerCase() === String(b).toLowerCase();
}

function ssChoiceToggles(page, rule, field) {
    const label = function (choice) {
        if (field.ElementKind === 'ItemKind') {
            return SS_KIND_SINGULAR[choice] || choice;
        }

        return SS_CHOICE_LABELS[choice] || choice;
    };
    return ssToggleGroup(page, rule, 'values', field.Choices.map(function (c) {
        return { value: c, text: label(c) };
    }), function (value) {
        return rule.values.some(function (v) {
            return ssSameValue(v, value);
        });
    }, function (value) {
        const at = rule.values.findIndex(function (v) {
            return ssSameValue(v, value);
        });
        if (at >= 0) {
            rule.values.splice(at, 1);
        } else {
            rule.values.push(value);
        }
    });
}

// The Library field's values: the user's libraries as buttons; a library the query names that the user can't open is
// kept and counted, never named.
function ssLibraryToggles(page, rule) {
    const known = page.libraries.map(function (l) {
        return ssId(l.Id);
    });
    const hidden = rule.values.filter(function (v) {
        return known.indexOf(ssId(v)) < 0;
    }).length;
    const group = ssToggleGroup(page, rule, 'values', page.libraries.map(function (l) {
        return { value: ssId(l.Id), text: l.Name };
    }), function (id) {
        return rule.values.some(function (v) {
            return ssId(v) === id;
        });
    }, function (id) {
        const at = rule.values.findIndex(function (v) {
            return ssId(v) === id;
        });
        if (at >= 0) {
            rule.values.splice(at, 1);
        } else {
            rule.values.push(id);
        }
    });
    return hidden > 0 ? [group, kit.h('span', { class: 'jfss-badge jfss-badge-unavailable', text: ssText('librariesHidden', { count: hidden }) })] : group;
}

// The value of a chip as shown and as compared.
function ssValueText(value) {
    return ssPlainValue(value) ? String(value.Name || value.Id || '') : String(value);
}

function ssPlainValue(value) {
    return !!value && typeof value === 'object';
}

function ssValueKey(value) {
    return ssPlainValue(value) ? 'id:' + ssId(value.Id) : 'v:' + String(value).toLowerCase();
}

// Chips with suggestions. The field's kind of value decides the rest: shows (references) come from GET
// /SmartSearch/Series after 2 letters and only as suggested (they need an id); years are typed whole numbers; other
// values are typed or suggested from GET /SmartSearch/Values for the libraries chosen. single: one value in the field
// itself (a parental rating's "at most"), suggestions filling it.
function ssPicker(page, rule, field, opts) {
    const h = kit.h;
    const series = field.ElementKind === 'Reference';
    const integer = field.ElementKind === 'Integer';
    const suggests = !!field.ValueSource && !integer;
    const single = !!opts.single;
    const minChars = series ? 2 : 0;
    const state = { timer: null, abort: null, shown: false };
    const input = ssFocusKey(kit.input({
        type: integer ? 'number' : 'text',
        class: 'jfss-picker-input',
        maxlength: integer ? null : String(SS_TEXT_MAX),
        inputmode: integer ? 'numeric' : null,
        'aria-label': single ? SS_TEXT.valueLabel : (series ? SS_TEXT.findShow : SS_TEXT.addValue),
        autocomplete: 'off',
        spellcheck: 'false',
        value: single ? rule.input.value : ''
    }), rule.id, single ? 'value' : 'values');
    input.setAttribute('data-jfss-part', single ? 'value' : 'values');
    const chips = h('div', { class: 'jfss-chips', role: 'list', 'aria-label': SS_TEXT.chosenValues });
    const suggestions = h('div', { class: 'jfss-suggestions', role: 'group', 'aria-label': SS_TEXT.suggestions });
    kit.setHidden(suggestions, true);
    const renderChips = function () {
        chips.replaceChildren();
        rule.values.forEach(function (value, i) {
            const text = ssDisplayText(ssValueText(value));
            const chip = h('button', {
                type: 'button',
                class: 'jfss-chip',
                'aria-label': ssText('removeValue', { name: text }),
                onclick: function () {
                    rule.values.splice(i, 1);
                    renderChips();
                    ssSyncActions(page);
                    input.focus();
                }
            }, [h('bdi', { text: text }), kit.icon('close', 'jfss-chip-icon')]);
            chips.append(h('div', { role: 'listitem', class: 'jfss-chip-item' }, chip));
        });
        kit.setHidden(chips, single || rule.values.length === 0);
    };
    const add = function (value) {
        if (single) {
            input.value = String(value);
            rule.input.value = input.value;
        } else if (value !== null && value !== '' && rule.values.length < SS_VALUES_MAX && !rule.values.some(function (v) {
            return ssValueKey(v) === ssValueKey(value);
        })) {
            rule.values.push(value);
            renderChips();
            input.value = '';
        }

        ssSyncActions(page);
        input.focus();
        schedule();
    };
    const typed = function () {
        const text = String(input.value || '').trim();
        if (!text || series) {
            return null;
        }

        if (integer) {
            return /^\d{1,4}$/.test(text) ? Number(text) : null;
        }

        return text.substring(0, SS_TEXT_MAX);
    };
    const refresh = function () {
        const text = String(input.value || '').trim();
        if (state.abort) {
            state.abort.abort();
        }

        if (!suggests || text.length < minChars || !page.context.isShown()) {
            suggestions.replaceChildren();
            kit.setHidden(suggestions, true);
            return;
        }

        const abort = typeof AbortController === 'function' ? new AbortController() : null;
        state.abort = abort;
        const signal = abort ? abort.signal : undefined;
        const lib = page.librarySelect.value;
        const request = series
            ? ssApi.series(text, SS_SUGGEST_LIMIT, signal).then(function (list) {
                return (list || []).map(function (s) {
                    return { value: { Id: ssId(s.Id), Name: s.Name }, text: s.ProductionYear ? ssText('seriesYear', { name: ssDisplayText(s.Name), year: s.ProductionYear }) : ssDisplayText(s.Name) };
                });
            })
            : ssApi.values(field.Id, text, lib ? lib.split(',') : [], SS_SUGGEST_LIMIT + rule.values.length, signal).then(function (list) {
                return (list || []).map(function (v) {
                    return { value: v.Value, text: ssText('suggestionCount', { name: ssDisplayText(v.Value), count: v.Count }) };
                });
            });
        request.then(function (items) {
            if (state.abort !== abort || !page.context.isShown()) {
                return;
            }

            const chosen = rule.values.map(ssValueKey);
            const fresh = items.filter(function (item) {
                return single || chosen.indexOf(ssValueKey(item.value)) < 0;
            }).slice(0, SS_SUGGEST_LIMIT);
            suggestions.replaceChildren();
            fresh.forEach(function (item) {
                suggestions.append(h('button', {
                    type: 'button',
                    class: 'jfss-suggestion',
                    onclick: function () {
                        add(item.value);
                    }
                }, h('bdi', { text: item.text })));
            });
            state.shown = fresh.length > 0;
            kit.setHidden(suggestions, fresh.length === 0);
        }).catch(function () {
            // Suggestions are a convenience: a failed or aborted request leaves the list as it was.
        });
    };
    const schedule = function () {
        clearTimeout(state.timer);
        state.timer = setTimeout(refresh, SS_SUGGEST_DELAY_MS);
    };
    input.addEventListener('input', function () {
        if (single) {
            rule.input.value = input.value;
            ssSyncActions(page);
        }

        schedule();
    });
    input.addEventListener('focus', function () {
        if (!state.shown) {
            schedule();
        }
    });
    input.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' && !event.isComposing) {
            event.preventDefault();
            const value = typed();
            const first = suggestions.querySelector('button');
            if (single || !String(input.value || '').trim()) {
                ssSubmit(page);
            } else if (value !== null) {
                add(value);
            } else if (first && !kit.isHidden(suggestions)) {
                first.click();
            }
        } else if (event.key === 'ArrowDown') {
            const first = suggestions.querySelector('button');
            if (first && !kit.isHidden(suggestions)) {
                event.preventDefault();
                first.focus();
            }
        }
    });
    suggestions.addEventListener('keydown', function (event) {
        const list = suggestions.querySelectorAll('button');
        const at = list.indexOf ? list.indexOf(document.activeElement) : Array.prototype.indexOf.call(list, document.activeElement);
        if (at < 0) {
            return;
        }

        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            const next = list[at + (event.key === 'ArrowDown' ? 1 : -1)];
            (next || (event.key === 'ArrowUp' ? input : list[at])).focus();
        } else if (event.key === 'Escape') {
            event.preventDefault();
            input.focus();
        }
    });
    renderChips();
    const row = [input];
    if (!single && !series) {
        row.push(kit.button(SS_TEXT.addValueButton, { className: 'jfss-picker-add', onClick: function () {
            const value = typed();
            if (value !== null) {
                add(value);
            } else {
                input.focus();
            }
        } }));
    }

    return h('div', { class: 'jfss-picker' + (single ? ' jfss-picker-single' : '') }, [h('div', { class: 'jfss-picker-row' }, row), chips, suggestions]);
}

// A field's options: "Include original title" (a check box), Provider (a select).
function ssRuleOptions(page, rule, field) {
    const h = kit.h;
    const out = [];
    if (ssHasOption(field, 'IncludeOriginalTitle')) {
        const box = ssFocusKey(h('input', { type: 'checkbox', class: 'jfss-rule-original' }), rule.id, 'option');
        box.checked = !!rule.options.IncludeOriginalTitle;
        box.addEventListener('change', function () {
            rule.options.IncludeOriginalTitle = box.checked;
            ssSyncActions(page);
        });
        out.push(h('label', { class: kit.cls('checkbox') + ' jfss-rule-option' }, [box, h('span', { text: SS_TEXT.includeOriginalTitle })]));
    }

    const provider = ssOption(field, 'Provider');
    if (provider && Array.isArray(provider.Choices)) {
        const select = ssFocusKey(kit.select({ class: 'jfss-rule-provider', 'aria-label': SS_TEXT.providerLabel }, provider.Choices.map(function (c) {
            return { value: c, text: SS_PROVIDER_LABELS[c] || c };
        })), rule.id, 'provider');
        select.setAttribute('data-jfss-part', 'provider');
        select.value = rule.options.Provider || provider.Choices[0];
        rule.options.Provider = select.value;
        select.addEventListener('change', function () {
            rule.options.Provider = select.value;
            ssSyncActions(page);
        });
        out.push(select);
    }

    return out;
}

// ------------------------------------------------------------------------------------------------ errors

// The rules' errors under their rules and groups: { nodeId: { part: message } }; the controls they name marked
// aria-invalid. A node gone from the tree is skipped. keepFocus: drawn again, the focus stays where it is.
function ssShowTreeErrors(page, errors, keepFocus) {
    if (!page.advanced) {
        return null;
    }

    page.treeErrors = errors && Object.keys(errors).length ? errors : null;
    page.advanced.querySelectorAll('[data-jfss-node-error]').forEach(function (node) {
        const found = (errors || {})[node.getAttribute('data-jfss-node-error')];
        node.textContent = found ? ssUniqueMessages(found).join(' ') : '';
    });
    page.advanced.querySelectorAll('[aria-invalid]').forEach(function (node) {
        node.removeAttribute('aria-invalid');
    });
    let first = null;
    Object.keys(errors || {}).forEach(function (id) {
        const box = page.advanced.querySelector('[data-jfss-node="' + id + '"]');
        if (!box) {
            return;
        }

        Object.keys(errors[id]).forEach(function (part) {
            const control = part === 'node' ? null : box.querySelector('[data-jfss-focus="' + id + ':' + part + '"]') || box.querySelector('[data-jfss-part="' + part + '"]');
            if (control) {
                control.setAttribute('aria-invalid', 'true');
                first = first || control;
            } else {
                first = first || box.querySelector('[data-jfss-focus]');
            }
        });
    });
    if (!keepFocus) {
        page.formNote.textContent = page.treeErrors ? SS_TEXT.checkRules : '';
    }

    return first;
}

function ssUniqueMessages(found) {
    const out = [];
    Object.keys(found).forEach(function (part) {
        if (out.indexOf(found[part]) < 0) {
            out.push(found[part]);
        }
    });
    return out;
}

// The advanced form's checks before a search or a save: errors shown, the first marked control focused; true when
// nothing stops it.
function ssCheckAdvanced(page) {
    const check = ssCheckTree(page.tree, page.catalog, ssPressedKinds(page));
    const first = ssShowTreeErrors(page, check.errors);
    if (check.count > 0 && first) {
        first.focus();
    }

    return check.count === 0;
}

// A server's errors ({ path, message }, camelCase or PascalCase) in the advanced form: those inside the Where under their
// rules; the rest are returned for the scope bar and the form's note.
function ssPlaceTreeErrors(page, list) {
    const errors = {};
    const rest = [];
    list.forEach(function (e) {
        const path = e && (e.path || e.Path);
        const message = (e && (e.message || e.Message)) || '';
        const at = ssNodeOfPath(page.tree, path);
        if (at) {
            errors[at.id] = errors[at.id] || {};
            if (!errors[at.id][at.part]) {
                errors[at.id][at.part] = message;
            }
        } else {
            rest.push({ path: path, message: message });
        }
    });
    const first = ssShowTreeErrors(page, errors);
    if (first) {
        first.focus();
    }

    return rest;
}
