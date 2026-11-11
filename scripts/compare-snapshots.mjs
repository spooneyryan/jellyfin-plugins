#!/usr/bin/env node
// Compares the DOM snapshots of two webcheck results (steps named "snapshot: ...", saved with probes/dom-snapshot.js),
// for "the UI is unchanged" checks: run a webcheck such as bookmarks-ui.json before and after a change, then
//   node scripts/compare-snapshots.mjs before.json after.json [--ignore-classes a,b] [--ignore-attrs x,y] [--out diff.json]
// Class lists compare as sets (order doesn't matter). --ignore-classes drops the named class tokens and --ignore-attrs
// the named attributes on both sides, so intentional changes can be listed and everything else must match. Prints one
// JSON result and exits with 0 when every snapshot matches, 1 otherwise. Node 22 built-ins only.
import fs from 'node:fs';
import path from 'node:path';

function usage(message) {
    if (message) {
        process.stderr.write(message + '\n');
    }

    process.stderr.write('usage: node scripts/compare-snapshots.mjs <before.json> <after.json> [--ignore-classes a,b] [--ignore-attrs x,y] [--out diff.json]\n');
    process.exit(2);
}

const args = process.argv.slice(2);
const files = [];
const options = { ignoreClasses: [], ignoreAttrs: [], out: null };
for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--ignore-classes' || arg === '--ignore-attrs' || arg === '--out') {
        const value = args[++i];
        if (value === undefined) {
            usage(arg + ' needs a value');
        }

        if (arg === '--out') {
            options.out = value;
        } else {
            options[arg === '--ignore-classes' ? 'ignoreClasses' : 'ignoreAttrs'] = value.split(',').map((s) => s.trim()).filter(Boolean);
        }
    } else if (arg === '--help' || arg === '-h') {
        usage();
    } else {
        files.push(arg);
    }
}

if (files.length !== 2) {
    usage('two webcheck result files are needed');
}

function snapshots(file) {
    const result = JSON.parse(fs.readFileSync(path.resolve(file), 'utf8'));
    const out = {};
    for (const step of result.steps || []) {
        if (step.ok && typeof step.name === 'string' && step.name.startsWith('snapshot:')) {
            out[step.name] = step.value;
        }
    }

    return out;
}

function normalise(node) {
    if (node === null || typeof node !== 'object') {
        return node;
    }

    if (Array.isArray(node)) {
        return node.map(normalise);
    }

    const copy = {};
    for (const [key, value] of Object.entries(node)) {
        if (key === 'attrs' && value && typeof value === 'object') {
            const attrs = {};
            for (const [name, v] of Object.entries(value)) {
                if (options.ignoreAttrs.includes(name)) {
                    continue;
                }

                if (name === 'class') {
                    const classes = String(v).split(/\s+/).filter((c) => c && !options.ignoreClasses.includes(c)).sort();
                    if (classes.length > 0) {
                        attrs.class = classes.join(' ');
                    }
                } else {
                    attrs[name] = v;
                }
            }

            if (Object.keys(attrs).length > 0) {
                copy.attrs = attrs;
            }
        } else {
            copy[key] = normalise(value);
        }
    }

    return copy;
}

function diff(a, b, where, out) {
    if (JSON.stringify(a) === JSON.stringify(b)) {
        return;
    }

    const bothObjects = a && b && typeof a === 'object' && typeof b === 'object' && Array.isArray(a) === Array.isArray(b);
    if (!bothObjects) {
        out.push({ path: where || '/', before: a === undefined ? '(missing)' : a, after: b === undefined ? '(missing)' : b });
        return;
    }

    const keys = Array.isArray(a)
        ? Array.from({ length: Math.max(a.length, b.length) }, (_, i) => i)
        : Array.from(new Set([...Object.keys(a), ...Object.keys(b)]));
    for (const key of keys) {
        const label = Array.isArray(a) ? '[' + key + ']' : '.' + key;
        const tag = a[key] && typeof a[key] === 'object' && a[key].tag ? '<' + a[key].tag + '>' : '';
        diff(a[key], b[key], (where || '') + label + tag, out);
    }
}

const before = snapshots(files[0]);
const after = snapshots(files[1]);
const result = { before: files[0], after: files[1], ignoreClasses: options.ignoreClasses, ignoreAttrs: options.ignoreAttrs, same: [], different: [], onlyBefore: [], onlyAfter: [] };
for (const name of Object.keys(before)) {
    if (!(name in after)) {
        result.onlyBefore.push(name);
        continue;
    }

    const differences = [];
    diff(normalise(before[name]), normalise(after[name]), '', differences);
    if (differences.length === 0) {
        result.same.push(name);
    } else {
        result.different.push({ name, differences });
    }
}

result.onlyAfter = Object.keys(after).filter((name) => !(name in before));
result.identical = result.different.length === 0 && result.onlyBefore.length === 0 && result.onlyAfter.length === 0 && result.same.length > 0;
const json = JSON.stringify(result, null, 2);
if (options.out) {
    fs.writeFileSync(path.resolve(options.out), json + '\n');
}

process.stdout.write(json + '\n');
process.exitCode = result.identical ? 0 : 1;
