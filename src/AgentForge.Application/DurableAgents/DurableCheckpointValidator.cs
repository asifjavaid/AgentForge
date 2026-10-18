using System.Text.Json;
using AgentForge.Application.Agents;

namespace AgentForge.Application.DurableAgents;

public sealed class DurableCheckpointValidator(DurableAgentOptions options)
{
    public void Validate(DurableAgentCheckpoint checkpoint)
    {
        if (checkpoint.SchemaVersion != DurableAgentCheckpoint.CurrentSchemaVersion)
            throw Corrupt("The checkpoint schema version is unsupported.");
        if (!Guid.TryParseExact(checkpoint.RunId, "N", out _))
            throw Corrupt("The checkpoint run identifier is invalid.");
        if (string.IsNullOrWhiteSpace(checkpoint.Question) || checkpoint.Question.Length > 10_000)
            throw Corrupt("The checkpoint question is invalid.");
        if (checkpoint.ModelStep < 0 || checkpoint.ToolCallCount < 0 || checkpoint.ResumeCount < 0 ||
            checkpoint.FencingToken < 0 || checkpoint.TotalToolResultCharacters < 0)
            throw Corrupt("The checkpoint counters are invalid.");
        if (checkpoint.Conversation.Count > options.MaxConversationMessages)
            throw Corrupt("The checkpoint conversation exceeds its configured bound.");
        if (checkpoint.Conversation.Any(message => message.Role is AgentMessageRole.System or AgentMessageRole.User))
            throw Corrupt("A checkpoint must not persist system or duplicate user messages.");
        if (checkpoint.Conversation.Any(message => (message.Content?.Length ?? 0) > options.MaxCheckpointBytes))
            throw Corrupt("A checkpoint message exceeds its configured bound.");
        if (checkpoint.ToolExecutions.Select(item => item.ExecutionId).Distinct(StringComparer.Ordinal).Count() !=
            checkpoint.ToolExecutions.Count)
            throw Corrupt("The checkpoint contains duplicate tool execution identifiers.");
        if (checkpoint.ToolExecutions.Any(item => item.Status == DurableToolExecutionStatus.Completed &&
            item.ResultContent is null))
            throw Corrupt("A completed tool execution is missing its durable result.");
        if (checkpoint.IsTerminal && checkpoint.ToolExecutions.Any(item => item.Status == DurableToolExecutionStatus.Pending))
            throw Corrupt("A terminal checkpoint contains a pending tool execution.");
        if (checkpoint.Status == DurableAgentRunStatus.Running &&
            (string.IsNullOrWhiteSpace(checkpoint.ExecutionOwner) || checkpoint.LeaseExpiresAt is null))
            throw Corrupt("A running checkpoint is missing execution ownership.");
        if (checkpoint.Status != DurableAgentRunStatus.Running &&
            (checkpoint.ExecutionOwner is not null || checkpoint.LeaseExpiresAt is not null))
            throw Corrupt("A non-running checkpoint cannot retain execution ownership.");
        if (checkpoint.FinalCitations.Any(citation => !checkpoint.Evidence.Contains(citation, StringComparer.Ordinal)))
            throw Corrupt("The checkpoint contains a citation without current-run evidence.");
        if (checkpoint.Evidence.Any(item =>
            !item.StartsWith("knowledge:", StringComparison.Ordinal) &&
            !item.StartsWith("repo:", StringComparison.Ordinal)))
            throw Corrupt("The checkpoint contains an invalid evidence identifier.");

        var size = JsonSerializer.SerializeToUtf8Bytes(checkpoint).Length;
        if (size > options.MaxCheckpointBytes)
            throw Corrupt("The checkpoint exceeds its configured storage bound.");
    }

    private static AgentOperationException Corrupt(string message) =>
        new(AgentFailureKind.CorruptState, message);
}
