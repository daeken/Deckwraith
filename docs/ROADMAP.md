# Delivery roadmap

The specification's eight vertical milestones are implemented and remain the release spine. Each
ended in an independently usable, end-to-end tested repository; the remaining v1 gate is a live
inside-out bootstrap through the ChatGPT-subscription provider.

## 1. State spine — implemented

Own the durable namespace and history before adding inference.

- Initialize a dedicated, restrictive, Git-backed deck-state repository without remotes.
- Create, resolve, and recovery-safely rename wraiths and haunts using human-readable canonical names only.
- Persist sparse identities—including personality and open calibration—and reserved aliases.
- Append sequenced, hash-checked per-wraith archive events without rewriting prior records.
- Store immutable content-addressed artifacts.
- Checkpoint every coherent public mutation with stable Deckwraith trailers.

Acceptance: an end-to-end test initializes a deck, creates `wraith1` and `deckwraith`, writes an artifact and event, renames both, resolves the old aliases, validates the unchanged archive, and verifies a clean Git worktree and checkpoint history.

## 2. Inference spine — implemented

Make one wraith complete a streamed fake-provider run without coupling durable state to a vendor SDK.

- Add provider-neutral request/event contracts and one OpenAI adapter after a fake adapter proves the lifecycle.
- Model runs and disposable shells with complete started/terminal operation records.
- Materialize and atomically maintain `context.json`.
- Produce deterministic, hash-addressed context manifests.
- Elide complete tool call/result pairs after the configured turn window while retaining raw archive events.

Acceptance: a fake-provider run survives shell replacement, reconstructs exact current context from the archive, and produces byte-stable manifests and pairwise tool elision.

The first concrete adapter is a replaceable ChatGPT-subscription bridge over the supported
Codex app-server protocol. It explicitly selects Codex's built-in `openai` provider, disables
provider-owned tool use at the Deckwraith boundary, and has a live subscription smoke test in
addition to deterministic notification and prompt-projection contract tests.

## 3. PowerShell runtime — implemented

Give each awake wraith an object-native, disposable working environment.

- Host a dedicated full-language PowerShell runspace per awake wraith.
- Add compiled discovery and state commands returning structured objects.
- Persist run-, wraith-, and haunt-scoped canonical values with compare-and-swap.
- Load and safely reload wraith-authored `.ps1` tools.
- Replace lost runspaces cold, explicitly reporting volatile-state loss and never replaying commands.

Acceptance: a runspace-loss test proves ordinary variables disappear, durable values survive, tool assignments refresh, and no prior pipeline executes again.

The runtime uses full-language `InitialSessionState` runspaces, compiled object-native state and
discovery commands, strict portable-value conversion, and atomic candidate-runspace swaps for
authored tool reload. Failed reloads retain the previous known-good command set and emit durable
diagnostics; successful replacement records an explicit no-replay lifecycle event.

## 4. Linear deckbooks — implemented

Turn mutable working context into an executable, Git-readable notebook.

- Persist named, sparsely ordered cells and language-appropriate source files.
- Implement insert, edit, move, rename, pin, and delete with linear suffix invalidation.
- Retain output hashes and execution provenance without erasing stale output.
- Execute one PowerShell cell or an explicit remaining suffix through the kernel contract.
- Compile pinned cells, an active-cell window, and the compact index into bounded model context.

Acceptance: property and end-to-end tests prove edits never execute, suffix staleness is exact, failures stop run-remaining, prior output remains inspectable, and context excludes unrelated large cells.

Deckbooks now persist a small ordered manifest, per-cell metadata and language-appropriate source
siblings, plus immutable hash-addressed output documents. A provider-neutral kernel contract owns
streaming values/errors/interruption; the PowerShell adapter executes against the wraith's hosted
runspace and records version and epoch provenance. Context compilation deterministically includes
pins and a bounded active-cell window while retaining a compact index of excluded cells.

## 5. C# kernel — implemented

Add a second persistent but disposable execution language without weakening the kernel boundary.

- Execute C# script cells through Roslyn with ambient per-wraith submission state.
- Record runtime version and monotonically increasing cold-replacement epochs.
- Expose canonical durable values and content-addressed artifacts through the same host authority as PowerShell.
- Support cooperative interruption and cancellation-terminal execution records.
- Replace a lost C# kernel cold without replay while retaining prior cell outputs.

Acceptance: a mixed PowerShell/C# deckbook exchanges a canonical value and artifact, a C# cell
is interrupted, and cold replacement loses an ordinary C# variable without replaying its producer
or erasing the previous output.

## 6. MCP and model-visible tools — implemented

Turn the existing execution surfaces into a discoverable inside-facing tool universe.

- Persist global and per-wraith MCP/tool assignments with deterministic precedence.
- Discover MCP capabilities and generate structured PowerShell proxy commands plus help.
- Refresh assigned commands only between invocations through controlled runspace replacement.
- Keep the initial model-visible schema to a tiny provider-neutral execution surface.
- Close the inference/tool loop so a wraith can invoke PowerShell and deckbook mutations itself.

