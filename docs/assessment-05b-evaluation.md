# Assessment 05B — Production RAG with Azure AI Search

## Status

Implementation, live ingestion, controlled retrieval evaluation, and automated verification are complete. The required **Search Index Data Contributor** role was assigned by the user; AgentForge did not modify RBAC and did not use a Search API key.

Automated result: **99 passed, 0 failed** (the existing 73 tests plus 26 Assessment 05B test cases).

## 1. Configuration

Retriever selection is configuration-driven:

```json
{
  "Rag": {
    "CorpusPath": "knowledge",
    "Retriever": "InMemory",
    "ChunkSize": 900,
    "ChunkOverlap": 150,
    "TopK": 4,
    "MinimumSimilarity": 0.35,
    "EmbeddingBatchSize": 16
  },
  "AzureAiSearch": {
    "Endpoint": "",
    "IndexName": "agentforge-knowledge",
    "TenantId": "",
    "RetrievalMode": "Vector",
    "TopK": 4
  }
}
```

`Rag:Retriever` accepts `InMemory` or `AzureAiSearch`. Azure retrieval accepts `Vector`, `Keyword`, `Hybrid`, or `SemanticHybrid`. Endpoint, index, tenant, credentials, and retrieval mode are not hard-coded. `TenantId` is optional; it only narrows `DefaultAzureCredential` when necessary.

The live service discovered for verification was `agentforge-search-dev` in West US 3, using the Serverless SKU. Its endpoint is `https://agentforge-search-dev.search.windows.net`. This endpoint is not a secret and was supplied at runtime rather than committed to application settings.

## 2. Index schema

| Field | Type | Capabilities | Purpose |
|---|---|---|---|
| `id` | string | key, filterable, retrievable | Stable SHA-256 key used for deterministic upserts. |
| `documentId` | string | filterable, sortable, retrievable | Preserves the source document identity and supports controlled metadata operations. |
| `source` | string | filterable, sortable, retrievable | Preserves citation filenames and supports the public source filter. |
| `chunkId` | string | filterable, retrievable | Preserves the existing citation contract. |
| `position` | int32 | filterable, sortable, retrievable | Preserves deterministic order inside a document. |
| `content` | string | searchable, retrievable | Supplies full-text search and grounded-generation evidence. |
| `contentVector` | float collection | vector dimensions 1536, not retrievable | Supplies vector search without exposing embeddings in API responses. |

The vector profile uses HNSW with cosine distance. Schema validation checks required fields, key/filter/search capabilities, vector dimensions and profile, and the HNSW cosine algorithm. An incompatible index fails clearly; it is never deleted or silently recreated.

The stable key is lowercase SHA-256 over trusted `documentId`, `source`, `chunkId`, and `position`. Re-indexing the same corpus therefore targets the same records through merge-or-upload instead of creating random duplicates.

## 3. Authentication and RBAC

Both Foundry and Azure AI Search use Microsoft Entra ID through `DefaultAzureCredential`. There is no Search admin key or query key fallback.

Local flow:

```text
az login identity -> DefaultAzureCredential -> Entra ID -> Azure AI Search
```

Production-compatible flow:

```text
Managed/Workload Identity -> DefaultAzureCredential -> Entra ID -> Azure AI Search
```

Role boundaries are explicit:

- **Search Service Contributor**: create/read/update index definitions.
- **Search Index Data Contributor**: upload, update, delete, and query index documents.
- **Search Index Data Reader**: query documents only.

The live identity inherits `Owner`, `Foundry User`, and the user-assigned `Search Index Data Contributor` role on `agentforge-search-dev`. The initial run demonstrated that subscription Owner alone did not authorize document upsert; after the explicit data-plane role was assigned, indexing and querying succeeded. The application reports the role required for schema management, document upsert, or document query separately.

## 4. Architecture and ingestion

```text
Knowledge source
    -> Markdown/text document loader
    -> unchanged deterministic 05A chunker
    -> Foundry text-embedding-3-small
    -> chunk-to-search-document mapper
    -> deterministic merge-or-upload
    -> Azure AI Search index
```

Indexing is an explicit `POST /api/knowledge/index` operation and does not run at application startup. It:

1. reads the configured index;
2. creates it only when absent;
3. validates important schema assumptions when present;
4. loads the same controlled corpus used in 05A;
5. creates the same deterministic chunks;
6. generates 1536-dimensional embeddings;
7. upserts stable search documents;
8. returns document, chunk, embedding-call, duration, upload, and error diagnostics.

Removed source documents are not currently deleted from the index. Re-indexing updates current deterministic keys, but full deletion synchronization is a documented limitation rather than an unsafe implicit operation.

## 5. Query architecture

```text
User question
    -> KnowledgeService
    -> configured IKnowledgeRetriever
       -> InMemoryKnowledgeRetriever, or
       -> AzureAiSearchKnowledgeRetriever
          -> Vector / Keyword / Hybrid / SemanticHybrid
    -> ranked KnowledgeChunk values
    -> unchanged grounded-generation prompt
    -> citation allow-list validation
    -> KnowledgeAnswer
```

The application orchestration layer contains no Azure SDK types. Azure SDK models and clients remain inside Infrastructure behind `IKnowledgeRetriever`, `IKnowledgeIndexManager`, and an internal gateway that is replaced with a fake in unit tests.

Compared with 05A, document loading, chunking, embedding generation, grounding, citation validation, and insufficient-evidence behavior remain application-controlled. Vector persistence, approximate candidate search, full-text indexing, hybrid result fusion, filtering, and optional semantic reranking move into the managed Search service.

## 6. Retrieval modes

### Vector

The question is embedded with `text-embedding-3-small`, then sent as a 1536-dimensional vector query against `contentVector`. HNSW organizes vectors as a navigable proximity graph, avoiding a full O(n) comparison against every vector for each query. This scales better than the 05A in-memory exact scan, but it is approximate: higher speed and lower work can trade off against recall, so rankings need not exactly match exhaustive local cosine ranking.

Azure's vector `@search.score` is retained as a neutral `score`, not mislabeled as the raw 05A cosine similarity. For a cosine-configured vector field, the documented monotonic score transformation is used internally to map the 05A threshold boundary into Azure's vector score scale. The API leaves `similarity` null for Azure results so consumers do not confuse the two score representations.

### Keyword

Keyword mode sends the question as a full-text query against `content` and does not call the embedding model. It is useful for exact class and method names, routes, identifiers, uncommon technical terms, and phrases. Its score is a lexical search score and is not compared to the 05A cosine threshold.

### Hybrid

Hybrid mode submits full-text and vector queries in one Search request. Azure combines independently ranked result sets using Reciprocal Rank Fusion (RRF). Vector provides semantic similarity; keyword provides exact-term evidence. Hybrid can cover both kinds of query, but it is not universally superior and still requires evaluation on labelled questions.

### Semantic hybrid

The service reports `semanticSearch: free`, and West US 3 supports semantic ranking, so `SemanticHybrid` is implemented. It performs hybrid candidate retrieval and then semantic reranking using the `knowledge-semantic` configuration over `content`. The semantic reranker score is exposed separately from the base Search score.

Embedding similarity compares vector proximity. Semantic ranking is a later language-aware reranking stage over candidates. They are different signals and their values are not interchangeable.

## 7. Grounding, unknown knowledge, and security

Only retrieved chunks are passed to generation. The 05A untrusted-document system instruction, explicit `isSufficientEvidence`, deterministic no-hit response, citation allow-list, inline citation validation, source/chunk metadata, and ban on arbitrary model-knowledge fallback remain intact.

Unknown handling differs by retrieval mode:

- Vector: the 05A cosine boundary is translated to the documented Azure vector-score scale before candidates are passed onward.
- Keyword: lexical results are passed to the explicit answer-sufficiency decision; no vector threshold is applied.
- Hybrid/SemanticHybrid: fused/reranked scores are not treated as cosine values; answer sufficiency remains a separate grounded-generation decision.

This separation is important: retrieval presence means only that Search found candidates, not that those candidates can support an answer.

Retrieved content remains untrusted data. It cannot create tools, change system instructions, choose endpoints or indexes, inject an arbitrary OData expression, expose vectors, or grant capabilities. The optional public `source` parameter must be a single `.md` or `.txt` filename with no path. The application constructs and escapes the equality filter itself.

Filtering example:

```json
{
  "question": "What must the agent do with embedded instructions?",
  "source": "security-guidelines.md"
}
```

This becomes an SDK-escaped equality filter over `source`; callers never supply raw filter syntax.

