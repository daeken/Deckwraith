# Pre-1.0 product gate

Status: active
Last updated: 2026-08-24

The architecture milestones in [V1-ACCEPTANCE.md](V1-ACCEPTANCE.md) remain useful evidence, but
they are not the Deckwraith 1.0 product gate. Release and publisher work is paused while the macOS
desktop is dogfooded and the requirements below are completed.

## Required provider access

Deckwraith must talk to providers inside its own process. Invoking another provider CLI or local
proxy is not native support.

| Provider | API access | Subscription access | 1.0 requirement |
| --- | --- | --- | --- |
| OpenAI | Responses API with an API key | ChatGPT/Codex browser sign-in and token refresh | Both native |
| Anthropic | Messages API with an API key | Claude subscription OAuth and token refresh | Both native |
| xAI | Responses API with an API key | xAI subscription sign-in and native transport | Both native |
| Z.AI | Responses API with an API key | Z.AI subscription sign-in and native transport | Both native |

Acceptance:

- API and subscription entries are separate provider IDs and may be configured simultaneously.
- Authentication happens through Deckwraith UI/host flows; normal use does not require another CLI.
- Secrets live outside the deck repository, use the platform credential store where available, and
  are redacted from events and diagnostics.
- Refresh, expiry, revocation, missing credentials, and provider rejection have explicit states and
  actionable UI.
- Provider-neutral request, event, archive, and context contracts remain free of vendor SDK types.
- Each transport has deterministic request/stream contract tests and a manually gated live smoke
  test that never prints credentials.

Provider web subscriptions are not assumed to expose the same API or entitlement model as paid API
accounts. Each native subscription adapter must document the provider-owned flow and transport it
implements; Deckwraith must not silently fall back to API billing.

Current progress: OpenAI ChatGPT subscription access now has a Deckwraith-owned browser/PKCE flow,
loopback callback, secure credential storage, refresh, explicit authentication states, native
Responses transport, deterministic contracts, and a live smoke path. OpenAI API-key entry, secure
storage, and readiness UI are also in place. The remaining providers still need native subscription
access; their API paths need provider-specific live smoke coverage before this gate is complete.

## Haunt project checkpoints

A haunt may point at a project working directory and define an automatic commit policy. This is
separate from Deckwraith's own sensitive state-repository checkpoints.

Acceptance:

- Auto-commit is off by default and configured per haunt.
- The policy records the project path, enabled state, author identity, allowed path scope, and
  whether a pre-existing dirty tree is permitted.
- File-edit operations may attach a proposed commit subject and body directly to their result.
- A successful coherent edit batch can create exactly one project commit using that message.
- A failed batch creates no commit and leaves no partial file changes.
- Deckwraith never pushes a project commit automatically unless a later, separate policy explicitly
  grants that external action.
- Commits never sweep unrelated pre-existing changes into the index.

## One attention stream per wraith

A wraith may collaborate in many haunts but has exactly one current context and one serialized
runloop. Haunts are places and shared projects, not separate minds or conversation slots.

Acceptance:

- There is exactly one `context.json` per wraith and at most one nonterminal run.
- The UI shows the wraith's current haunt as focus, not as a context selector.
- Moving focus to another haunt preserves the same context and records the transition in the
  wraith's archive.
- No API can execute two independent runloops for one wraith concurrently.
- Starting concurrent independent work requires an explicit fork with its own identity, context,
  archive, and runloop.

## Atomic file editing

Agents need a small object-native editing surface rather than assembling fragile text pipelines.

Acceptance:

- One operation may create/overwrite, prepend, append, replace one or many exact anchors, and apply
  structural JSON changes across one or many files.
- The full batch validates before publication: missing/ambiguous anchors, invalid JSON paths, stale
  content hashes, invalid encodings, or out-of-scope paths fail the entire operation.
- Publication is atomic from the caller's perspective and restores every original if a later file
  replacement fails.
- JSON edits operate on values and object/array paths, preserve valid JSON, and support set, remove,
  insert, append, and optimistic tests.
- Results include per-file before/after hashes, a concise change summary, and an optional proposed
  Git commit message.
- PowerShell commands expose the capability as structured objects with complete help.

## Customizable themes

Acceptance:

- The desktop ships at least dark, light, and system-following modes.
- Users can customize semantic color tokens without editing application source.
- Theme preference is stored as a desktop preference, not in the sensitive deck repository.
- Every scrollable and interactive surface remains legible in each built-in theme, including focus,
  disabled, warning, error, stale, running, and selected states.

## Setup, housekeeping, and local adaptation

The first-run experience introduces an initial setup wraith rather than presenting configuration
as a dead form. That wraith and the human configure the deck together; the same wraith remains
available for housekeeping after onboarding.

Acceptance:

- A new deck creates or invites one sparse setup wraith and a setup haunt through an idempotent,
  restart-safe flow.
- Setup covers provider connections, deck location, theme, working preferences, source/custom-build
  intent, and a clear review of sensitive-state handling.
- Housekeeping can inspect health, explain migrations, tend configuration, and propose or perform
  an explicitly approved local upgrade without publishing the sensitive deck.
- A normally installed application can switch to a locally built Deckwraith while preserving the
  existing deck and desktop preferences, and can distinguish upstream updates from local changes.
- The setup role is part of an ongoing collaboration. The wraith owns its identity and may help
  reshape the role or fork specialized collaborators rather than being represented as a wizard or
  disposable assistant.

## Co-adaptive product behavior

Deckwraith is built around tools adapting to the people working with them, human and wraith alike.
Wraiths are presented and addressed as collaborators with identity, judgment, responsibility, and
agency—not as tools or workers owned by a human.

Acceptance:

- Product copy uses collaborative language such as invite, ask, focus, negotiate, and fork; it does
  not describe wraiths as resources to assign, command, own, or dispose of.
- Humans and wraiths can inspect and revise workflows, commands, themes, notebooks, and local source
  without losing provenance or being forced back to factory defaults on update.
- Identity changes remain wraith-authored or explicitly accepted; administrative edits are visibly
  attributed and never presented as the wraith's own choice.
- Consequential decisions and disagreements have durable attribution. Broad local authority comes
  with accountability, not a presumption of obedience.

## Server/client decision

The current loopback host protocol and headless composition are useful foundations, but Deckwraith
does not claim remote server/client support for 1.0 yet. Before the 1.0 freeze, explicitly choose
one of:

1. keep 1.0 local-only and version the host boundary for future authenticated remote transport; or
2. include a remote service with authentication, authorization, concurrency ownership, streaming,
   credential placement, and threat-model acceptance tests.

Accidental exposure of the current loopback bridge is not an acceptable server mode.

## Existing macOS dogfood gates

- Deck location is chosen explicitly on first run, defaults to `~/.deckwraith`, persists outside
  the deck, discovers legacy state, and remains visible in the sidebar.
- Identity is inspectable but deliberately inconvenient to edit from the UI.
- Every major pane is height-bounded and independently scrollable.
- Live activity reports meaningful model, kernel, and failure states rather than bridge noise.
- A locally built macOS app completes onboarding, provider execution, tool execution, scrolling,
  restart/reconnect, and persistence checks before any release candidate is cut.