Acceptance: a fake MCP server contributes a side-effecting structured command that is absent from
the initial prompt, discoverable through `Get-Command` and `Get-Help`, usable in an object pipeline,
and invoked by an explicit model tool call with complete durable lifecycle records.

The host now owns JSON-RPC stdio MCP processes, host-environment credential references, durable
global/per-wraith assignments and exclusions, deterministic effective catalogs, original schemas,
and stable PowerShell module/function generation. Catalog changes cold-swap the runspace between
invocations. Models receive only `Invoke-PowerShell`; the acceptance model discovers a generated
command through help, explicitly invokes its side effect, preserves nested structure through a
pipeline, and produces paired outer tool and inner MCP archive lifecycles.

## 7. Continuity and recovery — implemented

Make crashes, compaction, and reversal ordinary inspectable state transitions.

- Compact only the oldest contiguous eligible archive prefix with a separately selected model.
- Reconcile `context.json`, runs, shells, tool calls, and cell executions from archives at startup.
- Mark abandoned side effects `outcome-unknown` without blind replay.
- Add crash injection around durable writes and checkpoint boundaries.
- Expose non-destructive Git-backed reversal that preserves the history being reversed.

Acceptance: a crash after a side-effecting start record is recovered as outcome-unknown, exact
current context is rebuilt, an oldest-prefix summary covers no gaps or overlaps, the deckbook is
unchanged, and a bad mutable-state checkpoint is reversed by a new commit.

Compaction now validates exact content-hash coverage of only the oldest eligible contiguous
archive prefix, uses an independently selected provider/model, and replaces only covered current
context while retaining the raw archive and deckbook byte-for-byte. Startup recovery reconciles
orphaned operation lifecycles without replay, rebuilds stale or corrupt projections, and cold-rolls
interrupted shells. Non-destructive reversal creates a recovery branch and an inverse checkpoint;
all three operations are available through the headless host.

## 8. Product shell and release — implemented

Ship the inspectable desktop product without making the renderer an authority boundary.

- Host the application runtime behind a versioned command/query/event bridge.
- Add the Electron/React identity, run, deckbook, archive, and checkpoint inspector/editor.
- Add Anthropic, Google, and OpenAI-compatible adapters behind shared provider contracts.
- Prove renderer reconnect/schema compatibility, cross-platform packaging, and headless parity.
- Exercise the complete architecture acceptance scenario and inside-out bootstrap flow.

Acceptance: the packaged shell creates and resumes a wraith, observes live model and cell events,
edits a deckbook through host commands, survives renderer reconnect, changes provider without
changing identity, and the same lifecycle remains green through the headless host on Linux.

The .NET host now exposes a versioned loopback-only command/query/event protocol with buffered
event replay, gap detection, and idempotent mutation request IDs. The Electron renderer covers
onboarding, identities (including personality and open calibration), runs and shell epochs,
deckbooks, archives, checkpoints, and reversible wraith archival. Electron runs with context
isolation, renderer sandboxing, Node integration disabled, a restrictive CSP, and no durable-state
authority.

Concrete Anthropic, Gemini, OpenAI-compatible Responses, and ChatGPT-subscription adapters remain
behind the shared provider contracts. CI proves the headless graph on Linux, renderer/protocol
compatibility, and self-contained desktop publishes on macOS, Linux, and Windows. Tag builds create
native Electron bundles; dependency audits and packaged macOS ZIP/DMG acceptance are green.

## Package boundaries

The implemented assembly boundaries are:

```text
Deckwraith.Desktop / Deckwraith.Headless (composition roots)
    └── Deckwraith.Hosting (versioned commands, queries, snapshots, events)
        └── Deckwraith.Application (use cases and ports)
            └── Deckwraith.Core (domain values and invariants)

Infrastructure beside Core/Application:
    Persistence       JSON/JSONL, durable values, artifacts, Git
    Providers.*       provider contracts and concrete adapters
    PowerShell        runspaces, compiled commands, authored tools
    Notebooks         cells, ordering, execution, bounded context
    Kernels.*         language-neutral contract, PowerShell, C#/Roslyn
    Mcp               stdio clients, assignments, catalogs
    Continuity        compaction, reconciliation, Git reversal
```

Desktop, concrete providers, kernels, persistence, and process-launching details never enter the
domain model. Tests mirror these boundaries, with focused contract suites and integration tests
owning complete vertical scenarios. `eng/verify-headless.sh` fails if Electron or Chromium enters
the portable headless publish graph.

## Decisions made for milestone 1

- A deck owns one dedicated state repository. Per-haunt worktrees remain an optional later deployment topology.
- Canonical names are 1–63 lowercase ASCII letters, digits, and interior hyphens. Input is case-folded; paths, dots, whitespace, Windows device names, and reused aliases are rejected.
- Alias maps live in `deck.json`; old archive envelopes are not rewritten after rename.
- Multi-file renames use a durable intent record and idempotent completion. Recovery completes a prepared rename rather than pretending filesystem operations were atomic.
- Artifacts are scoped to a haunt and keyed by the SHA-256 of their bytes.
