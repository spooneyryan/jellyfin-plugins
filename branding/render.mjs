// Renders the SVGs next to this file into the PNGs the repository ships, with headless Chrome (or Edge), then re-encodes
// them losslessly with optimize-png.mjs:
//   <Plugin>.svg       -> plugins/<Plugin>/image.png (the plugin's image on the Dashboard's Plugins page)
//   AutoCollection.svg -> plugins/SmartSearch/Jellyfin.Plugin.SmartSearch/AutoCollections/AutoCollection.png (embedded)
// Usage: node branding/gen.mjs && node branding/render.mjs   (CHROME=<path to chrome.exe or msedge.exe> to pick one)
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = dirname(here);
const targets = {
  AccessRoles: 'plugins/AccessRoles/image.png',
  Bookmarks: 'plugins/Bookmarks/image.png',
  LibraryMaintenance: 'plugins/LibraryMaintenance/image.png',
  SmartSearch: 'plugins/SmartSearch/image.png',
  TagCloud: 'plugins/TagCloud/image.png',
  UserRatings: 'plugins/UserRatings/image.png',
  AutoCollection: 'plugins/SmartSearch/Jellyfin.Plugin.SmartSearch/AutoCollections/AutoCollection.png',
};

const candidates = [
  process.env.CHROME,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
].filter(Boolean);
const chrome = candidates.find((c) => existsSync(c));
if (!chrome) throw new Error('No Chrome or Edge found; set CHROME to its path.');

const work = mkdtempSync(join(tmpdir(), 'branding-'));
try {
  for (const [name, target] of Object.entries(targets)) {
    const svg = join(here, `${name}.svg`);
    const size = readFileSync(svg, 'utf8').match(/<svg[^>]*\bwidth="(\d+)"[^>]*\bheight="(\d+)"/);
    const png = join(work, `${name}.png`);
    execFileSync(chrome, ['--headless=new', `--user-data-dir=${join(work, 'profile')}`, `--window-size=${size[1]},${size[2]}`,
      `--screenshot=${png}`, pathToFileURL(svg).href], { stdio: 'ignore' });
    execFileSync(process.execPath, [join(here, 'optimize-png.mjs'), png], { stdio: 'inherit' });
    copyFileSync(png, join(repo, target));
    console.log(`${name}.svg -> ${target}`);
  }
} finally {
  rmSync(work, { recursive: true, force: true });
}
