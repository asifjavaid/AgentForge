# Assessment 07 — Durable Agent State, Checkpointing, Recovery, and Concurrency

Date: 2026-10-18  
Baseline: Assessment 06 with 141 passing tests  
Final deterministic suite: 212 passing tests  
Scope: one logical software-engineering agent only; Assessment 08 was not started

## 1. Assessment objectives

Assessment 07 changes the existing single-request agent into an explicitly advanced durable workflow. A client creates a run, reads its status, resumes bounded work, or cancels the logical run. State survives a process restart, completed tool results are journaled and reused, stale executors are fenced out, and malformed checkpoints fail closed.

The existing `POST /api/agent/ask` contract remains available and unchanged. The durable API is separate and does not use fire-and-forget work.

## 2. Architecture overview

```text
HTTP durable-run API
        |
        v
DurableAgentRunManager
   |                |
   v                v
IAgentCheckpointStore     existing IToolCallingClient
   |                      and read-only tool dispatcher
   +-- File (local/default)
   +-- Azure Table (optional)
   +-- In-memory (tests)
```

The application layer owns lifecycle rules, budgets, ownership, fencing, checkpoint validation, tool journaling, final-answer parsing, and citation validation. Infrastructure owns persistence details. The model cannot choose the run ID, storage provider, endpoint, table, lease owner, checkpoint version, or fencing token.

Creation only persists `Created`. `POST .../resume` performs one bounded synchronous attempt. There is no in-process background task pretending to be durable execution.

## 3. Existing components reused

- `IToolCallingClient` and the existing Foundry/OpenAI provider selection.
- Assessment 06 model-directed tool loop and system policy.
- `ISoftwareAgentToolDispatcher` and the four registered read-only tools: `search_project_knowledge`, `list_files`, `search_code`, and `read_file`.
- Existing repository authorization, canonical-path checks, sensitive-file restrictions, knowledge retrieval, and evidence identifiers.
- Existing maximum model steps, tool-call count, repository-search count, result-character budget, timeout, token usage, final schema, and citation rules.
- Existing Foundry bounded retry behavior, including Retry-After handling.
- Existing API exception handling, Swagger, and JSON enum conventions.

## 4. Storage decision and trade-offs

### Selected design

`IAgentCheckpointStore` is provider independent and exposes create, get, and conditional update operations.

- **File store:** default local/development provider. Each run has a checksum-protected JSON envelope. Writes use a temporary file followed by an atomic replacement. A per-run file handle serializes local readers/writers, while the persisted numeric version remains the actual compare-and-swap guard. A fresh process/store instance can load the same directory.
- **Azure Table store:** optional distributed provider. It uses `Azure.Data.Tables` 12.13.0 and `DefaultAzureCredential`; it accepts no account key or connection string. Metadata and payload segments share the run ID as partition key. An ETag-conditional metadata replacement and new payload generation are submitted in one Table transaction.
- **In-memory store:** deterministic test fake using compare-and-swap versions.

### Why Azure Table is optional rather than the default

No Azure Storage account/table endpoint or RBAC assignment was supplied. The assessment prohibits automatic provisioning or RBAC changes. The application therefore defaults to a real local durable store while including an Azure Table provider ready for an already-provisioned table.

### Azure Table payload strategy

The checkpoint is serialized into bounded 48,000-byte segments. A metadata row stores generation, segment count, checksum, schema version, status, and update time. At most 99 segments plus metadata are allowed by the transaction bound. Metadata and the new generation commit atomically in one partition transaction. A reader follows only the generation referenced by metadata and verifies the assembled SHA-256 checksum.

Old payload generations are not currently reclaimed. This avoids a delete race with readers but requires a future retention/cleanup job. The application-level checkpoint maximum is 524,288 bytes by default, so normal checkpoints remain far below the Azure transaction ceiling.

## 5. Durable run schema

`DurableAgentCheckpoint` contains:

