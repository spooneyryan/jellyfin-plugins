// The advanced search's rule tree as data: the field catalogue (GET /SmartSearch/Fields), the operators each field
// takes and their value shapes, the tree the editor edits, its JSON (a query's Where), the edits (add, nest, duplicate,
// unwrap, remove), the checks made before a search or a save, and a server error's path to the node it names. Nothing
// here touches the page; 42-advanced.js draws and edits the tree.
//
// The tree: a group { id, type: 'group', match: 'All' | 'Any', negate, children } or a rule { id, type: 'rule', field,
// op, input: { value, from, to, amount } (what the fields hold, as text), unit, bool, values (the Values as sent: text,
// numbers, ids, { Id, Name }), options: { IncludeOriginalTitle, Provider, Role } }. A rule this page can't edit as it is (a field
// it doesn't know or isn't offered, an operator its field doesn't list, a value of another shape) is kept as it came:
// { locked: true, json }, sent unchanged, and can only be removed or duplicated.
//
// The limits are the server's (the query reader's): groups at most 4 deep, at most 50 rules, at most 50 children in a
// group, at most 100 values in a rule, text at most 200 characters, years 1800 to 2100.

const SS_MAX_DEPTH = 4;
const SS_MAX_RULES = 50;
const SS_MAX_CHILDREN = 50;
const SS_UNITS = Object.freeze(['Days', 'Weeks', 'Months', 'Years']);
const SS_UNIT_MAX = Object.freeze({ Days: 3650, Weeks: 521, Months: 120, Years: 10 });

// What an operator reads: nothing, Value, From and To, Amount and Unit, or Values.
const SS_OP_SHAPES = Object.freeze({
    IsEmpty: 'none',
    IsNotEmpty: 'none',
    Between: 'range',
    InLast: 'relative',
    NotInLast: 'relative',
    AnyOf: 'values',
    AllOf: 'values',
    NoneOf: 'values',
    NotAllOf: 'values'
});

function ssOpShape(op) {
    return SS_OP_SHAPES[op] || 'value';
}

// The catalogue of GET /SmartSearch/Fields: the fields in the server's order, by id, and their groups in order of first
// appearance. kinds: the item kinds the server allows (the scope bar's).
function ssCatalog(dto, kinds) {
    const fields = (dto && Array.isArray(dto.Fields) ? dto.Fields : []).filter(function (f) {
        return f && typeof f.Id === 'string' && Array.isArray(f.Operators) && f.Operators.length > 0;
    });
    const byId = {};
    const groups = [];
    fields.forEach(function (f) {
        byId[f.Id] = f;
        if (groups.indexOf(f.Group) < 0) {
            groups.push(f.Group);
        }
    });
    return { fields: fields, byId: byId, groups: groups, kinds: kinds || [] };
}

function ssField(catalog, id) {
    return catalog && Object.prototype.hasOwnProperty.call(catalog.byId, id) ? catalog.byId[id] : null;
}

// Whether a field applies to at least one of the kinds searched (the server refuses it otherwise).
function ssFieldFits(field, kinds) {
    return !kinds || kinds.length === 0 || (field.Kinds || []).some(function (kind) {
        return kinds.indexOf(kind) >= 0;
    });
}

function ssHasOption(field, name) {
    return (field.Options || []).some(function (o) {
        return o.Name === name;
    });
}

function ssOption(field, name) {
    return (field.Options || []).filter(function (o) {
        return o.Name === name;
    })[0] || null;
}

// The words an operator reads as after its field ("Title contains"), by the field's type.
function ssOpLabel(field, op) {
    const type = field ? field.Type : '';
    const byType = SS_OP_LABELS[type] || {};
    return byType[op] || SS_OP_LABELS.any[op] || op;
}

// ------------------------------------------------------------------------------------------------ nodes

let ssNodeCounter = 0;

function ssNewNodeId() {
    ssNodeCounter += 1;
    return 'n' + ssNodeCounter;
}

function ssNewGroup(match, negate, children) {
    return { id: ssNewNodeId(), type: 'group', match: match === 'Any' ? 'Any' : 'All', negate: !!negate, children: children || [] };
}

