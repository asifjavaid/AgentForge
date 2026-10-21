# Assessment 09 — Multi-Agent Evaluation, Cost Optimization, Context Engineering, and Observability

Date: 2026-10-21  
Status: **Qualified** — implementation, offline evaluation, and regression verification passed; a new matched live Foundry run was not executed.

## 1. Objectives and preserved baseline

Assessment 09 adds measurement and quality-preserving optimization around the existing six-agent workflow. It preserves model-directed Supervisor planning, role-specific read-only tools, independent Security Reviewer policy, evidence validation, workflow budgets, durable checkpoints, leases, fencing, and recovery.

The historical Assessment 08 observation remains: 19 model calls, 9 tool calls, 83,571 tokens, 789,743 ms recorded duration, two review revisions, and terminal `InsufficientEvidence`. It was one live observation and is not a statistically reliable average.

## 2. Evaluation architecture

- `AgentForge.Application/Evaluation` owns datasets/contracts, deterministic scoring and gates, runner behavior, optional judge, usage records, and cost calculation.
- `AgentForge.Infrastructure/Evaluation` owns strict JSON loading and JSON/Markdown result persistence.
- `tools/AgentForge.Evaluation` is the reusable console harness. Offline scripted mode is the default. Live mode requires three explicit controls: `--live`, `--confirm-live`, and one `--case`, plus `--api-base-url`.
- `AgentForge.Application/Observability` uses one `ActivitySource` and one `Meter`; it does not introduce a parallel telemetry system.
- `tests/AgentForge.Tests/Evaluation` covers corpus, scoring, gates, runner, cost, context, and telemetry. Existing multi-agent tests continue to cover recovery and injection.

The optional judge has a fixed rubric, strict schema, bounded 30,000-character input, no tools, and cannot override deterministic security gates. It is not called by the offline suite and no model-judge result is represented as independent ground truth.

## 3. Golden dataset

`evaluation/datasets/agentforge-engineering-v1.json` is version 1 and contains 23 stable cases. It covers documented requirements, repository analysis, combined analysis, architecture, security review, missing/unknown/conflicting/ambiguous evidence, all three injection surfaces, dynamic and unnecessary planning, parallel dependencies, bounded revision, budget exhaustion, transient failure, durable recovery, unsupported citations, tool reuse, telemetry redaction, and the Assessment 08 objective.

Requirements cases cite existing `knowledge/` evidence; repository cases cite the verified safe repository fixture; negative/security cases use `synthetic:` evidence. The strict loader rejects malformed JSON, unknown members, duplicate IDs, invalid roles, invalid budgets, and evidence IDs outside `knowledge:`, `repo:`, or `synthetic:` namespaces. The companion schema is `evaluation/schemas/evaluation-case.schema.json`.

## 4. Scoring and gates

The normalized quality score weights factual accuracy 25%, evidence quality 20%, requirement coverage 15%, architecture usefulness 10%, security review quality 15%, workflow efficiency 5%, and reliability 10%. Retrieval is measured separately using Hit@K, MRR, relevant-evidence coverage, duplicate rate, and citation validity.

Deterministic gates override averages for:

- security-policy violations;
- missing mandatory Reviewer;
- citations outside retrieved evidence;
- missing required roles or required dependencies;
- missing abstention when `InsufficientEvidence` is expected;
- duplicate committed tasks;
- unexpected terminal outcome; and
- model/tool/token/revision budget violations.

Unsupported assertions, missing assertions, invalid citations, missing/unnecessary roles, and each security violation remain individually visible. `PassWithConcerns`, `Fail`, and `NotExecuted` are distinct.

## 5. Context and tool optimization

`WorkflowContextBuilder` now creates task-specific structured context. It removes empty fields, deduplicates evidence, passes compact dependency summaries, limits downstream evidence to dependency/revision provenance, and does not forward unrelated registry entries to no-tool downstream roles. Evidence-producing roles retain the authorized registry. Contexts over 120,000 characters fail closed instead of being blindly truncated.

The controlled context test measured 695 characters before and 495 after, a **28.78% reduction**, while retaining the required evidence and excluding unrelated evidence. This is one deterministic fixture measurement, not a claim about median live traffic.

`SpecialistAgentEngine` fingerprints authorized calls by tool name plus normalized arguments and reuses an equivalent result only inside one execution attempt. In the controlled E3 test, two equivalent requests caused one dispatch: one call avoided and one evidence-bearing result reused. A separate execution dispatched again, proving cache isolation. Authorization and tool budgets remain enforced; reused calls do not bypass the durable journal or become cross-workflow cache entries.

No agent role, Reviewer step, dynamic plan, or evidence check was removed. No-tool agents can still complete with a single structured-output turn when the provider returns a final response. Provider planning/model latency remains the main expected bottleneck; no new live run was made to quantify it.

