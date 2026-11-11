// The emergency stop. At the server's start, the file data/accessroles/SAFE-MODE or
// EnforcementEnabled false in the plugin's configuration file made Access Roles inert: it registered nothing but this page,
// and every admin API answers 503 with the stop as the ProblemDetails' inert extension. The page then shows only this
// view: which switch is in effect, and how to turn Access Roles back on. EnforcementEnabled goes back to true through
// Jellyfin's own plugin configuration API (Plugins/{id}/Configuration, the whole configuration read and written back), which
// needs no Access Roles service; the SAFE-MODE file can only be deleted on the server, with the server stopped.
const AR_PLUGIN_ID = '7cbc1019-49a5-4b97-8aa2-279de946ef88';

// The stop a failed Diagnostics answer carries, or null.
function arInertOf(result) {
    const error = result && !result.ok ? result.error : null;
    const inert = error && error.status === 503 && error.problem ? error.problem.inert : null;
    return inert && typeof inert === 'object' ? inert : null;
}

function arInertView(inert) {
    const result = arEl('div', { id: 'AccessRolesInertResult', 'aria-live': 'polite' });
    let turnOn = null;
    // What an inert plugin leaves behind, and that uninstalling it now releases nothing.
    const leftBehind = arBanner('What Access Roles wrote to users\' own policies stays as it was last written: the native preference access its optional mirror turned off and the library access its roles gave. Uninstalling it while it is inert puts nothing back. Turn it back on and restart first, then use Release all locks on the Overview, or put the flags back by hand as the README says.');
    leftBehind.id = 'AccessRolesInertLeftBehind';
    const children = [
        arHeading(2, 'Access Roles is stopped'),
        arBanner('Access Roles is inert: the emergency stop was in effect when the server started, so it enforces nothing, writes no library access and leaves the web client as it is. Its roles and assignments are kept as they were.', 'error'),
        leftBehind
    ];

    if (inert.SafeMode) {
        children.push(arEl('div', { id: 'AccessRolesInertSafeMode', class: arClass('section') }, [
            arHeading(3, 'The SAFE-MODE file'),
            arEl('p', { text: 'The SAFE-MODE file exists: ' + (inert.SafeModeFile || 'data/accessroles/SAFE-MODE') + '.' }),
            arHint('To turn Access Roles back on, stop the server, delete that file, and start the server again. The page can\'t delete it: the file is the stop for when the page itself can\'t be trusted.')
        ]));
    }

    if (inert.EnforcementDisabled) {
        // Enabled again after a save (saving true again is harmless), so arBusy gives the focus back to it rather than leaving
        // it on the page.
        turnOn = arButton('Turn enforcement back on', function () {
            arBusy(turnOn, enable);
        }, { variant: 'primary' });
        turnOn.id = 'AccessRolesInertEnable';
        children.push(arEl('div', { id: 'AccessRolesInertDisabled', class: arClass('section') }, [
            arHeading(3, 'EnforcementEnabled is off'),
            arEl('p', { text: 'EnforcementEnabled is false in ' + (inert.ConfigurationFile || 'plugins/configurations/Jellyfin.Plugin.AccessRoles.xml') + '.' }),
            arHint('Turning it back on saves the setting now; Access Roles enforces again after the server restarts' + (inert.SafeMode ? ', once the SAFE-MODE file is gone too.' : '.')),
            arActions([turnOn]),
            result
        ]));
    }

    function enable() {
        return kit.confirm({ title: 'Turn enforcement back on', message: 'Set EnforcementEnabled to true? Access Roles enforces its roles again after the server restarts' + (inert.SafeMode ? ' without the SAFE-MODE file.' : '.'), confirmLabel: 'Turn it back on' }).then(function (ok) {
            if (!ok) {
                return null;
            }

            arClear(result);
            const path = 'Plugins/' + AR_PLUGIN_ID + '/Configuration';
            return kit.api('GET', path).then(function (configuration) {
                const changed = Object.assign({}, configuration || {}, { EnforcementEnabled: true });
                return kit.api('POST', path, { body: changed });
            }).then(function () {
                result.appendChild(arEl('p', { text: 'Saved: EnforcementEnabled is true. Restart the server (Dashboard, Restart) for Access Roles to enforce again' + (inert.SafeMode ? '; delete the SAFE-MODE file first, with the server stopped.' : '.') }));
            }, function (error) {
                result.appendChild(arBanner('The setting couldn\'t be saved: ' + arErrorText(error) + ' You can set EnforcementEnabled to true in the configuration file with the server stopped.', 'error'));
                arFocusIfLost(turnOn);
            });
        });
    }

    children.push(arHint('Until then nothing of Access Roles runs but this page. See the plugin\'s README, "Lockout and recovery": "The emergency stop" for both switches, and "Recovery after the plugin folder was deleted with the mirror on" to put the flags back by hand.'));
    return arEl('div', { id: 'AccessRolesInert', class: arClass('inert') }, children);
}
