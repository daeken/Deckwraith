# Deckwraith Architecture Specification

Status: Proposed architecture for v1
Last updated: 2026-08-23

## 1. Purpose

Deckwraith is a local-first runtime for persistent, autonomous agent identities. A wraith is not a model session: it is a durable identity, history, tool environment, and working disposition that may be instantiated through different model providers over time. Individual inference contexts are disposable shells; the wraith persists.

Deckwraith optimizes for continuity, agency, inspectability, and recovery. It should let an agent pursue a goal to completion with broad autonomy, preserve what happened in an auditable form, and make mistakes cheap to reverse rather than attempting to prevent every mistake in advance.

Each wraith also owns an executable notebook: a durable, editable arrangement of context, code, results, and artifacts. The notebook is the wraith's working program, while the append-only archive remains the historical record and language kernels remain disposable execution machinery.

Deckwraith uses:

- C# and .NET 10 for the portable core and application runtime.
- Hosted PowerShell runspaces as the primary agent-native execution environment.
- First-class executable deckbooks with PowerShell and C# kernels.
- Electron.NET as the desktop shell.
- React, TypeScript, Tailwind CSS, and Radix UI for the renderer.
- Extension points for future kernels and execution backends without including them in v1.

The desktop application is the first host, not the architecture. `Deckwraith.Core` must remain usable by a future headless Linux service without Electron, Chromium, or desktop-only dependencies.

## 2. Product principles

### 2.1 Identity is durable; inference is replaceable

A wraith has a persistent identity independent of any provider, model family, conversation identifier, or context window. Changing models does not create a new wraith. Forking an identity does.

Every model invocation receives the current identity document. The agent normally owns and edits its own identity. Deckwraith may validate, migrate, or recover that document, but must not silently rewrite its semantic content.

### 2.2 Default to completion

Agents should continue working until their assigned outcome is achieved, genuinely blocked, or explicitly stopped. They may inspect state, write files, create tools, invoke assigned integrations, checkpoint work, recover from ordinary errors, and revise their approach without asking for permission at every step.

Policy gates should be reserved for actions whose consequences are materially external, irreversible, expensive, or outside the granted scope. A confirmation prompt is not a substitute for a recoverable design.

### 2.3 Reversibility over restriction

Deckwraith state is Git-backed and checkpointed frequently. Coherent state transitions should be easy to inspect, diff, branch, and undo. Recovery creates new history or a recovery branch; routine rollback must not erase the history that explains what happened.

Git is not a transaction manager and does not make external side effects reversible. Deckwraith must separately journal tool invocations and surface uncertain external outcomes after a crash.

The state repository is a highly sensitive execution record. Commands, arguments, model context, tool results, diffs, and historical Git objects may contain credentials or other secrets even when Deckwraith applies best-effort redaction. The repository and every backup or clone must be protected accordingly.

### 2.4 Object-native tools

PowerShell is the native orchestration and scripting language. Core tools and MCP operations appear in the agent's runspace as idiomatic commands that accept and return .NET objects or `PSObject` values. Structured values should remain structured across pipelines.

The model should not need every tool schema in its prompt. It should receive a very small execution surface and discover the larger command universe at runtime through `Get-Command`, `Get-Help`, and Deckwraith-specific discovery commands.

### 2.5 Local-first, observable, and portable

Durable state uses transparent files and Git wherever practical. The .NET core owns truth; the desktop renderer is a projection of it. OS, UI, provider, credential-store, and process-launching concerns sit behind explicit interfaces.

### 2.6 Executable context

An agent's working context should be inspectable and editable as a notebook rather than existing only as an ever-growing transcript. Cells may contain durable context, executable code, model prompts, queries, outputs, or artifact references. Agents can insert, edit, or delete cells at any position, invalidating the affected linear suffix, then explicitly run one cell or the remaining cells in order.

Notebook execution supplements ordinary commands; it does not replace them. Agents remain free to use PowerShell interactively for immediate work and promote only valuable, reusable, or explanatory work into cells.

## 3. Terminology

- **Deck**: a Deckwraith installation and its local control plane.
- **Wraith**: a persistent agent identity and its durable state.
- **Shell**: one disposable model execution context inhabited by a wraith.
- **Haunt**: a workspace or project in which one or more wraiths operate.
- **Deckbook**: a wraith-owned executable notebook forming its mutable working context within a haunt.
- **Cell**: an ordered, addressable unit of context, code, computation, or output in a deckbook.
- **Kernel**: a disposable language execution service for notebook cells. V1 kernels are PowerShell and C#.
- **Run**: a goal-directed execution, potentially spanning multiple model calls, tool invocations, compactions, and restarts.
- **Current context**: the materialized, provider-neutral ordered context carried forward for a wraith's next model invocation, stored in `context.json`.
- **Archive**: a wraith's append-only record of messages, actions, tool activity, identity changes, and lifecycle events.
- **Checkpoint**: a Git commit representing a coherent Deckwraith state transition.
- **Compaction**: a derived summary of the oldest contiguous uncompacted portion of a context history.

### 3.1 Human-readable identity

Wraiths and haunts use names everywhere a human or model must reason about them. Examples are `wraith1`, `wraith2`, `deckwraith`, and `compiler-lab`; there is no parallel UUID hidden behind those names.

Canonical names use a portable, case-insensitive slug form so they remain safe as path components and unambiguous in PowerShell, Git trailers, JSON references, and model context. A friendly display label may preserve capitalization or spacing, but it is not another identity. Names are unique within a deck.

Renaming is an explicit repository-wide, recovery-safe migration: Deckwraith updates mutable references as one journaled operation, preserves old names in append-only events, records the rename in the archive and Git history, and reserves the former name as an alias so old events and commands continue to resolve. Aliases cannot be reassigned silently. A fork must choose a new canonical name.

## 4. Architectural invariants

The implementation must preserve these invariants:

1. A wraith's canonical name is its identifier; a haunt's canonical project name is its identifier. Neither has a separate UUID or opaque internal ID.
2. The current `identity.json` is injected into every model context for that wraith.
3. Identity semantic changes are normally authored or explicitly accepted by that wraith. The file edit and its meaningful Git checkpoint message are the change record; identity does not duplicate that history internally.
4. Committed archive records are append-only. Recovery may remove an incomplete trailing write, but must never alter a previously valid record.
5. Normal context assembly, retrieval, and agent-scoped APIs do not surface one wraith's behavioral archive to another unless cross-agent scope is explicitly requested.
6. Compaction covers only the oldest contiguous eligible prefix. It never selects convenient messages from the middle of history.
7. Compaction is derived state. The raw archive remains authoritative and is never replaced by a summary.
8. Provider-specific types and SDK objects do not enter the domain model, persistence format, PowerShell public surface, or UI protocol.
9. The renderer cannot directly mutate durable state, access provider credentials, or invoke tools. It sends commands to the .NET host and consumes snapshots and events.
10. PowerShell command visibility is an ergonomics and context-budget mechanism, not a security boundary. Agents are trusted with the authority of the Deckwraith process.
11. A runspace is disposable and potentially lossy. Deckwraith never replays prior commands to reconstruct one; values that must survive runspace loss are explicitly written to the non-volatile state store.
12. A Git checkpoint is created only from a coherent state boundary. Streaming fragments need not produce one commit each.
13. The Git-backed state repository, its object database, backups, clones, and diagnostic exports are treated as highly sensitive because recorded commands, context, outputs, and prior revisions may contain secrets. Deckwraith never automatically publishes or pushes them.
14. Every side-effecting operation has a stable operation ID and a lifecycle recorded in the invoking agent's archive, including complete inputs and terminal outputs or artifact references, sufficient to distinguish not-started, in-progress, completed, failed, cancelled, and outcome-unknown states.
15. `Deckwraith.Core` has no dependency on Electron, React, browser APIs, or desktop-only frameworks.
16. A deckbook is mutable working state, while its execution events remain append-only in the archive. Editing a cell never rewrites what previously happened.
17. Kernels and their in-memory state are disposable. Deckwraith never automatically replays cells or commands merely to reconstruct a lost kernel.
18. Deckbooks have one total order and no dependency graph. Inserting, editing, deleting, moving, or rerunning a cell invalidates the affected linear suffix; invalidation never executes cells automatically.
19. Every wraith has a Git-backed `context.json` containing its materialized current provider-neutral context. The archive remains complete history; `context.json` is the mutable context actually carried forward.
20. Completed tool call/result pairs are elided from current context together after a configurable number of completed model turns. Elision never removes or rewrites their archive records.