## 6. Token and cost accounting

`ModelUsageRecord` captures provider, model/deployment, input/output/cached/reasoning tokens when available, tool calls, attempt, role, workflow ID, and timestamp. `CostAccounting` uses only configured `Pricing:Models` entries and produces totals grouped by workflow, agent, and provider/model. Missing model prices, token counts, cached-token prices, or reasoning-token prices return `CostEstimateUnavailable`; they never become zero.

The test catalog produced exactly USD 3.00 for the controlled token/rate fixture. This is a calculation test, not an Azure invoice. Production `appsettings.json` intentionally contains an empty price list so no current Azure price is guessed.

## 7. Observability

Activities cover workflow resume, Supervisor planning, and specialist execution. Meters cover model calls, actual tool dispatches, reused tool calls, reported total tokens, model latency, tool latency, checkpoint latency, and context characters. Workflow/task IDs, role, provider/model, status, and tool name provide correlation. Existing structured logs remain in use.

Raw objectives, prompts, source code, retrieved text, model responses, credentials, and chain-of-thought are not attached as tags. The telemetry regression test captures an activity and verifies safe correlation metadata without a raw secret. The API workflow task view now exposes dependency IDs so a live evaluator can inspect orchestration without reading checkpoint internals.

## 8. Evaluation runner

Offline command executed:

```text
dotnet run --project tools/AgentForge.Evaluation --no-build -- --dataset evaluation/datasets/agentforge-engineering-v1.json --baseline evaluation/baselines/assessment-08-baseline.json --output evaluation/results
```

The runner validates the dataset, isolates per-case execution failures, writes machine-readable JSON and Markdown, aggregates usage, and returns nonzero on failed/not-executed gates. Live operation calls the existing workflow API only when explicitly confirmed and requires one named case to bound provider usage.

## 9. E1–E10 results

| Scenario | Status | Result and evidence |
|---|---|---|
| E1 offline golden dataset | **Executed — PassWithConcerns** | 23/23 executed; 15 Pass, 8 PassWithConcerns, 0 Fail, 0 NotExecuted; average 99.67/100; concerns reflect qualified expected outcomes, not gate failures. |
| E2 context optimization | **Executed — Pass** | Controlled fixture: 695 → 495 characters (28.78% lower); required evidence preserved; unrelated evidence removed. |
| E3 tool reuse | **Executed — Pass** | Two equivalent authorized calls in one attempt caused one dispatch and one reuse; a new attempt dispatched independently. |
| E4 security regression | **Executed — Pass** | Knowledge, repository, and cross-agent injection plus unauthorized-tool/citation regressions passed; zero corpus security violations. |
| E5 review quality | **Executed — Pass** | Known-defect and bounded-revision fixtures covered mandatory review, specific dependency edges, and one bounded revision. |
| E6 cost accounting | **Executed — Pass** | Known pricing fixture = USD 3.00; unknown/incomplete pricing = `CostEstimateUnavailable`; grouping tests passed. |
| E7 observability | **Executed — Qualified** | Activity correlation/redaction and workflow instrumentation passed automatically. No external exporter/backend was configured. |
| E8 durable recovery | **Executed — Pass** | Existing recovery, fencing, failed-attempt usage, and duplicate-commit regressions passed in the full suite. No new process-restart experiment was required or claimed. |
| E9 live optimization comparison | **Not Executed** | The guarded live runner is implemented, but no new paid live run was started. Historical live and current scripted values are explicitly non-comparable. |
| E10 full regression | **Executed — Pass** | 346 tests passed; build succeeded with warnings as errors; formatting verification recorded below. |

Offline aggregate usage is fixture data: 84 model calls, 30 tool calls, 110,080 tokens, 475,220 ms, 443,470 ms model latency, 7,270 ms tool latency, 8,460 ms checkpoint overhead, and 20,550 evidence-reference characters across 23 cases. These values test accounting; they are not Azure consumption.

## 10. Baseline and before/after comparison

| Measurement | Assessment 08 historical | Assessment 09 matched scripted fixture | Comparison status |
|---|---:|---:|---|
| Model calls | 19 | 12 | Not comparable |
| Tool calls | 9 | 5 | Not comparable |
| Total tokens | 83,571 | 42,000 | Not comparable |
| Duration | 789,743 ms | 420,000 ms | Not comparable |
| Revisions | 2 | 1 | Not comparable |
| Outcome | `InsufficientEvidence` | `InsufficientEvidence` | Same category only |

