using System.Text.Json;
using AgentForge.Application.Agents;

namespace AgentForge.Application.HumanApproval;

public sealed class ApprovalRecordValidator(HumanApprovalOptions options)
{
    public void Validate(ApprovalRecord record)
    {
        if (record.SchemaVersion != ApprovalRecord.CurrentSchemaVersion ||
            !Guid.TryParseExact(record.ApprovalId, "N", out _) ||
            !Guid.TryParseExact(record.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(record.IdempotencyKey) || record.IdempotencyKey.Length > 128 ||
            record.Proposal is null || record.ActionDigest.Length != 64 || string.IsNullOrWhiteSpace(record.PolicyVersion) ||
            string.IsNullOrWhiteSpace(record.RequesterId) || string.IsNullOrWhiteSpace(record.TenantId) ||
            string.IsNullOrWhiteSpace(record.ExecutionEnvironment) ||
            record.CreatedAt >= record.ExpiresAt || record.ExecutionAttempts < 0 || record.FencingToken < 0 ||
            record.AuditEvents is null || record.AuditEvents.Count is < 4 || record.AuditEvents.Count > options.MaxAuditEvents ||
            record.AuditEvents.Any(item => item.ApprovalId != record.ApprovalId ||
                item.PolicyVersion != record.PolicyVersion || item.ActionDigest != record.ActionDigest ||
                !Guid.TryParseExact(item.EventId, "N", out _) || string.IsNullOrWhiteSpace(item.ActorId) ||
                string.IsNullOrWhiteSpace(item.CorrelationId) || string.IsNullOrWhiteSpace(item.ReasonCode) ||
                item.ReasonCode.Length > 100) ||
            record.AuditEvents.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != record.AuditEvents.Count)
            Corrupt();
        var digest = ActionApprovalDigest.Compute(record.Proposal!, record.PolicyVersion,
            record.ExecutionEnvironment, record.TenantId);
        if (!string.Equals(digest, record.ActionDigest, StringComparison.Ordinal)) Corrupt();
        var proposal = record.Proposal!;
        if (proposal.ActionType != "UpdateSandboxFeatureFlag" ||
            proposal.TargetId != "sandbox-feature-flags" ||
            proposal.Parameters.ValueKind != JsonValueKind.Object ||
            proposal.Parameters.EnumerateObject().Count() != 2 ||
            !proposal.Parameters.TryGetProperty("flagName", out var flag) ||
            flag.ValueKind != JsonValueKind.String || flag.GetString() != proposal.FlagName ||
            !proposal.Parameters.TryGetProperty("enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            enabled.GetBoolean() != proposal.Enabled ||
            proposal.EvidenceIds is null || proposal.EvidenceIds.Count is < 1 or > 32)
            Corrupt();
        if (record.AuditEvents![^1].NewStatus != record.Status) Corrupt();
        if (record.Status == ApprovalStatus.PendingApproval && record.ApproverId is not null ||
            record.Status is ApprovalStatus.Approved or ApprovalStatus.Executing or ApprovalStatus.Executed &&
            string.IsNullOrWhiteSpace(record.ApproverId) ||
            record.Status == ApprovalStatus.Executing &&
            (string.IsNullOrWhiteSpace(record.ExecutionOwner) || record.ExecutionLeaseExpiresAt is null) ||
            record.Status != ApprovalStatus.Executing &&
            (record.ExecutionOwner is not null || record.ExecutionLeaseExpiresAt is not null) ||
            record.Status == ApprovalStatus.Executed && record.Receipt is null ||
            record.Receipt is not null && record.Receipt.OperationId != record.OperationId)
            Corrupt();
    }

    public void ValidateUpdate(ApprovalRecord current, ApprovalRecord updated)
    {
        Validate(current);
        Validate(updated);
        if (current.ApprovalId != updated.ApprovalId ||
            JsonSerializer.Serialize(current.Proposal) != JsonSerializer.Serialize(updated.Proposal) ||
            current.RequesterId != updated.RequesterId || current.RequesterType != updated.RequesterType ||
            current.TenantId != updated.TenantId || current.ExecutionEnvironment != updated.ExecutionEnvironment ||
            current.Risk != updated.Risk ||
            current.RequiredApprovalLevel != updated.RequiredApprovalLevel ||
            current.PolicyVersion != updated.PolicyVersion || current.ActionDigest != updated.ActionDigest ||
            current.CreatedAt != updated.CreatedAt || current.ExpiresAt != updated.ExpiresAt ||
            current.OperationId != updated.OperationId || current.IdempotencyKey != updated.IdempotencyKey ||
            updated.ExecutionAttempts < current.ExecutionAttempts || updated.FencingToken < current.FencingToken ||
            updated.AuditEvents.Count < current.AuditEvents.Count ||
            !current.AuditEvents.SequenceEqual(updated.AuditEvents.Take(current.AuditEvents.Count)))
            Corrupt();
        if (current.Status != updated.Status)
            ApprovalStateMachine.EnsureTransition(current.Status, updated.Status);
        else if (current.Status != ApprovalStatus.Executing || updated.FencingToken <= current.FencingToken)
            throw new AgentOperationException(AgentFailureKind.Conflict, "The approval update has no valid transition.");
    }

    private static void Corrupt() =>
        throw new AgentOperationException(AgentFailureKind.CorruptState, "The approval record failed integrity validation.");
}