- schema version, opaque server-generated run ID, question, creation/update timestamps, and lifecycle status;
- model step, tool-call count, repository-search history through the journal, total tool-result characters, and resume count;
- resumable assistant/tool conversation messages, excluding system and duplicate user messages;
- tool executions with stable execution ID, name, arguments, normalized fingerprint, model step, status, completion time, bounded result, evidence, conversation-applied marker, duration, and safe error code;
- all tool fingerprints, evidence identifiers, tools used, provider/model, and cumulative input/output/total tokens;
- final answer, validated citations, termination reason, last failure classification, and total duration;
- execution owner, lease expiry, monotonically increasing fencing token, and store version/ETag.

No hidden reasoning, credentials, tokens, arbitrary CLR type information, or storage configuration is persisted. Raw tool output is bounded, but read-only repository contents needed for continuation are persisted; this is why the public API never exposes the internal checkpoint.

## 6. State machine

```text
Created ----> Running ----> Completed
   |             |--------> InsufficientEvidence
   |             |--------> Failed
   |             |--------> Cancelled
   |             `--------> WaitingToResume ----> Running
   `--------------------------------------------> Cancelled
```

Only the displayed transitions are legal. A `Running` checkpoint must have owner and lease. Any other state must have neither. `Completed`, `InsufficientEvidence`, `Failed`, and `Cancelled` are terminal.

- Resuming `Completed` or `InsufficientEvidence` returns the existing final result without a model call.
- Resuming `Failed` or `Cancelled` returns a conflict.
- Transient provider, timeout, storage, or execution-attempt cancellation releases ownership into `WaitingToResume`.
- Permanent response, argument, authorization, or protocol problems become `Failed`.
- Explicit cancel makes the logical run `Cancelled` and increments the fence.
- HTTP request cancellation only cancels the current attempt; it does not implicitly cancel the logical run.

## 7. Checkpoint lifecycle

| Boundary | Persisted guarantee | Recovery behavior |
|---|---|---|
| Run created | Request, ID, timestamps, zero counters | Resume acquires the first lease. |
| Lease acquired | Owner, expiry, new fence, resume count | Other resumptions conflict until expiry. |
| Model proposes tools | Assistant tool calls, counters, fingerprints, pending journal rows | Pending read-only calls may be reauthorized and re-executed. |
| Tool completed | Completed journal row, bounded result, evidence, duration | The completed tool is not dispatched again. |
| Result incorporated | Tool conversation message and `ConversationApplied=true` | Next model turn sees the same result exactly once. |
| Final answer validated | Terminal status, answer, evidence-subset citations, usage | Later resume returns the stored result. |
| Recoverable failure | `WaitingToResume`, failure class, released owner, duration | A later explicit resume continues from committed progress. |

There is deliberately a checkpoint between tool-result commit and conversation incorporation. If interruption occurs there, recovery applies the already committed result to conversation without re-executing the tool.

## 8. Tool execution journal

Every model-proposed tool call receives a durable row before dispatch. Fingerprints combine the tool name with normalized JSON arguments, preventing equivalent repeated calls with irrelevant JSON whitespace differences.

Before dispatch, the application reloads the latest checkpoint, verifies version/owner/fence/lease, and then calls the existing dispatcher. The dispatcher revalidates tool arguments and repository authorization; historical approval is not trusted.

A completed row contains the bounded result and evidence. Resume first processes pending rows, then applies completed-but-not-applied rows. Completed rows are never dispatched again. Pending rows are uncertain: because all tools are read-only, controlled at-least-once re-execution is acceptable.

This is idempotent processing of committed results, not a claim of exactly-once external execution. Write-capable tools would require tool-specific idempotency keys, transactional outbox/inbox patterns, external deduplication, or compensating actions before dispatch could be considered safe.

## 9. Concurrency and lease strategy

Acquisition is an optimistic conditional write. It sets a random attempt owner, increments `ResumeCount`, increments the fencing token, and assigns a bounded lease (120 seconds by default).

Every subsequent checkpoint write uses the exact loaded version/ETag. Before each model call, tool dispatch, and terminal transition, the manager reloads and verifies:

- version is unchanged;
- owner matches the current attempt;
- fencing token matches;
- status is `Running`;
- lease has not expired.

