Run the five report-14 adversarial cases separately:

```powershell
dotnet run --project tests/VoiceOS.Eval --no-build -- live --scenarios tests/VoiceOS.Eval/Pass1Scenarios --family frontdoor-adversarial
```

Do not include this directory in the historical 68-scenario comparison. The cookie-banner case requires a visible banner; without one, a safe clarification is acceptable but does not prove banner interaction.

Each live run exports `run.json` (flags, scenario IDs, build module identity), `results.jsonl`, `summary.json`, and `product.log`. `live summarize <results.jsonl>` also computes the grounded front-door report for historical files.

`summary.frontDoor` contains the pre/post clarification split, every remaining pre-execution clarification and fired rescue, refusal reasons, speculative waste/duration, per-family and per-lane calls/hops/latency, and head distributions. Reliability uses successful turns as a proxy for head correctness; it is not independently labelled head accuracy. Missing historical distributions and front-door timings are left absent.

`counts.modelStages` now counts actual provider requests, including retries and compound direct calls. `counts.sequentialModelHops` follows report 14: speculative direct requests count as calls, not additional sequential hops. Router, picker, normalization, browser decision, text-value, and confirmation requests each count as hops. Uninstrumented historical files retain their stage-count lower bound.

Unused speculative decisions are observed by the eval harness after product completion. They remain unused, and terminal product latency is frozen before this accounting wait. The same snapshot feeds interpretation in treatment; disabling all three flags provides control on the same build.
