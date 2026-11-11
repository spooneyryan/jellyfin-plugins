// Generates librarymaintenance-move-dialog-release.json, the Release variant of librarymaintenance-move-dialog.json that
// Library Maintenance 1.0.2's release checks ran on a Release build in the `move` sandbox (269 steps; scripts/README.md,
// "Library Maintenance checks"). A Release build ignores JFLM_MOVE_TEST_VOLUMES, so Movies 2 is on the same drive as the
// other libraries: the dialog's library list (step 21) has "Movies 2" without "(can't be chosen)", the loose movie's "the other
// libraries can't be chosen" (step 117) is left out, and Movies 2 on another drive (steps 98 to 102) is left out.
// Everything else is the Debug webcheck's own steps.
// Run from the repository root: node scripts/webchecks/gen-move-dialog-release.mjs [--out <folder>]
// (Repo.Tests writes it into a temporary folder and compares it with the committed file.)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const outIndex = process.argv.indexOf('--out');
const folder = outIndex > 0 ? path.resolve(process.argv[outIndex + 1]) : here;
const source = path.join(here, 'librarymaintenance-move-dialog.json');
const target = path.join(folder, 'librarymaintenance-move-dialog-release.json');

const d = JSON.parse(fs.readFileSync(source, 'utf8'));
const s = d.steps;

// Each change checks the step it changes first, so an edit of the Debug webcheck that moves them stops the generator
// instead of writing a variant that tests something else.
const listed = "'Movies 2 (can\\'t be chosen)'";
if (typeof s[21].assert !== 'string' || !s[21].assert.includes(listed)) {
    throw new Error("step 21 no longer lists 'Movies 2 (can't be chosen)'");
}
s[21].assert = s[21].assert.replace(listed, "'Movies 2'");

const others = / && __vars\.f1s\.libraries\.slice\(1\)\.every\(\(l\) => .*?\)\)(?= &&|$)/;
if (typeof s[117].assert !== 'string' || !others.test(s[117].assert)) {
    throw new Error("step 117 no longer checks that the other libraries can't be chosen");
}
s[117].assert = s[117].assert.replace(others, '');

if (s[98].name !== 'another drive: Movies 2' || s[102].screenshot !== 'lm-move-f3-other-drive.png') {
    throw new Error('steps 98 to 102 are no longer the other drive\'s');
}
s.splice(98, 5);

d.name = 'Release variant (gen-move-dialog-release.mjs; Movies 2 on the same drive, no other drive): ' + d.name;
fs.mkdirSync(folder, { recursive: true });
fs.writeFileSync(target, JSON.stringify(d, null, 2) + '\n');
console.log(`${path.relative(process.cwd(), target)}: ${s.length} steps`);