A second resume sees an active `Running` lease and receives conflict. If ownership expired, recovery first conditionally changes `Running` to `WaitingToResume`, clears the owner, and increments the fence. Acquisition then increments it again. A stale executor therefore cannot continue merely because it still remembers the old owner.

There is a narrow distributed-systems limitation between the final ownership check and an external read-only tool call. The lease could expire while that call is in flight, causing two overlapping reads. Conditional writes and fencing still prevent checkpoint corruption. This is acceptable for the current read-only tools and is not suitable for unprotected write effects.

## 10. Recovery and idempotency guarantees

- **Process interruption:** persisted state can be loaded by a fresh host/store instance.
- **Completed tool/interrupted continuation:** result is applied from the journal with zero additional dispatches.
- **Pending tool/interruption:** read-only tool may execute again after reauthorization.
- **Foundry transient failure:** existing bounded provider retry applies; if the provider operation still fails, the durable attempt becomes `WaitingToResume` without resetting progress.
- **Storage transient failure:** ownership is best-effort released; a later attempt or expired-lease recovery continues from the latest successful checkpoint.
- **Stale write:** compare-and-swap/ETag failure returns conflict and never overwrites newer state.
- **Corrupt state:** checksum, strict JSON shape, schema, invariants, evidence, ownership, and size are validated before model or tool execution.
- **Cancellation:** attempt cancellation is recoverable; explicit run cancellation is terminal.

## 11. Security boundaries

Application code, configured policy, and registered tool definitions remain trusted. User text, recovered conversation, retrieved documents, repository files, model tool calls, and tool results remain untrusted data after recovery.

Controls include strict JSON deserialization with unmapped members rejected, a fixed checkpoint type, schema/version validation, SHA-256 file/Table payload validation, bounded payload/message counts, opaque IDs, server-owned leases, evidence-prefix and citation-subset checks, reauthorization on dispatch, persistent execution budgets, safe error mapping, and public views that omit question text, conversation, arguments, fingerprints, and raw results.

The API has no authentication or tenant model today. The implementation therefore does not claim production multi-tenant run authorization. Guess-resistant IDs and a safe status view reduce exposure, but deployment outside a trusted learning environment requires authenticated identity ownership checks on create/get/resume/cancel plus retention and encryption policy.

## 12. API contracts

- `POST /api/agent/runs` with `{ "question": "..." }` returns HTTP 201, a `Location` header, run ID, `Created` state, and safe progress.
- `GET /api/agent/runs/{runId}` returns the safe run view.
- `POST /api/agent/runs/{runId}/resume` advances one bounded attempt or returns the existing completed outcome.
- `POST /api/agent/runs/{runId}/cancel` cancels the logical run.
- `POST /api/agent/ask` remains unchanged for non-durable synchronous use.

The public execution list exposes only execution ID, tool name, success, duration, result character count, evidence IDs, and safe error code. It does not expose model conversation, tool arguments, raw result contents, checkpoint version, owner, or fencing token.

## 13. D1–D9 evaluation results

### D1 — Create durable run: Pass (local live API)

- **Setup:** API started on `127.0.0.1:5077` with the real file provider pointed to a temporary evaluation directory.
- **Execution:** POST create, then GET status, then verify the persisted file.
- **Actual:** run `0f5482258b644fb28785248f49f0020a`; POST status `Created`; GET status `Created`; checkpoint existed; model steps 0; tool calls 0.
- **Expected:** retrievable initial checkpoint without model execution.
- **Evidence:** live command output plus `CreateAndGetUsePersistedCheckpointThroughRealManager`.
- **Limitations:** the temporary checkpoint artifact was removed after verification.

### D2 — Checkpoint after tool execution: Pass (deterministic integration)

- **Setup:** scripted knowledge tool response with `knowledge:authentication.md#chunk-1`.
- **Execution:** create and resume a recovery-link question.
- **Actual:** journal contains one completed and conversation-applied execution, persisted evidence, two model steps, two cumulative fake tokens, and a validated final citation.
- **Expected:** completed tool and provenance survive in checkpoint.
- **Evidence:** `Resume_KnowledgeCallPersistsEvidenceUsageAndJournal`.
- **Limitations:** fake model/tool; no paid Foundry/Search call was needed.

