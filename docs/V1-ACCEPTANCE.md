# V1 acceptance ledger

Status: release candidate  
Last verified: 2026-08-23

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
| 17 | A different provider shell inherits the same durable wraith | provider contract suite plus inference shell-replacement end-to-end; live subscription bootstrap | Live gate pending |
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

## Release decision

Do not tag `v1.0.0` until one disposable deck completes a real inside-out run through
`openai-codex-subscription`, using the installed ChatGPT session, and its identity, context,
archive, run records, checkpoints, and clean Git state have been inspected. Record that evidence
here before changing the status from release candidate to released.