function ssEmptyInput() {
    return { value: '', from: '', to: '', amount: '' };
}

// A new rule on the first field that fits the kinds searched (or the field asked for), with its first operator.
function ssNewRule(catalog, kinds, fieldId) {
    let field = fieldId ? ssField(catalog, fieldId) : null;
    if (!field) {
        field = catalog.fields.filter(function (f) {
            return f.Available !== false && ssFieldFits(f, kinds);
        })[0] || catalog.fields[0] || null;
    }

    const rule = { id: ssNewNodeId(), type: 'rule', field: field ? field.Id : '', op: field ? field.Operators[0] : '', input: ssEmptyInput(), unit: 'Days', bool: true, values: [], options: {} };
    ssApplyOptionDefaults(rule, field);
    return rule;
}

// A required option with choices starts on its first choice (Provider: Tmdb).
function ssApplyOptionDefaults(rule, field) {
    const options = {};
    (field ? field.Options || [] : []).forEach(function (o) {
        if (o.Name === 'IncludeOriginalTitle') {
            options.IncludeOriginalTitle = !!rule.options.IncludeOriginalTitle;
        } else if (o.Required && Array.isArray(o.Choices) && o.Choices.length) {
            options[o.Name] = rule.options[o.Name] && o.Choices.indexOf(rule.options[o.Name]) >= 0 ? rule.options[o.Name] : o.Choices[0];
        } else if (rule.options[o.Name]) {
            options[o.Name] = rule.options[o.Name];
        }
    });
    rule.options = options;
}

// A rule moved to another field: the operator is kept when the field takes it and the typed text when the type stays;
// the chosen values and anything else start empty.
function ssSetRuleField(rule, catalog, fieldId) {
    const before = ssField(catalog, rule.field);
    const field = ssField(catalog, fieldId);
    if (!field || rule.locked) {
        return;
    }

    const sameType = !!before && before.Type === field.Type;
    rule.field = field.Id;
    if (field.Operators.indexOf(rule.op) < 0) {
        rule.op = field.Operators[0];
    }

    if (!sameType) {
        rule.input = ssEmptyInput();
        rule.bool = true;
    }

    // Values belong to their field (a genre isn't a tag): another field starts without them.
    if (!before || before.Id !== field.Id) {
        rule.values = [];
    }

    ssApplyOptionDefaults(rule, field);
}

// A rule given another operator: what was typed carries over between the shapes (a value becomes From, From a value).
function ssSetRuleOp(rule, catalog, op) {
    const field = ssField(catalog, rule.field);
    if (!field || rule.locked || field.Operators.indexOf(op) < 0) {
        return;
    }

    const from = ssOpShape(rule.op);
    const to = ssOpShape(op);
    if (from === 'value' && to === 'range' && !rule.input.from) {
        rule.input.from = rule.input.value;
    } else if (from === 'range' && to === 'value' && !rule.input.value) {
        rule.input.value = rule.input.from;
    }

    rule.op = op;
}

function ssCloneNode(node) {
    if (node.type === 'group') {
        return ssNewGroup(node.match, node.negate, node.children.map(ssCloneNode));
    }

    const copy = JSON.parse(JSON.stringify(node));
    copy.id = ssNewNodeId();
    return copy;
}

// ------------------------------------------------------------------------------------------------ the tree's shape

// The node with this id: { node, parent (a group, or null for the root), index, depth (the root group is 1) }.
function ssFindNode(root, id) {
    const walk = function (node, parent, index, depth) {
        if (node.id === id) {
            return { node: node, parent: parent, index: index, depth: depth };
        }

        if (node.type !== 'group') {
            return null;
        }

        for (let i = 0; i < node.children.length; i++) {
            const found = walk(node.children[i], node, i, depth + 1);
            if (found) {
                return found;
            }
        }

        return null;
    };
    return walk(root, null, -1, 1);
}

function ssCountRules(node) {
    if (node.type !== 'group') {
        return 1;
    }

    return node.children.reduce(function (sum, child) {
        return sum + ssCountRules(child);
    }, 0);
}