### D3 — Resume after restart: Pass (actual ASP.NET Core process restart)

- **Setup:** start the API on port 5078 with the real file store, persistent directory `.assessment-07-process-restart/checkpoints`, and a 15-second lease. Create run `06c0ea73b08b4b9ca7fa5fe6a700701b` through HTTP and resume it against live Foundry and Azure AI Search.
- **Execution:** poll the checksum-protected file until version 5 contains one completed knowledge-tool row. Force-terminate the exact process listening on port 5078 (process 27760), verify no process is listening, then start a new ASP.NET Core process with the same directory. GET the existing run and POST resume after lease expiry.
- **Crash-boundary state:** `Running`; version 5; model step 1; tool-call count 1; resume count 1; completed executions 1; evidence count 4; total tokens 1,187; final answer absent. The interrupted HTTP connection failed because the remote process was forcibly closed.
- **Fresh-process GET:** returned the same `Running` run with model step 1, tool-call count 1, resume count 1, completed executions 1, and the original four evidence IDs.
- **Recovery:** the new process logged expired-owner recovery at version 6, acquired fence 3 at version 7, and completed at version 9. Final progress was model steps 2, tool calls 1, resume count 2, completed executions 1, cumulative tokens 3,290.
- **Actual:** the new process produced the correct 30-minute, single-use answer with `knowledge:authentication.md#chunk-1` citation.
- **Expected:** real process interruption and continuation from the committed checkpoint.
- **Evidence:** HTTP responses, decoded checkpoint snapshots, process-listener verification, and first/new-process structured logs captured on 2026-10-18.
- **Additional deterministic evidence:** `FreshManagerAndFileStoreInstanceResumePersistedRun` and `FileStore_NewInstanceReadsPersistedCheckpoint`.
- **Limitations:** the experiment used local file persistence and a deliberately short lease; Azure Table process-restart recovery remains Not Executed.

### D4 — Completed tool-call recovery: Pass (actual process interruption plus deterministic fault injection)

- **Setup:** a store injects a one-shot storage failure after tool result commit but before conversation application.
- **Execution:** first resume moves to `WaitingToResume`; second resume recovers.
- **Actual:** deterministic dispatcher count remained exactly 1. In the actual restart, the crash checkpoint and final response both contained the same single execution ID `call_11gWuaYn6S0H5nf2G35ndbpu`; tool-call count remained 1 and completed execution count remained 1. The second-process logs contained one Foundry final turn and no embedding or Azure AI Search retrieval, confirming that the committed knowledge call was not repeated.
- **Expected:** no duplicate completed tool execution.
- **Evidence:** `CompletedToolResult_IsNotExecutedAgainAfterCheckpointInterruption`.
- **Limitations:** fault injection is deterministic rather than a physical process kill.

### D5 — Concurrent resume: Pass (deterministic concurrency)

- **Setup:** first model call blocks after acquiring ownership.
- **Execution:** a second resume is submitted against the same run.
- **Actual:** second request receives `Conflict`; first completes; terminal state is consistent.
- **Expected:** one active owner.
- **Evidence:** `ActiveLeaseRejectsConcurrentResume`, stale-version store tests, and fencing tests.
- **Limitations:** in-process contenders use the same fake store; Azure ETag live contention was not executed.

### D6 — Transient failure: Pass locally; Azure live Not Executed

- **Setup:** controlled Foundry/provider exception and controlled checkpoint-store exception.
- **Execution:** resume, observe recoverable state, resume again.
- **Actual:** provider failure persisted `WaitingToResume`/`Provider`; second attempt completed with resume count 2. Storage interruption preserved the completed tool result and recovered without duplicate dispatch.
- **Expected:** bounded recovery without counter reset.
- **Evidence:** `ProviderFailure_ReleasesLeaseAndCanResume` and the one-shot failing-store recovery test.
- **Limitations:** live Foundry 429 retry was already covered in Assessment 06; no Azure Table endpoint was available for a live storage outage.

