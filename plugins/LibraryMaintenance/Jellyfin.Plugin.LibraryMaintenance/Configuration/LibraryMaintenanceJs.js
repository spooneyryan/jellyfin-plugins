export default function LibraryMaintenancePage(view, params) {
    var note = view.querySelector('#LibraryMaintenanceScriptNote');
    import(ApiClient.getUrl('LibraryMaintenance/AdminScript')).then(function (module) {
        module.mount(view, params || {});
    }, function () {
        if (note) {
            note.textContent = 'The Library Maintenance page could not load its script. Reload the page; if it stays like this, the server log has the details.';
            note.setAttribute('role', 'alert');
        }
    });
}