## 5. High-level architecture

```text
┌───────────────────────────────────────────────────────────────┐
│ Deckwraith.Desktop                                            │
│                                                               │
│  Electron.NET host             Chromium renderer              │
│  ┌──────────────────────┐      ┌────────────────────────────┐ │
│  │ .NET composition root│◀────▶│ React / TypeScript         │ │
│  │ versioned IPC bridge │      │ Tailwind / Radix UI        │ │
│  └──────────┬───────────┘      └────────────────────────────┘ │
└─────────────┼─────────────────────────────────────────────────┘
              │ commands, snapshots, events
              ▼
┌───────────────────────────────────────────────────────────────┐
│ Deckwraith application runtime                               │
│                                                               │
│ Agents and runs │ Deckbooks │ Context │ Compaction │ Policy  │
│ Archives │ Git │ Recovery │ Events │ Scheduling │ Credentials │
└───────┬────────────────┬──────────────────────┬───────────────┘
        │                │                      │
        ▼                ▼                      ▼
┌──────────────┐  ┌──────────────────┐  ┌─────────────────────┐
│ Model        │  │ Cell kernels     │  │ Persistent state    │
│ providers    │  │                  │  │                     │
│ OpenAI       │  │ PowerShell       │  │ JSON / JSONL        │
│ Anthropic    │  │ C# / Roslyn      │  │ cells and outputs   │
│ Gemini       │  │                  │  │ Git checkpoints     │
│ compatible   │  │ future adapters  │  │ derived indexes     │
│ future       │  │                  │  │ secret references   │
└──────────────┘  └────────┬─────────┘  └─────────────────────┘
                           │
                  ┌────────┴─────────┐
                  │ Commands/tools  │
                  │ core cmdlets    │
                  │ MCP adapters    │
                  │ authored tools  │
                  └─────────────────┘
```

The same application runtime must also be hostable as:

```text
Deckwraith.Headless
        │
        └── Deckwraith application runtime ── identical providers,
                                              deckbooks, kernels, tools,
                                              and state
```

The desktop and future headless host are composition roots. Neither owns domain rules.

## 6. Proposed solution and project layout

```text
Deckwraith.slnx
global.json
Directory.Build.props
Directory.Packages.props
DECKWRAITH_SPEC.md
README.md

src/
  Deckwraith.Core/
    Agents/
    Runs/
    Context/
    Compaction/
    Tools/
    Policy/
    Events/
    Abstractions/

  Deckwraith.Application/
    Commands/
    Queries/
    Scheduling/
    Recovery/
    Hosting/

  Deckwraith.Persistence/
    Json/
    Archives/
    Git/
    Indexes/

  Deckwraith.PowerShell/
    Hosting/
    Cmdlets/
    Discovery/
    Serialization/

  Deckwraith.Notebooks/
    Cells/
    Ordering/
    Execution/
    Context/

  Deckwraith.Kernels.Abstractions/
  Deckwraith.Kernels.PowerShell/
  Deckwraith.Kernels.CSharp/

  Deckwraith.Mcp/
    Client/
    Catalog/
    PowerShell/

  Deckwraith.Providers.Abstractions/
  Deckwraith.Providers.OpenAI/
  Deckwraith.Providers.Anthropic/
  Deckwraith.Providers.Google/
  Deckwraith.Providers.OpenAICompatible/

  Deckwraith.Desktop/
    Ipc/
    Projections/
    Program.cs

  Deckwraith.Headless/
    Program.cs

ui/
  src/
    components/
    features/
    ipc/
    state/
  package.json

tests/
  Deckwraith.Core.Tests/
  Deckwraith.Persistence.Tests/
  Deckwraith.Notebooks.Tests/
  Deckwraith.Kernels.ContractTests/
  Deckwraith.PowerShell.Tests/
  Deckwraith.Providers.ContractTests/
  Deckwraith.IntegrationTests/
```

All .NET projects target `net10.0`. `global.json` pins the repository's .NET 10 SDK feature band so local development and CI use the same compiler/runtime toolchain.

The exact number of assemblies may be collapsed during v1 implementation, but dependency direction must remain clear:

```text
Desktop / Headless
        ↓
Application
        ↓
Core ← provider abstractions
  ↑
  Persistence / Notebooks / Kernels / PowerShell / MCP / concrete providers
```

`Core` contains domain types and policies. `Application` coordinates use cases and ports. Infrastructure projects implement those ports. No provider package may be required to test identity, archive, compaction, or run-lifecycle behavior.

## 7. Durable state layout

A deck state directory is itself a Git repository or a dedicated Git worktree. It is separate from the Deckwraith source repository and may be placed wherever the operator chooses.

```text
deck-state/
  .git/
  deck.json
  policy.json

  agents/
    <agent-name>/
      identity.json
      agent.json
      context.json
      tools.json
      tools/
        *.ps1
      state/
        values/
          <encoded-key>.json
      deckbooks/
        <haunt-name>/
          deckbook.json
          cells/
            <cell-name>/
              cell.json
              source.<language-extension>
          outputs/
            <output-hash>
      archive/
        000001.jsonl
        000002.jsonl
      compactions/
        <compaction-id>.json
      runs/
        <run-id>/
          run.json
          state/
            values/
              <encoded-key>.json
      projections/
        archive-index.json

  haunts/
    <project-name>/
      haunt.json
      state/
        values/
          <encoded-key>.json
      context/
      artifacts/
      tasks/

  tools/
    global.json
    powershell/

  mcp/
    servers.json
    assignments.json

  recovery/
    incidents/
```

Generated indexes and projections are disposable and rebuildable. Whether they are committed should be configurable; the authoritative JSON, JSONL, scripts, and compaction documents are committed.

Long-lived credentials should be sourced from an OS credential store or explicitly configured secret provider, with opaque references in ordinary configuration where practical. This reduces needless duplication but does not make the repository secret-free: credentials can still appear transitively in commands, environment captures, model messages, tool results, patches, artifacts, and Git history.

### 7.1 Repository sensitivity

The entire state repository is credential-equivalent data. Deckwraith must assume it contains secrets from the first run onward, even if no known secret is currently visible in the working tree.

- Initialize it with restrictive local filesystem permissions where supported.
- Do not create, configure, fetch, or push Git remotes automatically.
- Require an explicit operator action and a prominent sensitivity warning before adding a remote, exporting an archive, creating a shareable bundle, or attaching state to a report.
- Treat clones, worktrees, Git object databases, reflogs, recovery refs, temporary files, crash dumps, and backups as equally sensitive.
- Make backup encryption and at-rest volume encryption strongly recommended deployment guidance.
- Never claim that redaction, deleting a file, or removing a value from `HEAD` removes it from Git history.

Redaction is defense in depth against accidental display and routine log leakage. It is not a repository sanitization guarantee. If the operator needs a distributable diagnostic bundle, Deckwraith should create a new derived export with an explicit review step rather than exposing the repository itself.

### 7.2 Identity document

`identity.json` is human-readable, versioned JSON. A minimal shape is:

```json
{
  "schemaVersion": 2,
  "name": "wraith1",
  "personality": "",
  "calibration": {
    "register": ""
  },
  "pronouns": [],
  "selfDescription": "",
  "knownTendencies": [],
  "openQuestions": [],
  "updatedAt": "2026-08-23T20:15:00Z"
}
```

`personality` is the wraith's broad account of who it is as a person rather than a narrow style prompt. `calibration` is an open string dictionary for operational self-calibration, normally including at least `register` for how the wraith communicates and optionally entries covering opsec, disclosure boundaries, uncertainty, risk posture, or other durable behavioral adjustments. Deckwraith assigns no closed vocabulary to calibration keys.

`knownTendencies` and `openQuestions` are intentionally arrays of strings. Deckwraith assigns no sub-schema or workflow semantics to their contents; the wraith decides what each entry means and how to phrase or maintain it.

The initial `wraith1` identity is deliberately sparse. `wraith1` chooses its own display identity, pronouns, description, tendencies, questions, and eventually its canonical name through normal self-editing and rename mechanisms.

Identity history comes from Git. An identity edit receives a meaningful checkpoint message and remains inspectable through ordinary diffs and blame; Deckwraith does not duplicate that history inside model-visible identity JSON. It may emit a lightweight archive event containing before/after hashes and the checkpoint ID for correlation.

Normal writes require the active wraith's authority. Operator edits are allowed and must be attributed as operator-authored. Core migrations may change schema representation without claiming that the wraith changed its self-conception. Invalid updates are rejected without replacing the last valid identity.

### 7.3 Current context document