// How deep a node's own groups go (a rule 0, a group of rules 1).
function ssGroupHeight(node) {
    if (node.type !== 'group') {
        return 0;
    }

    return 1 + node.children.reduce(function (max, child) {
        return Math.max(max, ssGroupHeight(child));
    }, 0);
}

function ssCanAddRule(root, groupId) {
    const at = ssFindNode(root, groupId);
    return !!at && at.node.type === 'group' && ssCountRules(root) < SS_MAX_RULES && at.node.children.length < SS_MAX_CHILDREN;
}

// A new group holds one rule, so it needs room for a rule and a level.
function ssCanAddGroup(root, groupId) {
    const at = ssFindNode(root, groupId);
    return ssCanAddRule(root, groupId) && at.depth < SS_MAX_DEPTH;
}

function ssCanDuplicate(root, id) {
    const at = ssFindNode(root, id);
    return !!at && !!at.parent && at.parent.children.length < SS_MAX_CHILDREN && ssCountRules(root) + ssCountRules(at.node) <= SS_MAX_RULES;
}

function ssCanUnwrap(root, id) {
    const at = ssFindNode(root, id);
    return !!at && !!at.parent && at.node.type === 'group' && at.parent.children.length - 1 + at.node.children.length <= SS_MAX_CHILDREN;
}

function ssAddRule(root, groupId, catalog, kinds) {
    if (!ssCanAddRule(root, groupId)) {
        return null;
    }

    const rule = ssNewRule(catalog, kinds);
    ssFindNode(root, groupId).node.children.push(rule);
    return rule;
}

function ssAddGroup(root, groupId, catalog, kinds) {
    if (!ssCanAddGroup(root, groupId)) {
        return null;
    }

    const group = ssNewGroup('Any', false, [ssNewRule(catalog, kinds)]);
    ssFindNode(root, groupId).node.children.push(group);
    return group;
}

function ssDuplicateNode(root, id) {
    if (!ssCanDuplicate(root, id)) {
        return null;
    }

    const at = ssFindNode(root, id);
    const copy = ssCloneNode(at.node);
    at.parent.children.splice(at.index + 1, 0, copy);
    return copy;
}

// A group's children take its place in its parent (the group's match and negation go with it).
function ssUnwrapGroup(root, id) {
    if (!ssCanUnwrap(root, id)) {
        return null;
    }

    const at = ssFindNode(root, id);
    const children = at.node.children;
    Array.prototype.splice.apply(at.parent.children, [at.index, 1].concat(children));
    return children;
}

function ssRemoveNode(root, id) {
    const at = ssFindNode(root, id);
    if (!at || !at.parent) {
        return false;
    }

    at.parent.children.splice(at.index, 1);
    return true;
}

// ------------------------------------------------------------------------------------------------ to and from JSON

function ssPlain(value) {
    return !!value && typeof value === 'object' && !Array.isArray(value);
}

// A number typed into a field, as the query sends it; text that isn't one is sent as it is (and refused).
function ssNumberOf(text) {
    const t = String(text === undefined || text === null ? '' : text).trim();
    return /^[-+]?(\d+\.?\d*|\.\d+)$/.test(t) ? Number(t) : t;
}

function ssScalarOut(field, text) {
    return field.Type === 'Number' ? ssNumberOf(text) : String(text === undefined || text === null ? '' : text);
}

function ssScalarIn(value) {
    return value === undefined || value === null ? '' : String(value);
}

function ssRuleJson(rule, catalog) {
    if (rule.locked) {
        return JSON.parse(JSON.stringify(rule.json));
    }

    const field = ssField(catalog, rule.field);
    const json = { Field: rule.field, Op: rule.op };
    const shape = ssOpShape(rule.op);
    if (shape === 'value') {
        json.Value = field.Type === 'Bool' ? !!rule.bool : ssScalarOut(field, rule.input.value);
    } else if (shape === 'range') {
        json.From = ssScalarOut(field, rule.input.from);
        json.To = ssScalarOut(field, rule.input.to);
    } else if (shape === 'relative') {
        json.Amount = ssNumberOf(rule.input.amount);
        json.Unit = rule.unit;
    } else if (shape === 'values') {
        json.Values = rule.values.map(function (v) {
            return ssPlain(v) ? { Id: v.Id, Name: v.Name } : v;
        });
    }

    if (ssHasOption(field, 'IncludeOriginalTitle') && rule.options.IncludeOriginalTitle) {
        json.IncludeOriginalTitle = true;
    }

    // A Role is written only as one of its choices (one the page doesn't offer: the rule is locked as it came).
    ['Provider', 'Role'].forEach(function (name) {
        const option = ssOption(field, name);
        if (option && rule.options[name] && (name !== 'Role' || !Array.isArray(option.Choices) || option.Choices.indexOf(rule.options[name]) >= 0)) {
            json[name] = rule.options[name];
        }
    });
    return json;
}

