namespace AgentForge.Application.HumanApproval;

public interface IApprovalStore
{
    Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApprovalRecord>> ListAsync(ApprovalStatus? status, string tenantId,
        CancellationToken cancellationToken = default);
    Task<ApprovalRecord> CreateAsync(ApprovalRecord record, CancellationToken cancellationToken = default);
    Task<ApprovalWriteResult> TryUpdateAsync(ApprovalRecord record, string expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IActionProposalValidator
{
    Task<ValidatedActionProposal> ValidateAsync(AgentActionProposal proposal, ActorContext actor,
        CancellationToken cancellationToken = default);
}

public interface IActionProposalSourceValidator
{
    Task ValidateAsync(string workflowId, string taskId, IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default);
}

public interface IActionTargetVersionProvider
{
    Task<long> GetVersionAsync(string targetId, CancellationToken cancellationToken = default);
}

public interface IActionPolicyEvaluator
{
    ActionPolicyDecision Evaluate(ValidatedActionProposal proposal, ActorContext actor);
}

public sealed record ApprovedAction(
    string ApprovalId,
    string OperationId,
    string IdempotencyKey,
    long FencingToken,
    string ActionDigest,
    string ActionType,
    string TargetId,
    string FlagName,
    bool Enabled,
    long ExpectedTargetVersion);

public interface IControlledActionExecutor
{
    Task<ActionExecutionReceipt> ExecuteAsync(ApprovedAction action,
        CancellationToken cancellationToken = default);
}

public interface IHumanApprovalService
{
    Task<ApprovalView> CreateAsync(AgentActionProposal proposal, ActorContext actor,
        string correlationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApprovalView>> ListAsync(ApprovalStatus? status, ActorContext actor,
        CancellationToken cancellationToken = default);
    Task<ApprovalView> GetAsync(string approvalId, ActorContext actor,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApprovalAuditEvent>> GetAuditAsync(string approvalId, ActorContext actor,
        CancellationToken cancellationToken = default);
    Task<ApprovalView> ApproveAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default);
    Task<ApprovalView> RejectAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default);
    Task<ApprovalView> RevokeAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default);
    Task<ApprovalView> ExecuteAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default);
}