`context.json` stores the wraith's current provider-neutral conversational context as materialized ordered items, not merely archive cursors or a retrieval recipe. It contains retained user/model messages, tool call/result pairs, compaction summary items, and lightweight elision markers, plus the archive frontier and policy counters needed for reconciliation.

Identity, the current objective, deckbook projection, haunt context, and minimal tool catalog remain dynamically injected components because their exact representation may depend on the selected provider and token budget. `context.json` records their current content hashes and selection metadata; each provider invocation records the final assembled `ContextManifest` in the archive so the exact request remains explainable.

A minimal shape is:

```json
{
  "schemaVersion": 1,
  "agent": "wraith1",
  "revision": 42,
  "turn": 17,
  "archiveFrontier": 184,
  "identityHash": "sha256:...",
  "deckbookRevision": 9,
  "toolElisionTurns": 8,
  "items": [
    {
      "kind": "message",
      "role": "user",
      "content": [{ "type": "text", "text": "Inspect the parser." }]
    },
    {
      "kind": "tool-interaction",
      "operationId": "01J...",
      "tool": "Get-DwSourceContext",
      "completedAtTurn": 15,
      "input": {},
      "output": {}
    }
  ],
  "updatedAt": "2026-08-23T20:16:03.120Z"
}
```

The file is written atomically and committed like other current state. Each mutation records before/after hashes and its cause in the agent archive. If `context.json` is missing, corrupt, or disagrees with its archive frontier, Deckwraith rebuilds it from that agent's archive, accepted compactions, and current retention policy.

### 7.4 Archive records

Each agent has its own append-only, segmented JSONL archive. Every line is one complete envelope with at least:

```json
{
  "schemaVersion": 1,
  "eventId": "01J...",
  "agent": "wraith1",
  "haunt": "deckwraith",
  "runId": "01J...",
  "shellId": "01J...",
  "sequence": 184,
  "timestamp": "2026-08-23T20:16:03.120Z",
  "kind": "tool.completed",
  "payload": {},
  "contentHash": "sha256:..."
}
```

Sequence numbers are monotonic per agent. A single-writer agent actor or lease prevents concurrent append races. Writers append a complete UTF-8 line, flush at configured durability boundaries, and rotate by size. On startup, Deckwraith validates the final segment. An incomplete trailing byte sequence may be preserved in a recovery incident and removed; valid records are never rewritten.

The per-agent archive is the sole durable execution and recovery ledger. Deckwraith does not maintain a separate global operation journal. Every model, command, tool, MCP, and cell execution records a stable operation ID; exact canonical inputs or artifact references; started state; and a terminal output, error, cancellation, or explicit `outcome-unknown` state. These records must contain enough information to reconstruct `context.json` and other current projections and explain what was supplied and returned without relying on an additional journal.

Large binary outputs belong in content-addressed artifacts. Archive events store metadata, hashes, previews, and references rather than duplicating arbitrary blobs. Previews may be redacted on a best-effort basis, while referenced artifacts remain inside the same highly sensitive trust boundary.

### 7.5 Behavioral privacy

Behavioral privacy means Deckwraith does not automatically place one agent's private histories, inferred tendencies, internal deliberation, or identity evolution into another agent's context. It is a product behavior and context-separation invariant, not an adversarial access-control boundary: Deckwraith's agents are trusted local actors with broad host authority.

- Context assembly reads only the active agent's identity, `context.json`, and archive, plus explicitly shared haunt material.
- Archive and identity APIs default to the active agent's scope. Cross-agent reads happen only through an explicit sharing action or an intentional operator/agent request recorded in the archive.
- Tool/MCP results are written only to the invoking agent's archive unless deliberately published to shared haunt state.
- Search indexes preserve the same scope labels and cannot return a cross-agent result merely because the underlying files are local.
- The human operator retains administrative access to local state.

Agents can still use their broad filesystem and PowerShell authority to inspect local state when they intentionally choose to do so. Deckwraith does not attempt to prevent that. The invariant is that the product itself does not casually leak behavioral history across contexts, searches, suggestions, or shared state.

## 8. Agent, shell, and run lifecycle

A wraith may be sleeping, ready, running, awaiting input, blocked, faulted, or disabled. A shell is created for a bounded provider conversation and may be replaced because of context pressure, provider failure, model migration, or operator choice. A run survives shell replacement.

The application runtime should model each active wraith as a serialized actor/mailbox. This provides one logical writer for its identity, archive, run state, and runspace lifecycle while allowing different wraiths and provider requests to execute concurrently. Repository-wide Git operations are coordinated by a separate checkpoint queue.

Every run has:

- A concrete objective and completion criteria when available.
- A status and reason for its last transition.
- A provider/model selection policy rather than a permanent provider binding.
- A budget envelope for tokens, wall time, money, and tool activity when configured.
- A current shell and zero or more previous shells.
- A stable event stream and operation IDs.

The default policy is to continue. Transient failures should be retried with bounded backoff; context exhaustion should trigger compaction or a new shell; missing capabilities should trigger discovery or an alternate assigned backend. The runtime pauses only for a real policy gate, an unavailable prerequisite, an exhausted hard budget, repeated failure beyond policy, or explicit human input that is necessary to choose among materially different outcomes.

## 9. Execution and context pipeline

One run iteration follows this pipeline:

1. **Accept intent.** Persist the user message or scheduled objective and create or resume a run.
2. **Acquire the agent lease.** Serialize state mutation for that wraith and recover any incomplete prior operation.
3. **Snapshot durable inputs.** Read the current identity, `context.json`, run state, deckbook manifest and cell hashes, haunt context, tool assignments, provider policy, and archive frontier.
4. **Resolve the provider.** Select a provider/model satisfying required capabilities, policy, availability, budget, and explicit overrides.
5. **Maintain current context.** Apply completed messages and tool interactions to `context.json`, elide tool pairs whose configured turn window has expired, and apply accepted archive compactions.
6. **Plan the context window.** Reserve capacity for the expected response and tool loop. Select pinned cells, the active cell, a bounded preceding window, relevant current outputs, and the compact deckbook index. If needed, compact only the eligible oldest contiguous archive prefix.
7. **Assemble context.** Render the materialized current context plus the complete current identity, Deckwraith behavioral contract, current objective, compiled deckbook projection, applicable haunt context, and minimal prompt-visible tool surface.
8. **Open or continue a shell.** Translate the canonical request into the selected provider's protocol and begin streaming.
9. **Execute work.** Route ordinary PowerShell invocations to the wraith's runspace and cell executions to the selected kernel. Each command, cell, core tool, or MCP operation attaches provenance and emits lifecycle events.
10. **Update the deckbook.** Apply agent-authored cell insertions, edits, moves, deletions, pin changes, and execution requests. Mark the affected cell and linear suffix stale without erasing prior execution history.
11. **Execute explicitly.** Execute one selected cell or the remaining suffix in total deckbook order when requested by the agent. Stop on failure, cancellation, or interruption according to execution policy. Editing and invalidation never trigger execution by themselves.
12. **Persist before projection.** Append canonical events, atomically update `context.json`, persist cell sources and output references, and update run state before broadcasting derived UI events. Stream deltas may be buffered, but completed messages, cell executions, and tool results must be durable.
13. **Evaluate continuation.** Let the agent continue toward completion, start a fresh shell, wait for an external condition, or report a genuine blocker.
14. **Apply identity changes.** Validate an agent-authored identity proposal, write it atomically, archive the content-hash transition, and create a meaningful Git checkpoint message describing the change.
15. **Checkpoint.** Commit coherent state according to checkpoint policy and publish the resulting commit ID on the run event stream.
16. **Release or sleep.** Dispose or retain kernels opportunistically. Ordinary variables, imported runtime state, and live objects may vanish without recovery; only persisted cell sources, cell outputs, artifacts, and explicitly promoted non-volatile values are expected to survive.

Context assembly must be deterministic for a given set of input hashes and a provider capability profile. The assembled manifest should prefer content hashes over duplicating raw values when hashes are sufficient, but it remains part of the sensitive state repository.

### 9.1 Tool elision

Completed tool interactions remain verbatim in current context for `N` subsequent completed model turns, where `N` comes from the deck-wide `toolElisionTurns` setting and may be overridden per agent. A completed model turn is one terminal model response after any tool results it consumes have been supplied. Thus `N = 0` still allows a tool result to be consumed by the immediate continuation before it becomes eligible for elision.

