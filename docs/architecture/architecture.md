# AgentForge architecture

Assessment 02 preserves the original layered dependency direction while adding an LLM provider behind an Application port.

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
Infrastructure -> OpenAI Provider -> OpenAI API
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

The API is the composition root. It selects `LlmRequirementAnalyzer` for `IRequirementAnalyzer`, selects the OpenAI implementation for `IStructuredOutputClient`, binds configuration, validates HTTP input, and maps normalized LLM failures to sanitized Problem Details responses.

### Application

Application owns the use case, the provider-neutral structured-output interface, the system prompt, and the strict schema aligned with `RequirementAnalysis`. The submitted requirement is passed separately as the user message. Provider SDK types do not cross this boundary.

### Domain

Domain contains the stable analysis model and complexity vocabulary. It has no API, Infrastructure, or OpenAI dependencies.

### Infrastructure

Infrastructure contains all OpenAI-specific concerns: SDK calls, schema response-format configuration, typed deserialization, timeouts, provider error classification, model/token metadata, and duration logging. A future Microsoft Foundry, Azure OpenAI, Claude, or Gemini adapter can implement the same Application port without changing the controller or Domain contract.

## Structured output flow

```text
Natural language requirement
    -> system instructions + separate user message
    -> OpenAI model
    -> strict JSON-schema constrained output
    -> RequirementAnalysis
```

Strict Structured Outputs ensure schema adherence. Standard JSON deserialization converts that schema-constrained response into the Domain type; there is no delimiter extraction, JSON repair, or ad hoc parsing.

## Security and operations

- API credentials come from user secrets or environment configuration and are excluded from source control.
- The requirement is untrusted user content and is never interpolated into the system prompt.
- Full requirement text is not logged by default.
- Provider exception details are retained internally but replaced with safe public error messages.
- Duration and token counts are recorded as structured metadata for future Application Insights export and cost analysis.

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
