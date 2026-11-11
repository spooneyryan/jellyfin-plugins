// Takes sandbox session tokens and API keys out of text a script prints or saves (webcheck.mjs's result: jellyfin-web's
// console lines and failed request URLs carry them, such as the WebSocket's `?api_key=`). Only values named as a token
// or key are replaced; ids (items, users, devices: 32 hex digits too) are kept, so results stay readable.
//
// Works on plain text and on JSON text alike: a quote may be escaped (`\"`), since a console line inside a result is a
// JSON string.

export const REDACTED = 'REDACTED';

const RULES = [
    // Query parameters: ?api_key=…, &ApiKey=…, access_token=…
    [/([?&;]\s*(?:api_?key|access_?token)=)([^&\s"'\\#]+)/gi, `$1${REDACTED}`],
    // Headers written out: X-Emby-Token: …, X-MediaBrowser-Token=…
    [/(X-(?:Emby|MediaBrowser)-Token\\?"?\s*[:=]\s*\\?"?)([A-Za-z0-9._~+/=-]+)/gi, `$1${REDACTED}`],
    // The MediaBrowser authorization header's Token="…" (also Token=\"…\" inside a JSON string).
    [/(\bToken=\\?")([^"\\]+)/g, `$1${REDACTED}`],
    // JSON properties: "AccessToken": "…", "api_key": "…" (also escaped inside a JSON string).
    [/(\\?"(?:access_?token|api_?key)\\?"\s*:\s*\\?")([^"\\]+)/gi, `$1${REDACTED}`],
];

export function redactSecrets(text) {
    if (typeof text !== 'string' || text.length === 0) return text;
    let out = text;
    for (const [pattern, replacement] of RULES) out = out.replace(pattern, replacement);
    return out;
}