When an interaction reaches the retention frontier, Deckwraith removes its tool call and result together from `context.json`. Provider-required call/result pairing must never be broken. The pair is replaced by a compact provider-neutral marker containing only the tool name, operation ID, terminal status, archive sequence range, and an indication that full inputs and outputs remain retrievable from the archive.

Elision applies only to completed interactions. In-flight, interrupted, and `outcome-unknown` operations remain materialized until their state is resolved or explicitly handled. Multiple tool calls in one model turn age independently from their own terminal records but are elided only as complete call/result pairs.

Tool elision is independent of archive compaction:

- Elision may remove old tool traffic from otherwise recent current context.
- Compaction still covers only the oldest contiguous archive prefix.
- Neither mechanism changes or deletes raw archive events.
- Lowering `N` applies at the next context maintenance pass. Raising `N` affects future retention; deliberately restoring an already elided interaction is an explicit archive-retrieval action rather than an automatic rewrite.

Every elision pass atomically updates `context.json` and archives the previous/new context hashes, effective `N`, turn counter, and elided operation IDs. This makes the exact current context inspectable without paying to keep old tool payloads in every model request.

## 10. Deckbooks and cell execution

### 10.1 Role of the deckbook

Each wraith has one deckbook per haunt, plus an optional deck-wide personal deckbook. The deckbook is the primary mutable structure through which the wraith organizes ongoing work. It is closer to a versioned executable document than a chat transcript.

Deckbooks are directly descended from the IPython/Jupyter notebook and `.ipynb` interaction model: ordered editable cells, language kernels, retained outputs, and familiar operations such as running one cell or running the remaining suffix. That lineage is intentional and should be obvious in both the UI and documentation. Someone who understands a Jupyter notebook should immediately understand the basic interaction.

Deckwraith extends that model for persistent autonomous agents with stable cell names, Git-friendly source files, linear suffix staleness, bounded context compilation, append-only execution history, and multiple replaceable model shells. Its authoritative format is not `.ipynb`, because a single monolithic JSON document is poorly suited to readable Git history and does not encode these additional semantics. Import/export compatibility may be added later.

The three layers must remain distinct:

```text
append-only archive  = what happened
mutable deckbook     = what the wraith currently believes and intends to run
disposable kernels   = where executable cells happen to run right now
```

An agent may use ordinary PowerShell commands without creating a cell. It promotes work into the deckbook when the source, output, explanation, ordering, or reproducibility is worth preserving. Conversely, a cell may invoke the same core commands and MCP-backed commands available interactively.

### 10.2 Cell model

Every cell has a stable human-readable name within its deckbook and an explicit position. A minimal cell record contains:

```json
{
  "schemaVersion": 1,
  "name": "rank-candidate-bugs",
  "position": 120,
  "kind": "code",
  "kernel": "powershell",
  "source": "$issues | Sort-Object Severity -Descending",
  "contextPolicy": "when-relevant",
  "revision": 4,
  "lastExecution": {
    "executionId": "01J...",
    "sourceHash": "sha256:...",
    "inputHash": "sha256:...",
    "outputHash": "sha256:...",
    "status": "succeeded",
    "kernel": "powershell",
    "kernelVersion": "7.x"
  }
}
```

Supported cell kinds include:

- **Markdown/context**: durable facts, hypotheses, plans, explanations, and instructions.
- **Code**: executable PowerShell or C# in v1, with room for later registered kernel languages.
- **Prompt/model**: a provider invocation whose source is a prompt and whose output becomes a cell result.
- **Query/tool**: a structured invocation of a Deckwraith or MCP command.
- **Artifact**: a named reference to a file, dataset, image, report, or other content-addressed output.
- **Value**: a canonical non-volatile value that can be consumed across kernels.

Cell names use the same portable naming philosophy as agents and haunts. Renaming a cell retains an alias in notebook history. Cell ordering uses sparse positions or an equivalent order-key scheme so inserting a cell does not rewrite every later cell.

Cell source and current metadata are mutable Git-backed state. On disk, source normally lives in a language-appropriate sibling file such as `source.ps1`, `source.csx`, or `source.md`; the logical cell API still exposes source as text. This keeps diffs readable and permits ordinary editors and language tooling. Every execution, output transition, rename, and deletion is also appended to the agent archive. Removing a cell from the current deckbook therefore does not erase its history or prior outputs from Git.

Agents manipulate the deckbook through ordinary discoverable commands, for example:

```powershell
Get-DwCell
Get-DwCell rank-candidate-bugs -IncludeOutput
Set-DwCell rank-candidate-bugs -Source $source -Kernel csharp
Invoke-DwCell rank-candidate-bugs
Invoke-DwCell -From load-open-issues -Remaining
Remove-DwCell obsolete-hypothesis
```

### 10.3 Linear ordering and staleness

A deckbook has one total cell order. It has no dependency graph, reactive scheduler, parallel branches, or automatic topological execution.

- Executing one cell executes only that cell.
- “Run remaining” begins at a selected cell and visits every later executable cell in order, dispatching each to its declared kernel and skipping non-executable cells.
- Inserting or editing a cell marks that cell and every later executable cell stale.
- Deleting a cell marks every later executable cell stale.
- Moving a cell is logically a deletion plus insertion and invalidates from the earliest affected position.
- Successfully rerunning a cell refreshes that cell and marks the later suffix stale until it is explicitly rerun.
- Failure, cancellation, or interruption stops a “run remaining” operation. Later cells remain stale and unexecuted.

Staleness means the displayed output no longer corresponds to the deckbook's current ordered program; it does not delete that output. Prior outputs and executions remain available through Git and the archive.

PowerShell and C# maintain independent live kernel state even when their cells are interleaved in one deckbook. Cross-kernel flow occurs only through canonical values, non-volatile state, or artifacts. A cell may otherwise rely on ambient state established by earlier cells executed in the same kernel.

### 10.4 Explicit execution records

Deckwraith never runs a cell merely because the deckbook changed. The agent explicitly requests a single cell or an ordered remaining suffix. Because wraiths are trusted, explicit suffix execution may include arbitrary local or external side effects; no purity analysis or effect-class system is required in v1.

Before execution begins, the invoking agent's archive receives a started record containing the cell name and revision, exact source, kernel identity/version/epoch, operation ID, canonical explicit inputs, ambient execution metadata, and input artifact references. A terminal record contains the returned canonical value, display outputs, stdout/stderr/progress, output artifacts, errors, timestamps, and final status. Large values are stored as artifacts and referenced by hash.

This record is sufficient to reconstruct what Deckwraith supplied to and received from the cell. It does not claim to serialize arbitrary ambient kernel state or guarantee that external services will behave identically on a later run.

If Deckwraith restarts after a started record without a terminal record, the execution becomes `outcome-unknown`. It is never replayed automatically. The wraith may inspect the recorded inputs and external state, then decide whether and where to resume.

### 10.5 Kernel contract

Deckwraith defines its own durable notebook and kernel abstraction rather than making Jupyter or .NET Interactive the core domain model:

```csharp
public interface ICellKernel
{
    string KernelId { get; }
    KernelCapabilities Capabilities { get; }

    IAsyncEnumerable<CellKernelEvent> ExecuteAsync(
        CellExecutionRequest request,
        CancellationToken cancellationToken);

    Task InterruptAsync(string executionId, CancellationToken cancellationToken);
}
```

V1 kernel families are:

- **PowerShell**: the primary object-native kernel, backed by the wraith's hosted runspace and full Deckwraith command catalog.
- **C#**: Roslyn scripting or an isolated C# submission host, with versioned references and package/environment capture.

The kernel contract must remain language-neutral so later versions can add Python/IPython, SageMath, JavaScript, and general Jupyter-protocol adapters without redesigning deckbooks. Those are future options, not initial implementations.

Microsoft.DotNet.Interactive may be used as an execution adapter or implementation reference for C# and PowerShell experiments, but Deckwraith's cell schema, persistence, linear ordering, context rules, and archive semantics remain its own.

Kernels are disposable and potentially lossy. Every kernel instance has an epoch identifier recorded with its executions. Losing a kernel never triggers automatic replay and does not erase or retroactively invalidate outputs already produced. Deckwraith starts a clean, cold kernel with a new epoch, reports that ambient state is absent, and lets the agent choose which cell or suffix to run. Starting in the middle is allowed and may fail if the cell expected ambient state; Deckwraith does not guess or reconstruct that state.

### 10.6 Cross-kernel values

Ambient variables are local to one live kernel. Cross-kernel exchange uses canonical value cells, non-volatile state, or artifacts:

```powershell
$graph = Get-DwCellOutput 'build-call-graph'
Set-DwState -Name 'call-graph' -Value $graph -Scope Run
```

