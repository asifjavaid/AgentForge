using System.Diagnostics;
using AgentForge.Application.Agents;
using AgentForge.Application.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.Application.HumanApproval;

public sealed class HumanApprovalService(
    IApprovalStore store,
    IActionProposalValidator proposalValidator,
    IActionProposalSourceValidator sourceValidator,
    IActionPolicyEvaluator policy,
    IControlledActionExecutor executor,
    ApprovalRecordValidator recordValidator,
    HumanApprovalOptions options,
    TimeProvider timeProvider,
    ILogger<HumanApprovalService> logger) : IHumanApprovalService
{
    public async Task<ApprovalView> CreateAsync(AgentActionProposal proposal, ActorContext actor,
        string correlationId, CancellationToken cancellationToken = default)
    {
        using var activity = AgentForgeTelemetry.Start("approval.create", proposal.WorkflowId, proposal.TaskId, actor.ActorType.ToString());
        var validated = await proposalValidator.ValidateAsync(proposal, actor, cancellationToken);
        var decision = policy.Evaluate(validated, actor);
        if (!decision.Allowed)
        {
            AgentForgeTelemetry.PolicyDenials.Add(1);
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "The action policy denied the proposal.");
        }
        var now = timeProvider.GetUtcNow();
        var approvalId = Guid.NewGuid().ToString("N");
        var digest = ActionApprovalDigest.Compute(validated, decision.PolicyVersion,
            options.EnvironmentName, actor.TenantId);
        var record = new ApprovalRecord(ApprovalRecord.CurrentSchemaVersion, approvalId, validated,
            actor.ActorId, actor.ActorType, actor.TenantId, options.EnvironmentName, decision.Risk, decision.RequiredApprovalLevel,
            decision.PolicyVersion, digest, ApprovalStatus.PendingApproval, now, now + decision.ApprovalLifetime,
            null, null, null, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 0, null, null, 0,
            null, InitialAudit(approvalId, actor, now, correlationId, decision.PolicyVersion, digest, decision.ReasonCode), "");
        recordValidator.Validate(record);
        var created = await store.CreateAsync(record, cancellationToken);
        AgentForgeTelemetry.ApprovalRequests.Add(1, Tag("agentforge.action.type", validated.ActionType));
        logger.LogInformation("Approval requested. ApprovalId: {ApprovalId}; WorkflowId: {WorkflowId}; ActionType: {ActionType}; Risk: {Risk}",
            created.ApprovalId, created.Proposal.WorkflowId, created.Proposal.ActionType, created.Risk);
        return View(created);
    }

    public async Task<IReadOnlyList<ApprovalView>> ListAsync(ApprovalStatus? status, ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        SandboxActionProposalValidator.Require(actor, ApprovalPermissions.Review);
        var records = await store.ListAsync(status, actor.TenantId, cancellationToken);
        return records.Select(View).ToArray();
    }

    public async Task<ApprovalView> GetAsync(string approvalId, ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        SandboxActionProposalValidator.Require(actor, ApprovalPermissions.ProposalRead);
        return View(await LoadForActorAsync(approvalId, actor, cancellationToken));
    }

    public async Task<IReadOnlyList<ApprovalAuditEvent>> GetAuditAsync(string approvalId, ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        SandboxActionProposalValidator.Require(actor, ApprovalPermissions.AuditRead);
        return (await LoadForActorAsync(approvalId, actor, cancellationToken)).AuditEvents;
    }

    public Task<ApprovalView> ApproveAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default) => DecideAsync(approvalId, actor, correlationId, true, cancellationToken);

    public Task<ApprovalView> RejectAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default) => DecideAsync(approvalId, actor, correlationId, false, cancellationToken);

    public async Task<ApprovalView> RevokeAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default)
    {
        SandboxActionProposalValidator.Require(actor, ApprovalPermissions.Approve);
        RequireHuman(actor);
        var current = await LoadForActorAsync(approvalId, actor, cancellationToken);
        current = await ExpireIfNeededAsync(current, actor, correlationId, cancellationToken);
        if (current.Status == ApprovalStatus.Revoked) return View(current);
        ApprovalStateMachine.EnsureTransition(current.Status, ApprovalStatus.Revoked);
        var updated = current with
        {
            Status = ApprovalStatus.Revoked,
            DecisionAt = timeProvider.GetUtcNow(),
            DecisionReasonCode = "HumanRevocation",
            AuditEvents = Append(current, actor, ApprovalAuditEventType.ApprovalRevoked,
                ApprovalStatus.Revoked, correlationId, "HumanRevocation")
        };
        return View(await UpdateAsync(current, updated, cancellationToken));
    }

    public async Task<ApprovalView> ExecuteAsync(string approvalId, ActorContext actor, string correlationId,
        CancellationToken cancellationToken = default)
    {
        SandboxActionProposalValidator.Require(actor, ApprovalPermissions.Execute);
        if (actor.ActorType == ApprovalActorType.Agent)
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "An agent cannot execute a controlled action.");
        var current = await LoadForActorAsync(approvalId, actor, cancellationToken);
        current = await ExpireIfNeededAsync(current, actor, correlationId, cancellationToken);
        if (current.Status == ApprovalStatus.Executed) return View(current);
        var now = timeProvider.GetUtcNow();
        if (current.Status == ApprovalStatus.Executing && current.ExecutionLeaseExpiresAt > now)
            throw new AgentOperationException(AgentFailureKind.Conflict, "The action already has an active executor.");
        if (current.Status is not (ApprovalStatus.Approved or ApprovalStatus.Executing))
            throw new AgentOperationException(AgentFailureKind.Conflict, "The approval is not executable.");
        await RevalidateAsync(current, cancellationToken);
        var owner = Guid.NewGuid().ToString("N");
        var fence = current.FencingToken + 1;
        var acquired = current with
        {
            Status = ApprovalStatus.Executing,
            ExecutionOwner = owner,
            ExecutionLeaseExpiresAt = now + options.ExecutionLease,
            FencingToken = fence,
            ExecutionAttempts = current.ExecutionAttempts + 1,
            AuditEvents = Append(current, actor, ApprovalAuditEventType.ExecutionStarted,
                ApprovalStatus.Executing, correlationId,
                current.Status == ApprovalStatus.Executing ? "ExpiredLeaseRecovery" : "ExecutionAuthorized")
        };
        acquired = await UpdateAsync(current, acquired, cancellationToken);
        using var activity = AgentForgeTelemetry.Start("approval.execute", current.Proposal.WorkflowId,
            current.Proposal.TaskId, actor.ActorType.ToString());
        AgentForgeTelemetry.ActionExecutions.Add(1, Tag("agentforge.action.type", current.Proposal.ActionType));
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ExecutionTimeout);
            var receipt = await executor.ExecuteAsync(new ApprovedAction(acquired.ApprovalId, acquired.OperationId,
                acquired.IdempotencyKey, acquired.FencingToken, acquired.ActionDigest, acquired.Proposal.ActionType,
                acquired.Proposal.TargetId, acquired.Proposal.FlagName, acquired.Proposal.Enabled,
                acquired.Proposal.ExpectedTargetVersion), timeout.Token);
            var targetStatus = receipt.Outcome switch
            {
                ControlledActionOutcome.Executed or ControlledActionOutcome.Reconciled => ApprovalStatus.Executed,
                ControlledActionOutcome.Rejected => ApprovalStatus.ExecutionFailed,
                _ => ApprovalStatus.ExecutionUncertain
            };
            var eventType = receipt.Outcome switch
            {
                ControlledActionOutcome.Executed => ApprovalAuditEventType.ExecutionCompleted,
                ControlledActionOutcome.Reconciled => ApprovalAuditEventType.ExecutionReconciled,
                ControlledActionOutcome.Rejected => ApprovalAuditEventType.ExecutionFailed,
                _ => ApprovalAuditEventType.ExecutionUncertain
            };
            var finalized = acquired with
            {
                Status = targetStatus,
                Receipt = receipt,
                ExecutionOwner = null,
                ExecutionLeaseExpiresAt = null,
                AuditEvents = Append(acquired, actor, eventType, targetStatus, correlationId, receipt.ReasonCode)
            };
            var saved = await UpdateAsync(acquired, finalized, CancellationToken.None);
            if (receipt.Outcome == ControlledActionOutcome.Reconciled) AgentForgeTelemetry.ActionReconciliations.Add(1);
            if (targetStatus == ApprovalStatus.ExecutionUncertain) AgentForgeTelemetry.ActionUncertain.Add(1);
            return View(saved);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Controlled action outcome is uncertain. ApprovalId: {ApprovalId}; OperationId: {OperationId}",
                acquired.ApprovalId, acquired.OperationId);
            var uncertain = acquired with
            {
                Status = ApprovalStatus.ExecutionUncertain,
                ExecutionOwner = null,
                ExecutionLeaseExpiresAt = null,
                AuditEvents = Append(acquired, actor, ApprovalAuditEventType.ExecutionUncertain,
                    ApprovalStatus.ExecutionUncertain, correlationId, "ExecutorException")
            };
            AgentForgeTelemetry.ActionUncertain.Add(1);
            return View(await UpdateAsync(acquired, uncertain, CancellationToken.None));
        }
        finally
        {
            timer.Stop();
            AgentForgeTelemetry.ActionExecutionDuration.Record(timer.Elapsed.TotalMilliseconds,
                Tag("agentforge.action.type", current.Proposal.ActionType));
        }
    }

    private async Task<ApprovalView> DecideAsync(string approvalId, ActorContext actor, string correlationId,
        bool approve, CancellationToken cancellationToken)
    {
        SandboxActionProposalValidator.Require(actor,
            approve ? ApprovalPermissions.Approve : ApprovalPermissions.Reject);
        RequireHuman(actor);
        var current = await LoadForActorAsync(approvalId, actor, cancellationToken);
        current = await ExpireIfNeededAsync(current, actor, correlationId, cancellationToken);
        if (approve && current.Status == ApprovalStatus.Approved && current.ApproverId == actor.ActorId)
            return View(current);
        if (!approve && current.Status == ApprovalStatus.Rejected && current.ApproverId == actor.ActorId)
            return View(current);
        if (current.RequesterId == actor.ActorId)
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "Four-eyes policy forbids requester self-approval.");
        var next = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        ApprovalStateMachine.EnsureTransition(current.Status, next);
        await RevalidateAsync(current, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var updated = current with
        {
            Status = next,
            ApproverId = actor.ActorId,
            DecisionAt = now,
            DecisionReasonCode = approve ? "HumanApproval" : "HumanRejection",
            AuditEvents = Append(current, actor,
                approve ? ApprovalAuditEventType.ApprovalGranted : ApprovalAuditEventType.ApprovalRejected,
                next, correlationId, approve ? "HumanApproval" : "HumanRejection")
        };
        var saved = await UpdateAsync(current, updated, cancellationToken);
        AgentForgeTelemetry.ApprovalDecisions.Add(1, Tag("agentforge.approval.decision", next.ToString()));
        AgentForgeTelemetry.ApprovalLatency.Record((now - current.CreatedAt).TotalMilliseconds,
            Tag("agentforge.approval.decision", next.ToString()));
        return View(saved);
    }

    private async Task RevalidateAsync(ApprovalRecord record, CancellationToken cancellationToken)
    {
        recordValidator.Validate(record);
        await sourceValidator.ValidateAsync(record.Proposal.WorkflowId, record.Proposal.TaskId,
            record.Proposal.EvidenceIds, cancellationToken);
        var requester = new ActorContext(record.RequesterId, record.RequesterType, record.TenantId,
            new HashSet<string>([ApprovalPermissions.ProposalCreate], StringComparer.Ordinal));
        var decision = policy.Evaluate(record.Proposal, requester);
        var digest = ActionApprovalDigest.Compute(record.Proposal, decision.PolicyVersion,
            options.EnvironmentName, record.TenantId);
        if (!decision.Allowed || decision.PolicyVersion != record.PolicyVersion ||
            !string.Equals(digest, record.ActionDigest, StringComparison.Ordinal))
            throw new AgentOperationException(AgentFailureKind.Conflict,
                "The approved action no longer matches current policy and binding.");
    }

    private async Task<ApprovalRecord> ExpireIfNeededAsync(ApprovalRecord current, ActorContext actor,
        string correlationId, CancellationToken cancellationToken)
    {
        if (current.ExpiresAt > timeProvider.GetUtcNow() ||
            current.Status is not (ApprovalStatus.PendingApproval or ApprovalStatus.Approved)) return current;
        var expired = current with
        {
            Status = ApprovalStatus.Expired,
            AuditEvents = Append(current, actor, ApprovalAuditEventType.ApprovalExpired,
                ApprovalStatus.Expired, correlationId, "ApprovalLifetimeElapsed")
        };
        AgentForgeTelemetry.ApprovalsExpired.Add(1);
        return await UpdateAsync(current, expired, cancellationToken);
    }

    private async Task<ApprovalRecord> LoadForActorAsync(string approvalId, ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(approvalId, "N", out _))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The approval identifier is invalid.");
        var record = await store.GetAsync(approvalId, cancellationToken) ??
            throw new AgentOperationException(AgentFailureKind.NotFound, "The approval was not found.");
        recordValidator.Validate(record);
        if (record.TenantId != actor.TenantId)
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "The approval is outside the actor scope.");
        return record;
    }

    private async Task<ApprovalRecord> UpdateAsync(ApprovalRecord current, ApprovalRecord updated,
        CancellationToken cancellationToken)
    {
        recordValidator.ValidateUpdate(current, updated);
        var result = await store.TryUpdateAsync(updated, current.Version, cancellationToken);
        if (!result.Succeeded || result.Record is null)
            throw new AgentOperationException(AgentFailureKind.Conflict, "The approval was changed concurrently.");
        return result.Record;
    }

    private IReadOnlyList<ApprovalAuditEvent> Append(ApprovalRecord record, ActorContext actor,
        ApprovalAuditEventType eventType, ApprovalStatus status, string correlationId, string reasonCode) =>
        record.AuditEvents.Append(Event(record.ApprovalId, actor, timeProvider.GetUtcNow(), eventType,
            record.Status, status, correlationId, record.PolicyVersion, record.ActionDigest, reasonCode)).ToArray();

    private static IReadOnlyList<ApprovalAuditEvent> InitialAudit(string approvalId, ActorContext actor,
        DateTimeOffset now, string correlationId, string policyVersion, string digest, string reasonCode) =>
    [
        Event(approvalId, actor, now, ApprovalAuditEventType.ProposalCreated, null,
            ApprovalStatus.PendingApproval, correlationId, policyVersion, digest, "ProposalReceived"),
        Event(approvalId, actor, now, ApprovalAuditEventType.ProposalValidated, null,
            ApprovalStatus.PendingApproval, correlationId, policyVersion, digest, "SchemaAndSourceValidated"),
        Event(approvalId, actor, now, ApprovalAuditEventType.PolicyEvaluated, null,
            ApprovalStatus.PendingApproval, correlationId, policyVersion, digest, reasonCode),
        Event(approvalId, actor, now, ApprovalAuditEventType.ApprovalRequested, null,
            ApprovalStatus.PendingApproval, correlationId, policyVersion, digest, "HumanApprovalRequired")
    ];

    private static ApprovalAuditEvent Event(string approvalId, ActorContext actor, DateTimeOffset timestamp,
        ApprovalAuditEventType eventType, ApprovalStatus? previous, ApprovalStatus next, string correlationId,
        string policyVersion, string digest, string reasonCode) => new(Guid.NewGuid().ToString("N"), approvalId,
        actor.ActorId, actor.ActorType, timestamp, eventType, previous, next,
        string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
        policyVersion, digest, reasonCode);

    private static void RequireHuman(ActorContext actor)
    {
        if (actor.ActorType != ApprovalActorType.Human)
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "Only an authenticated human may decide an approval.");
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private static ApprovalView View(ApprovalRecord record) => new(record.ApprovalId,
        record.Proposal.WorkflowId, record.Proposal.TaskId, record.Proposal.ActionType, record.Proposal.TargetId,
        record.Proposal.Parameters.Clone(), record.Proposal.Justification, record.Proposal.EvidenceIds,
        record.Risk, record.PolicyVersion, "HumanApprovalRequired", record.RequesterId, record.TenantId,
        record.ExecutionEnvironment,
        record.CreatedAt, record.ExpiresAt, record.ActionDigest, record.Status, record.ApproverId,
        record.DecisionAt, record.Receipt);
}
