# AgentForge

AgentForge is a production-oriented Agentic AI software engineering platform built incrementally to explore LLM engineering, retrieval-augmented generation, tool calling, agent orchestration, evaluation, observability, and Microsoft Azure AI technologies.

Assessment 03 supports OpenAI and Microsoft Foundry as selectable providers for LLM-powered structured requirement analysis. Both produce the existing strongly typed `RequirementAnalysis` contract.

## Architecture

The solution follows dependency inversion:

```text
HTTP request
    -> IRequirementAnalyzer
    -> LlmRequirementAnalyzer
    -> IStructuredOutputClient
    -> OpenAiStructuredOutputClient / AzureFoundryStructuredOutputClient
    -> OpenAI / Microsoft Foundry
```

- `AgentForge.Domain` contains `RequirementAnalysis` and `RequirementComplexity`.
- `AgentForge.Application` owns the analyzer contracts, use-case prompt, strict JSON schema, and provider-neutral structured-output port.
- `AgentForge.Infrastructure` implements the port for OpenAI and Microsoft Foundry. Foundry uses the OpenAI-compatible .NET SDK with Azure Identity.
- `AgentForge.Api` owns HTTP validation, configuration-based provider selection, dependency injection, Problem Details, JSON serialization, and Swagger.
- `AgentForge.Tests` uses fakes and makes no paid API calls.

`DeterministicRequirementAnalyzer` remains available for isolated tests and local scenarios, but `LlmRequirementAnalyzer` is the configured API execution path.

See [the architecture note](docs/architecture/architecture.md) for more detail.

## Structured output and prompt separation

The analyzer supplies a dedicated system message and the client requirement as a separate user message. The user content is treated as untrusted data and cannot become part of the system instructions. Both providers request strict JSON-schema output aligned with `RequirementAnalysis`; neither relies on prompt-only JSON formatting or string extraction.

Prompt injection remains a broader security concern. Assessment 02 provides role separation and explicit system instructions, but does not claim comprehensive prompt-injection prevention.

## Model configuration

The default provider is `OpenAI` with `gpt-4.1-mini`, configured in `appsettings.json`. It supports Structured Outputs and is suitable for bounded extraction. Select either provider without recompilation:

```powershell
$env:AI__Provider = "OpenAI"       # or "AzureFoundry"
$env:OpenAI__Model = "gpt-4.1-mini"
```

The OpenAI path uses temperature `0.1` for consistent analytical output. The Foundry `gpt-5-mini` deployment does not set temperature or a reasoning parameter; model defaults apply.

### Microsoft Foundry configuration

The existing Foundry deployment is named `agentforge-gpt5-mini` and uses the `gpt-5-mini` model. Configure the project endpoint outside source control:

```powershell
dotnet user-secrets set "AzureFoundry:Endpoint" "https://YOUR-RESOURCE.services.ai.azure.com/api/projects/agentforge-dev" --project src/AgentForge.Api
$env:AI__Provider = "AzureFoundry"
az login
dotnet run --project src/AgentForge.Api
```

Alternatively, set `$env:AzureFoundry__Endpoint` in the shell. The endpoint may be the Foundry project URL or an OpenAI-compatible resource URL; the client appends `/openai/v1` when needed. `AzureFoundry:DeploymentName` defaults to `agentforge-gpt5-mini` and is overrideable with `$env:AzureFoundry__DeploymentName`. The URL is configuration, not a bearer token or API key. Do not place secrets in `appsettings.json`.

If Azure CLI has access to multiple tenants, set the optional `AzureFoundry:TenantId` (or `$env:AzureFoundry__TenantId`) to the Foundry resource's tenant ID. A token from another tenant is rejected even when token acquisition succeeds. Do not change the machine's default Azure subscription just to run this app.

The Foundry client uses `DefaultAzureCredential` and obtains a token for `https://ai.azure.com/.default`. Locally, the chain can use Azure CLI, Visual Studio, or another supported developer credential. In Azure, the same code can use Managed Identity when assigned the appropriate Foundry access; for production hardening, pin the credential to Managed Identity so a different credential cannot be selected inadvertently.

No Azure resources are created or modified by AgentForge. The application sends inference requests to the configured project endpoint and uses the deployment name—not the underlying model ID—as the request's `model` value. The Foundry SDK retry policy is set to zero; callers can observe failures without hidden extra paid requests.

## Local secret configuration

Never store the API key in `appsettings.json` or commit it to source control.

Use .NET user secrets:

```powershell
dotnet user-secrets set "OpenAI:ApiKey" "YOUR_API_KEY" --project src/AgentForge.Api
```

Or set the standard OpenAI environment variable for the current shell:

```powershell
$env:OPENAI_API_KEY = "YOUR_API_KEY"
```

Configuration variables using .NET's hierarchical form are also supported:

```powershell
$env:OpenAI__TimeoutSeconds = "30"
$env:OpenAI__Model = "gpt-4.1-mini"
```

## Build, run, and test

Prerequisite: .NET 10 SDK.

```powershell
dotnet restore AgentForge.sln
dotnet build AgentForge.sln
dotnet run --project src/AgentForge.Api
```

Open `/swagger` on the application URL shown in the terminal to test the endpoint interactively.

Automated tests do not need an API key or Azure sign-in and never make provider network calls:

```powershell
dotnet test AgentForge.sln --no-build
```

## API

```http
POST /api/requirements/analyze
Content-Type: application/json

{
  "requirement": "Add forgot-password functionality. Users should receive an email containing a secure password-reset link."
}
```

Example structured response:

```json
{
  "summary": "Add a secure self-service password-reset flow using an emailed reset link.",
  "functionalRequirements": [
    "Allow a user to request a password reset.",
    "Send a password-reset link to the user's email address.",
    "Validate the reset token before allowing a password change."
  ],
  "nonFunctionalRequirements": [
    "Do not reveal whether an account exists.",
    "Rate-limit password-reset requests."
  ],
  "technicalConsiderations": [
    "Use a cryptographically secure, time-limited, single-use token."
  ],
  "securityRisks": [
    "User enumeration, token theft, replay, and request flooding."
  ],
  "questions": [
    "What reset-token lifetime is required?",
    "Should a successful reset revoke active sessions?"
  ],
  "complexity": "Medium"
}
```

The exact analysis is generated by the selected deployment/model. Provider configuration, authentication, rate-limit, unavailable-deployment, timeout, invalid-response, and provider failures are returned as sanitized RFC 7807-style Problem Details responses.

## Observability

Successful provider operations log only:

- model
- duration in milliseconds
- input tokens
- output tokens
- total tokens

Failures log the model, duration, and normalized failure category. API keys, authorization headers, and full user requirements are not logged. These structured log fields can later be exported to Application Insights.

See [Assessment 03 evaluation](docs/assessment-03-evaluation.md) for live verification status and the P1–P4 comparison.