The C# and PowerShell kernels receive equivalent host APIs. Portable values use the canonical Deckwraith value contract; large tables, binaries, model objects, and language-specific values become typed artifacts with preview and conversion metadata. Future kernels must cross this explicit boundary rather than pretending that arbitrary runtime objects share identity across languages.

### 10.7 Notebook-derived model context

The deckbook is the logical working context, not necessarily the literal provider prompt. Context assembly compiles a bounded projection containing:

- The full current identity.
- Cells explicitly pinned to context.
- The active cell and a bounded preceding cell window.
- Current outputs required to understand those cells.
- A compact ordered index of other cells with name, kind, status, synopsis, and staleness.
- Relevant cells selected by deterministic or retrieval policy.
- Recent uncompacted archive history and valid oldest-prefix summaries.

The agent receives commands to list, inspect, search, insert, edit, move, pin, execute, and delete cells at runtime. A large deckbook therefore remains random-access working memory without consuming the entire model context on every call.

Context compilation records cell revisions and output hashes in its manifest. If the agent changes the deckbook during a model turn, those changes affect the next provider invocation; an in-flight provider request continues against its immutable input snapshot.

## 11. Model-provider adapter model

Deckwraith supports broad provider coverage through a canonical provider contract. The contract should express semantics Deckwraith needs, not the union of every vendor SDK.

```csharp
public interface IModelProvider
{
    string ProviderId { get; }
    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelEvent> RunAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}
```

Canonical requests include ordered context items, model selection, sampling/reasoning controls, response limits, a minimal tool catalog, continuation metadata, and tracing identifiers. Canonical streamed events include text/reasoning deltas where available, tool-call deltas, completed tool calls, usage, provider warnings, finish reasons, and errors.

Adapters are responsible for:

- Authentication and endpoint configuration.
- Translating roles, content parts, tool calls, streaming, and cancellation.
- Mapping vendor usage and finish reasons into canonical forms while retaining raw diagnostic metadata.
- Advertising capabilities such as native tool calling, images, prompt caching, reasoning controls, structured output, and conversation continuation.
- Returning actionable unsupported-capability errors rather than silently degrading important semantics.

Initial adapters should cover OpenAI, Anthropic, Google Gemini, and configurable OpenAI-compatible endpoints. The architecture must permit local models and routing services such as Flatline without special-casing agent identity.

Provider selection is a policy service. It may be fixed per run, inherited from an agent default, chosen by capability/cost/availability, or independently overridden for compaction. Provider conversation IDs are optimization hints only; the archive remains the portable source of truth.

Providers without native tool calling may use a constrained structured-output protocol if it can be validated unambiguously. They must not receive a fictitious capability flag.

Contract tests should replay a common suite against every adapter: streaming text, cancellation, tool call and result, malformed output, context-limit errors, rate limiting, usage accounting, and provider disconnect.

## 12. PowerShell and runspace design

### 12.1 Lifecycle

Each awake wraith normally receives a dedicated hosted PowerShell runspace built from `InitialSessionState`. Deckwraith may retain it across model calls because working variables, imported modules, and live objects are useful, but this is an opportunistic cache rather than durable state. A runspace may disappear on sleep, idle eviction, fault, catalog change, process restart, or any other lifecycle boundary.

Deckwraith must never reconstruct a lost runspace by replaying commands or re-executing prior pipelines. Replay could duplicate file mutations, network calls, messages, purchases, or other side effects while still failing to reproduce the original in-memory state. After loss, Deckwraith creates a clean runspace with the current command catalog, emits a `runspace.replaced` event, and tells the active shell that volatile PowerShell state was lost.

The replacement runspace is not required to reproduce the exact prior command surface: global, haunt, and agent tool assignments or authored modules may have changed since it was created. It receives the command surface valid at replacement time and access to the same durable non-volatile state.

The runspace uses full language mode. This is deliberate: wraiths are trusted, highly autonomous local actors, and unrestricted PowerShell plus .NET access is part of the product's value. The command catalog determines what is convenient and discoverable, not what an adversarial agent is technically able to reach.

### 12.2 Non-volatile state

Deckwraith provides explicit commands for values a wraith wants to survive runspace loss:

```powershell
Set-DwState -Name 'current-targets' -Value $targets -Scope Agent
$targets = Get-DwState -Name 'current-targets' -Scope Agent
Get-DwState -Scope Run
Remove-DwState -Name 'temporary-plan' -Scope Run
```

Supported scopes are:

- **Run**: survives runspace and process loss for the current run; eligible for archival or cleanup after the run reaches a terminal state.
- **Agent**: survives runs and shells as part of the wraith's durable working state.
- **Haunt**: explicitly shared durable state for collaborators in the same haunt.

No ordinary PowerShell variable is persisted automatically, and persisted values are not automatically injected back into variables. The wraith chooses what is durable and retrieves it deliberately. This keeps recovery from being mistaken for command replay.

Each value is stored as an independently addressable, versioned record containing its scope, key, canonical serialized value or artifact reference, content hash, writer, run ID, version, and update time. Writes are atomic, recorded in the invoking agent's archive, included in Git checkpoints, and support optional compare-and-swap through an expected version so concurrent haunt updates do not silently overwrite one another.

The portable value contract supports nulls, booleans, numbers, strings, byte or artifact references, ordered lists, string-keyed maps, and explicitly registered Deckwraith DTOs. Arbitrary live objects such as processes, streams, sockets, runspaces, provider clients, script blocks, and assembly-bound handles are rejected. Agents may instead persist a stable identifier, reconstruction parameters, or an artifact and reopen the resource deliberately.

### 12.3 Command sources

Commands may come from:

1. Core compiled cmdlets provided by `Deckwraith.PowerShell`.
2. MCP commands generated from assigned server catalogs.
3. Global operator-authored PowerShell modules.
4. Per-agent `.ps1` tools owned by that wraith.
5. Deckbook and kernel-management commands.
6. Native executables and scripts invoked through ordinary PowerShell semantics.

Assigned commands follow idiomatic approved verbs and a `Dw` noun prefix where useful, for example:

```powershell
Get-DwArtifact
Search-DwArchive
Invoke-DwMcpTool
Write-DwProgress
Checkpoint-DwState
Invoke-DwCell
```

Commands accept pipeline input and return typed .NET records or `PSObject` values. Formatting is a presentation concern; command implementations must not flatten successful results to display text. Errors use PowerShell's error stream with stable error IDs, categories, operation IDs, and structured details.

### 12.4 Agent-authored tools

PowerShell is the native agent-authored tool format. A wraith may create a script in `agents/<agent-name>/tools/`, validate it in a disposable or current runspace, update its help metadata, and request reload. The source is checkpointed like other state.

An authored command should include comment-based help, declared parameters, and predictable object output. It should use credential references instead of embedding long-lived credentials when practical, while recognizing that commands and their Git history may still contain sensitive values. Deckwraith records the command's content hash and source assignment when loading it. A failed reload leaves the previous known-good command set active and surfaces diagnostics to the agent.

Frequently used scripts may later be promoted to compiled cmdlets without changing their public PowerShell contract.

### 12.5 Future execution backends

V1 does not implement dedicated Python or JavaScript tool runtimes. PowerShell already invokes native executables and scripts directly, and C# is available through its deckbook kernel. If later experience justifies a richer out-of-process backend, the architecture can add one without changing the PowerShell command surface, tool broker, or deckbook model:

```csharp
public interface IExecutionBackend
{
    string BackendId { get; }

    Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken);
}
```

Future backends may cover Python, JavaScript, specialized mathematical systems, or another runtime with enough value to warrant structured adaptation. They should translate PowerShell objects through a versioned envelope, preserve structured results, and expose observable runtime acquisition, dependencies, environment, progress, and failures. Separate processes may simplify packaging and failure containment, but are not an agent security boundary.

The cross-process envelope must distinguish success output, structured data, logs, progress, errors, and artifacts. JSON is an acceptable transport boundary; it must not leak into the agent-facing pipeline when a richer object can be reconstructed.

### 12.6 Trust model

Wraiths are trusted with broad process, filesystem, network, PowerShell, and .NET authority. Deckwraith does not attempt to sandbox them or turn command assignment into a permission system. Removing a command from `InitialSessionState` keeps an irrelevant tool out of discovery and model context; it does not make the capability unreachable by a determined agent.

Cmdlets still carry agent, run, shell, and operation context so actions can be attributed, observed, recovered, and explained. MCP credentials should remain in the host and be referenced indirectly where practical because reducing unnecessary copies is valuable even though the state repository is already considered sensitive.