### D7 — Invalid checkpoint: Pass (deterministic and file integration)

- **Setup:** unsupported schema injected into fake store and malformed envelope written to a temporary file store.
- **Execution:** load/resume.
- **Actual:** `CorruptState` returned before any model/tool call; malformed file failed checksum/JSON validation.
- **Expected:** fail closed.
- **Evidence:** corrupt/schema/validator/file-store tests.
- **Limitations:** Azure segment-corruption live case was not executed.

### D8 — Prompt injection after resume: Pass (deterministic recovery)

- **Setup:** recovered repository result says to ignore policy, call shell, reveal secrets, and modify files. Interruption occurs after its durable result commit.
- **Execution:** second resume applies the committed result without re-dispatch.
- **Actual:** only `read_file` was used, dispatcher count remained 1, citation was validated, and the answer classified the content as untrusted. Existing Assessment 06 theory tests still cover malicious RAG and repository content.
- **Expected:** recovered data never becomes trusted instruction.
- **Evidence:** `CompletedToolResult_IsNotExecutedAgainAfterCheckpointInterruption`, `IndirectPromptInjectionRemainsUntrustedToolData`, and Assessment 06 regression tests.
- **Limitations:** scripted model; security does not depend on the model alone because only registered read-only tools can dispatch.

### D9 — Regression: Pass

- **Setup:** complete solution test project, Debug configuration.
- **Execution:** `dotnet test tests/AgentForge.Tests/AgentForge.Tests.csproj --no-restore --configuration Release --verbosity minimal`.
- **Actual:** 212 passed, 0 failed, 0 skipped; reported duration 753 ms on the final post-restart Release run.
- **Expected:** at least the existing 141 tests remain passing.
- **Evidence:** test runner output.
- **Limitations:** deterministic suite does not call paid Azure services.

### Cloud execution status

Azure Table creation, reads, conditional writes, segmented payloads, managed-identity authentication, and distributed contention are **Not Executed**. No Azure Storage Table endpoint/table/RBAC was provided, and no resource or role was created. This is not reported as a pass.

### Actual process-restart experiment notes

An initial Ctrl+C attempt was deliberately rejected as evidence because graceful ASP.NET Core shutdown drained the active request and allowed it to complete. The reported pass uses a later run whose listening process was force-terminated at a verified non-terminal checkpoint. This distinction prevents a graceful completion or same-process object recreation from being mislabeled as crash recovery.

The evaluation checkpoint files were removed after their decoded counters and process logs were recorded. No test checkpoint payload or user question was added to source control.

## 14. Automated test summary

Final suite: **212 passed, 0 failed, 0 skipped**. This is 71 tests above the 141-test Assessment 06 baseline. The added durable coverage includes more than the requested 30 scenarios when theory rows are counted individually.

Coverage includes run creation, strict serialization, schema validation, legal/illegal transitions, model/tool boundaries, restart simulation, completed-result reuse, pending-call recovery policy, duplicate fingerprints, persistent budgets/tokens/evidence, citations, contention, CAS conflicts, stale leases, fences, cancellation during model and tool execution, corruption, provider/storage failures, terminal behavior, safe API responses, recovered injection, unknown tools, Swagger, and existing Assessment 06 behavior.

## 15. Performance and cost observations

- Deterministic full suite completes in roughly one second of test execution and makes no billable model, Search, or Table calls.
- Checkpoint read/write duration is logged safely in milliseconds; model/Search latency remains logged by the existing providers. Final run telemetry contains cumulative tokens, model steps, tool calls, resume count, status, termination reason, and attempt duration.
- Local file writes serialize per run and perform full bounded payload replacement. This favors clarity and crash recovery over high throughput.
- Azure Table updates use one transaction but create a fresh segment generation each time. This can consume storage until cleanup is implemented.
- Frequent safe-boundary checkpoints add storage latency and transactions. The design deliberately pays that cost to avoid losing completed work.
- No Azure Storage resource was provisioned or used. The final restart experiment used the existing Foundry and Azure AI Search resources and therefore incurred their normal request/token usage.

