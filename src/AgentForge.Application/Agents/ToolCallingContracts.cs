namespace AgentForge.Application.Agents;

public enum AgentMessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    string JsonSchema);

public sealed record AgentToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record AgentMessage(
    AgentMessageRole Role,
    string? Content = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,
    string? ToolCallId = null);

public sealed record AgentTokenUsage(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens);

public sealed record ToolCallingResponse(
    string Provider,
    string Model,
    string? FinalJson,
    IReadOnlyList<AgentToolCall> ToolCalls,
    AgentTokenUsage Usage);

public sealed record ToolCallingRequest(
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    string ResponseSchemaName,
    string ResponseJsonSchema);

public interface IToolCallingClient
{
    Task<ToolCallingResponse> CompleteAsync(
        ToolCallingRequest request,
        CancellationToken cancellationToken = default);
}
