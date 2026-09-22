# AgentForge architecture

Assessment 04 preserves the layered dependency direction while adding a separate bounded repository-analysis agent beside the existing requirement analyzer.

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

The API is the composition root. It selects `LlmRequirementAnalyzer` for `IRequirementAnalyzer` and chooses both the structured-output and tool-calling provider adapters from `AI:Provider`. This remains the only provider-selection switch. It binds repository limits, registers only the three read-only tools, validates HTTP input, and maps failures to sanitized Problem Details responses.

### Application

Application owns both use cases. Requirement analysis continues through `IStructuredOutputClient`. Repository analysis uses the provider-neutral `IToolCallingClient`, owns the bounded loop, carries assistant tool calls and tool results across turns, and produces the strict `RepositoryAnalysis` contract. Provider SDK types do not cross this boundary.

### Domain

Domain contains the stable requirement and repository analysis models. It has no API, Infrastructure, or provider SDK dependencies.

### Infrastructure

Infrastructure contains both provider adapters and the filesystem tool implementations. Provider adapters translate application-owned messages, strict function schemas, assistant tool calls, tool results, and final response schemas into SDK types. Filesystem infrastructure canonicalizes every requested path, checks link targets, rejects sensitive/unsupported files, skips generated directories, and applies bounded reads/searches/listings. The dispatcher maps exact registered names to implementations; it never reflects over arbitrary model strings.

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
    -> Tool Calling + read-only Repository Intelligence (current)
    -> RAG
    -> Agents
    -> Multi-Agent Orchestration
    -> MCP
    -> Evaluation
    -> Azure Deployment
```
