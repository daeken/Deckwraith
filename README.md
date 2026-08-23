# Deckwraith

Deckwraith is a local-first runtime for durable autonomous agent identities. Models are replaceable shells; identity, current context, archives, tools, and executable deckbooks belong to the wraith.

The repository currently implements the first four milestones: the **state spine**,
the **inference spine**, the **PowerShell runtime**, and **linear deckbooks**. It provides:

- Git-backed deck-state initialization with restrictive local permissions and no automatic remote.
- Portable, case-insensitive canonical names and reserved aliases.
- Sparse JSON identities and explicit recovery-safe wraith/haunt renames.
- Hash-checked, append-only, segmented per-wraith JSONL archives.
- Content-addressed haunt artifacts.
- Coherent Git checkpoints for every public mutation.
- Provider-neutral streaming requests and events, persistent runs and disposable shells.
- A Git-backed materialized `context.json`, deterministic context manifests, and pairwise tool elision.
- A fake-provider end-to-end lifecycle and a replaceable ChatGPT-subscription bridge through
  the supported Codex app-server protocol.
- Dedicated full-language hosted PowerShell runspaces with object-native compiled commands.
- Explicit run-, wraith-, and haunt-scoped non-volatile values with content hashes and CAS.
- Cold runspace replacement without replay and atomic reload of wraith-authored `.ps1` tools.
- Git-readable named deckbook cells with sparse total ordering and stable rename aliases.
- Exact linear-suffix staleness for insert, edit, move, delete, and rerun operations.
- Immutable hash-addressed cell outputs, kernel/version/epoch provenance, and explicit
  run-cell/run-remaining execution through a language-neutral kernel contract.
- Bounded model-context projections containing pins, the active-cell window, current outputs,
  and a compact index rather than the entire deckbook.
- A small headless command surface and end-to-end tests.

See [SPEC.md](SPEC.md) for the full architecture and [docs/ROADMAP.md](docs/ROADMAP.md) for the delivery plan and package boundaries.

## Build and test

```text
dotnet build Deckwraith.slnx
dotnet test Deckwraith.slnx
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

## Subscription-backed inference

Sign in to Codex with ChatGPT as described in the
[official OpenAI authentication documentation](https://developers.openai.com/codex/auth),
then start and continue a durable run:

```text
dotnet run --project src/Deckwraith.Headless -- start-run /path/to/deck-state wraith1 deckwraith gpt-5.6-terra "Implement the next change"
dotnet run --project src/Deckwraith.Headless -- turn /path/to/deck-state wraith1 RUN_ID "Begin."
```

For a one-shot smoke test, `run-openai` combines those operations. Deckwraith launches
[`codex app-server`](https://developers.openai.com/codex/app-server) with Codex's reserved
built-in `openai` provider, injects the complete durable identity and current context, and
keeps tool execution outside the adapter. Set `DECKWRAITH_CODEX_PATH` when `codex` is not on
`PATH` and the ChatGPT desktop-bundled executable is unavailable.

## Hosted PowerShell

The headless host can execute one structured PowerShell invocation for a wraith:

```text
dotnet run --project src/Deckwraith.Headless -- powershell /path/to/deck-state wraith1 - deckwraith "Get-DwRuntime"
```

Hosted sessions expose `Get-DwState`, `Set-DwState`, `Remove-DwState`, `Get-DwRuntime`,
`Get-DwTool`, and `Reload-DwTools`. Library hosts retain one runspace per awake wraith;
the one-shot CLI intentionally disposes its runspace when the process exits. Ordinary variables
are volatile. Values written through the state commands survive cold replacement and process loss.

## Linear deckbooks

Create Git-readable cells and explicitly execute one cell or a remaining suffix:

```text
dotnet run --project src/Deckwraith.Headless -- add-cell /path/to/deck-state wraith1 deckwraith load code powershell "$global:n = 40; $n"
dotnet run --project src/Deckwraith.Headless -- add-cell /path/to/deck-state wraith1 deckwraith answer code powershell "$global:n += 2; [pscustomobject]@{ answer = $n }"
dotnet run --project src/Deckwraith.Headless -- run-remaining /path/to/deck-state wraith1 deckwraith load
dotnet run --project src/Deckwraith.Headless -- deckbook-context /path/to/deck-state wraith1 deckwraith answer
```

`Deckwraith.Notebooks` also exposes insert, edit, move, rename, pin, delete, run-cell,
run-remaining, and bounded-context APIs. Milestone 4 supplies the PowerShell cell kernel;
the C# kernel remains milestone 5.

Treat every deck-state repository as credential-equivalent data. Deckwraith does not add or push Git remotes.
