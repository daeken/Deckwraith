# Operating Deckwraith

Deckwraith has two composition roots: the desktop product and the headless CLI. They use the same
application runtime, providers, kernels, tools, and durable state formats.

## State repository

The desktop resolves its deck path in this order:

1. `--deck-path /absolute/path`
2. `DECKWRAITH_DECK_PATH`
3. The folder selected and saved by the desktop
4. A previously initialized legacy deck under the platform-local `Deckwraith/deck-state`
5. `~/.deckwraith`

Before initializing a new deck, the desktop shows the resolved folder, allows direct path entry or
a native folder chooser, and can open an existing deck instead. The active absolute path remains
visible in the sidebar after initialization. The small desktop preference file contains only that
path; the deck itself remains the authority for durable state.

The CLI always takes the deck path as its second argument. Initialize it before other operations:

```text
dotnet run --project src/Deckwraith.Headless -- init /path/to/deck-state
```

Deckwraith initializes restrictive filesystem permissions where supported and does not configure
a Git remote. The repository is still credential-equivalent: current files, Git objects, reflogs,
archives, artifacts, and backups may contain model context, tool arguments, results, or secrets.

Use one owning Deckwraith process per deck. Lifecycle coordination and runtime managers are shared
inside a host, but v1 does not claim cross-process transactionality for two desktop/headless
processes writing the same repository concurrently.

## Providers

Provider and model are selected independently for each disposable shell. Model names are passed to
the selected provider without Deckwraith aliases.

| Provider ID | Authentication | Base URL override |
| --- | --- | --- |
| `openai-codex-subscription` | Deckwraith ChatGPT session in the platform credential store | `DECKWRAITH_OPENAI_SUBSCRIPTION_BASE_URL` |
| `anthropic` | `ANTHROPIC_API_KEY` | `DECKWRAITH_ANTHROPIC_BASE_URL` |
| `google-gemini` | `GEMINI_API_KEY` | `DECKWRAITH_GOOGLE_BASE_URL` |
| `openai-api` | `OPENAI_API_KEY` | `DECKWRAITH_OPENAI_BASE_URL` |
| `xai-api` | `XAI_API_KEY` | `DECKWRAITH_XAI_BASE_URL` |
| `zai-api` | `ZAI_API_KEY` | `DECKWRAITH_ZAI_BASE_URL` |
| `openai-compatible` | `OPENAI_API_KEY` | `DECKWRAITH_OPENAI_BASE_URL` |

The default HTTP endpoints are Anthropic's Messages API, Google's Gemini API, and OpenAI's
Responses API respectively. Override variables must contain an absolute base URI; Deckwraith adds
the provider-specific API path.

### ChatGPT subscription

