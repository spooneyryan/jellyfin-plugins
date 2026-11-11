// The plugin's API: every call goes through kit.api (the token header, a 30 s timeout, ProblemDetails turned into
// errors with the server's own sentence).
const LM_ROUTE = 'LibraryMaintenance/';

function lmGet(path, opts) {
    return kit.api('GET', LM_ROUTE + path, opts);
}

function lmPost(path, body, opts) {
    return kit.api('POST', LM_ROUTE + path, Object.assign({}, opts || {}, body === undefined ? {} : { body: body }));
}
