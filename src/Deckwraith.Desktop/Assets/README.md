# Desktop build resources

Electron packaging resolves this directory on every platform. Product icons and
installer artwork can be added here without changing the packaging manifest.

`deckwraith-icon-source.png` is the canonical application icon supplied by the
project. The `.png`, `.icns`, and `.ico` siblings are platform packaging assets
derived from that source; there is deliberately no independent SVG design.

`image-size-shim` deliberately disables Electron.NET's optional splash-image
decoder. Deckwraith has no splash screen, and the upstream parser currently has
unfixed denial-of-service advisories despite being imported unconditionally.
