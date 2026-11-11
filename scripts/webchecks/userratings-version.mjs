// The User Ratings version that gen-details.mjs, gen-moderation.mjs and gen-stars.mjs write into their webchecks (each
// checks that the served script reports it). It is read from plugins/UserRatings/build.yaml, so a version bump changes
// it in one place (before, each generator had its own copy). After a bump, run the five
// generators again. Repo.Tests compares their files with what they write, so a stale file fails there.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const buildYaml = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'plugins', 'UserRatings', 'build.yaml');
const match = /^version:\s*"(\d+\.\d+\.\d+\.\d+)"\s*$/m.exec(fs.readFileSync(buildYaml, 'utf8'));
if (!match) {
    throw new Error(`${buildYaml} has no line version: "a.b.c.d"`);
}

/** The four-part version, as the server and the web script report it (0.3.16.0). */
export const VERSION = match[1];

/** The version without its last part when that is 0, as the webchecks' titles name it (0.3.16). */
export const SHORT_VERSION = VERSION.replace(/\.0$/, '');
