# Operating Deckwraith

Deckwraith has two composition roots: the desktop product and the headless CLI. They use the same
application runtime, providers, kernels, tools, and durable state formats.

## State repository

The desktop resolves its deck path in this order:

1. `--deck-path /absolute/path`
2. `DECKWRAITH_DECK_PATH`
3. The platform-local application-data directory under `Deckwraith/deck-state`

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
| `openai-codex-subscription` | Existing Codex sign-in with ChatGPT | Not applicable |
| `anthropic` | `ANTHROPIC_API_KEY` | `DECKWRAITH_ANTHROPIC_BASE_URL` |
| `google-gemini` | `GEMINI_API_KEY` | `DECKWRAITH_GOOGLE_BASE_URL` |
| `openai-compatible` | `OPENAI_API_KEY` | `DECKWRAITH_OPENAI_BASE_URL` |

The default HTTP endpoints are Anthropic's Messages API, Google's Gemini API, and OpenAI's
Responses API respectively. Override variables must contain an absolute base URI; Deckwraith adds
the provider-specific API path.

### ChatGPT subscription

The subscription adapter launches `codex app-server` and uses Codex's built-in `openai` provider.
It does not require `OPENAI_API_KEY`. Sign in with ChatGPT using the
[official OpenAI authentication flow](https://learn.chatgpt.com/docs/auth), then verify that the
Codex executable can read the session.

Deckwraith resolves the executable in this order:

1. `DECKWRAITH_CODEX_PATH`
2. `/Applications/ChatGPT.app/Contents/Resources/codex` on macOS when present
3. `codex` from `PATH`

Example:

```text
DECKWRAITH_CODEX_PATH=/Applications/ChatGPT.app/Contents/Resources/codex \
dotnet run --project src/Deckwraith.Headless -- run-openai \
  /path/to/deck-state wraith1 deckwraith gpt-5.6-sol \
  "Inspect the project and finish one bounded improvement" "Begin."
```

The bridge injects the complete current identity and materialized provider-neutral context on every
invocation. Deckwraith retains authority over tool execution and accepts only its constrained tool
envelope from the model; Codex-native commands are disabled at this boundary.

### API-backed providers

Examples:

```text
ANTHROPIC_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith anthropic MODEL OBJECTIVE MESSAGE

GEMINI_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith google-gemini MODEL OBJECTIVE MESSAGE

OPENAI_API_KEY=... dotnet run --project src/Deckwraith.Headless -- run-provider \
  /path/to/deck-state wraith1 deckwraith openai-compatible MODEL OBJECTIVE MESSAGE
```

Credentials are read from the host environment at invocation time and are not written into normal
provider configuration. They can still leak transitively if a tool, model, log, or artifact emits
them, so this is not a repository-sanitization guarantee.

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
