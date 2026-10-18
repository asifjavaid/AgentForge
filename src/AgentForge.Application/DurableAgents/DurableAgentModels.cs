using AgentForge.Application.Agents;
using AgentForge.Domain.SoftwareAgents;

namespace AgentForge.Application.DurableAgents;

public enum DurableAgentRunStatus
{
    Created,
    Running,
    WaitingToResume,
    Completed,
    InsufficientEvidence,
    Failed,
    Cancelled
}

public enum DurableToolExecutionStatus
{
    Pending,
    Completed,
    Failed
}

public sealed record DurableToolExecution(
    string ExecutionId,
    string ToolName,
    string ArgumentsJson,
    string Fingerprint,
    int ModelStep,
    DurableToolExecutionStatus Status,
    DateTimeOffset? CompletedAt,
    long DurationMilliseconds,
    string? ResultContent,
    IReadOnlyList<string> Evidence,
    bool ConversationApplied,
    string? ErrorCode);

public sealed record DurableAgentCheckpoint(
    int SchemaVersion,
    string RunId,
    string Question,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DurableAgentRunStatus Status,
    int ModelStep,
    int ToolCallCount,
    IReadOnlyList<AgentMessage> Conversation,
    IReadOnlyList<DurableToolExecution> ToolExecutions,
    IReadOnlyList<string> ToolCallFingerprints,
    IReadOnlyList<string> Evidence,
    string Provider,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int TotalToolResultCharacters,
    long TotalDurationMilliseconds,
    IReadOnlyList<string> ToolsUsed,
    string? FinalAnswer,
    IReadOnlyList<string> FinalCitations,
    SoftwareAgentTerminationReason? TerminationReason,
    string? ExecutionOwner,
    DateTimeOffset? LeaseExpiresAt,
    long FencingToken,
    int ResumeCount,
    string? LastFailureClassification,
    string Version)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsTerminal => Status is DurableAgentRunStatus.Completed or
        DurableAgentRunStatus.InsufficientEvidence or DurableAgentRunStatus.Failed or
        DurableAgentRunStatus.Cancelled;
}

public sealed record CheckpointWriteResult(
    bool Succeeded,
    DurableAgentCheckpoint? Checkpoint);

public sealed record DurableAgentProgress(
    int ModelSteps,
    int ToolCalls,
    int ResumeCount,
    int CompletedToolExecutions);

public sealed record DurableAgentRunView(
    string RunId,
    DurableAgentRunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DurableAgentProgress Progress,
    IReadOnlyList<SoftwareAgentToolExecution> ToolExecutions,
    SoftwareAgentRun? FinalResult,
    string? LastFailureClassification);
