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

