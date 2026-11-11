// The settings panel: every choice of the cloud's look, applied live while the panel is open. Lists are
// selects and numbers are sliders with their value beside them, so each works with a mouse, a finger and a TV remote.

function tcRange(label, value, min, max, step, format, onInput) {
    const output = h('span', { class: 'jftc-range-value', text: format(value) });
    const input = h('input', { type: 'range', class: 'jftc-range', min: String(min), max: String(max), step: String(step), value: String(value), 'aria-label': label });
    input.addEventListener('input', function () {
        const n = Number(input.value);
        output.textContent = format(n);
        onInput(n);
    });
    return kit.field(label, input, [output]);
}

function tcSelectField(label, list, value, onChange) {
    const select = kit.select({ 'aria-label': label }, list.map(function (choice) {
        return { value: choice.value, text: choice.text };
    }));
    select.value = value;
    select.addEventListener('change', function () {
        onChange(select.value);
    });
    return kit.field(label, select);
}

function tcSection(title, children) {
    return h('section', { class: 'jftc-settings-section' }, [h('h4', { class: 'jftc-settings-title', text: title })].concat(children));
}

// Opens the panel over the page. getLook() reads the look shown; onChange(look) is called with each new look.
function openTcSettings(getLook, onChange, maxWordsLimit) {
    const dialog = kit.dialog({ title: 'Cloud settings', scopedKeys: true });
    const content = h('div', { class: 'jftc-settings' });
    const change = function (patch) {
        onChange(tcNormalizeLook(Object.assign({}, getLook(), patch), maxWordsLimit));
        render();
    };
    const pct = function (n) {
        return Math.round(n) + ' %';
    };
    const em = function (n) {
        return (Math.round(n * 100) / 100) + ' em';
    };
    const rem = function (n) {
        return (Math.round(n * 100) / 100) + ' rem';
    };

    function render() {
        const look = getLook();
        const packed = look.Arrangement === 'Packed';
        const focused = document.activeElement && content.contains(document.activeElement) ? document.activeElement.getAttribute('aria-label') : null;
        content.textContent = '';
        kit.append(content,
            tcSection('Layout', [
                tcSelectField('Arrangement', TC_ARRANGEMENTS, look.Arrangement, function (v) {
                    change({ Arrangement: v });
                }),
                packed ? tcSelectField('Shape', TC_SHAPES, look.Shape, function (v) {
                    change({ Shape: v });
                }) : tcSelectField('Order', TC_ORDERS, look.Order, function (v) {
                    change({ Order: v });
                }),
                packed ? tcRange('Mix sizes', look.SizeMixing * 100, 0, 100, 10, pct, function (n) {
                    onChange(tcNormalizeLook(Object.assign({}, getLook(), { SizeMixing: n / 100 }), maxWordsLimit));
                }) : null
            ]),
            tcSection('Spacing', [
                tcRange('Word spacing', look.WordSpacing, 0, 2, 0.05, em, function (n) {
                    onChange(tcNormalizeLook(Object.assign({}, getLook(), { WordSpacing: n }), maxWordsLimit));
                }),
                tcRange('Line spacing', look.LineSpacing, 0, 2, 0.05, em, function (n) {
                    onChange(tcNormalizeLook(Object.assign({}, getLook(), { LineSpacing: n }), maxWordsLimit));
                })
            ]),
            tcSection('Text', [
                tcSelectField('Letter case', TC_TEXT_CASES, look.TextCase, function (v) {
                    change({ TextCase: v });
                }),
                tcSelectField('Font', TC_FONTS, look.Font, function (v) {
                    change({ Font: v });
                })
            ]),
            tcSection('Size', [
                tcRange('Largest word', look.MaxFontSize, 1.5, 8, 0.25, rem, function (n) {
                    onChange(tcNormalizeLook(Object.assign({}, getLook(), { MaxFontSize: n }), maxWordsLimit));
                }),
                tcRange('Smallest word', look.MinFontSize, 0.4, 2, 0.05, rem, function (n) {
                    onChange(tcNormalizeLook(Object.assign({}, getLook(), { MinFontSize: n }), maxWordsLimit));
                }),
                tcSelectField('Scale', TC_SCALES, look.Scale, function (v) {
                    change({ Scale: v });
                })
            ]),
            tcSection('Colours', [
                tcSelectField('Palette', TC_PALETTES, look.Palette, function (v) {
                    change({ Palette: v });
                }),
                tcSelectField('Colour by', TC_COLOR_BYS, look.ColorBy, function (v) {
                    change({ ColorBy: v });
                }),
                tcSelectField('Background', TC_BACKGROUNDS, look.Background, function (v) {
                    change({ Background: v });
                })
            ]),
            h('div', { class: 'jftc-settings-actions' }, [
                // The defaults, keeping the word count the viewer chose.
                kit.button('Reset to defaults', {
                    onClick: function () {
                        change(Object.assign({}, TC_LOOK_DEFAULTS, { MaxWords: getLook().MaxWords, Seed: getLook().Seed }));
                    }
                }),
                kit.button('Done', { variant: 'primary', onClick: dialog.close })
            ]));
        if (focused) {
            const again = content.querySelector('[aria-label="' + focused.replace(/"/g, '') + '"]');
            if (again) {
                again.focus();
            }
        }
    }

    dialog.body.append(content);
    render();
    return dialog;
}