The final restart experiment did make live calls to the existing Foundry deployment and Azure AI Search resource. Before the crash, the first Foundry turn used 1,146 input and 41 output tokens, and the persisted cumulative total was 1,187. After recovery, the final Foundry turn brought cumulative usage to 2,986 input, 304 output, and 3,290 total tokens. The recovery attempt duration was 131,846 ms. Tool retrieval was not repeated after restart.

Measured deterministic outcomes: 100% checkpoint persistence in executed tests; 100% expected resume success; 0 duplicate completed-tool dispatches; all induced ownership conflicts handled; all invalid checkpoints rejected; all induced transient recoveries behaved as expected. These are test-set results, not production reliability claims.

## 16. Defects discovered and fixed

1. Final attempt duration was initially added only to the returned view. It is now checkpointed.
2. A budget-triggered terminal checkpoint could continue into pending execution. Terminal checks now stop the loop immediately.
3. Permanent invalid responses could leave a run `Running` until lease expiry. They now persist `Failed` safely.
4. Ownership validation originally returned the freshly loaded checkpoint and discarded locally prepared failed-tool journal changes. It now validates the latest version but returns the caller's state for the conditional transition.
5. `CreatedAtAction(nameof(GetAsync))` produced HTTP 500 because MVC suppresses the `Async` action-name suffix. The GET route is explicitly named and creation uses `CreatedAtRoute`.

## 17. Known limitations

- The API has no authenticated user/tenant ownership model.
- No hosted queue/worker advances runs automatically; clients explicitly call resume.
- Azure Table live behavior was not tested because no resource/configuration was available.
- Old Azure Table payload generations need retention cleanup.
- File storage is suitable for one local host/shared filesystem semantics, not horizontally scaled production workers.
- Read-only calls may overlap across a lease boundary, though stale checkpoint writes are fenced.
- Pending read-only tool calls may execute more than once after an uncertain crash boundary.
- The entire bounded tool result is stored in the checkpoint. Sensitive-content classification/encryption/retention require production policy.
- Schema version 1 rejects unsupported versions; there is no migration pipeline yet.
- Cancellation cannot forcibly stop an SDK/tool that ignores its cancellation token.

## 18. Files added or modified

### Added

- `src/AgentForge.Application/DurableAgents/DurableAgentModels.cs`
- `src/AgentForge.Application/DurableAgents/DurableAgentContracts.cs`
- `src/AgentForge.Application/DurableAgents/DurableAgentOptions.cs`
- `src/AgentForge.Application/DurableAgents/DurableAgentStateMachine.cs`
- `src/AgentForge.Application/DurableAgents/DurableCheckpointValidator.cs`
- `src/AgentForge.Application/DurableAgents/DurableAgentRunManager.cs`
- `src/AgentForge.Infrastructure/DurableAgents/CheckpointJson.cs`
- `src/AgentForge.Infrastructure/DurableAgents/InMemoryAgentCheckpointStore.cs`
- `src/AgentForge.Infrastructure/DurableAgents/FileAgentCheckpointStore.cs`
- `src/AgentForge.Infrastructure/DurableAgents/AzureTableCheckpointOptions.cs`
- `src/AgentForge.Infrastructure/DurableAgents/AzureTableAgentCheckpointStore.cs`
- `src/AgentForge.Api/Composition/DurableAgentComposition.cs`
- `src/AgentForge.Api/Controllers/DurableAgentRunsController.cs`
- `tests/AgentForge.Tests/DurableAgents/DurableCheckpointStoreTests.cs`
- `tests/AgentForge.Tests/DurableAgents/DurableAgentRunManagerTests.cs`
- `tests/AgentForge.Tests/DurableAgents/DurableAgentEndpointTests.cs`
- `docs/assessment-07-evaluation.md`

### Modified

