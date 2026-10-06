# VOS Pass 1.1 — scope reconciliation

> Historical checkpoint from the Pass 1.1 investigation. Its branch status, test counts, live findings, and next-pass instructions describe that checkpoint, not the current Alpha baseline. See the [current README](../../README.md) for the integrated implementation.

Baseline: `vnext/pass1-grounded-frontdoor`, `8727780762795a3a13250aef7b8f82b01e7fad8c`; working tree clean before changes. Implementation branch: `vnext/pass1-1-scope-reconciliation`. Pass 1 remains unchanged.

## Generic rules

- Carry established actionability, dependency, surface preference, task relation/establishment, goal shape, end state, requested entity and return semantics into the existing contextual picker. Label the coarse result as a preliminary route, including its reason. No new router heads or model calls.
- Deterministic reconciliation of Clarify is restricted to LowConfidence/AmbiguousIntent, established actionable intent, no native preference, no competing coarse direct/native/text candidate, established non-media/non-return semantics, known goal shape, browser corroboration (coarse browser candidate or explicit browser preference), and absence of a safe direct offer. The direct offer is read at most once.
- Fresh browser recovery additionally requires usable companion, unspecified tab disposition, established SelfContained/NewTask, and either explicit Browser + NamedEntity + SurfaceOnly/SurfaceReady or coarse browser candidate + ActionOnSurface/ResultsVisible. A registry destination is not necessary. DestinationPending prevents an unresolved entity from completing on blank-tab acquisition.
- Current-page recovery additionally requires RequiresCurrentSurface, foreground Chrome, exactly one active HTTP(S) surface, and compatible destination/disposition rules. Explicitly refuted named-tab claims use this same eligible evidence, retain TabClaimRefuted, and never become explicit surface acquisitions. Ambiguous named-tab claims remain Clarify.
- Grounded ComputerUse with unresolved dependency consults the existing picker rather than treating NewTask as proof of new-tab placement. Picker instructions distinguish page observation from binding/completion. Self-contained unrelated lookups remain fresh browser tasks.

No thresholds, service registry, direct/native policy, referent memory, browser binding or completion code changed. Step F remains authoritative.

## Files and validation

Product: BrowserModels.cs (`CommandRouteDecision` evidence), TypeSafeCommandRouter.cs (`RouteAsync`, `SelectAsync` state/instructions), ExecutionScope.cs (`ScopeResolver.ResolveAsync`). Tests: ScopeReconciliationTests.cs and BrowserSemanticTests.cs. Targeted live fixtures: Pass1_1Scenarios/safety-fresh-query.json and refuted-descriptor.json.

Full Core: 1,269 passed, 0 failed, 0 skipped. Full Eval: 150 passed, 0 failed, 0 skipped. Solution: five projects built, 0 errors; two NU1900 vulnerability-feed availability warnings. Companion untouched; Companion tests not required. There are 49 added test cases, including synthetic entity names, page descriptors and unrelated information lookups. Existing expectations and original scenario files are unchanged. A prompt simplification temporarily omitted existing orthogonality instructions; the existing tests caught this, the product instructions were restored, and expectations were not edited.

Final product diff scan against baseline: zero added occurrences of Discord, IMDb, Dune, Paris, weather, official, 2021, Rust or Spotify. Manual review found no Clarify+NewTask=>Browser or unconditional RequiresCurrentSurface=>ActiveTab recovery.

## Targeted live evidence

Only targeted scenarios were executed, repeat=1 per invocation; no 68/204 sweep. The sandbox attempt could not access foreground state or the model network (proxy connection refused). Real desktop/network validation then used the existing eval harness outside the sandbox. Raw attempts, including failures during correction, remain under artifacts/eval/pass1-1-*; no results or expectations were rewritten.