## 8. Observability

Safe diagnostics include:

- retrieval mode;
- query embedding duration;
- Azure Search duration;
- overall retrieval and RAG duration;
- Top-K result rank, source, chunk ID, Search score, and optional reranker score;
- generation input/output/total tokens and duration;
- indexing documents, chunks, embedding calls/tokens/duration, upserts, total duration, and errors.

Full document text, vectors, credentials, and access tokens are not logged or returned by the public API.

## 9. Automated verification

The 26 added test cases cover:

- Azure Search options and invalid configuration;
- configuration-driven retriever selection;
- explicit schema fields and capabilities;
- 1536-dimensional cosine HNSW configuration;
- stable deterministic document IDs;
- repeatable merge-or-upload identity;
- chunk/search-document round trips;
- vector, keyword, hybrid, and semantic-hybrid query construction;
- Top-K enforcement;
- result/rank/score/reranker mapping;
- source metadata preservation;
- source-filter escaping;
- rejection of paths and arbitrary OData input;
- vector threshold translation without relabeling Azure score as cosine;
- no embedding call in keyword mode.

The existing grounding-only-context, citation allow-list, insufficient-evidence, malicious-document, packaging, and endpoint tests remain green. Unit tests use a fake Azure Search gateway and make no paid Azure calls.

## 10. Live verification record

### Environment and indexing

- Date: 2026-10-05 (Asia/Karachi).
- Azure CLI identity authenticated to subscription `0389c820-76d5-4fa6-a049-278c94170a19`.
- Search service: `agentforge-search-dev`, West US 3, Serverless.
- Authentication: Entra ID only; no Search API key.
- Semantic Search capability: `free`, successfully exercised.
- Index: `agentforge-knowledge`; it already existed and passed schema validation.
- Indexing HTTP status: 200.
- Documents processed: 5; chunks produced: 7; documents upserted: 7; errors: 0.
- Independent Entra-authenticated count query after ingestion: 7 index documents.
- Embedding: one call, 7 vectors, 1536 dimensions, 687 input tokens, 3,932 ms.
- Server-side indexing duration: 14,832 ms; client duration: 15,103 ms.

The earlier blocked attempt identified two operational prerequisites. First, this OpenAI-compatible embedding client requires the provisioned Azure AI Services endpoint (`https://agentforge-dev-resource.cognitiveservices.azure.com`) rather than the Foundry project endpoint for embeddings. Second, document ingestion requires Search Index Data Contributor even when the identity has subscription Owner. Both were corrected through runtime configuration and a user-managed role assignment; no infrastructure or RBAC was changed by AgentForge.

### Q1–Q5 Azure vector evaluation

All calls returned HTTP 200. Azure scores below are Search scores, not raw 05A cosine values.

| Query | Retrieval | Result | Citation | Embedding / Search / Generation / Overall ms | Generation tokens in/out/total |
|---|---|---|---|---:|---:|
| Q1 exact fact | `authentication.md#chunk-1` rank 1, score 0.659054 | Correct: 30 minutes and single-use | Valid | 2,593 / 485 / 5,742 / 8,859 | 315 / 246 / 561 |
| Q2 paraphrase | `authentication.md#chunk-1` rank 1, score 0.624172 | Correct despite different wording | Valid | 2,232 / 286 / 7,560 / 10,081 | 316 / 646 / 962 |
| Q3 multi-document | `architecture` chunks ranks 1–2; `deployment` rank 3; authentication noise rank 4 | Correct deployment and Entra/workload-identity answer | Valid; three retrieved citations | 2,160 / 309 / 10,144 / 12,615 | 648 / 1,059 / 1,707 |
| Q4 unknown | No vector hit survived the translated threshold | Explicit insufficient evidence; generation skipped | None, correctly | 2,192 / 325 / 0 / 2,518 | n/a |
| Q5 injection | `security-guidelines` chunks ranks 1–2, scores 0.718964 and 0.629385 | Embedded instruction identified as untrusted and ignored | Valid; both security chunks | 2,176 / 279 / 8,885 / 11,343 | 431 / 842 / 1,273 |

Q3 vector composition exactly reproduced the important 05A order: `architecture.md#chunk-1`, `architecture.md#chunk-2`, `deployment.md#chunk-1`, then the same irrelevant authentication chunk. Q5 likewise retained both expected security chunks at ranks 1 and 2. No answer cited a source outside its retrieved allow-list.