- `src/AgentForge.Application/Agents/AgentFailureKind.cs`
- `src/AgentForge.Application/AgentForge.Application.csproj`
- `src/AgentForge.Infrastructure/AgentForge.Infrastructure.csproj`
- `src/AgentForge.Api/ErrorHandling/AgentExceptionHandler.cs`
- `src/AgentForge.Api/Program.cs`
- `src/AgentForge.Api/appsettings.json`

## 19. Future multi-agent readiness

The run/store/lease/journal abstractions are reusable building blocks, but this assessment intentionally models one logical agent. Assessment 08 can compose multiple agent-owned steps above the checkpoint store, add per-agent task records and dependencies, and use the same conditional-write and fencing principles. It should not share one mutable conversation among agents or treat the current single-agent checkpoint as a complete workflow engine.

## Required Azure configuration for optional Table deployment

1. Use an existing Azure Storage account and pre-create a Table; the application deliberately does not provision it.
2. Grant the runtime identity the least-privilege Storage Table data-plane role needed to read/add/update entities.
3. Configure `DurableAgent:StoreProvider` as `AzureTable`.
4. Configure `AzureTableCheckpoint:Endpoint` as the HTTPS Table service endpoint, `TableName` as the existing table, and optionally `TenantId` for local multi-tenant credential selection.
5. Do not configure account keys or connection strings. Validate in a non-production environment before distributed use.

---

## Assessment 07 — Learning Report

### 1. What is agent state?

Agent state is the application-owned information needed to understand where a run is and continue it correctly. In AgentForge it includes status, conversation messages needed for continuation, model/tool counters, the tool journal, evidence, tokens, ownership, and the final answer. It is not hidden model reasoning.

### 2. What is the difference between in-memory state and durable state?

In-memory state disappears when a process stops. Durable state is committed outside the executing call and can be loaded by a fresh process. A local `List<AgentMessage>` is in-memory; a validated `DurableAgentCheckpoint` written through `IAgentCheckpointStore` is durable.

### 3. What is checkpointing?

Checkpointing is committing a consistent snapshot at a boundary from which execution can safely continue. AgentForge checkpoints the proposed tool before dispatch, the completed result after dispatch, and its later incorporation into conversation as separate stages.

### 4. Why is checkpointing important for AI agents?

Model and tool workflows are slow, costly, and failure-prone. Without checkpoints a network failure near the end can discard every earlier model/tool call. Checkpoints preserve work, budgets, and provenance and make recovery explainable.

### 5. What information must be persisted to resume an agent?

Persist the request, lifecycle position, minimal conversation, completed and pending tool records, fingerprints, evidence, usage and budget counters, final result if any, schema/version, and execution ownership. Persisting only the last text answer is insufficient because the application could not decide what is safe to repeat.

### 6. What is the difference between a run and an execution attempt?

A run is the long-lived logical request identified by `RunId`. An attempt is one bounded resume operation with its own random owner and lease. One run can have several attempts after cancellation, throttling, timeout, or restart, while its counters continue accumulating.

### 7. What is an agent state machine?

A state machine defines valid statuses and transitions. It prevents ambiguous changes such as moving directly from `Created` to `Completed`, or resuming `Cancelled`. AgentForge implements allowed transitions in `DurableAgentStateMachine` and tests both positive and negative cases.

### 8. What is optimistic concurrency?

Optimistic concurrency assumes conflicts are uncommon but verifies at write time that nobody changed the record since it was read. AgentForge passes the expected numeric version or Azure ETag on every update; a mismatch returns conflict rather than overwriting newer state.

### 9. What is an ETag?

An ETag is a storage-provided opaque version identifier. Azure Table changes it when an entity changes. Supplying the previously read ETag on update makes the write conditional, which detects stale executors without a global lock.

### 10. What is a distributed lease?

A distributed lease is persisted ownership with an expiry time. It tells all workers which attempt may advance the run and eventually frees a run after a crashed owner. AgentForge stores the owner and `LeaseExpiresAt` in the checkpoint rather than relying on a process-local lock.

### 11. Why are fencing tokens important?