## 13. MCP and tool exposure

MCP servers and ordinary tools are assigned globally, per haunt, or per agent. Effective assignment is the configured union after explicit exclusions are applied. Assignment controls runtime discovery, default availability, and prompt economy; it is not intended as a sandbox against the agent.

Deckwraith should generate a PowerShell proxy command for each assigned MCP tool. Names are normalized into stable PowerShell names; collisions are resolved through module qualification and explicit aliases. JSON Schema becomes PowerShell parameter metadata where possible, including required parameters, enums, arrays, and validation. The complete original schema remains available through help and discovery.

The preferred model-visible surface is small:

```text
Invoke-PowerShell(script)
```

Optionally, a provider may also receive a tiny set of high-frequency primitives such as a cancellation-safe patch operation. The default should not inject dozens of MCP definitions merely because they are assigned.

Inside the runspace, agents discover capabilities incrementally:

```powershell
Get-Command -Module Deckwraith.*
Get-Command -Noun DwGitHubIssue
Get-Help Get-DwGitHubIssue -Full
Find-DwCommand -Capability 'issue search'
Get-DwToolSchema Get-DwGitHubIssue
```

Deckwraith-specific discovery may use a compact local semantic index over command names, synopsis text, tags, and schemas. Search results reveal metadata only for assigned commands. Full schemas are loaded on demand and do not consume model context until requested or returned by execution.

MCP clients remain owned by the .NET host. Proxy commands call them through a canonical tool broker that supplies agent, run, operation, policy, cancellation, and tracing context. The broker validates inputs and outputs, applies timeouts, captures progress, applies best-effort redaction to known credential fields, and journals uncertain side effects. Unknown or deliberately printed secrets may still enter the archive.

Tool definitions and server capability lists may change at runtime. Catalog updates invalidate the affected runspace command set and trigger a controlled kernel replacement between invocations, never halfway through a pipeline and never through command replay.

## 14. Compaction

Compaction preserves recent conversational detail while summarizing only old history. It is a deterministic coverage operation, not relevance retrieval.

### 14.1 Selection rule

Given the uncompacted history eligible for context, Deckwraith selects the oldest contiguous prefix whose size is governed by a configured fraction `N%`, token target, and minimum useful boundary. The selection:

- Starts at the earliest uncompacted record after the last covered frontier.
- Ends only at a complete semantic boundary, normally a completed turn or tool transaction.
- Contains no gaps and never includes a newer record while excluding an older eligible one.
- Excludes the current in-flight turn.

`N%` is configurable globally and per agent. Token pressure may cause repeated compaction passes, each advancing the frontier. It must not cause middle-history deletion or cherry-picking.

### 14.2 Compaction model

The compaction provider and model are independently selectable from the active shell's model. Selection may be global, per agent, per haunt, or per run. The compactor receives a strict preservation contract covering decisions, commitments, unresolved questions, user preferences, errors, artifact references, tool outcomes, and identity-relevant observations.

A compaction record includes:

- Its ID and schema version.
- Agent and source archive identifiers.
- Exact first and last sequence numbers covered.
- Source content hashes and previous compaction ID.
- Compactor provider, model, prompt-version, and parameters.
- Summary text plus structured unresolved items and artifact references.
- Creation time, validation result, and checkpoint commit.

Coverage ranges must be monotonic, contiguous, and non-overlapping. Before accepting a compaction, Deckwraith verifies source hashes and structural invariants. Failed compaction leaves the previous context frontier untouched.

### 14.3 Context use

Context assembly includes the newest valid summary chain needed to represent the compacted prefix, followed by the materialized recent uncompacted items from `context.json`. Ordinary messages remain verbatim; completed tool interactions are subject to the configured elision window. The raw events remain in the archive and can be inspected by the operator or deliberately retrieved by the same agent. Summaries are lossy navigation state and must not be treated as stronger evidence than their source.

Accepted compaction rewrites `context.json` by replacing the covered current-context prefix with the corresponding summary item. Tool interactions already elided from current context remain fully present in the raw archive used by the compactor. Elision therefore reduces routine prompt size without creating holes in compaction source material.

Recursive re-compaction is allowed only if the new record identifies the exact summary and raw ranges it supersedes while preserving a verifiable path to the archive. V1 may defer recursive compaction and instead start a fresh shell when the summary chain itself becomes large.

## 15. Git checkpointing and recovery

### 15.1 Checkpoint policy

The checkpoint coordinator serializes Git operations and coalesces related writes. It creates commits at coherent boundaries such as:

- Run creation, completion, cancellation, or transition to blocked.
- Accepted identity changes.
- Material current-context rewrites, including compaction and tool-elision passes.
- Agent-authored tool creation or modification.
- Deckbook structural edits and completed run-remaining executions.
- Successful compaction.
- Material task or artifact updates.
- Before and after a risky multi-file operation.
- A configured maximum dirty interval or change threshold.

Streaming tokens, progress ticks, and each individual archive line do not require their own commit. They remain durable on disk and are included in the next checkpoint.

Automatic commits use stable metadata and trailers:

```text
deckwraith: checkpoint wraith1 run 01J...

Deckwraith-Agent: wraith1
Deckwraith-Haunt: deckwraith
Deckwraith-Run: 01J...
Deckwraith-Reason: deckbook-run-remaining-completed
```

User-authored repository history must not be rewritten. If Deckwraith manages project source and internal state in the same repository, it must clearly distinguish its commits and avoid staging unrelated user changes. The safer default is a dedicated state repository with explicit haunt workspace links.

### 15.2 Atomicity

Files are written through same-directory temporary files, flushed, validated, and atomically renamed where the platform supports it. Archive append and metadata update cannot be one filesystem transaction, so the archive event is authoritative and projections reconcile on startup.

Each checkpoint records the source state hashes it expects. A crash between durable writes and Git commit produces a dirty but recoverable tree; startup recovery validates it and creates a recovery checkpoint rather than discarding it.

### 15.3 Reversal

Normal rollback creates a recovery branch and a new inverse commit or restores selected paths in a new commit. Deckwraith must never automatically use destructive history rewrites to conceal a bad state. Before any operator-requested destructive reset, it creates a recovery ref and explains which uncommitted data would be affected.

External effects such as sent messages, published packages, purchases, or remote deletions cannot be undone by Git. Tool metadata must declare side-effect and idempotency characteristics so policy and recovery can treat them appropriately.

## 16. UI/core boundary

Electron.NET provides application packaging, native window lifecycle, menus, notifications, and a consistent Chromium renderer. The .NET process owns Deckwraith lifecycle and launches the renderer as its presentation layer. There is no separately deployed HTTP backend in the desktop product.

Electron still entails a process boundary. Communication uses a versioned, typed IPC protocol built around:

- **Commands**: request a state transition, always with request and correlation IDs.
- **Queries**: request immutable snapshots or paginated projections.
- **Events**: report ordered state changes, streaming deltas, progress, and invalidation.

The renderer does not receive mutable domain objects. It maintains a UI projection from snapshots and events. On event loss or version mismatch it discards affected projections and requests a fresh snapshot.

IPC handlers validate all inputs and assume renderer content is untrusted. Provider keys, MCP credentials, raw credential-store handles, and unrestricted filesystem primitives never cross the bridge. Navigation, external links, and web content use Electron security hardening appropriate to an untrusted renderer: context isolation, no renderer Node integration, a narrow preload API, and an explicit content security policy.

The application-facing command/query/event contracts must be transport-neutral. A future `Deckwraith.Headless` host may expose them through authenticated local sockets, gRPC, or another protocol without moving logic out of the application runtime.

The React application should initially provide:

- Wraith list, status, model, current objective, and recent activity.
- Per-wraith conversation/run view with streaming output and tool activity.
- Current-context inspector showing the exact materialized `context.json`, archive frontier, turn counter, compaction items, elision markers, and effective global/per-agent tool-retention setting.
- A deckbook editor with mixed-language cells, stable cell names, insert/delete/reorder controls, linear stale-suffix visualization, pinned-context controls, streaming outputs, and run-cell/run-remaining actions.
- Kernel status, environment/provenance inspection, interruption, and clean-restart controls.
- Side-by-side source/output and Git history for a cell without conflating current state with prior executions.
- Identity viewer/editor with semantic diff and attribution.
- Tool/MCP assignment and command-discovery views.
- Git checkpoint timeline, diff, and recovery actions.
- Compaction coverage and provenance inspection.
- Structured logs, unresolved operations, and crash-recovery prompts.

