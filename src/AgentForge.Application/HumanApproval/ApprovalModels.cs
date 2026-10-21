using System.Text.Json;

namespace AgentForge.Application.HumanApproval;

public enum ApprovalStatus
{
    PendingApproval,
    Approved,
    Rejected,
    Expired,
    Revoked,
    Executing,
    Executed,
    ExecutionFailed,
    ExecutionUncertain
}

public enum ActionRisk
{
    Low,
    Medium,
    High
}

public enum ApprovalActorType
{
    Agent,
    Human,
    Service
}

public enum ApprovalAuditEventType
{
    ProposalCreated,
    ProposalValidated,
    PolicyEvaluated,
    ApprovalRequested,
    ApprovalGranted,
    ApprovalRejected,
    ApprovalRevoked,
    ApprovalExpired,
    ExecutionStarted,
    ExecutionCompleted,
    ExecutionFailed,
    ExecutionReconciled,
    ExecutionUncertain
}

public enum ControlledActionOutcome
{
    Executed,
    Reconciled,
    Rejected,
    Uncertain
}

public sealed record AgentActionProposal(
    string WorkflowId,
    string TaskId,
    string ActionType,
    string TargetId,
    JsonElement Parameters,
    string Justification,
    IReadOnlyList<string> EvidenceIds);

public sealed record ActorContext(
    string ActorId,
    ApprovalActorType ActorType,
    string TenantId,
    IReadOnlySet<string> Permissions);

public sealed record ValidatedActionProposal(
    string WorkflowId,
    string TaskId,
    string ActionType,
    string TargetId,
    JsonElement Parameters,
    string Justification,
    IReadOnlyList<string> EvidenceIds,
    string FlagName,
    bool Enabled,
    long ExpectedTargetVersion);

public sealed record ActionPolicyDecision(
    bool Allowed,
    ActionRisk Risk,
    string RequiredApprovalLevel,
    TimeSpan ApprovalLifetime,
    string PolicyVersion,
    string ReasonCode);

public sealed record ApprovalAuditEvent(
    string EventId,
    string ApprovalId,
    string ActorId,
    ApprovalActorType ActorType,
    DateTimeOffset Timestamp,
    ApprovalAuditEventType EventType,
    ApprovalStatus? PreviousStatus,
    ApprovalStatus NewStatus,
    string CorrelationId,
    string PolicyVersion,
    string ActionDigest,
    string ReasonCode);

public sealed record ActionExecutionReceipt(
    string OperationId,
    ControlledActionOutcome Outcome,
    string ActionType,
    string TargetId,
    long PreviousTargetVersion,
    long CurrentTargetVersion,
    DateTimeOffset CompletedAt,
    string ReasonCode);

public sealed record ApprovalRecord(
    int SchemaVersion,
    string ApprovalId,
    ValidatedActionProposal Proposal,
    string RequesterId,
    ApprovalActorType RequesterType,
    string TenantId,
    string ExecutionEnvironment,
    ActionRisk Risk,
    string RequiredApprovalLevel,
    string PolicyVersion,
    string ActionDigest,
    ApprovalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? ApproverId,
    DateTimeOffset? DecisionAt,
    string? DecisionReasonCode,
    string OperationId,
    string IdempotencyKey,
    int ExecutionAttempts,
    string? ExecutionOwner,
    DateTimeOffset? ExecutionLeaseExpiresAt,
    long FencingToken,
    ActionExecutionReceipt? Receipt,
    IReadOnlyList<ApprovalAuditEvent> AuditEvents,
    string Version)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsTerminal => Status is ApprovalStatus.Rejected or ApprovalStatus.Expired or
        ApprovalStatus.Revoked or ApprovalStatus.Executed or ApprovalStatus.ExecutionFailed or
        ApprovalStatus.ExecutionUncertain;
}

public sealed record ApprovalWriteResult(bool Succeeded, ApprovalRecord? Record);

public sealed record ApprovalView(
    string ApprovalId,
    string WorkflowId,
    string TaskId,
    string ActionType,
    string TargetId,
    JsonElement Parameters,
    string Justification,
    IReadOnlyList<string> EvidenceIds,
    ActionRisk Risk,
    string PolicyVersion,
    string PolicyDecision,
    string RequesterId,
    string TenantId,
    string ExecutionEnvironment,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string ActionDigest,
    ApprovalStatus Status,
    string? ApproverId,
    DateTimeOffset? DecisionAt,
    ActionExecutionReceipt? Receipt);

public static class ApprovalPermissions
{
    public const string ProposalCreate = "ActionProposal.Create";
    public const string ProposalRead = "ActionProposal.Read";
    public const string Review = "ActionApproval.Review";
    public const string Approve = "ActionApproval.Approve";
    public const string Reject = "ActionApproval.Reject";
    public const string Execute = "ControlledAction.Execute";
    public const string AuditRead = "ActionAudit.Read";
}
