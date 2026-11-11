// The module's one export (the kit's AdminScript exports mount): configPage.html mounts the Settings tab
// with { tab: 'Settings' }, the Data tab with { tab: 'Data' }, its detached list with { tab: 'Detached' } and the
// Moderation tab with anything else, from the one import they share.
function mount(view, params) {
    const tab = params && params.tab;
    return tab === 'Settings' ? mountSettings(view, params)
        : tab === 'Data' ? mountData(view, params)
        : tab === 'Detached' ? mountDetached(view, params)
        : mountModeration(view, params);
}
