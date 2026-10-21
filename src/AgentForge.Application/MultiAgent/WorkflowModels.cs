using AgentForge.Application.Agents;

namespace AgentForge.Application.MultiAgent;

public enum AgentRole
{
    Supervisor,
    RequirementsAnalyst,
    CodeAnalysis,
    SolutionArchitect,
    SecurityReviewer,
    ReportSynthesis
}

public enum WorkflowStatus
{
    Created,
    Planning,
    Running,
    WaitingToResume,
    Completed,
    CompletedWithConcerns,
    InsufficientEvidence,
    Failed,
    Cancelled
}

public enum WorkflowTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum WorkflowMessageType
{
    WorkflowTaskAssigned,
    TaskStarted,
    TaskCompleted,
    TaskFailed,
    ReviewRequested,
    RevisionRequested,
    ReviewCompleted,
    WorkflowCompleted
}

public enum ReviewOutcome
{
    NotApplicable,
    Approved,
    ApprovedWithConcerns,
    RevisionRequired,
    InsufficientEvidence
}

public enum WorkflowTerminationReason
{
    Completed,
    CompletedWithConcerns,
    InsufficientEvidence,
    PlanningFailed,
    ReviewFailed,
    MaxTasksReached,
    MaxBudgetReached,
    TaskFailure,
    InvalidPlan,
    Cancelled
}

public sealed record WorkflowPlan(
    string Objective,
    IReadOnlyList<PlannedAgentTask> Tasks);

public sealed record PlannedAgentTask(
    string TaskId,
    AgentRole Role,
    string Objective,
    IReadOnlyList<string> DependsOn);

public sealed record AgentTaskOutput(
    string Summary,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Evidence,
    bool InsufficientEvidence,
    ReviewOutcome ReviewOutcome,
    string? RevisionTargetTaskId,
    string? RevisionReason,
    string? ReportMarkdown);

public sealed record AgentTaskMetrics(
    string Provider,
    string Model,
    int ModelCalls,
    int ToolCalls,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    long DurationMilliseconds,
    IReadOnlyList<string> ToolsUsed,
    int ReusedToolCalls = 0,
    int? ContextCharacters = null,
    int? OriginalContextCharacters = null,
    long ModelLatencyMilliseconds = 0,
    long ToolLatencyMilliseconds = 0);

public sealed record AgentTaskExecutionResult(
    AgentTaskOutput Output,
    AgentTaskMetrics Metrics,
    IReadOnlyList<string> ProducedEvidence);

internal static class AgentTaskAttemptTelemetry
{
    private const string MetricsKey = "AgentForge.MultiAgent.AttemptMetrics";
    private const string EvidenceKey = "AgentForge.MultiAgent.AttemptEvidence";

    internal static void Attach(
        Exception exception,
        AgentTaskMetrics metrics,
        IReadOnlyList<string> producedEvidence)
    {
        exception.Data[MetricsKey] = metrics;
        exception.Data[EvidenceKey] = producedEvidence;
    }

    internal static AgentTaskMetrics? GetMetrics(Exception exception) =>
        exception.Data[MetricsKey] as AgentTaskMetrics;

    internal static IReadOnlyList<string> GetEvidence(Exception exception) =>
        exception.Data[EvidenceKey] as IReadOnlyList<string> ?? [];
}

public sealed record WorkflowTaskState(
    string TaskId,
    AgentRole Role,
    string Objective,
    IReadOnlyList<string> DependsOn,
    WorkflowTaskStatus Status,
    int AttemptCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    AgentTaskOutput? Output,
    IReadOnlyList<AgentTaskOutput> PreviousOutputs,
    AgentTaskMetrics? Metrics,
    string? FailureClassification);

public sealed record WorkflowMessage(
    int SchemaVersion,
    string WorkflowId,
    string TaskId,
    AgentRole Sender,
    AgentRole Recipient,
    WorkflowMessageType MessageType,
    string CorrelationId,
    string PayloadSummary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset Timestamp);

public sealed record MultiAgentWorkflowCheckpoint(
    int SchemaVersion,
    string WorkflowId,
    string Objective,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    WorkflowStatus Status,
    WorkflowPlan? Plan,
    IReadOnlyList<WorkflowTaskState> Tasks,
    IReadOnlyList<WorkflowMessage> Events,
    IReadOnlyList<string> Evidence,
    int TotalModelCalls,
    int TotalToolCalls,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int RevisionCount,
    int ResumeCount,
    long TotalDurationMilliseconds,
    string? ExecutionOwner,
    DateTimeOffset? LeaseExpiresAt,
    long FencingToken,
    ReviewOutcome ReviewOutcome,
    WorkflowTerminationReason? TerminationReason,
    string? FinalReport,
    string? LastFailureClassification,
    string Version)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsTerminal => Status is WorkflowStatus.Completed or
        WorkflowStatus.CompletedWithConcerns or WorkflowStatus.InsufficientEvidence or
        WorkflowStatus.Failed or WorkflowStatus.Cancelled;
}

public sealed record WorkflowCheckpointWriteResult(
    bool Succeeded,
    MultiAgentWorkflowCheckpoint? Checkpoint);

public sealed record AgentExecutionContext(
    string WorkflowId,
    string CorrelationId,
    string Objective,
    IReadOnlyList<WorkflowTaskState> DependencyResults,
    IReadOnlyList<string> AvailableEvidence,
    int MaxModelCalls,
    int MaxToolCalls,
    int MaxOutputCharacters,
    int RevisionNumber);

public sealed record WorkflowTaskView(
    string TaskId,
    AgentRole Role,
    IReadOnlyList<string> DependsOn,
    WorkflowTaskStatus Status,
    int AttemptCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int ModelCalls,
    int ToolCalls,
    int? TotalTokens,
    long DurationMilliseconds,
    IReadOnlyList<string> Evidence,
    string? FailureClassification);

public sealed record WorkflowUsageView(
    int ModelCalls,
    int ToolCalls,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    long DurationMilliseconds);

public sealed record MultiAgentWorkflowView(
    string WorkflowId,
    WorkflowStatus Status,
    string CurrentStage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int ResumeCount,
    int RevisionCount,
    IReadOnlyList<WorkflowTaskView> Tasks,
    WorkflowUsageView Usage,
    ReviewOutcome ReviewOutcome,
    WorkflowTerminationReason? TerminationReason,
    string? FinalReport,
    string? LastFailureClassification);

public sealed record WorkflowTaskCall(
    AgentToolCall Call,
    AgentRole Role);
