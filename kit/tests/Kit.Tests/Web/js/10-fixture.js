// Kit.Tests' stand-in plugin script (prefix jfkt), embedded by Kit.props as Web.js.10-fixture.js: the fixture of the
// EmbeddedScript tests and of the ClientScript checks (kit/web/tests/client-script.test.mjs). Its anchors are declared
// in Web/anchors.json and found through kit.anchors, as a plugin's 10-selectors.js does.
const FIXTURE = { scans: 0, list: null, route: '#/fixture' };

kit.observe(function () {
    FIXTURE.scans++;
    FIXTURE.list = kit.anchors.find('fixtureList');
});

kit.onView(function () {
    if (window.location.hash.indexOf(FIXTURE.route) === 0) {
        kit.health.expect(['fixtureList', { name: 'fixtureButton', within: 'fixtureList' }], 'fixture');
    }
});
