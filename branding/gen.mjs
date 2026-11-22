// Writes the plugin cards (<Plugin>.svg, 1280x720) and the auto-collection poster (AutoCollection.svg, 1000x1500)
// next to this file. render.mjs turns them into the PNGs the plugins ship; see README.md.
import { writeFileSync } from 'node:fs';
const out = (name) => new URL(name, import.meta.url);
const W = 1280, H = 720;
const defs = `
<defs>
  <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
    <stop offset="0" stop-color="#1b1033"/><stop offset="1" stop-color="#0a2a3d"/>
  </linearGradient>
  <linearGradient id="accent" x1="0" y1="0" x2="1" y2="1">
    <stop offset="0" stop-color="#AA5CC3"/><stop offset="1" stop-color="#00A4DC"/>
  </linearGradient>
  <radialGradient id="glow" cx="0.28" cy="0.5" r="0.45">
    <stop offset="0" stop-color="#7a4fd0" stop-opacity="0.35"/><stop offset="1" stop-color="#7a4fd0" stop-opacity="0"/>
  </radialGradient>
</defs>`;
const S = 'fill="none" stroke="#fff" stroke-width="22" stroke-linecap="round" stroke-linejoin="round"';
// icons drawn in a 300x300 box
const icons = {
  AccessRoles: `<path ${S} d="M150 30 L250 70 V150 C250 215 205 255 150 275 C95 255 50 215 50 150 V70 Z"/>
    <circle cx="150" cy="135" r="28" fill="#fff"/><path fill="#fff" d="M135 150 h30 l10 65 h-50 z"/>`,
  Bookmarks: `<path ${S} d="M80 35 H220 V270 L150 215 L80 270 Z"/>
    <path fill="#fff" d="M150 85 l14 30 33 4 -24 22 6 33 -29 -16 -29 16 6 -33 -24 -22 33 -4z"/>`,
  LibraryMaintenance: `<rect x="45" y="60" width="50" height="190" rx="10" ${S}/>
    <rect x="115" y="40" width="50" height="210" rx="10" ${S}/>
    <path ${S} d="M190 85 l45 -12 48 175"/><path ${S} d="M30 262 H210"/>
    <circle cx="225" cy="225" r="58" fill="#fff"/><path fill="none" stroke="#5a7fd6" stroke-width="20" stroke-linecap="round" stroke-linejoin="round" d="M197 226 l20 20 36 -40"/>`,
  SmartSearch: `<circle cx="125" cy="125" r="80" ${S}/><path ${S} d="M185 185 L265 265"/>
    <path fill="none" stroke="#fff" stroke-width="16" stroke-linecap="round" d="M88 102 H162 M100 127 H150 M112 152 H138"/>`,
  TagCloud: `<path ${S} d="M82 220 H220 A52 52 0 0 0 228 116 A74 74 0 0 0 86 108 A56 56 0 0 0 82 220 Z"/>
    <rect x="98" y="138" width="104" height="26" rx="13" fill="#fff"/>
    <rect x="88" y="178" width="50" height="16" rx="8" fill="#fff"/><rect x="150" y="178" width="68" height="16" rx="8" fill="#fff"/>
    <rect x="126" y="110" width="40" height="12" rx="6" fill="#fff"/>`,
  UserRatings: `<path fill="#fff" d="M150 30 l35 72 79 11 -57 56 14 79 -71 -38 -71 38 14 -79 -57 -56 79 -11z"/>
    <circle cx="150" cy="150" r="30" fill="url(#accent)"/><path fill="url(#accent)" d="M108 225 a42 38 0 0 1 84 0 z"/>`,
};
const plugins = [
  ['AccessRoles', 'Access Roles'],
  ['Bookmarks', 'Bookmarks And Clips'],
  ['LibraryMaintenance', 'Library Maintenance'],
  ['SmartSearch', 'Smart Search And Dynamic Collections'],
  ['TagCloud', 'Tag Cloud'],
  ['UserRatings', 'User Ratings And Reviews'],
];
function wrap(text, max) { const out = []; let line = ''; for (const w of text.split(' ')) { if ((line + ' ' + w).trim().length > max) { out.push(line); line = w; } else line = (line + ' ' + w).trim(); } out.push(line); return out; }
for (const [key, name] of plugins) {
  // A name longer than 19 characters goes on two lines; the size fits the longest line into 660px.
  const nameLines = name.length > 19 ? wrap(name, 19) : [name];
  const nameSize = Math.min(nameLines.length > 1 ? 80 : 92, Math.floor(660 / (0.56 * Math.max(...nameLines.map((l) => l.length)))));
  // The title is centred on the icon tile (y 170 to 550).
  const lineHeight = Math.round(nameSize * 1.12);
  const nameTop = Math.round(360 - ((nameLines.length - 1) * lineHeight) / 2 + nameSize * 0.36);
  const nameText = nameLines.map((l, i) => `<text x="560" y="${nameTop + i * lineHeight}" font-size="${nameSize}" font-weight="700" fill="#fff">${l}</text>`).join('');
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" font-family="Segoe UI, Arial, sans-serif">${defs}
  <rect width="${W}" height="${H}" fill="url(#bg)"/><rect width="${W}" height="${H}" fill="url(#glow)"/>
  <rect x="0" y="${H - 14}" width="${W}" height="14" fill="url(#accent)"/>
  <rect x="110" y="170" width="380" height="380" rx="72" fill="url(#accent)"/>
  <g transform="translate(150,210) scale(1)">${icons[key]}</g>
  ${nameText}
</svg>`;
  writeFileSync(out(`${key}.svg`), svg);
}
// auto-collection poster 1000x1500
const defs2 = defs.replace('</defs>', '<radialGradient id="glow2" cx="0.5" cy="0.42" r="0.6"><stop offset="0" stop-color="#7a4fd0" stop-opacity="0.35"/><stop offset="1" stop-color="#7a4fd0" stop-opacity="0"/></radialGradient></defs>');
const P = `<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="1500" viewBox="0 0 1000 1500" font-family="Segoe UI, Arial, sans-serif">${defs2}
  <rect width="1000" height="1500" fill="url(#bg)"/>
  <rect width="1000" height="1500" fill="url(#glow2)"/>
  <g transform="translate(500,620)">
    <rect x="-250" y="-330" width="380" height="540" rx="28" fill="#2a2150" stroke="#6f5bb5" stroke-width="6" transform="rotate(-10)"/>
    <rect x="-170" y="-330" width="380" height="540" rx="28" fill="#1e3a5c" stroke="#3f88b8" stroke-width="6" transform="rotate(6)"/>
    <rect x="-190" y="-300" width="380" height="540" rx="28" fill="url(#accent)"/>
    <g fill="none" stroke="#fff" stroke-width="30" stroke-linecap="round" stroke-linejoin="round">
      <path d="M-95 -60 A110 110 0 0 1 95 -55"/><path d="M95 -55 l5 -60 M95 -55 l-58 -12"/>
      <path d="M95 90 A110 110 0 0 1 -95 85"/><path d="M-95 85 l-5 60 M-95 85 l58 12"/>
    </g>
  </g>
  <text x="500" y="1150" text-anchor="middle" font-size="96" font-weight="700" fill="#fff">Auto collection</text>
  <text x="500" y="1235" text-anchor="middle" font-size="46" fill="#cfd6e6">Kept up to date by Smart Search</text>
  <rect x="0" y="1480" width="1000" height="20" fill="url(#accent)"/>
</svg>`;
writeFileSync(out('AutoCollection.svg'), P);