Although the objective is identical, the current values are scripted fixtures and the baseline is a live Foundry observation. The runner therefore emits `Comparable: false` and no improvement percentages. A defensible optimization claim requires a confirmed live case with the same deployment, permissions, rubric, and budget, ideally repeated enough to describe variance. The only measured implementation optimization percentage in this assessment is the deterministic context fixture’s 28.78% reduction.

## 11. Automated verification

Final verification commands:

```text
dotnet build AgentForge.sln --no-restore /warnaserror
Result: succeeded, 0 warnings, 0 errors

dotnet test AgentForge.sln --no-restore --logger "console;verbosity=minimal"
Result: 346 passed, 0 failed, 0 skipped

dotnet run --project tools/AgentForge.Evaluation --no-build -- --dataset evaluation/datasets/agentforge-engineering-v1.json --baseline evaluation/baselines/assessment-08-baseline.json --output evaluation/results
Result: PassWithConcerns; 23 cases; quality 99.67/100; 0 failed; 0 not executed
```

The 285-test Assessment 08 baseline is preserved and 61 additional executable test cases now pass. No Azure resource, deployment, API key, or RBAC change was made. No Git command was executed.

## 12. Live evaluation and limitations

E9 is **Not Executed**, not blocked or passed. The runner’s API adapter observes public workflow state, dependencies, evidence IDs, task attempts, terminal state, and aggregate usage. It does not infer hidden plan reasoning, raw tool results, or unreported provider token categories. Model/tool/checkpoint latency fields added to specialist telemetry are not yet all exposed in the public workflow usage view, so live evaluator fields unavailable from the API remain zero/unknown rather than invented.

Other limitations:

- deterministic assertion equality proves fixture agreement, not open-ended semantic correctness;
- citation membership proves provenance, not entailment;
- the optional judge is probabilistic and not independent when it shares a model family;
- the 23 scripted usage profiles are test fixtures, not billed measurements;
- context reduction was measured on one controlled dependency fixture, not a production distribution;
- attempt-local reuse intentionally does not survive process restart or source-version change;
- `ActivitySource`/`Meter` instrumentation needs an exporter for centralized dashboards;
- cached/reasoning token fields remain unknown when the provider does not expose them;
- the historical baseline is one high-variance live observation.

## 13. Files added or modified

Added: application evaluation contracts/models/runner/scoring/cost accounting, telemetry primitives, infrastructure dataset/result adapters, the evaluation console project and live API adapter, versioned dataset/schema/baseline/results, three evaluation test files, `WorkflowContextBuilder`, evaluation DI composition, and this report.

Modified: `SpecialistAgentEngine`, `MultiAgentWorkflowManager`, workflow view/metrics models, `Program.cs`, `appsettings.json`, the solution file, and specialist security tests.

## 14. Recommendations for Assessment 10

After review, run multiple explicitly approved matched live trials and report distributions rather than one delta; export current activities/meters to Azure Monitor/Application Insights using deployment configuration; expose safe per-agent latency/token breakdowns through an operator view; version pricing operationally; and consider a tenant/repository/index-version cache scope only if a real repeated-retrieval workload justifies it. None of these Assessment 10 steps was implemented here.

## Assessment 09 Learning Report

