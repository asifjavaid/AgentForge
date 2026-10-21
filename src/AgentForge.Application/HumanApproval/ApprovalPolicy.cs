using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Application.Agents;
using AgentForge.Application.MultiAgent;

namespace AgentForge.Application.HumanApproval;

public static class ApprovalStateMachine
{
    public static bool CanTransition(ApprovalStatus from, ApprovalStatus to) => (from, to) switch
    {
        (ApprovalStatus.PendingApproval, ApprovalStatus.Approved) => true,
        (ApprovalStatus.PendingApproval, ApprovalStatus.Rejected) => true,
        (ApprovalStatus.PendingApproval, ApprovalStatus.Revoked) => true,
        (ApprovalStatus.PendingApproval, ApprovalStatus.Expired) => true,
        (ApprovalStatus.Approved, ApprovalStatus.Expired) => true,
        (ApprovalStatus.Approved, ApprovalStatus.Revoked) => true,
        (ApprovalStatus.Approved, ApprovalStatus.Executing) => true,
        (ApprovalStatus.Executing, ApprovalStatus.Executed) => true,
        (ApprovalStatus.Executing, ApprovalStatus.ExecutionFailed) => true,
        (ApprovalStatus.Executing, ApprovalStatus.ExecutionUncertain) => true,
        _ => false
    };

    public static void EnsureTransition(ApprovalStatus from, ApprovalStatus to)
    {
        if (!CanTransition(from, to))
            throw new AgentOperationException(AgentFailureKind.Conflict,
                $"Approval transition {from} -> {to} is not permitted.");
    }
}

public static class ActionApprovalDigest
{
    public static string Compute(ValidatedActionProposal proposal, string policyVersion,
        string environmentName, string tenantId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("actionType", proposal.ActionType);
            writer.WriteString("targetId", proposal.TargetId);
            writer.WriteStartObject("parameters");
            writer.WriteBoolean("enabled", proposal.Enabled);
            writer.WriteString("flagName", proposal.FlagName);
            writer.WriteEndObject();
            writer.WriteString("policyVersion", policyVersion);
            writer.WriteString("workflowId", proposal.WorkflowId);
            writer.WriteString("taskId", proposal.TaskId);
            writer.WriteString("environment", environmentName);
            writer.WriteString("tenantId", tenantId);
            writer.WriteNumber("expectedTargetVersion", proposal.ExpectedTargetVersion);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

public sealed class SandboxActionProposalValidator(
    IActionProposalSourceValidator sourceValidator,
    IActionTargetVersionProvider versionProvider,
    HumanApprovalOptions options) : IActionProposalValidator
{
    public async Task<ValidatedActionProposal> ValidateAsync(
        AgentActionProposal proposal,
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        Require(actor, ApprovalPermissions.ProposalCreate);
        if (!Guid.TryParseExact(proposal.WorkflowId, "N", out _) ||
            string.IsNullOrWhiteSpace(proposal.TaskId) || proposal.TaskId.Length > 80 ||
            proposal.ActionType != "UpdateSandboxFeatureFlag" ||
            proposal.TargetId != "sandbox-feature-flags" ||
            string.IsNullOrWhiteSpace(proposal.Justification) || proposal.Justification.Length > 2_000 ||
            proposal.EvidenceIds is null || proposal.EvidenceIds.Count is < 1 or > 32 ||
            proposal.EvidenceIds.Distinct(StringComparer.Ordinal).Count() != proposal.EvidenceIds.Count ||
            proposal.EvidenceIds.Any(item => !IsEvidence(item)))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest,
                "The controlled action proposal is invalid or unregistered.");
        if (proposal.Parameters.ValueKind != JsonValueKind.Object)
            throw InvalidParameters();
        var properties = proposal.Parameters.EnumerateObject().ToArray();
        if (properties.Length != 2 || properties.Any(item => item.Name is not ("flagName" or "enabled")) ||
            properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !proposal.Parameters.TryGetProperty("flagName", out var flagProperty) ||
            flagProperty.ValueKind != JsonValueKind.String ||
            !proposal.Parameters.TryGetProperty("enabled", out var enabledProperty) ||
            enabledProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidParameters();
        var flagName = flagProperty.GetString()!;
        if (!options.AllowedFlagNames.Contains(flagName, StringComparer.Ordinal))
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "The sandbox flag is not allow-listed.");
        await sourceValidator.ValidateAsync(proposal.WorkflowId, proposal.TaskId, proposal.EvidenceIds, cancellationToken);
        var targetVersion = await versionProvider.GetVersionAsync(proposal.TargetId, cancellationToken);
        return new ValidatedActionProposal(proposal.WorkflowId, proposal.TaskId, proposal.ActionType,
            proposal.TargetId, proposal.Parameters.Clone(), proposal.Justification.Trim(),
            proposal.EvidenceIds.ToArray(), flagName, enabledProperty.GetBoolean(), targetVersion);
    }

    private static AgentOperationException InvalidParameters() =>
        new(AgentFailureKind.InvalidArguments, "The controlled action parameters are invalid.");

    private static bool IsEvidence(string value) => value.Length <= 300 &&
        (value.StartsWith("knowledge:", StringComparison.Ordinal) ||
         value.StartsWith("repo:", StringComparison.Ordinal));

    internal static void Require(ActorContext actor, string permission)
    {
        if (string.IsNullOrWhiteSpace(actor.ActorId) || string.IsNullOrWhiteSpace(actor.TenantId) ||
            !actor.Permissions.Contains(permission))
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "The actor is not authorized.");
    }
}

public sealed class DeterministicActionPolicyEvaluator(HumanApprovalOptions options) : IActionPolicyEvaluator
{
    public ActionPolicyDecision Evaluate(ValidatedActionProposal proposal, ActorContext actor)
    {
        var allowed = proposal.ActionType == "UpdateSandboxFeatureFlag" &&
            proposal.TargetId == "sandbox-feature-flags" &&
            options.AllowedFlagNames.Contains(proposal.FlagName, StringComparer.Ordinal) &&
            actor.Permissions.Contains(ApprovalPermissions.ProposalCreate) &&
            !string.IsNullOrWhiteSpace(actor.TenantId);
        return new ActionPolicyDecision(allowed, ActionRisk.Medium, "HumanFourEyes",
            options.ApprovalLifetime, options.PolicyVersion, allowed ? "RegisteredSandboxMutation" : "PolicyDenied");
    }
}

public sealed class WorkflowProposalSourceValidator(IMultiAgentWorkflowManager workflows) : IActionProposalSourceValidator
{
    public async Task ValidateAsync(string workflowId, string taskId, IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        var workflow = await workflows.GetAsync(workflowId, cancellationToken);
        if (workflow.WorkflowId != workflowId)
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The source workflow identity is invalid.");
        var task = workflow.Tasks.SingleOrDefault(item => item.TaskId == taskId) ??
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The source workflow task is invalid.");
        if (task.Status != WorkflowTaskStatus.Completed ||
            evidenceIds.Any(item => !task.Evidence.Contains(item, StringComparer.Ordinal)))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest,
                "The proposal evidence is not proven by the completed source task.");
    }
}