function ssNodeJson(node, catalog) {
    if (node.type !== 'group') {
        return ssRuleJson(node, catalog);
    }

    const json = { Match: node.match };
    if (node.negate) {
        json.Negate = true;
    }

    json.Children = node.children.map(function (child) {
        return ssNodeJson(child, catalog);
    });
    return json;
}

// The tree as a query's Where.
function ssWhereFromTree(root, catalog) {
    return ssNodeJson(root, catalog);
}

// A rule as compared: what it says, without the defaults the server writes (Negate false, IncludeOriginalTitle false)
// and without empty properties.
function ssNormalRule(json) {
    const out = {};
    Object.keys(json).sort().forEach(function (key) {
        const value = json[key];
        if (value === undefined || value === null || (key === 'Negate' && value === false) || (key === 'IncludeOriginalTitle' && value === false)
            || ((key === 'Role' || key === 'Provider') && value === '')) {
            return;
        }

        out[key] = value;
    });
    return out;
}

function ssSameJson(a, b) {
    return JSON.stringify(a) === JSON.stringify(b);
}

// A rule of a query as the editor's rule, or a locked rule when the editor can't show it as it is: read, then written
// again, it must say the same.
function ssRuleFromJson(json, catalog) {
    const locked = { id: ssNewNodeId(), type: 'rule', locked: true, json: JSON.parse(JSON.stringify(json)), field: ssPlain(json) ? String(json.Field || '') : '', op: ssPlain(json) ? String(json.Op || '') : '', input: ssEmptyInput(), unit: 'Days', bool: true, values: [], options: {} };
    if (!ssPlain(json) || json.Negate === true) {
        return locked;
    }

    const field = ssField(catalog, json.Field);
    if (!field || field.Operators.indexOf(json.Op) < 0) {
        return locked;
    }

    const rule = { id: locked.id, type: 'rule', field: field.Id, op: json.Op, input: ssEmptyInput(), unit: 'Days', bool: true, values: [], options: {} };
    const shape = ssOpShape(json.Op);
    if (shape === 'value') {
        if (field.Type === 'Bool') {
            rule.bool = json.Value === true;
        } else {
            rule.input.value = ssScalarIn(json.Value);
        }
    } else if (shape === 'range') {
        rule.input.from = ssScalarIn(json.From);
        rule.input.to = ssScalarIn(json.To);
    } else if (shape === 'relative') {
        rule.input.amount = ssScalarIn(json.Amount);
        rule.unit = SS_UNITS.indexOf(json.Unit) >= 0 ? json.Unit : 'Days';
    } else if (shape === 'values') {
        rule.values = Array.isArray(json.Values) ? JSON.parse(JSON.stringify(json.Values)) : [];
    }

    rule.options = {
        IncludeOriginalTitle: json.IncludeOriginalTitle === true,
        Provider: typeof json.Provider === 'string' ? json.Provider : '',
        Role: typeof json.Role === 'string' ? json.Role : ''
    };
    return ssSameJson(ssNormalRule(ssRuleJson(rule, catalog)), ssNormalRule(json)) ? rule : locked;
}

