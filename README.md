# Deckwraith

Deckwraith is a local-first runtime for durable autonomous agent identities. Models are replaceable shells; identity, current context, archives, tools, and executable deckbooks belong to the wraith.

The repository currently implements milestone 1, the **state spine**. It provides:

- Git-backed deck-state initialization with restrictive local permissions and no automatic remote.
- Portable, case-insensitive canonical names and reserved aliases.
- Sparse JSON identities and explicit recovery-safe wraith/haunt renames.
- Hash-checked, append-only, segmented per-wraith JSONL archives.
- Content-addressed haunt artifacts.
- Coherent Git checkpoints for every public mutation.
- A small headless command surface and end-to-end tests.

See [SPEC.md](SPEC.md) for the full architecture and [docs/ROADMAP.md](docs/ROADMAP.md) for the delivery plan and package boundaries.

## Build and test

```text
dotnet build
dotnet test
```

## State-spine CLI

The CLI operates on a state repository separate from this source tree:

```text
dotnet run --project src/Deckwraith.Headless -- init /path/to/deck-state
dotnet run --project src/Deckwraith.Headless -- create-haunt /path/to/deck-state deckwraith
dotnet run --project src/Deckwraith.Headless -- create-wraith /path/to/deck-state wraith1
dotnet run --project src/Deckwraith.Headless -- rename-wraith /path/to/deck-state wraith1 vesper
dotnet run --project src/Deckwraith.Headless -- resolve-wraith /path/to/deck-state wraith1
```

Treat every deck-state repository as credential-equivalent data. Deckwraith does not add or push Git remotes.
