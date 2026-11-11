// Kit.Tests' stand-in dashboard module (Web/admin/js): declares mount(view, params), which the AdminScript exports.
function mount(view, params) {
    kit.injectStyles();
    view.append(kit.h('p', { class: 'jfkt-admin', text: 'Mounted ' + ((params && params.name) || '') }));
    return kit;
}
