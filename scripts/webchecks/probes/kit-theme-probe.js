// Kit theming probe: the computed colours of one of our dialogs next to the theme's own native
// ones. Installed with webcheck's initScript step; it only reads the page (and adds and removes one off-screen probe).
//
//   window.__kitThemeProbe('.jfbm-backdrop .jfbm-panel') -> { theme, accentVar, panelBackground, nativeDialogBackground, ... }
(function () {
    'use strict';
    if (window.__kitThemeProbe) {
        return;
    }

    function style(element, property) {
        return element ? getComputedStyle(element).getPropertyValue(property).trim() : null;
    }

    window.__kitThemeProbe = function (panelSelector) {
        var panel = document.querySelector(panelSelector);
        if (!panel) {
            return null;
        }

        var nativeDialog = document.createElement('div');
        nativeDialog.className = 'dialog';
        nativeDialog.style.position = 'absolute';
        nativeDialog.style.left = '-9999px';
        var nativeInput = document.createElement('input');
        nativeInput.className = 'emby-input';
        nativeDialog.appendChild(nativeInput);
        document.body.appendChild(nativeDialog);
        try {
            return {
                theme: document.documentElement.getAttribute('data-theme'),
                accentVar: style(document.documentElement, '--jf-palette-primary-main'),
                panelBackground: style(panel, 'background-color'),
                nativeDialogBackground: style(nativeDialog, 'background-color'),
                panelColor: style(panel, 'color'),
                htmlColor: style(document.documentElement, 'color'),
                inputBackground: style(panel.querySelector('input[type="text"]'), 'background-color'),
                // A textarea (kit.textarea, Bookmarks' note); every 10.11 theme styles emby-textarea with
                // emby-input, so it has the native input's colour. null in a dialog without one.
                textareaBackground: style(panel.querySelector('textarea'), 'background-color'),
                nativeInputBackground: style(nativeInput, 'background-color'),
                primaryBackground: style(panel.querySelector('[class*="-button-primary"]'), 'background-color'),
                primaryColor: style(panel.querySelector('[class*="-button-primary"]'), 'color'),
                neutralButtonBackground: style(panel.querySelector('[class*="-actions"] button:not([class*="-button-primary"])'), 'background-color')
            };
        } finally {
            nativeDialog.remove();
        }
    };
})();
