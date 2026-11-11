// The pure part of a cloud: which words to show, how big and how dark each one is, and in what order. Sizes
// follow the weights on the chosen scale, the shuffled orders and colours by word are stable (an FNV-1a hash of the key), and
// tests/.../Web/tagcloud.test.mjs checks the cases.

const tcLayout = (function () {
    // The number of shade tiers words are bucketed into.
    const TIER_COUNT = 5;

    function transform(weight, scale) {
        if (scale === 'Linear') {
            return weight;
        }

        if (scale === 'Logarithmic') {
            return Math.log(weight + 1);
        }

        return Math.sqrt(weight);
    }

    // FNV-1a over the key's UTF-16 code units, as the C# StableHash: the same words always land in the same places.
    function stableHash(key) {
        let hash = 2166136261;
        const text = String(key);
        for (let i = 0; i < text.length; i++) {
            hash = Math.imul(hash ^ text.charCodeAt(i), 16777619) >>> 0;
        }

        return hash >>> 0;
    }

    function compareText(a, b) {
        return String(a).localeCompare(String(b), undefined, { sensitivity: 'base' });
    }

    function compareOrdinal(a, b) {
        return a < b ? -1 : (a > b ? 1 : 0);
    }

    // Picks the heaviest maxItems words, sizes them between minSize and maxSize rem, and orders them. Duplicate keys keep
    // their first occurrence. items: [{ key, text, weight }]; returns [{ item, fontSize, tier, fraction }].
    function arrange(items, maxItems, minSize, maxSize, scale, order) {
        if (!items || maxItems <= 0) {
            return [];
        }

        if (minSize > maxSize) {
            const swap = minSize;
            minSize = maxSize;
            maxSize = swap;
        }

        const seen = Object.create(null);
        const chosen = items
            .filter(function (item) {
                const key = String(item.key);
                if (!item.text || !String(item.text).trim() || seen[key]) {
                    return false;
                }

                seen[key] = true;
                return true;
            })
            .map(function (item, index) {
                return { item: item, index: index, weight: Math.max(0, Number(item.weight) || 0) };
            })
            .sort(function (a, b) {
                return b.weight - a.weight || compareText(a.item.text, b.item.text) || a.index - b.index;
            })
            .slice(0, maxItems);

        if (chosen.length === 0) {
            return [];
        }

        let low = Infinity;
        let high = -Infinity;
        chosen.forEach(function (c) {
            const value = transform(c.weight, scale);
            low = Math.min(low, value);
            high = Math.max(high, value);
        });

        const placed = chosen.map(function (c) {
            // With every weight equal there is no spread to show, so all sit at the midpoint.
            const fraction = high > low ? (transform(c.weight, scale) - low) / (high - low) : 0.5;
            const size = minSize + fraction * (maxSize - minSize);
            const tier = 1 + Math.min(TIER_COUNT - 1, Math.floor(fraction * TIER_COUNT));
            return { item: c.item, fontSize: Math.round(size * 1000) / 1000, tier: tier, fraction: fraction };
        });

        return sortPlaced(placed, order);
    }

    function sortPlaced(placed, order) {
        if (order === 'ByWeight') {
            return placed;
        }

        if (order === 'Alphabetical') {
            return placed.slice().sort(function (a, b) {
                return compareText(a.item.text, b.item.text);
            });
        }

        if (order === 'Shuffled') {
            return placed.slice().sort(function (a, b) {
                return stableHash(a.item.key) - stableHash(b.item.key) || compareOrdinal(String(a.item.key), String(b.item.key));
            });
        }

        // Centered: deal heaviest-first onto alternating ends of the line, so the heaviest words meet in the middle. A
        // stable shuffle within each band of similar weight keeps it from reading as a mirror image.
        const bands = placed.slice().sort(function (a, b) {
            return b.tier - a.tier || stableHash(a.item.key) - stableHash(b.item.key);
        });
        const dealt = [];
        let toEnd = true;
        bands.forEach(function (p) {
            if (toEnd) {
                dealt.push(p);
            } else {
                dealt.unshift(p);
            }

            toEnd = !toEnd;
        });
        return dealt;
    }

    // The word as shown: cased per textCase.
    function displayText(text, textCase) {
        if (textCase === 'Upper') {
            return String(text).toLocaleUpperCase();
        }

        if (textCase === 'Lower') {
            return String(text).toLocaleLowerCase();
        }

        return String(text);
    }

    // The colour a placed word wears from colors, or null to keep the shade tiers. By word, each word gets a stable pick;
    // by weight, the heaviest get the first colour.
    function colorFor(placement, colors, colorBy) {
        if (!colors || colors.length === 0) {
            return null;
        }

        const index = colorBy === 'Weight'
            ? Math.min(colors.length - 1, Math.floor((1 - placement.fraction) * colors.length))
            : stableHash(placement.item.key) % colors.length;
        return colors[index];
    }

    return Object.freeze({
        TIER_COUNT: TIER_COUNT,
        arrange: arrange,
        displayText: displayText,
        colorFor: colorFor,
        stableHash: stableHash
    });
})();