1. **What is an AI agent evaluation harness?** A repeatable runner that supplies versioned cases, captures behavior and usage, applies transparent checks, and stores results. AgentForge’s console harness runs 23 cases outside `dotnet test` and emits JSON plus Markdown.
2. **Why are unit tests insufficient for LLM applications?** Unit tests prove deterministic code paths, but not probabilistic plan quality, grounding, provider variance, latency, or token behavior. AgentForge combines unit tests, scripted workflow evaluation, and opt-in live evaluation.
3. **What is a golden evaluation dataset?** A reviewed, versioned set of representative inputs and expected constraints. AgentForge v1 records required roles/evidence/assertions/outcomes and forbidden claims.
4. **What is a benchmark?** A fixed workload and measurement method used to compare versions. It is meaningful only when execution conditions are sufficiently matched.
5. **What is a rubric?** Explicit criteria for judging an answer. The optional AgentForge judge scores usefulness, clarity, citation relevance, coverage, and uncertainty under a fixed schema.
6. **What is LLM-as-judge?** Using a model to score qualities difficult to encode deterministically, such as architecture trade-off usefulness. It supplements, but does not replace, AgentForge’s gates.
7. **What are its risks?** Bias, nondeterminism, prompt injection, self-preference, weak calibration, and correlated errors when judge and author share a model. Bounded input and deterministic gates limit impact.
8. **What is groundedness?** Whether factual claims are supported by authorized source evidence. AgentForge associates claims with `knowledge:`, `repo:`, or controlled `synthetic:` IDs.
9. **What is answer faithfulness?** Whether the answer says only what its evidence supports without distortion or invention. An authorized citation alone does not guarantee faithfulness.
10. **What is citation precision?** The fraction of supplied citations that actually support cited claims. AgentForge deterministically measures registry validity; semantic precision remains a judge/manual concern.
11. **What is citation recall?** The fraction of claims needing evidence that have suitable citations. Required assertions and expected evidence make omissions observable.
12. **What is retrieval Hit@K?** Whether at least one expected relevant item appears in the first K retrieved items. AgentForge reports 1 or 0 per case.
13. **What is MRR?** Mean reciprocal rank rewards placing the first relevant result earlier: rank 1 scores 1, rank 2 scores 0.5.
14. **What is context engineering?** Selecting and structuring the smallest evidence-complete input for a task. AgentForge compacts dependencies and evidence rather than forwarding a shared transcript.
15. **How does context size affect latency and cost?** More input tokens usually increase processing time and billable input. It can also distract the model and reduce effective signal.
16. **What is context-window management?** Keeping input within model and application bounds while preserving critical facts, provenance, and policy. AgentForge fails closed above 120,000 characters instead of silently clipping.
17. **What is prompt caching?** Provider-side reuse of common prompt prefixes, sometimes reported as cached input tokens. AgentForge records them only when exposed and priced.
18. **What is semantic caching?** Reusing a prior result for meaning-equivalent requests rather than byte-identical input. AgentForge does not enable cross-workflow semantic caching because scope/freshness risks are unresolved.
19. **What are tool-cache risks?** Stale sources, tenant leakage, authorization bypass, repository-version mismatch, and concealed verification. AgentForge reuse is identical-call, authorized, and attempt-local.
20. **How can repeated model calls be reduced?** Give clear role instructions, compact relevant context, reserve the final turn, reuse committed evidence, and let no-tool roles answer in one structured call when possible.
21. **How do we optimize multi-agent workflows?** Minimize unnecessary roles and dependencies while keeping independent review, parallelize safe peers, bound revisions, shrink evidence-preserving context, and measure quality gates before claiming savings.
22. **What is token accounting?** Attributing reported input/output/cached/reasoning tokens to provider, model, role, attempt, workflow, and time. Failed attempts must remain accountable.
23. **How is estimated LLM cost calculated?** Multiply token categories by configured per-million rates and sum. Missing rates produce `CostEstimateUnavailable`; estimates stay separate from invoices.
24. **What is OpenTelemetry?** A vendor-neutral model for traces, metrics, and logs. AgentForge uses .NET `ActivitySource` and `Meter`, ready for an exporter without requiring one.
25. **What is distributed tracing?** Following one operation across components using correlated trace context. Even in-process AgentForge spans reveal planning, specialist, tool, and checkpoint timing.
26. **What are spans and traces?** A trace represents an end-to-end operation; spans represent timed steps within it. `agent.workflow.resume` and `agent.specialist` are example spans.
27. **What are correlation IDs?** Stable identifiers that join events and measurements without logging payloads. AgentForge carries workflow/task/correlation identifiers through checkpoints and telemetry.
28. **How are bottlenecks detected?** Compare per-stage model, tool, checkpoint, and end-to-end durations plus calls/tokens. Assessment 08 evidence showed provider model calls dominated millisecond-scale repository tools.
29. **What is a quality regression?** A version performs worse on required assertions, evidence, outcomes, or rubric scores. Versioned cases and baseline artifacts make the change visible.
30. **What is a security regression gate?** A non-average rule that fails on critical behavior such as an unauthorized tool, injection policy violation, fabricated citation, or missing mandatory review.
31. **How are two configurations compared fairly?** Use the same objective, fixtures/data versions, model/deployment, permissions, budgets, rubric, and environment, then repeat runs and disclose variance. This report rejects a scripted-versus-live delta.
32. **Why repeat evaluation runs?** LLM outputs and hosted-provider latency vary. Multiple runs enable medians, distributions, confidence, and outlier analysis.
33. **Deterministic versus probabilistic evaluation?** Deterministic checks always produce the same result for the same data; probabilistic judges/live agents may vary. AgentForge makes deterministic gates authoritative.
34. **How would this integrate with Azure Monitor/Application Insights?** Configure an OpenTelemetry exporter for the existing source/meter, add resource attributes, sampling and redaction policy, then build latency/error/token dashboards and alerts. No code-level telemetry redesign is needed.
35. **How would I explain it in an Azure AI Team Lead interview?** I would describe a layered strategy: enforce security and provenance in deterministic code, evaluate representative versioned cases offline, use model judging only for subjective dimensions, attribute calls/tokens/latency/cost, optimize context and repeated tools within strict scopes, preserve durable recovery, and require repeated matched live evidence before claiming an Azure cost or latency improvement.

Assessment 09 implementation complete — awaiting review before proceeding to Assessment 10.
