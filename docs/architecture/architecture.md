# AgentForge architecture

Assessment 05A preserves the layered dependency direction while adding deterministic retrieval-augmented generation beside the existing requirement analyzer and repository agent.

```text
API
 |
 v
Application -> IRequirementAnalyzer
 |                    |
 v                    v
Domain         LlmRequirementAnalyzer
                       |
                       v
             IStructuredOutputClient
                       ^
                       |
Infrastructure -> OpenAI Provider --------> OpenAI API
               -> Azure Foundry Provider -> Foundry endpoint

RepositoryAnalysisAgent -> IToolCallingClient -> selected provider
          |
          v
IRepositoryToolDispatcher -> list_files / read_file / search_code
          |
          v
canonical repository boundary + deterministic limits

KnowledgeService -> IKnowledgeDocumentLoader -> Markdown/text corpus
       |          -> IKnowledgeChunker -> character chunks
       |          -> IEmbeddingClient -> selected provider
       |          -> IKnowledgeVectorStore -> explicit cosine + Top-K
       `----------> IGroundedGenerationClient -> selected provider
```

Project references remain:

```text
AgentForge.Api ------------> AgentForge.Application
       |                              |
       v                              v
AgentForge.Infrastructure -> AgentForge.Application -> AgentForge.Domain
```

## Responsibilities

### API

The API is the composition root. It selects `LlmRequirementAnalyzer` for `IRequirementAnalyzer` and chooses structured-output, tool-calling, embedding, and grounded-generation provider adapters from `AI:Provider`. This remains the only provider-selection switch. It binds repository and RAG limits, validates HTTP input, and maps provider failures to sanitized Problem Details responses.

### Application

Application owns the use cases. Requirement analysis continues through `IStructuredOutputClient`. Repository analysis uses the provider-neutral `IToolCallingClient`, owns the bounded loop, carries assistant tool calls and tool results across turns, and produces the strict `RepositoryAnalysis` contract.

Knowledge retrieval is separate from the tool-calling agent. `KnowledgeService` indexes the corpus once per application lifetime, creates a query embedding, performs deterministic thresholded Top-K retrieval, and sends only selected chunks to grounded generation. It validates every returned citation against the retrieved source identifiers. Provider SDK types do not cross the Application boundary.

### Domain

Domain contains stable requirement, repository-analysis, document/chunk, retrieval, answer, source, and diagnostic models. It has no API, Infrastructure, or provider SDK dependencies. Chunk embeddings are retained for local search but excluded from JSON serialization.

### Infrastructure

Infrastructure contains provider adapters, filesystem tools, and knowledge document loading. Provider adapters translate application-owned contracts into SDK types. Filesystem infrastructure canonicalizes every requested path, checks link targets, rejects sensitive/unsupported files, skips generated directories, and applies bounded reads/searches/listings. The dispatcher maps exact registered names to implementations; it never reflects over arbitrary model strings.

Knowledge infrastructure loads bounded UTF-8 Markdown/text documents and implements provider adapters for embeddings and grounded chat. Foundry chat can use the project endpoint while embeddings can optionally use a distinct resource endpoint. Both use Entra ID and `DefaultAzureCredential`; no Azure API key is introduced.

## RAG flow

```text
first-question ingestion (once per application lifecycle)
  load -> validate size/format -> chunk -> batch embed -> in-memory store

each question
  embed -> cosine score every chunk -> minimum threshold -> Top-K
  -> no matches: deterministic insufficient-evidence response
  -> matches: system instructions + separate question + delimited untrusted context
  -> strict answer/citation JSON -> citation allow-list validation -> API response
```

The character chunker defaults to 900 characters with 150 characters of overlap and prefers paragraph boundaries. The vector store deliberately uses a linear scan and an explicit dot-product/norm cosine calculation. This keeps ingestion, scoring, thresholding, and ranking visible for learning; token-aware chunking, persistence, and approximate nearest-neighbor indexing are deferred.

## Repository agent loop

```text
User goal
  -> model turn with three registered tools
  -> untrusted tool proposal
  -> exact-name dispatch + argument validation + path authorization
  -> bounded read-only result (or sanitized rejection)
  -> model continuation
  -> strict RepositoryAnalysis JSON
```

The loop defaults to ten model turns and has an overall timeout. Parallel tool calls are disabled because strict structured function schemas require it. A denied call is returned to the model as a sanitized tool error so useful analysis can continue; no denied operation is executed.

## Structured output flow

```text
Natural language requirement
    -> system instructions + separate user message
    -> selected OpenAI model or Foundry deployment
    -> strict JSON-schema constrained output
    -> RequirementAnalysis
```

Strict Structured Outputs ensure schema adherence. Standard JSON deserialization converts that schema-constrained response into the Domain type; there is no delimiter extraction, JSON repair, or ad hoc parsing.

## Security and operations

- The OpenAI API key comes from user secrets or environment configuration. Foundry uses Entra ID through `DefaultAzureCredential`, so no Azure API key is stored.
- The requirement is untrusted user content and is never interpolated into the system prompt.
- Full requirement text is not logged by default.
- Provider exception details are retained internally but replaced with safe public error messages.
- Duration and token counts are recorded as structured metadata for future Application Insights export and cost analysis.
- Provider SDK retries are disabled to avoid unexpected usage. Repository analysis may make multiple visible model turns, bounded by configuration.
- Repository content and tool results are untrusted. The malicious fixture README proves they cannot register a new tool or expand filesystem access.
- Tool logs contain names, durations, result sizes, iterations, provider/model, and token totals—not file contents, credentials, or bearer tokens.

Prompt instructions guide behavior, but authorization is deterministic: exact registered tools, canonical filesystem containment, sensitive-file rules, size/count limits, and time/iteration limits remain authoritative even when direct or indirect prompt injection succeeds behaviorally.

## Planned evolution

```text
Structured Output
    -> Tool Calling + read-only Repository Intelligence
    -> Local RAG fundamentals (current)
    -> Azure AI Search / production retrieval
    -> Agents
    -> Multi-Agent Orchestration
    -> MCP
    -> Evaluation
    -> Azure Deployment
```
