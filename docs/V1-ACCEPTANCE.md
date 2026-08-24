# V1 acceptance ledger

Status: historical architecture milestone evidence
Last verified: 2026-08-23

This is not the current product-release gate. The active macOS dogfood and 1.0 requirements live in
[PRE-1.0.md](PRE-1.0.md); release work is paused until that gate is complete.

This ledger maps the architecture scenario in [SPEC.md](../SPEC.md#22-acceptance-criteria-for-the-architecture)
to executable evidence. The scenario is intentionally composed from focused deterministic tests;
the live bootstrap gate exercises the assembled runtime against the ChatGPT-subscription provider.

| # | Required behavior | Evidence | Status |
| --- | --- | --- | --- |
| 1 | Canonical wraith and haunt names without UUIDs | `StateSpineEndToEndTests.StateSpinePreservesNamesHistoryArtifactsAndCleanCheckpoints` | Green |
| 2 | Sparse, wraith-editable identity and assignments | `IdentityEditingEndToEndTests.IdentityPersonalityAndCalibrationAreEditedAsOneCheckpoint`; host bridge acceptance | Green |
| 3 | Mixed PowerShell/C# deckbook exchanges a value and artifact | `CSharpDeckbookEndToEndTests.PowerShellAndCSharpExchangeCanonicalValuesAndArtifacts` | Green |
| 4 | Exact suffix staleness without execution or output loss | `DeckbookRuntimeTests.StructuralEditsUseSparseOrderAndInvalidateOnlyTheLinearSuffix`; randomized structural theories | Green |
| 5 | MCP side effect occurs only during explicit suffix execution | `DeckbookToolAcceptanceTests.ExplicitSuffixUsesAuthoredAndMcpToolsWithDurableProvenance` | Green |
| 6 | Cold kernel replacement never replays and retains durable state | PowerShell and C# kernel end-to-end tests; `RunspaceLossTests` | Green |
| 7 | `context.json` is the exact materialized next-request context | `InferenceSpineEndToEndTests.FakeProviderTurnPersistsContextToolsElisionAndOperationLifecycles` | Green |
| 8 | Paired tool elision preserves complete archive records | inference end-to-end and core `ContextTests` | Green |
| 9 | Bounded context includes pins/window/index, not the whole notebook | `DeckbookRuntimeTests.ContextProjectionIncludesPinsAndActiveWindowButExcludesUnrelatedLargeCells` | Green |
| 10 | MCP command is discovered through PowerShell help and preserves objects | `McpInferenceEndToEndTests.ModelDiscoversAndExplicitlyExecutesMcpThroughOnlyPowerShell` | Green |
| 11 | Authored PowerShell tool runs from a cell with Git-visible provenance | `DeckbookToolAcceptanceTests.ExplicitSuffixUsesAuthoredAndMcpToolsWithDurableProvenance` | Green |
| 12 | Behavioral context and archives remain wraith-private | `InferenceSpineEndToEndTests.ModelContextAndArchiveStayPrivateToTheActiveWraith` | Green |
| 13 | Compaction covers only the oldest exact prefix and preserves raw state | `CompactionEndToEndTests.OldestContiguousPrefixUsesIndependentModelAndPreservesRawState`; coverage properties | Green |
| 14 | Crash recovery reconciles outcome-unknown without blind replay | `RecoveryEndToEndTests.CrashRecoveryMarksUnknownRebuildsProjectionAndRollsShellCold` | Green |
| 15 | Git reversal preserves the history it reverses | `RecoveryEndToEndTests.ReversalCreatesRecoveryBranchAndNewInverseCommit` | Green |
| 16 | Rename updates mutable references and reserves historical aliases | `StateSpineEndToEndTests` rename/recovery scenarios | Green |
| 17 | A different provider shell inherits the same durable wraith | inference end-to-end explicitly replaces a `fake` shell with `capture` while retaining identity/objective/context; all provider contracts; live subscription bootstrap | Green |
| 18 | Core lifecycle runs on Linux without desktop dependencies | `eng/verify-headless.sh`; Linux CI | Green |

Additional v1 product evidence:

- All 79 .NET tests pass in Release configuration.
- Renderer type-check, host-protocol compatibility, lint, and production build pass.
- Host tests cover bridge versioning, request idempotency, reconnect replay, and refresh after a gap.
- Manual desktop acceptance covers onboarding, identity personality/calibration edits, run lifecycle,
  PowerShell deckbook execution, archive/checkpoint inspection, renderer restart/reconnect, wraith
  creation, and a clean browser console. Archival/restoration is covered through the typed host
  bridge and integration suites.
- Release automation publishes a headless payload with no Electron/Chromium dependencies and builds
  native Electron bundles on macOS, Linux, and Windows.
- Production dependency audits for both the renderer and packaged Electron host report no known
  vulnerabilities at the time of this candidate.

## Live inside-out bootstrap

On 2026-08-23, a calibrated `bootstrap` wraith completed run
`01a0319ca1f4783d92c9524be0624c33` through `openai-codex-subscription` and `gpt-5.6-sol` using the
installed ChatGPT session. Starting from source commit `1374760640606d40077b6c706ceb369d0eace5a1`,
it used only Deckwraith's constrained `Invoke-PowerShell` surface to inspect the specification,
acceptance ledger, subscription adapter, headless composition, and inference tests.

The wraith ran 17 focused tests successfully, found no release-blocking defect, changed no source
files, and persisted structured agent-scoped `bootstrap-v1` evidence with content hash
`sha256:b693cf542c31cde61cc50a9d9a61168c4d043d8e504c75358346222de11303b2`. One exploratory tool
call failed on a PowerShell serialization edge and was durably closed as failed; the wraith adapted
without replay and completed five subsequent calls.

Post-run inspection verified:

- terminal run and shell records with an explicit completion reason;
- identity personality plus `register` and `opsec` calibration in every model request;
- `context.json` revision 9, turn 1, archive frontier 35, six tool interactions, and two messages;
- 37 sequenced archive records with paired operation lifecycles, including the recovered failure;
- the persisted durable evidence value, a clean deck worktree, a clean source worktree, and a clean
  deck `git fsck`;
- final deck checkpoints for durable-state write, model-turn completion, and run completion.

The same candidate also passed the complete 79-test Release suite, renderer protocol/lint/build,
the portable headless dependency gate, and local signed macOS ZIP/DMG packaging for version 1.0.0.
