# Delivery roadmap

The specification's eight vertical milestones remain the release spine. The first four are the immediate build plan; each ends in an independently usable, end-to-end tested repository.

## 1. State spine — implemented

Own the durable namespace and history before adding inference.

- Initialize a dedicated, restrictive, Git-backed deck-state repository without remotes.
- Create, resolve, and recovery-safely rename wraiths and haunts using human-readable canonical names only.
- Persist sparse identities—including personality and open calibration—and reserved aliases.
- Append sequenced, hash-checked per-wraith archive events without rewriting prior records.
- Store immutable content-addressed artifacts.
- Checkpoint every coherent public mutation with stable Deckwraith trailers.

Acceptance: an end-to-end test initializes a deck, creates `wraith1` and `deckwraith`, writes an artifact and event, renames both, resolves the old aliases, validates the unchanged archive, and verifies a clean Git worktree and checkpoint history.

## 2. Inference spine

Make one wraith complete a streamed fake-provider run without coupling durable state to a vendor SDK.

- Add provider-neutral request/event contracts and one OpenAI adapter after a fake adapter proves the lifecycle.
- Model runs and disposable shells with complete started/terminal operation records.
- Materialize and atomically maintain `context.json`.
- Produce deterministic, hash-addressed context manifests.
- Elide complete tool call/result pairs after the configured turn window while retaining raw archive events.

Acceptance: a fake-provider run survives shell replacement, reconstructs exact current context from the archive, and produces byte-stable manifests and pairwise tool elision.

## 3. PowerShell runtime

Give each awake wraith an object-native, disposable working environment.

- Host a dedicated full-language PowerShell runspace per awake wraith.
- Add compiled discovery and state commands returning structured objects.
- Persist run-, wraith-, and haunt-scoped canonical values with compare-and-swap.
- Load and safely reload wraith-authored `.ps1` tools.
- Replace lost runspaces cold, explicitly reporting volatile-state loss and never replaying commands.

Acceptance: a runspace-loss test proves ordinary variables disappear, durable values survive, tool assignments refresh, and no prior pipeline executes again.

## 4. Linear deckbooks

Turn mutable working context into an executable, Git-readable notebook.

- Persist named, sparsely ordered cells and language-appropriate source files.
- Implement insert, edit, move, rename, pin, and delete with linear suffix invalidation.
- Retain output hashes and execution provenance without erasing stale output.
- Execute one PowerShell cell or an explicit remaining suffix through the kernel contract.
- Compile pinned cells, an active-cell window, and the compact index into bounded model context.

Acceptance: property and end-to-end tests prove edits never execute, suffix staleness is exact, failures stop run-remaining, prior output remains inspectable, and context excludes unrelated large cells.

Milestones 5–8 then add the C# kernel, MCP discovery, compaction/recovery hardening, and the Electron/React product shell plus the remaining providers.

## Package boundaries

The repository grows assemblies only when a milestone needs them:

```text
Deckwraith.Headless (composition root)
    └── Deckwraith.Application (use cases and ports)
            ├── Deckwraith.Core (domain values and invariants)
            └── Deckwraith.Persistence (JSON/JSONL, artifacts, Git)
                    └── Deckwraith.Core
```

Milestones 2–4 add `Providers.Abstractions`, `Providers.OpenAI`, `PowerShell`, `Notebooks`, `Kernels.Abstractions`, and `Kernels.PowerShell` beside—not inside—Core. Desktop, concrete providers, kernels, and process-launching details never enter the domain model. Tests mirror those boundaries, with integration tests owning complete vertical scenarios.

## Decisions made for milestone 1

- A deck owns one dedicated state repository. Per-haunt worktrees remain an optional later deployment topology.
- Canonical names are 1–63 lowercase ASCII letters, digits, and interior hyphens. Input is case-folded; paths, dots, whitespace, Windows device names, and reused aliases are rejected.
- Alias maps live in `deck.json`; old archive envelopes are not rewritten after rename.
- Multi-file renames use a durable intent record and idempotent completion. Recovery completes a prepared rename rather than pretending filesystem operations were atomic.
- Artifacts are scoped to a haunt and keyed by the SHA-256 of their bytes.