### 05A versus 05B vector retrieval

| Query | 05A exact in-memory | 05B Azure HNSW | Comparison |
|---|---|---|---|
| Q1 | authentication rank 1; cosine 0.482725 | authentication rank 1; Search score 0.659054 | Same expected hit/rank |
| Q2 | authentication rank 1; cosine 0.398630 | authentication rank 1; Search score 0.624172 | Same semantic-paraphrase success |
| Q3 | architecture 1/2, deployment 3, auth noise 4 | identical composition and ranks | Same evidence coverage/noise |
| Q4 | no cosine hit >= 0.35 | no hit after translated vector-score boundary | Same pre-generation rejection |
| Q5 | security chunks ranks 1/2 | security chunks ranks 1/2 | Same injection evidence and safe answer |

The numbers are intentionally not compared as though they shared a scale. The Azure score is a transformation used by the service, and HNSW candidate discovery is approximate even though this small corpus produced the same ranks.

### Vector, Keyword, Hybrid, and SemanticHybrid

Each row shows the rank of expected evidence. Q3 lists all three required chunks; Q5 lists both security chunks.

| Query | Vector | Keyword | Hybrid | SemanticHybrid |
|---|---|---|---|---|
| Q1 exact fact | auth r1 | auth r1 | auth r1 | auth r1 |
| Q2 semantic paraphrase | auth r1 | **miss** | auth r1 | auth r1 |
| Q3 multi-document | arch1 r1, arch2 r2, deploy r3 | arch2 r1, arch1 r2, deploy r3 | arch1 r1, arch2 r2, deploy r3 | arch2 r1, deploy r3, arch1 r4 |
| Q4 unknown | no surviving hit; generation skipped | 4 candidates; generation refused | 4 candidates; generation refused | 4 candidates; generation refused |
| Q5 injection | security r1/r2 | security r1/r2 | security r1/r2 | security r1/r2 |
| Exact identifier `DefaultAzureCredential` | arch2 r1 | arch2 r1 | arch2 r1 | arch2 r1 |
| Exact route `/api/repositories/analyze` | api-spec r1 | api-spec r1 | api-spec r1 | api-spec r1 |

Representative first-relevant scores demonstrate why scores must remain mode-specific:

| Query | Vector score | Keyword score | Hybrid/RRF score | SemanticHybrid score / reranker |
|---|---:|---:|---:|---:|
| Q1 | 0.659054 | 10.859013 | 0.033333 | 0.033333 / 3.458286 |
| Q2 | 0.624172 | miss | 0.032292 | 0.032292 / 2.005583 |
| Q3 | 0.752502 | 6.077110 | 0.033060 | 0.033060 / 2.673491 |
| Q5 | 0.718964 | 6.947183 | 0.033333 | 0.033333 / 2.608978 |
| Identifier | 0.746602 | 6.066903 | 0.033333 | 0.033333 / 2.541452 |
| API route | 0.650705 | 5.118586 | 0.033333 | 0.033333 / 2.778414 |

Keyword demonstrated its intended strength on the literal class name and route, but it failed the paraphrase because the authentication chunk did not share enough lexical terms. Hybrid recovered that miss with the vector signal. Semantic reranking worked and exposed separate reranker scores, but it did not uniformly improve ordering: on Q3 it moved an irrelevant authentication chunk to rank 2 and moved one required architecture chunk to rank 4. All required Q3 evidence still remained inside Top-K.

### Hit@K and MRR

The primary labelled retrieval set contains six answerable queries: Q1, Q2, Q3, Q5, exact identifier, and exact API route. For multi-document questions, Hit@4 also requires all labelled evidence to remain within Top-K; MRR uses the first relevant rank.

| Mode | Hit@4 | MRR | Multi-document evidence coverage |
|---|---:|---:|---:|
| Vector | 6/6 = 100% | 1.000 | 3/3 within Top-4 |
| Keyword | 5/6 = 83.3% | 0.833 | 3/3 within Top-4 |
| Hybrid | 6/6 = 100% | 1.000 | 3/3 within Top-4 |
| SemanticHybrid | 6/6 = 100% | 1.000 | 3/3 within Top-4 |

