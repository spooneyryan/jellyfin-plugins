// How a cloud looks: the choices the settings panel offers,
// with the defaults and ranges the server holds a saved cloud to (Clouds/CloudLook.cs). The Theme
// choices follow jellyfin-web's theme through the kit's accent variable; the other backdrops are plain colours,
// each with a text colour that reads on it.

const TC_LOOK_DEFAULTS = Object.freeze({
    MaxWords: 100,
    Arrangement: 'Packed',
    Order: 'Centered',
    Shape: 'Wide',
    Seed: 0,
    SizeMixing: 0,
    WordSpacing: 0.5,
    LineSpacing: 0.25,
    TextCase: 'AsIs',
    Font: 'Theme',
    MinFontSize: 0.85,
    MaxFontSize: 4,
    Scale: 'SquareRoot',
    Palette: 'Theme',
    ColorBy: 'Word',
    Background: 'Theme'
});

const TC_ARRANGEMENTS = Object.freeze([{ value: 'Packed', text: 'Cloud' }, { value: 'Flow', text: 'Rows' }]);

const TC_ORDERS = Object.freeze([
    { value: 'Centered', text: 'Centered' },
    { value: 'Shuffled', text: 'Shuffled' },
    { value: 'Alphabetical', text: 'A–Z' },
    { value: 'ByWeight', text: 'Biggest first' }
]);

// Target width-to-height ratios of a packed cloud.
const TC_SHAPES = Object.freeze([
    { value: 'Wide', text: 'Wide', ratio: 2.4 },
    { value: 'Landscape', text: 'Landscape', ratio: 1.6 },
    { value: 'Square', text: 'Square', ratio: 1.0 },
    { value: 'Tall', text: 'Tall', ratio: 0.7 }
]);

const TC_TEXT_CASES = Object.freeze([
    { value: 'AsIs', text: 'Normal case' },
    { value: 'Upper', text: 'ALL CAPS' },
    { value: 'Lower', text: 'lowercase' }
]);

// System font stacks only: the kit has no font endpoint, and the packer measures whichever font draws.
const TC_FONTS = Object.freeze([
    { value: 'Theme', text: 'Theme', family: null },
    { value: 'Impact', text: 'Impact', family: 'Impact, Haettenschweiler, "Arial Narrow Bold", sans-serif' },
    { value: 'Heavy', text: 'Heavy', family: '"Arial Black", "Segoe UI Black", sans-serif' },
    { value: 'Condensed', text: 'Condensed', family: '"Bahnschrift SemiCondensed", "Arial Narrow", "Roboto Condensed", sans-serif' },
    { value: 'Serif', text: 'Serif', family: 'Georgia, Cambria, "Times New Roman", serif' },
    { value: 'Typewriter', text: 'Typewriter', family: '"Courier New", Courier, monospace' },
    { value: 'Handwritten', text: 'Handwritten', family: '"Segoe Print", "Bradley Hand", "Comic Sans MS", cursive' }
]);

const TC_SCALES = Object.freeze([
    { value: 'SquareRoot', text: 'Balanced' },
    { value: 'Linear', text: 'Dramatic' },
    { value: 'Logarithmic', text: 'Even' }
]);

