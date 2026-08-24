# Desktop build resources

Electron packaging resolves this directory on every platform. Product icons and
installer artwork can be added here without changing the packaging manifest.

`image-size-shim` deliberately disables Electron.NET's optional splash-image
decoder. Deckwraith has no splash screen, and the upstream parser currently has
unfixed denial-of-service advisories despite being imported unconditionally.