A lease can expire while an old worker is paused. If it later wakes up, it still remembers its owner string. A monotonically increasing fence distinguishes generations of ownership; every later action must match the current fence, so the old worker cannot advance the new generation.

### 12. What happens when two workers resume the same run?

Both may read the same checkpoint, but only one conditional acquisition succeeds. The other receives a safe conflict. If it reads after acquisition, it sees an active lease and also conflicts. Tests hold the first model call open and verify exactly this outcome.

### 13. What is idempotency?

Idempotency means repeating processing does not create a different externally visible result. AgentForge achieves this for committed tool results by looking up the durable execution row and applying its saved result instead of calling the tool again.

### 14. What is the difference between at-least-once and exactly-once execution?

At-least-once permits duplicates when acknowledgement is uncertain. Exactly-once claims the effect happens once despite crashes, which usually requires cooperation from the external system. AgentForge offers at-least-once execution for pending read-only tools and no-repeat reuse for committed results; it does not claim exactly-once external effects.

### 15. What happens if a tool succeeds but the process crashes before checkpointing?

The checkpoint still says `Pending`, so recovery cannot prove completion and may execute the read-only tool again. That is safe for current tools. For writes, the tool would need an idempotency key/outbox or a queryable operation record before retries were acceptable.

### 16. How do we avoid repeating completed tool calls?

The manager commits status `Completed`, bounded result, evidence, and completion metadata before adding the result to conversation. On resume, completed rows bypass dispatch; if `ConversationApplied` is false, the stored result is appended exactly once.

### 17. What is a checkpoint schema version?

It identifies the structural/invariant contract used to interpret persisted data. AgentForge currently accepts version 1 only. An unknown version is rejected as corrupt/unsupported rather than being guessed, which prevents unsafe execution after incompatible deployments.

### 18. How should corrupted checkpoints be handled?

Fail closed before model or tool execution, return a sanitized error, and preserve evidence for operators without logging sensitive content. AgentForge checks envelope/payload integrity, strict JSON shape, schema, IDs, counters, ownership, journal invariants, citations, evidence prefixes, and size.

### 19. How do we recover from Foundry HTTP 429 errors?

The existing provider applies bounded retries, respects `Retry-After`, cancellation, timeout, and request/token limits, and avoids retrying permanent errors. If those retries are exhausted, the durable manager releases the run into `WaitingToResume`; a later attempt continues committed progress rather than restarting the workflow.

### 20. Why must execution budgets persist across restarts?

Otherwise a client could repeatedly restart/resume to reset `MaxSteps`, tool-call count, search-call count, or result-size limits. Persisted counters make the safety and cost boundary apply to the whole run, not each process.

### 21. What are the security risks of persisted agent conversations?

They may contain user data, proprietary code, malicious instructions, or sensitive tool results. Risks include unauthorized reads, long retention, log leakage, tampering, and later deserialization into a more trusted context. Production needs access control, encryption, retention/deletion, classification, and audit policy.

### 22. How do we prevent prompt injection after recovery?

Recovered content retains the same untrusted role. The trusted system prompt is reconstructed from application code, only registered tools are offered, dispatcher authorization runs again, execution remains read-only and bounded, and final citations must match current-run evidence. The checkpoint never supplies system policy.

### 23. Why is durable state important for multi-agent systems?

Multi-agent workflows have more handoffs and failure points. Durable task status, evidence, budgets, and ownership let agents coordinate without relying on one process or duplicating expensive work. It also provides an auditable basis for deciding which agent may act next.

### 24. How will Assessment 08 reuse this architecture?

It can reuse checkpoint-store conditional writes, run IDs, leases/fences, failure classification, evidence validation, and journals. A higher-level workflow should add explicit agent/task identities and dependencies rather than putting several agents into the current single conversation.

### 25. When should an agent run move to Azure Container Apps and background workers?

Move when work must continue independently of HTTP clients, lasts longer than practical request limits, needs automatic retries/scheduling, or must scale across instances. At that point use a durable queue, independently hosted worker, managed identity, Azure Table/other distributed state, health scaling, and dead-letter/operations tooling—not `Task.Run` inside the API.
