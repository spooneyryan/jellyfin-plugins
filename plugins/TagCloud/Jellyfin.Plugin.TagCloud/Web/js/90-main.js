// Start-up. The Tag Cloud page (60-page-cloud.js) is registered with the kit's page host, which renders it on its route
// (#/jftc-cloud, or Plugin Pages' link) and links it from "Library tools" while the server's head fragment lists it as on.
// The kit waits for window.ApiClient and the signed-in user before the page renders.

kit.pages.register(Object.assign({ render: renderTcPage }, TC_PAGE));
kit.start();
