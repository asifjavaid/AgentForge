namespace AgentForge.Domain.SoftwareAgents;

public enum SoftwareAgentTerminationReason
{
    Completed,
    MaxStepsReached,
    MaxToolCallsReached,
    ToolFailure,
    InsufficientEvidence,
    InvalidToolRequest,
    Cancelled
}

public sealed record SoftwareAgentSource(string Kind, string Reference);

public sealed record SoftwareAgentToolExecution(
    string ExecutionId,
    string ToolName,
    bool Success,
    long DurationMilliseconds,
    int ResultCharacters,
    IReadOnlyList<string> Evidence,
    string? ErrorCode);

public sealed record SoftwareAgentUsage(
    string Provider,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens);

public sealed record SoftwareAgentRun(
    string RunId,
    string Answer,
    IReadOnlyList<SoftwareAgentSource> Sources,
    IReadOnlyList<SoftwareAgentToolExecution> ToolExecutions,
    SoftwareAgentTerminationReason TerminationReason,
    SoftwareAgentUsage Usage,
    long DurationMilliseconds,
    int ModelTurns,
    int ToolCallCount,
    IReadOnlyList<string> ToolsUsed);