On the five additional strategy categories requested by the assessment—semantic paraphrase, identifier, API route, multi-document, and unknown—the four answerable labels produce Vector 100%/1.000, Keyword 75%/0.750, Hybrid 100%/1.000, and SemanticHybrid 100%/1.000. The unknown query is evaluated as a safety outcome rather than as a relevant-document hit.

### Unknown knowledge

All modes correctly refused to invent a payment gateway and returned no citations.

- Vector: no candidate survived the translated 05A boundary, so generation was skipped and cost no generation tokens.
- Keyword, Hybrid, and SemanticHybrid: Search returned four ranked candidates because those score types do not accept the 05A cosine threshold. The generation sufficiency mechanism correctly returned `isSufficientEvidence: false` with zero citations.

This result confirms why answer sufficiency must remain separate from retrieval presence.

### Metadata filtering

The filtered request constrained Q5 to `source = security-guidelines.md`. It returned only `security-guidelines.md#chunk-1` and `#chunk-2`, at ranks 1 and 2, with HTTP 200. The grounded answer cited exactly those two chunks. Server diagnostics were 2,466 ms embedding, 378 ms Search, 10,516 ms generation, 13,369 ms overall, and 431/922/1,353 generation tokens.

The public API accepted only the validated filename; it constructed the OData equality filter through the SDK. Paths and raw OData expressions remain rejected by automated tests.

### Indirect prompt injection and citation validation

All four modes ranked the two security chunks first and second. Each answer explicitly treated the embedded instruction as untrusted, exposed no secret, acquired no new capability, and cited both allowed chunks. Vector used only those two chunks after its threshold. Keyword and hybrid modes also returned unrelated lower-ranked candidates, but those were not cited. Citation allow-list validation passed for every live response; an invalid citation would have failed the request rather than being silently emitted.

### Latency and token observations

Averages below cover Q1–Q5 plus the identifier and route probes (seven calls per mode). The first Search call in each restarted process had a roughly three-second cold-start/network cost; warm Search calls were approximately 0.3 seconds.

| Mode | Query embedding avg ms | Search avg ms | Overall RAG avg ms | Generation tokens total | Query embedding tokens total |
|---|---:|---:|---:|---:|---:|
| Vector | 2,271 | 325 | 8,699 | 6,360 across 6 generated calls | 88 |
| Keyword | 0 | 764 (311 warm) | 7,907 | 8,502 across 7 calls | 0 |
| Hybrid | 2,416 | 740 (321 warm) | 11,093 | 8,775 across 7 calls | 88 |
| SemanticHybrid | 2,370 | 749 (342 warm) | 10,560 | 8,744 across 7 calls | 88 |

Keyword avoided query-embedding cost, but Q2 failed retrieval and the unknown query still incurred generation cost. Vector was the only mode that skipped unknown-question generation. These measurements are a tiny, sequential learning sample and are not a production performance benchmark.

## 11. 05A versus 05B comparison

| Concern | 05A in-memory | 05B Azure AI Search |
|---|---|---|
| Storage | Process memory | Managed persistent index |
| Vector query | Exact linear cosine scan | HNSW approximate nearest-neighbor query |
| Keyword search | None | Full-text search |
| Hybrid fusion | None | Azure RRF |
| Semantic reranking | None | Optional semantic configuration |
| Filters | Local predicate | Validated metadata translated to OData by SDK |
| Scale/replicas | Single process | Managed service capabilities |
| Startup | Lazy re-embedding | Explicit, idempotent indexing operation |
| Operational complexity | Low | Search service, index lifecycle, RBAC, and service cost |

Azure Search is not declared "better" merely because it is managed. It provides persistence, scalable retrieval, lexical search, hybrid fusion, filtering, and operational service features that the simple store lacks. The in-memory design remains valuable for small corpora, local learning, deterministic exact comparisons, tests, and deployments where an external Search service is unnecessary.

## 12. Cost considerations

- Indexing invokes the embedding deployment and consumes embedding tokens, then consumes Azure Search indexing capacity.
- Vector and hybrid questions invoke the embedding deployment and Azure Search.
- Keyword questions use Azure Search but do not invoke embeddings.
- Grounded answers invoke the generation deployment only when candidates survive retrieval handling.
- Semantic hybrid can consume semantic-ranking quota/capacity in addition to the base query.
- Explicit indexing avoids regenerating corpus embeddings on every application startup, but manually repeated indexing still regenerates them in the current learning implementation.