// No colours means the theme's weight shading (the tiers in tagcloud.css).
const TC_PALETTES = Object.freeze([
    { value: 'Theme', text: 'Theme', colors: [] },
    { value: 'Accent', text: 'Accent', colors: ['var(--jftc-accent)'] },
    { value: 'Ink', text: 'Ink', colors: ['currentColor', 'rgba(128, 128, 128, 1)'] },
    { value: 'Rainbow', text: 'Rainbow', colors: ['#e6194b', '#f58231', '#ffe119', '#3cb44b', '#42d4f4', '#4363d8', '#911eb4', '#f032e6'] },
    { value: 'Sunset', text: 'Sunset', colors: ['#ff6b6b', '#ff9f43', '#feca57', '#ee5253', '#f368e0', '#ff9ff3'] },
    { value: 'Citrus', text: 'Citrus', colors: ['#f9a825', '#ef6c00', '#9ccc65', '#4db6ac', '#ffd54f'] },
    { value: 'Ocean', text: 'Ocean', colors: ['#0abde3', '#48dbfb', '#2e86de', '#54a0ff', '#01a3a4', '#00d2d3'] },
    { value: 'Forest', text: 'Forest', colors: ['#10ac84', '#1dd1a1', '#6ab04c', '#badc58', '#26de81', '#78e08f'] },
    { value: 'Neon', text: 'Neon', colors: ['#00f5ff', '#ff00e5', '#adff00', '#ffea00', '#ff3131'] },
    { value: 'Pastel', text: 'Pastel', colors: ['#ffadad', '#ffd6a5', '#fdffb6', '#caffbf', '#9bf6ff', '#a0c4ff', '#bdb2ff', '#ffc6ff'] },
    // Deep tones for light backdrops, like a printed Wordle.
    { value: 'Classic', text: 'Classic', colors: ['#8b1a1a', '#b8860b', '#2f4f4f', '#556b2f', '#4b0082', '#8b4513', '#191970'] }
]);

const TC_COLOR_BYS = Object.freeze([{ value: 'Word', text: 'Each word' }, { value: 'Weight', text: 'Size' }]);

// color: the backdrop, null for the page's own; ink: the words' colour on it, null for the page's text colour.
const TC_BACKGROUNDS = Object.freeze([
    { value: 'Theme', text: 'Theme', color: null, ink: null },
    { value: 'Black', text: 'Black', color: '#000000', ink: '#ffffff' },
    { value: 'Midnight', text: 'Midnight', color: '#0b1226', ink: '#e8ecf6' },
    { value: 'White', text: 'White', color: '#ffffff', ink: '#1a1a1a' },
    { value: 'Paper', text: 'Paper', color: '#f4ecd8', ink: '#3b2f1e' },
    { value: 'Slate', text: 'Slate', color: '#26303d', ink: '#e6ebf2' }
]);

function tcChoice(list, value) {
    for (let i = 0; i < list.length; i++) {
        if (list[i].value === value) {
            return list[i];
        }
    }

    return list[0];
}

function tcClampNumber(value, min, max, fallback) {
    const n = Number(value);
    return Number.isFinite(n) ? Math.round(Math.min(max, Math.max(min, n)) * 1000) / 1000 : fallback;
}

// A look with every name known and every number in its range, as the server keeps it (CloudLook.Normalize).
function tcNormalizeLook(look, maxWordsLimit) {
    const d = TC_LOOK_DEFAULTS;
    const l = look || {};
    const limit = maxWordsLimit || 1000;
    const words = Math.round(tcClampNumber(l.MaxWords, 25, limit, d.MaxWords) / 25) * 25;
    const smallest = tcClampNumber(l.MinFontSize, 0.4, 2, d.MinFontSize);
    return {
        MaxWords: Math.max(25, Math.min(limit - (limit % 25), words)),
        Arrangement: tcChoice(TC_ARRANGEMENTS, l.Arrangement).value,
        Order: tcChoice(TC_ORDERS, l.Order).value,
        Shape: tcChoice(TC_SHAPES, l.Shape).value,
        Seed: Number.isFinite(Number(l.Seed)) ? (Number(l.Seed) | 0) : 0,
        SizeMixing: tcClampNumber(l.SizeMixing, 0, 1, d.SizeMixing),
        WordSpacing: tcClampNumber(l.WordSpacing, 0, 2, d.WordSpacing),
        LineSpacing: tcClampNumber(l.LineSpacing, 0, 2, d.LineSpacing),
        TextCase: tcChoice(TC_TEXT_CASES, l.TextCase).value,
        Font: tcChoice(TC_FONTS, l.Font).value,
        MinFontSize: smallest,
        MaxFontSize: Math.max(smallest, tcClampNumber(l.MaxFontSize, 1.5, 8, d.MaxFontSize)),
        Scale: tcChoice(TC_SCALES, l.Scale).value,
        Palette: tcChoice(TC_PALETTES, l.Palette).value,
        ColorBy: tcChoice(TC_COLOR_BYS, l.ColorBy).value,
        Background: tcChoice(TC_BACKGROUNDS, l.Background).value
    };
}