// A query's Where as the editor's tree (a missing or odd root is an empty All group). Match is read ignoring case, as
// the server reads enum names. What the tree can't hold (member names in another case, a member a group doesn't have)
// is lost here: a saved query is compared with what the form sends back (ssSameQuery) and opens locked when they differ.
function ssTreeFromWhere(where, catalog) {
    const read = function (json) {
        if (ssPlain(json) && Array.isArray(json.Children) && json.Field === undefined) {
            return ssNewGroup(String(json.Match).toLowerCase() === 'any' ? 'Any' : 'All', json.Negate === true, json.Children.map(read));
        }

        return ssRuleFromJson(json, catalog);
    };
    return ssPlain(where) && Array.isArray(where.Children) ? read(where) : ssNewGroup('All', false, []);
}

// Whether a Where read from the hash looks like one: a group, small enough, nested no deeper than the server reads.
function ssWhereShapeOk(where) {
    const depth = function (node, d) {
        if (d > SS_MAX_DEPTH + 1) {
            return false;
        }

        return !ssPlain(node) || !Array.isArray(node.Children) || node.Children.every(function (c) {
            return depth(c, d + 1);
        });
    };
    return ssPlain(where) && Array.isArray(where.Children) && depth(where, 1);
}

// ------------------------------------------------------------------------------------------------ checks

const SS_DATE_DAY = /^(\d{4})-(\d{2})-(\d{2})$/;
const SS_DATE_TIME = /^(\d{4})-(\d{2})-(\d{2})T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$/;

// A date value's year, or null when it isn't a date (yyyy-MM-dd, or a time with Z or an offset).
function ssDateYear(text) {
    const t = String(text || '').trim();
    const m = SS_DATE_DAY.exec(t) || SS_DATE_TIME.exec(t);
    if (!m) {
        return null;
    }

    const y = Number(m[1]);
    const mo = Number(m[2]);
    const d = Number(m[3]);
    const date = new Date(Date.UTC(2000, mo - 1, d));
    return mo >= 1 && mo <= 12 && date.getUTCMonth() === mo - 1 && date.getUTCDate() === d ? y : null;
}

// One value as its field reads it: { number } or { error } (a message of SS_TEXT).
function ssCheckScalar(field, text) {
    const t = String(text === undefined || text === null ? '' : text).trim();
    if (t === '') {
        return { error: SS_TEXT.ruleValueRequired };
    }

    if (field.Type === 'Number') {
        const n = ssNumberOf(t);
        if (typeof n !== 'number' || !isFinite(n)) {
            return { error: SS_TEXT.ruleNumberInvalid };
        }

        if (field.Integer && Math.floor(n) !== n) {
            return { error: SS_TEXT.ruleWholeNumber };
        }

        const min = typeof field.Min === 'number' ? field.Min : null;
        const max = typeof field.Max === 'number' ? field.Max : null;
        if ((min !== null && n < min) || (max !== null && n > max)) {
            return { error: max !== null ? ssText('ruleNumberRange', { min: min === null ? '' : min, max: max }) : ssText('ruleNumberMin', { min: min }) };
        }

        return { number: n };
    }

    if (field.Type === 'Date') {
        const year = ssDateYear(t);
        if (year === null) {
            return { error: SS_TEXT.ruleDateInvalid };
        }

        if (year < SS_YEAR_MIN || year > SS_YEAR_MAX) {
            return { error: SS_TEXT.ruleDateRange };
        }

        return { number: Date.parse(SS_DATE_DAY.test(t) ? t + 'T00:00:00Z' : t) };
    }

    if (t.length > SS_TEXT_MAX) {
        return { error: SS_TEXT.ruleTextTooLong };
    }

    return {};
}