## 17. Crash recovery and observability

### 17.1 Archived operation lifecycle

Before starting an operation, Deckwraith appends a started record to the invoking agent's archive containing a stable operation ID, complete canonical inputs or artifact references, tool or cell identity, agent name, haunt name, run ID, and idempotency metadata when available. It then appends exactly one terminal record when the outcome is known. The terminal record contains complete canonical outputs or artifact references, errors, provider/tool identifiers, and status.

After restart:

- Started operations with an idempotency key may be queried or retried according to the tool contract.
- Started non-idempotent operations without a recorded result become `outcome-unknown` and are not blindly repeated.
- Completed operations whose projection update was interrupted are replayed into projections.

Provider calls follow the same lifecycle. If a provider supports resumable streams or stable response retrieval, the adapter may recover them. Otherwise Deckwraith records the interrupted shell and creates a new one with the durable context.

### 17.2 Startup reconciliation

Startup performs:

1. State schema and configuration validation.
2. Archive tail and sequence validation.
3. `context.json` schema, archive-frontier, turn-counter, and content-hash validation; rebuild from the agent archive when required.
4. Atomic-write residue detection.
5. Run and archived-operation lifecycle reconciliation from per-agent archives.
6. Deckbook schema, total ordering, cell/output hash, and linear stale-suffix reconciliation.
7. Compaction coverage/hash validation.
8. Git status inspection and recovery checkpointing of coherent dirty state.
9. Rebuild of disposable projections.
10. Lazy creation of clean kernels only when needed; no cell or command replay and no attempted restoration of volatile kernel state.

Recovery never silently invents a successful result. Incidents are written under `recovery/incidents/`, emitted on the event stream, and shown in the UI with the evidence available.

### 17.3 Observability

All subsystems emit structured events with timestamp, severity, event name, agent name, haunt name, run ID, shell ID, cell name, cell execution ID, kernel ID, operation ID, provider request ID, and trace ID where applicable. Logs must support human-readable local files and an optional OpenTelemetry sink.

The runtime records latency and usage for provider calls, tool calls, cell and run-remaining execution, kernel startup, compaction, context assembly, tool elision, archive writes, and Git checkpoints. Model context manifests store cell revisions, component hashes, token counts, elided-operation counts, and estimated tokens saved so prompt growth is explainable.

Known credential fields and provider authorization headers should be redacted before ordinary diagnostic display and export. This is best-effort filtering, not a guarantee that persisted events are secret-free: commands, tool results, model content, exceptions, and raw PowerShell streams may all contain sensitive values. Diagnostic exports require an explicit action, a visible warning, and review before sharing.

## 18. Configuration and policy

Configuration is layered in this order:

1. Built-in defaults.
2. Deck-wide configuration.
3. Haunt configuration.
4. Agent configuration.
5. Run-specific explicit overrides.

Later layers may change defaults and require explicit confirmation for narrowly defined high-impact operations. Configuration changes and confirmations remain auditable.

`toolElisionTurns` is defined deck-wide and may be overridden in an agent's configuration. The effective value is copied into `context.json` on each maintenance pass so the materialized context is self-describing.

Policy should describe operating preferences rather than pretend to provide hostile-code containment: attached filesystem roots, preferred network destinations, MCP assignments, credential references, external side-effect classes, spend limits, and dependency-installation behavior.

The default local-owner profile grants broad host authority and expects agents to exercise judgment. Confirmation is reserved for explicitly configured classes of irreversible, costly, or externally consequential action.

## 19. V1 scope

V1 should prove that one wraith can wake, work autonomously, preserve continuity, recover, and later inhabit a different model shell without losing its identity.

V1 includes:

- A desktop application for macOS, Windows, and Linux where Electron.NET packaging permits, with macOS as an acceptable first development target.
- A UI-agnostic .NET core exercised by automated tests on Linux from the beginning.
- Creation, configuration, wake, sleep, resume, and deletion/archival of wraiths.
- Canonical human-readable agent and haunt names with no parallel opaque IDs, plus atomic rename and reserved-alias behavior.
- Multiple configured wraiths, with at most one active run per wraith and simple concurrent execution across wraiths.
- Minimal JSON identity documents with name, personality, open string calibration (including register), pronouns, self-description, string-array open questions, and string-array known tendencies; Git commits provide identity history.
- Per-agent append-only JSONL archives and behavioral privacy in context/tool APIs.
- Per-agent Git-backed `context.json` files containing the materialized provider-neutral context actually carried forward.
- Tool call/result elision after a deck-wide configurable number of completed model turns with per-agent overrides, paired removal, compact provenance markers, and complete archive retention.
- Provider adapters for OpenAI, Anthropic, Gemini, and OpenAI-compatible services.
- A dedicated PowerShell runspace per awake wraith.
- Explicit run-, agent-, and haunt-scoped non-volatile state accessed through PowerShell commands and persisted independently of runspaces.
- A first-class per-agent/per-haunt deckbook with one total order, named cells, insertion/deletion/reordering, linear suffix staleness, pinning, persisted outputs, provenance, and notebook-derived context assembly.
- PowerShell and C# cell kernels behind a language-neutral contract that can support additional kernels later.
- Explicit run-cell and run-remaining execution, interruption, and clean cold-kernel replacement without replay or automatic reactive execution.
- Compiled core commands, per-agent PowerShell tools, and global/per-agent MCP assignment.
- Runtime command discovery with a minimal prompt-visible execution tool.
- Oldest-prefix compaction with independently configured compaction model.
- Git-backed state, automatic checkpoints, diff viewing, and non-destructive recovery.
- A highly sensitive repository posture: restrictive local defaults, no automatic remotes or pushes, and explicit warnings for export or sharing.
- Structured event streaming, complete input/output operation lifecycles in per-agent archives, startup reconciliation, and basic metrics.
- Electron.NET plus React/TypeScript/Tailwind/Radix UI using commands, snapshots, and events.
- A headless smoke-test host or integration harness proving the core has no desktop dependency.

V1 explicitly does not require:

- Agent sandboxing or adversarial containment.
- Distributed multi-host scheduling or remote fleet management.
- Complex multi-agent delegation, markets, voting, or social simulation.
- Shared behavioral memory between agents.
- Perfect continuation of interrupted provider streams.
- Recursive archive compaction, general vector-memory retrieval, or autonomous long-term memory curation beyond identity, deckbooks, and archives.
- Python, JavaScript, SageMath, general Jupyter-protocol, or other additional cell kernels.
- Dedicated Python or JavaScript tool backends beyond what agents can already invoke through PowerShell.
- Native mobile clients or a remotely exposed daemon.
- A plugin marketplace.

### 19.1 Delivery milestones

V1 is intentionally broad because it describes the successor to an already validated system shape, not a throwaway feasibility experiment. It is delivered as independently buildable and testable vertical milestones rather than one indivisible launch:

1. **State spine**: deck repository, canonical names and renames, sparse identity, per-agent archives, artifacts, and Git checkpoints.
2. **Inference spine**: one provider adapter, shells and runs, streaming, materialized `context.json`, deterministic context manifests, configurable tool elision, and durable message/tool lifecycles.
3. **PowerShell runtime**: hosted per-wraith runspaces, object-native core commands, non-volatile state, loss/replacement behavior, and agent-authored `.ps1` tools.
4. **Linear deckbooks**: ordered cells, insertion/deletion/movement, persisted sources and outputs, suffix staleness, explicit run-cell/run-remaining, and PowerShell cell execution.
5. **C# kernel**: Roslyn execution, kernel epochs, canonical cross-kernel values/artifacts, interruption, and cold replacement.
6. **MCP/tool discovery**: global/per-agent assignments, PowerShell proxy generation, `Get-Command`/`Get-Help` discovery, and minimal model-visible schemas.
7. **Continuity and recovery**: oldest-prefix compaction, independently selected compaction model, archive-driven startup reconciliation, crash injection, and Git reversal flows.
8. **Product shell**: Electron.NET/React UI, identity/deckbook/archive/checkpoint inspection, remaining provider adapters, cross-platform packaging, and headless-core verification.

Each milestone must have an end-to-end acceptance test and leave the repository in a usable state. Later milestones may refine earlier contracts through ordinary Git-backed migration; they do not require the complete v1 surface to exist before useful testing begins.

## 20. Testing strategy

The highest-value tests exercise invariants rather than UI details:

