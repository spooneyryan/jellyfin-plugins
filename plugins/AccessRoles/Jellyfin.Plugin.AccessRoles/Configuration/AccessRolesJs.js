// Access Roles' dashboard page script: jellyfin-web imports it for the page's
// data-controller and calls `new` on its default export with the view and the route's parameters. It imports the
// AdminScript module (the kit and Web/admin/js; anonymous, static) and mounts it at once; a script that doesn't load, or a
// mount that throws, says so on the page with no unhandled rejection. It writes only its own sentences.
export default function AccessRolesPage(view, params) {
    var note = view.querySelector('#AccessRolesScriptNote');
    function failed(text) {
        if (note) {
            note.textContent = text;
            note.removeAttribute('hidden');
            note.setAttribute('role', 'alert');
        }
    }

    import(ApiClient.getUrl('AccessRoles/AdminScript')).then(function (module) {
        try {
            module.mount(view, params || {});
        } catch (e) {
            failed('The Access Roles page could not start. Reload the page; if it stays like this, the server log has the details.');
        }
    }, function () {
        failed('The Access Roles page could not load its script. Reload the page; if it stays like this, the server log has the details.');
    });
}
