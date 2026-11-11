/*
 * Access Roles' web guard for jellyfin-web 10.11, on the shared kit's web part. The kit's
 * EmbeddedScript concatenates kit.js and the files of this folder in name order into one IIFE, served by
 * GET /AccessRoles/ClientScript and added to index.html by the kit's middleware; the head fragment (Web/head.js and the
 * CSS Web/AccessRolesHead builds from Web/anchors.json) has set <html>'s classes from the browser's cache before
 * jellyfin-web's bundles ran. Driven by GET /AccessRoles/Me, it:
 *   - keeps the classes in step with the signed-in user's locks (the first-load rule: jfar-pending until /Me
 *     answers, 3 s at most, while a slower /Me still counts when it answers; every answer cached, Managed false too; a
 *     sign-in or a user switch without a reload; a /Me that failed asked again on a later view);
 *   - guards the locked settings routes (as measured on 10.11.5: redirect on the blocked page's own viewshow, or at once
 *     while a lazy navigation to it is pending, never on an idle update alone; the startup check; a capture-phase click
 *     blocker) with a toast;
 *   - marks hidden MUI user-menu items aria-disabled and moves the focus off a hidden first item; puts the note on the
 *     Settings page; disables the Display (account) fields; replaces the no-libraries text; tells the user when the
 *     server kept locked settings of a save; shows administrators a banner on the stock user pages and Add user.
 * Everything here is cosmetic: the server (the settings locks' global filter) refuses or merges every locked change
 * whatever the web does, so a /Me that fails or never answers leaves the page as jellyfin-web made it. Every value is
 * set with textContent.
 *
 * This file: the user-visible strings.
 */

kit.addStrings({
    pageManaged: 'This page is managed by your server administrator.',
    settingsManaged: 'Settings are managed by your administrator',
    noLibraries: 'No libraries have been shared with you yet. Ask your server administrator.',
    kept: 'Some of these settings are managed by your server administrator, so they weren\'t changed: {sections}.',
    adminManaged: 'Access Roles manages this user (roles: {roles}).',
    adminManagedNoRole: 'Access Roles manages this user (overrides only, no role).',
    adminLibraries: 'Library access is set by the roles: changes to it on this page are replaced by the roles\' result. Change the user\'s roles or overrides on the Access Roles page instead.',
    adminLocked: 'Settings locked for this user: {sections}.',
    adminExcluded: 'Access Roles leaves this user alone (Excluded).',
    adminDefaultRoles: 'New users get Access Roles\' default roles: {roles}. Their library access is set by those roles, so the library ticks on this page are replaced once the user is created.',
    adminEnforcementOff: 'Access Roles\' enforcement is switched off, so nothing of this is applied now.',
    noneOfThem: 'none'
});

// The words for a lock, as the server's refusals name them (AccessRulesActionFilter.Title).
const SECTION_TITLES = Object.freeze({
    Profile: 'Profile',
    DisplayAccount: 'Display (account)',
    DisplayDevice: 'Display (this device)',
    Home: 'Home screen',
    Playback: 'Playback',
    Subtitles: 'Subtitles',
    Controls: 'Controls',
    QuickConnect: 'Quick Connect',
    EnhancedPanel: 'Jellyfin Enhanced',
    AdminSurfaces: 'Dashboard data and server calls',
    UserDirectory: 'User directory'
});

function sectionTitles(names) {
    return (names || []).map(function (name) {
        return Object.prototype.hasOwnProperty.call(SECTION_TITLES, name) ? SECTION_TITLES[name] : String(name);
    }).join(', ');
}
