using System.Text.Json;

namespace AgentForge.Api.Contracts;

public sealed record CreateActionProposalRequest(
    string WorkflowId,
    string TaskId,
    string ActionType,
    string TargetId,
    JsonElement Parameters,
    string Justification,
    IReadOnlyList<string> EvidenceIds);