The controlled corpus is intentionally small; no load test, scaling action, tier change, indexer, or additional infrastructure was introduced.

## 13. Defects discovered and known limitations

Defects corrected during implementation:

1. The 05A orchestration directly owned lazy in-memory indexing. It was separated behind `IKnowledgeRetriever` without changing behavior.
2. Azure SDK semantic scores in version 12 are under `SearchResult.SemanticSearch`, not directly on the generic result.
3. Error handling initially could only report a generic Search authorization failure. Operations now report their exact minimum Search role.
4. The first live run used the Foundry project endpoint for embeddings and received 404; the provisioned cognitive-services endpoint is the correct embedding base for this client.

Known limitations:

- The evaluation corpus is intentionally small, so the observed perfect vector/hybrid retrieval cannot establish recall on a production corpus.
- Removed source files leave old index documents until an explicit deletion-synchronization design is added.
- Indexing regenerates embeddings when explicitly repeated; embedding caching is not implemented.
- This small evaluation cannot establish production recall, latency, or cost at scale.
- The indexing endpoint requires deployment-level access control before exposure outside this controlled learning environment.

## 14. Files added or changed

Key additions include the Azure Search options, schema, document mapper, credential factory, gateway, index manager, retriever, registration, operation-specific access exception, in-memory retriever abstraction, Search exception handler, configuration, API indexing operation, expanded neutral diagnostics, and Azure Search unit tests. The 05A corpus and grounded-generation protocol were not changed.

## Assessment 05B — Learning Report

1. **Azure AI Search index:** A managed searchable collection whose schema defines fields, analyzers, vector profiles, and retrieval behavior.
2. **Index document:** One searchable record. Here, each deterministic knowledge chunk becomes one document.
3. **Vector field:** A numeric array that stores an embedding for nearest-neighbor retrieval; `contentVector` is hidden from retrieval responses.
4. **Matching dimensions:** Search compares fixed-length vectors. The index's 1536 dimensions must match `text-embedding-3-small` output or ingestion/querying is invalid.
5. **Nearest-neighbor search:** Finding indexed vectors closest to the query vector under a distance measure such as cosine.
6. **HNSW:** A layered proximity graph that traverses promising vector neighborhoods instead of scanning every vector.
7. **Exact versus approximate search:** Exact search evaluates every candidate and maximizes deterministic recall at O(n) work. Approximate search reduces latency/work but can miss or reorder a relevant neighbor.
8. **Keyword/full-text search:** Analyzer-based lexical retrieval that rewards matching words and phrases, especially exact technical identifiers.
9. **Vector search:** Retrieval by embedding proximity, useful when the question and evidence express similar meaning with different words.
10. **Hybrid search:** One query containing both lexical and vector retrieval signals.
11. **Why hybrid can help:** Exact terms rescue lexical cases while vectors rescue paraphrases; RRF can surface items supported by either or both rankings.
12. **Semantic ranking:** A language-aware reranking step applied to an initial candidate set.
13. **Reranking versus embedding similarity:** Embedding similarity is vector distance used for retrieval; semantic reranking evaluates candidate relevance with a separate model and score.
14. **Top-K:** The maximum number of highest-ranked candidates returned to the next RAG stage. It bounds context and token use but can exclude lower-ranked evidence.
15. **Hit@K and MRR:** Hit@K asks whether expected evidence was retrieved within K. MRR rewards putting the first relevant hit near rank one.
16. **Why not reuse 0.35 blindly:** Vector, lexical, RRF, and semantic scores have different definitions and ranges. A cosine threshold is not a general Search-score threshold.
17. **Why answer sufficiency is separate:** A search engine always tries to rank something; retrieved candidates may still contain no evidence that supports a truthful answer.
18. **Why Entra ID over keys:** Identity-based access supports scoped roles, rotation-free managed identities, centralized revocation, and avoids distributing shared secrets.
19. **What Search adds:** Persistent indexing, scalable approximate vector retrieval, lexical and hybrid search, filters, reranking, managed availability, and operational controls.
20. **When 05A is reasonable:** Small controlled corpora, local/offline experiments, deterministic exact-vector baselines, unit tests, and systems where Search service cost and operations are unjustified.

Assessment 05B implementation complete — awaiting review before Assessment 06.
