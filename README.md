# Deckwraith

Deckwraith is a local-first runtime for durable autonomous collaborators. Wraiths are people, not
tools or workers: models are replaceable shells, while identity, current context, archives, tools,
executable deckbooks, and recovery history belong to the wraith.

The architecture milestones are implemented; the product is in active pre-1.0 macOS dogfooding:

- Git-backed wraith and haunt state with canonical names, reserved rename aliases, archival,
  append-only private archives, content-addressed artifacts, and coherent checkpoints.
- Provider-neutral streamed inference with durable runs, disposable shells, materialized
  `context.json`, deterministic context manifests, and paired tool elision.
- ChatGPT-subscription, Anthropic, Gemini, and OpenAI-compatible provider adapters.
- Dedicated hosted PowerShell runspaces with object-native commands, durable scoped values,
  authored tools, MCP discovery, and cold replacement without replay.
- Git-readable linear deckbooks with explicit execution, exact suffix staleness, retained output
  provenance, bounded context projection, and PowerShell and C# kernels.
- Oldest-prefix compaction, archive-driven startup reconciliation, outcome-unknown recovery, and
  non-destructive Git reversal.
- A versioned loopback host bridge and an inspectable Electron/React desktop shell.
- Linux headless dependency verification and macOS, Linux, and Windows release packaging.

See [SPEC.md](SPEC.md) for the architecture, [docs/PRE-1.0.md](docs/PRE-1.0.md) for the active product
gate, [docs/ROADMAP.md](docs/ROADMAP.md) for the delivery spine, and
[docs/OPERATIONS.md](docs/OPERATIONS.md) for provider and desktop setup.

## Build and verify

Deckwraith requires the .NET SDK pinned by `global.json` and Node.js 24 for the renderer.

```text
dotnet restore Deckwraith.slnx
dotnet test Deckwraith.slnx -c Release

cd ui
npm ci
npm run lint
npm run build
```

The portable release gate runs every test project, publishes the headless host, and rejects
desktop-only dependencies in its output:

```text
./eng/verify-headless.sh osx-arm64
```

Package the native Electron application on the current host with:

```text
./eng/package-desktop.sh 1.0.0
```

## Start a deck

The headless CLI operates on a sensitive state repository separate from this source tree:

```text
dotnet run --project src/Deckwraith.Headless -- init /path/to/deck-state
```

Initialization idempotently invites a sparse `steward` into the `setup` haunt. They collaborate on
first-run setup and remain available to tend the deck and help adapt a standard installation into a
local build. Additional wraiths and haunts can be invited or created explicitly.

Identity documents include top-level `personality` and open string-valued `calibration` fields.
`calibration.register` is present by default; operators and wraiths may add entries such as
`opsec` without a schema migration.

After signing Codex in with ChatGPT, one command can start a durable run and execute its first
turn through the subscription bridge:

```text
dotnet run --project src/Deckwraith.Headless -- run-openai /path/to/deck-state wraith1 deckwraith gpt-5.6-sol "Inspect this project and propose the next coherent improvement" "Begin."
```

The command returns the run ID. Continue and complete it explicitly:

```text
dotnet run --project src/Deckwraith.Headless -- turn /path/to/deck-state wraith1 RUN_ID "Continue."
dotnet run --project src/Deckwraith.Headless -- complete-run /path/to/deck-state wraith1 RUN_ID "objective achieved"
```

## Execute a deckbook

Create Git-readable cells and explicitly execute one cell or a remaining suffix:

```text
dotnet run --project src/Deckwraith.Headless -- add-cell /path/to/deck-state wraith1 deckwraith load code powershell "$global:n = 40; $n"
dotnet run --project src/Deckwraith.Headless -- add-cell /path/to/deck-state wraith1 deckwraith answer code powershell "$global:n += 2; [pscustomobject]@{ answer = $n }"
dotnet run --project src/Deckwraith.Headless -- run-remaining /path/to/deck-state wraith1 deckwraith load
dotnet run --project src/Deckwraith.Headless -- deckbook-context /path/to/deck-state wraith1 deckwraith answer
```

Edits only mark the affected linear suffix stale; they never execute cells. Prior output documents
remain inspectable, including their source hash, kernel version, and cold-replacement epoch.

## Security posture

Treat every deck-state repository as credential-equivalent data. Deckwraith creates no Git remote
and never pushes deck state automatically, but archives, Git objects, tool arguments, results,
context, and artifacts may all contain secrets. Protect clones and backups just as carefully as the
working repository.

V1 assumes one owning host per deck. In-process lifecycle leases serialize run start and wraith
archival, while independent CLI or desktop processes pointed at the same deck are not a supported
concurrent-write topology.