// The problems of a rule before it is sent: { part: message }, part one of field, op, value, from, to, amount, values,
// provider, node. kinds: the kinds searched.
function ssCheckRule(rule, catalog, kinds) {
    const errors = {};
    if (rule.locked) {
        return errors;
    }

    const field = ssField(catalog, rule.field);
    if (!field) {
        errors.field = SS_TEXT.ruleFieldUnknown;
        return errors;
    }

    if (!ssFieldFits(field, kinds)) {
        errors.field = ssText('ruleFieldNotForKinds', { field: field.Label });
    }

    const shape = ssOpShape(rule.op);
    if (shape === 'value' && field.Type !== 'Bool') {
        const v = ssCheckScalar(field, rule.input.value);
        if (v.error) {
            errors.value = v.error;
        }
    } else if (shape === 'range') {
        const a = ssCheckScalar(field, rule.input.from);
        const b = ssCheckScalar(field, rule.input.to);
        if (a.error) {
            errors.from = a.error;
        }

        if (b.error) {
            errors.to = b.error;
        }

        if (!a.error && !b.error && a.number > b.number) {
            errors.node = SS_TEXT.ruleRangeOrder;
        } else if (!a.error && !b.error && typeof field.MaxSpan === 'number' && b.number - a.number > field.MaxSpan) {
            errors.node = ssText('ruleRangeSpan', { span: field.MaxSpan });
        }
    } else if (shape === 'relative') {
        const n = ssNumberOf(rule.input.amount);
        const max = SS_UNIT_MAX[rule.unit] || SS_UNIT_MAX.Days;
        if (typeof n !== 'number' || Math.floor(n) !== n || n < 1 || n > max) {
            errors.amount = ssText('ruleAmountRange', { max: max });
        }
    } else if (shape === 'values') {
        if (rule.values.length === 0) {
            errors.values = SS_TEXT.ruleValuesRequired;
        } else if (rule.values.length > SS_VALUES_MAX) {
            errors.values = SS_TEXT.ruleTooManyValues;
        }
    }

    const provider = ssOption(field, 'Provider');
    if (provider && provider.Required && !rule.options.Provider) {
        errors.provider = SS_TEXT.ruleProviderRequired;
    }

    return errors;
}

// The problems of the whole tree: { nodeId: { part: message } } and how many nodes have one. A group must hold a rule
// (the root may be empty: it matches everything in scope, so only as "All of").
function ssCheckTree(root, catalog, kinds) {
    const errors = {};
    let count = 0;
    const add = function (id, found) {
        if (Object.keys(found).length > 0) {
            errors[id] = found;
            count += 1;
        }
    };
    const walk = function (node, depth) {
        if (node.type !== 'group') {
            add(node.id, ssCheckRule(node, catalog, kinds));
            return;
        }

        const found = {};
        if (node.children.length === 0 && depth > 1) {
            found.node = SS_TEXT.groupEmpty;
        } else if (node.children.length === 0 && (node.match === 'Any' || node.negate)) {
            found.node = SS_TEXT.rootEmptyAny;
        } else if (depth > SS_MAX_DEPTH) {
            found.node = SS_TEXT.groupTooDeep;
        } else if (node.children.length > SS_MAX_CHILDREN) {
            found.node = SS_TEXT.groupTooManyChildren;
        }

        add(node.id, found);
        node.children.forEach(function (child) {
            walk(child, depth + 1);
        });
    };
    walk(root, 1);
    if (ssCountRules(root) > SS_MAX_RULES) {
        errors[root.id] = Object.assign(errors[root.id] || {}, { node: SS_TEXT.tooManyRules });
        count += 1;
    }

    return { errors: errors, count: count };
}

// A server error's path inside the Where ("Where.Children[1].Children[0].Values[2]", a saved query's "Query.Where…")
// as { id, part } of the tree it was sent from, or null for a path outside the Where.
function ssNodeOfPath(root, path) {
    const text = String(path || '').replace(/^Query\./, '');
    if (text.indexOf('Where') !== 0) {
        return null;
    }

    let node = root;
    let rest = text.substring('Where'.length);
    let m;
    while ((m = /^\.Children\[(\d+)\]/.exec(rest)) !== null) {
        if (node.type !== 'group' || !node.children[Number(m[1])]) {
            return { id: node.id, part: 'node' };
        }

        node = node.children[Number(m[1])];
        rest = rest.substring(m[0].length);
    }

    const prop = /^\.([A-Za-z]+)/.exec(rest);
    const parts = { Field: 'field', Op: 'op', Value: 'value', From: 'from', To: 'to', Amount: 'amount', Unit: 'amount', Values: 'values', Provider: 'provider', IncludeOriginalTitle: 'option', Role: 'option' };
    return { id: node.id, part: prop && parts[prop[1]] ? parts[prop[1]] : 'node' };
}