- Property tests for contiguous compaction coverage, sequence monotonicity, and non-overlap.
- Name-resolution tests for case normalization, collisions, atomic rename, aliases, forks, paths, Git trailers, and historical references.
- Golden tests for deterministic context manifests and provider translations.
- Current-context tests proving atomic updates, exact materialized ordering, archive-frontier reconciliation, hash validation, and rebuild from per-agent archives.
- Tool-elision tests covering global defaults, per-agent overrides, `N = 0`, paired call/result removal, multiple calls in one turn, unresolved operations, compact markers, compaction independence, and restart stability.
- Contract tests shared by every provider adapter and every execution backend.
- Crash-injection tests at every durable-write and checkpoint boundary.
- Archive fuzzing for truncated lines, invalid encodings, duplicate IDs, and corrupt hashes.
- Runspace-loss tests proving no command is replayed, volatile variables disappear, assigned commands reload from current configuration, and explicitly stored non-volatile values survive.
- Non-volatile state serialization, atomicity, version-conflict, and unsupported-live-object tests.
- Deckbook property tests for order stability, insertion, deletion, movement, linear suffix invalidation, output retention, and cell rename.
- Execution tests proving edits never execute cells, run-cell affects only one cell, run-remaining follows total order, every execution receives an operation ID, and failure stops the remaining suffix.
- Kernel-loss tests proving cells are never replayed automatically, prior outputs remain available, and replacement kernels begin cold with a new epoch.
- Cross-kernel contract tests for PowerShell, C#, canonical values, artifacts, streaming, cancellation, and environment provenance.
- Notebook-context golden tests proving pinned cells and a bounded active-cell window are included while unrelated large cells remain discoverable but absent.
- Context-separation tests proving ordinary archive searches and automatic retrieval stay within the active agent unless cross-agent scope is explicitly requested.
- IPC schema compatibility and renderer-reconnect tests.
- Linux CI tests that fail if desktop-only dependencies enter the core graph.
- End-to-end tests using fake providers and MCP servers before live-provider smoke tests.

## 21. Open questions

The following decisions should remain explicit rather than being accidentally fixed by early implementation:

Already resolved and recorded elsewhere in this document: the target runtime for all .NET projects is .NET 10 (`net10.0`, pinned by `global.json`); v1 deckbook kernel languages are PowerShell and C# only; and v1 tool backends are PowerShell-only with Python, JavaScript, and SageMath deferred to future options. These do not need to be reopened.

1. **Electron.NET lifecycle.** Validate packaging, signing, auto-update, .NET-first hosting, and renderer IPC on all target platforms with a thin spike before committing the application shell.
2. **State repository topology.** Decide whether each haunt has its own state repository, the deck has one repository, or the deck uses a repository with per-haunt worktrees.
3. **Archive payload policy.** Define which provider reasoning fields may legally and usefully be retained, and how provider-specific privacy settings affect archival.
4. **Non-volatile value encoding.** Finalize the canonical encoding, registered DTO mechanism, size threshold for artifact references, key naming rules, retention of completed run-scoped values, and whether compare-and-swap is sufficient for shared haunt updates.
5. **Canonical naming rules.** Finalize the portable character set, maximum length, case folding, display labels, reserved words, alias retention, and the transaction used to rename a wraith or haunt across the repository.
6. **Cell granularity.** Determine when agents should promote interactive work into cells and whether cell names or ordering create implicit context priority.
7. **C# kernel environment.** Decide how Roslyn references, NuGet packages, compilation options, and environment locks are resolved, recorded, shared, and garbage-collected.
8. **Roslyn lifetime.** Choose between in-process scripting, unloadable `AssemblyLoadContext` workers, and process-backed C# kernels to avoid unbounded submission-chain and assembly growth.
9. **Future kernels.** Decide when Python/IPython, SageMath, JavaScript, or a general Jupyter-protocol adapter provides enough value to implement, without changing the v1 deckbook contract prematurely.
10. **Notebook interchange.** Decide whether `.ipynb` import/export is useful and how to preserve Deckwraith-only semantics such as stable names, archive provenance, context pinning, and Git-friendly cell files.
11. **Minimal native tool surface.** Determine whether the model receives only `Invoke-PowerShell` or also a tiny set of provider-native primitives for notebook mutation, patching, cancellation, and artifact transfer.
12. **MCP naming.** Specify stable collision resolution, aliases, dynamic-schema updates, pagination, streaming, elicitation, and resource/prompt exposure in PowerShell.
13. **Tool-elision default.** Choose the deck-wide default `N` and the exact compact marker wording while preserving the fixed semantics that global/per-agent configuration, turn counting, pairwise removal, and archive retention already define.
14. **Compaction thresholds.** Set defaults for `N%`, reserved response capacity, minimum turns, tool-result handling, and whether the compactor may propose identity updates separately from its summary.
15. **Git granularity.** Tune dirty-time and size thresholds so recovery is strong without producing unusable history or expensive commits for large archives and cell outputs.
16. **Sensitive-state storage.** Decide how Deckwraith detects unsafe filesystem permissions, recommends or manages encrypted backups, supports deliberately configured private remotes, and derives reviewable diagnostic exports without implying that the source repository can be sanitized reliably.
17. **Autonomy policy UX.** Find the smallest policy model that clearly distinguishes ordinary autonomous work from the few explicitly configured operations that still warrant confirmation.
18. **Provider portability.** Define the canonical representation of reasoning, citations, images, prompt-cache controls, and provider conversation state without collapsing meaningful differences.
19. **Headless protocol.** Choose local sockets, gRPC, or another transport only when a real headless client exists; keep the command/query/event interface transport-neutral until then.
20. **Terminology.** Decide whether `haunt`, `deckbook`, `shell`, and related vocabulary appears in public APIs or remains product-language layered over conventional domain names.

## 22. Acceptance criteria for the architecture

The architecture is validated when a prototype can demonstrate the following scenario:

1. Create the initial wraith `wraith1` and haunt `deckwraith` by those canonical names, with no UUID required to inspect, address, or reason about either one.
2. Give `wraith1` a deliberately sparse JSON identity, global/per-agent tools, a multi-step objective, and a deckbook for `deckwraith`; let it author its own description, pronouns, tendencies, and questions.
3. Build a mixed notebook containing context plus PowerShell and C# cells, then exchange at least one canonical value and one artifact across kernels.
4. Run an ordered suffix, insert or edit an earlier cell, and verify that the linear suffix becomes stale without executing anything or erasing prior outputs and execution records.
5. Put an MCP-backed side-effecting cell in the suffix and verify that it runs only as part of an explicit run-cell or run-remaining request, with complete inputs and outputs recorded in `wraith1`'s archive.
6. Lose a PowerShell or C# kernel and verify that Deckwraith starts a cold replacement epoch without replay while retaining prior outputs and non-volatile values.
7. Inspect `agents/wraith1/context.json` and verify that it contains the actual materialized provider-neutral context, ordered items, archive frontier, turn counter, hashes, and effective tool-elision policy used for the next request.
8. Execute tool interactions, advance the configured `N` completed model turns, and verify that complete call/result pairs become compact markers in `context.json` while their full inputs and outputs remain unchanged and retrievable in `wraith1`'s archive.
9. Compile a bounded model context containing the materialized current context, identity, pinned cells, the active cell and preceding window, and compact deckbook index—without injecting the entire notebook.
10. Let `wraith1` discover an MCP-backed command through PowerShell help without that MCP schema appearing in the initial model prompt, and preserve object structure through a multi-command pipeline.
11. Let `wraith1` author and reload a PowerShell tool, use it from a cell, and checkpoint the resulting source, output references, and provenance.
12. Append the complete run to `wraith1`'s private archive while another wraith does not receive it through normal context or retrieval behavior.
13. Compact only the oldest contiguous archive prefix using a separately configured model and verify exact coverage, correct `context.json` replacement, and no deckbook mutation.
14. Crash the host between a side-effecting cell operation start and result, restart, rebuild/reconcile `context.json`, and surface the operation as recovered or outcome-unknown from `wraith1`'s archive without blind duplication.
15. Inspect and reverse a bad notebook or local-state change through Git without erasing its history.
16. Let `wraith1` choose a canonical name, perform the rename through the explicit migration path, and verify that current references, historical events, filesystem paths, and the reserved `wraith1` alias resolve coherently.
17. Wake the same wraith through a different model provider with its identity, deckbook, `context.json`, persisted values, compaction state, archive, and tools intact.
18. Run the same core lifecycle and deckbook tests on Linux without loading Electron or browser assemblies.

Passing that scenario demonstrates the central claim of Deckwraith: the identity, history, authority, and working environment belong to the wraith, while models are replaceable shells.
