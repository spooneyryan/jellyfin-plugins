# Synthetic web client files

`index.html` is a hand-written page shaped like jellyfin-web 10.11.5's `index.html`: everything in one `<head>` line,
then `</head><body>`, the stale `CustomRatings` tag the real file carries and
`</body></html>` at the end, plus multi-byte text so byte counts differ from character counts. No jellyfin-web code is
copied (it is GPL). `WebHostHarness` (`kit/testsupport/Web`) serves it as the web root's `index.html`; tests derive the
other variants (no `</head>`, no `</body>`, upper-case tags, over 1 MB) from it.