The subscription adapter talks directly to OpenAI's Codex Responses transport. It does not start
Codex or a local proxy, and it never falls back to `OPENAI_API_KEY` billing. This follows OpenAI's
[documented distinction between ChatGPT subscription sign-in and API-key access](https://learn.chatgpt.com/docs/auth).

During the current macOS dogfood phase, **Provider access → Use existing sign-in** imports an
existing `~/.codex/auth.json` ChatGPT session into Deckwraith's own macOS Keychain item. The import
is explicit; the source file is never copied into the deck. Deckwraith then refreshes the session
natively through OpenAI before expiry and retries one rejected request after a forced refresh. A
Deckwraith-owned browser sign-in remains part of the active pre-1.0 gate, so this import is a rapid
testing bridge rather than the finished onboarding flow.

Example:

```text
dotnet run --project src/Deckwraith.Headless -- run-openai \
  /path/to/deck-state wraith1 deckwraith gpt-5.6-sol \
  "Inspect the project and finish one bounded improvement" "Begin."
```

The adapter injects the complete current identity and materialized provider-neutral context on
every invocation. Deckwraith retains authority over tool execution; only the tools in its canonical
request are exposed to the model.

### API-backed providers

Examples:

```text
ANTHROPIC_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith anthropic MODEL OBJECTIVE MESSAGE

GEMINI_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith google-gemini MODEL OBJECTIVE MESSAGE

OPENAI_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith openai-api MODEL OBJECTIVE MESSAGE

XAI_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith xai-api MODEL OBJECTIVE MESSAGE

ZAI_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith zai-api MODEL OBJECTIVE MESSAGE
```

`openai-compatible` remains registered as a compatibility alias for pre-1.0 decks that already
reference it. New OpenAI shells should select `openai-api`.

API credentials are currently read from the host environment at invocation time. Subscription
credentials use the macOS Keychain; platforms without an integrated credential store currently use
an atomic owner-only fallback under the platform application-data directory. Neither source writes
credentials into ordinary provider configuration or the deck. Credentials can still leak
transitively if a tool, model, log, or artifact emits them, so this is not a
repository-sanitization guarantee.

## Atomic file edits

Hosted wraith runspaces expose `Invoke-DwFileEdit`. One call validates and publishes a complete
batch across one or many UTF-8 files. Supported operation kinds are `write`, `prepend`, `append`,
`replace`, `json-set`, `json-remove`, `json-insert`, `json-append`, and `json-test`.

```powershell
$operations = @(
    [pscustomobject]@{
        path = 'src/example.txt'
        kind = 'replace'
        match = 'old anchor'
        replacement = 'new text'
        expectedCount = 1
    },
    [pscustomobject]@{
        path = 'settings.json'
        kind = 'json-set'
        pointer = '/features/deckwraith'
        value = $true
    },
    [pscustomobject]@{
        path = 'settings.json'
        kind = 'json-append'
        pointer = '/contributors'
        value = 'steward'
    }
)

$edit = @{
    RootPath = '/path/to/project'
    Operation = $operations
    CommitSubject = 'Adapt the project workflow'
    CommitBody = 'Update the exact text anchor and structured settings together.'
}
Invoke-DwFileEdit @edit
```

Every anchor count, JSON pointer/test, expected content hash, encoding, and root-relative path is
checked before publication. Paths cannot cross a symbolic link beneath the edit root. A missing
anchor or invalid JSON operation leaves every file untouched. The command stages same-directory
temporary files, rechecks originals for races, restores earlier files if a later publication fails,
retains recovery backups if restoration cannot complete, and returns per-file before/after hashes.
Use `expectedHash = 'sha256:…'` for optimistic concurrency or `expectedHash = 'missing'` when
creating a file.

The optional commit subject and body are returned as an edit-authored proposal. When the current
haunt has auto-commit enabled, `CommitSubject` is required, the configured project becomes the
default edit root, and the successful batch creates one project commit. The result's `Commit`
receipt contains the commit ID, repository path, author, and exact committed paths.

## Haunt project commits

Each `haunt.json` may contain a project policy like this:

```json
{
  "project": {
    "projectPath": "/path/to/project",
    "autoCommitEnabled": true,
    "author": {
      "mode": "wraith",
      "name": null,
      "email": null
    },
    "allowedPaths": ["src", "tests"],
    "allowDirtyWorkingTree": false
  }
}
```

Project auto-commit is off until explicitly enabled per haunt. `wraith` attribution uses the
current wraith's canonical name and `<wraith>@deckwraith.local`; a `fixed` author requires explicit
`name` and `email` values. Allowed paths are project-relative scopes. A false
`allowDirtyWorkingTree` rejects the edit before publication if the repository already has changes.

Deckwraith builds the commit from a temporary Git index containing only the successful edit
receipt, then realigns those paths in the existing index. Unrelated staged and unstaged changes are
preserved. Detached heads, unresolved conflicts, and merge/rebase/cherry-pick/revert/bisect state
are refused. Commit hooks are disabled for the automatic commit so a hook cannot add unrelated
paths or publish the commit. Deckwraith never runs `git push` from this policy.

## Desktop appearance

The desktop supports `system`, `dark`, and `light` modes. Appearance is saved in the platform
desktop preference file alongside the selected deck path, outside the deck and its sensitive Git
history. On macOS this is `~/Library/Application Support/Deckwraith/desktop.json`.

The appearance dialog also exposes semantic color tokens for background, surfaces, text, muted
text, accent, borders, danger, and success states. Overrides are validated hex colors and layer over
the selected built-in palette. Restoring built-in colors removes the overrides without changing the
selected mode.

## Desktop development and packaging

Build the renderer and .NET payload:

```text
cd ui
npm ci
npm run lint
npm run build

cd ..
dotnet publish src/Deckwraith.Desktop/Deckwraith.Desktop.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -o artifacts/desktop-publish/osx-arm64
```

Create the native Electron bundle for the current host:

```text
./eng/package-desktop.sh 1.0.0
```

Set `DECKWRAITH_ELECTRON_TARGET` to `osx`, `linux`, or `win` when host detection is not appropriate.
Electron.NET writes packaged output below `src/Deckwraith.Desktop/obj/artifacts/desktop/`.

The renderer is not an authority boundary. It connects only to the loopback host using the
versioned command/query/event protocol; provider credentials, filesystem mutation, kernels, and
tools remain in the .NET process.

## Wraith lifecycle

At most one nonterminal run may exist for a wraith. Complete or cancel it before starting another.
Different wraiths may run concurrently inside the owning host.

Archival is non-destructive and refuses an active run:

```text
dotnet run --project src/Deckwraith.Headless -- archive-wraith /path/to/deck-state wraith1
dotnet run --project src/Deckwraith.Headless -- restore-wraith /path/to/deck-state wraith1
```

Archived wraiths cannot start runs. Their identity, aliases, contexts, deckbooks, archives, run
records, artifacts, and Git history remain intact and become active again on restoration.

## Release gates

Run these from a clean source checkout before a release:

```text
./eng/verify-headless.sh osx-arm64

cd ui
npm ci
npm run lint
npm run build

cd ..
dotnet test Deckwraith.slnx -c Release --no-restore --nologo
./eng/package-desktop.sh 1.0.0
```

Pushing a `v*` tag triggers native packaging on macOS, Linux, and Windows and attaches the outputs
to a GitHub release.
