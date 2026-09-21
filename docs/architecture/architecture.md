# AgentForge architecture

Assessment 03 preserves the layered dependency direction while selecting between two LLM providers behind the existing Application port.

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

The API is the composition root. It selects `LlmRequirementAnalyzer` for `IRequirementAnalyzer` and chooses the OpenAI or Azure Foundry implementation of `IStructuredOutputClient` from `AI:Provider`. This is the only provider-selection switch. It also binds configuration, validates HTTP input, and maps normalized LLM failures to sanitized Problem Details responses.

### Application

Application owns the use case, the provider-neutral structured-output interface, the system prompt, and the strict schema aligned with `RequirementAnalysis`. The submitted requirement is passed separately as the user message. Provider SDK types do not cross this boundary.

### Domain

Domain contains the stable analysis model and complexity vocabulary. It has no API, Infrastructure, or OpenAI dependencies.

### Infrastructure

Infrastructure contains both provider adapters. Each owns its SDK calls, strict schema response-format configuration, typed deserialization, timeouts, error classification, and model/token/duration logging. The Foundry adapter additionally owns Entra ID authentication and endpoint/deployment configuration. A future provider can implement the same Application port without changing the controller or Domain contract.

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
- Foundry inference is one request per analysis, with SDK retries disabled to avoid unexpected usage.

Prompt separation reduces instruction confusion but is not a complete prompt-injection defense. Stronger input/output controls and security evaluation belong to a later assessment.

## Planned evolution

```text
Structured Output (current)
    -> Tool Calling
    -> Repository Intelligence
    -> RAG
    -> Agents
    -> Multi-Agent Orchestration
    -> MCP
    -> Evaluation
    -> Azure Deployment
```
