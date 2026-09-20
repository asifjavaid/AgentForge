namespace AgentForge.Application.Llm;

public sealed record StructuredOutputRequest(
    string SystemPrompt,
    string UserPrompt,
    string SchemaName,
    string JsonSchema);