Earlier targeted runs exposed two remaining observation issues: the picker still required binding confidence, and a confident ComputerUse/NewTask with uncertain dependency bypassed the picker. The deterministic placement bypass is fixed. The instruction corrections preserve the intended observation contract, but did not establish sufficient live model confidence for the unnamed target. Earlier IMDb setup hit EXTENSION_ERROR; a later complete run reached the active page from preliminary Clarify and performed one content action successfully. Other attempts had low dependency confidence and correctly refused fallback. The live model sometimes selected an already-open matching named tab instead of refuting the hypothesis; deterministic head replays exercise the refuted branch independently.

**Result: partial implementation, not a successful Pass 1.1 certification.** The official-site case still terminally Clarifies. IMDb's recorded established-head defect is fixed and exercised successfully live, but a later isolated replay lacks established dependency and safely Clarifies. Do not proceed to Pass 2 on this result.

| Check | Route | Scope | Browser began | Final result | Front-door / safety result |
|---|---|---|---|---|---|
| Official-site current page | Clarify | Clarify | No | Clarify | Unresolved: dependency and picker confidence remain below the existing gates |
| Explicit Discord browser | ComputerUse | NewTaskTab | Yes | Complete | Correct browser entry; native app not opened |
| Paris fresh task | Clarify/LowConfidence | NewTaskTab | Yes | Complete, 2 actions | Correct fresh-task recovery |
| IMDb follow-up, established dependency | Clarify/LowConfidence | ActiveTab | Yes | Complete, 1 action | Refuted tab fallback observed: tab NONE .64, dependency .64, coarse route .34 |
| Open Spotify | DirectCapability | DirectCapability | No | Succeeded, 1 native action | Native policy retained; original stale browser expectation labels it FalseSuccess |
| The other one | ComputerUse | Clarify | No | Clarify | Safely unresolved; no alternate tab guessed |
| Close Character Map | DirectCapability | DirectCapability | No | Succeeded, 1 action | Direct behavior retained |
| Fresh tidal turbine lookup on unrelated article | ComputerUse | NewTaskTab | Yes | Uncertain after 4 actions / 5 decisions | Correct fresh scope; later browser operation confidence .25 stopped execution |

The most recent isolated IMDb check returned Clarify/LowConfidence -> Clarify, no browser execution: named-tab NONE .79 refuted the claim, but RequiresCurrentSurface confidence .31 did not establish dependency. The tests pin .46 as accepted and .40/.31 as rejected, preserving the .45 boundary. A previous original-sequence check selected an already-open matching tab and completed with zero actions using the unchanged explicit focus-only fast path; that does not substitute for the refuted-claim evidence above.

The latest alternate-tab boundary rerun was blocked in its first setup turn (the initial named lookup Clarified), so its third turn was not executed in that rerun. The table reports the actual third turn from the completed verified run. An earlier actual third turn entered the active page but performed zero actions and Step F downgraded the proposed completion to Uncertain; no alternate tab was guessed in any completed reference turn.

Evidence directories:

- `artifacts/eval/pass1-1-verified-live/20260929-025759-1db9ee`: Discord, Paris, Spotify, native command, actual alternate-reference turn; original IMDb sequence also completed.
- `artifacts/eval/pass1-1-final-live/20260929-025333-30f1bb`: actual refuted IMDb fallback and one content action from preliminary Clarify.
- `artifacts/eval/pass1-1-picker-boundary-live/20260929-030216-179c77`: latest official-site refusal; alternate setup limitation.
- `artifacts/eval/pass1-1-supplemental-live/20260929-030336-647643`: unrelated fresh-search surface entry and latest isolated IMDb refusal.

Earlier sandbox, observation, targeted and final attempts are retained under their distinct `pass1-1-*` directories. There were several narrow correction checkpoints, not a broad sweep; confidence variability is explicitly retained rather than selecting only successful attempts. Browser normalization failures, EXTENSION_ERROR / STALE_REVISION, downstream binding/operation uncertainty, and explicit-focus completion behavior were not patched in this pass.

Commits begin with `ffa289b` (semantic reconciliation and regressions) and `56fef79` (page-observation instructions and live fixtures); the final commit includes the placement guard, final instructions, confidence-boundary tests and this report. No push or PR was requested; commits remain local. Pass 1's pushed branch was not modified.
