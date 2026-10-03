# AgentForge Architecture

AgentForge uses a layered .NET architecture. The API project owns HTTP endpoints and composition. The Application project owns use cases and provider-neutral interfaces. The Domain project owns response and knowledge models. Infrastructure implements Microsoft Foundry and OpenAI clients plus controlled file access.

The repository-analysis agent from Assessment 04 uses a bounded tool-calling loop. The software-knowledge capability is deliberately different: it performs deterministic embedding retrieval first and then makes one grounded generation call.

The application is stateless across process restarts. Assessment 05A keeps knowledge vectors only in memory and rebuilds the index once during an application lifecycle.

Provider authentication is implemented with `DefaultAzureCredential` and Microsoft Entra ID. AgentForge does not use an API key to call Microsoft Foundry.
