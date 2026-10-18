using System.Text.Json;
using AgentForge.Application.Agents;

namespace AgentForge.Application.MultiAgent;

public sealed class WorkflowCheckpointValidator(
    MultiAgentWorkflowOptions options,
    WorkflowPlanValidator planValidator)
{
    public void Validate(MultiAgentWorkflowCheckpoint checkpoint)
    {
        if (checkpoint.SchemaVersion != MultiAgentWorkflowCheckpoint.CurrentSchemaVersion)
            throw Corrupt("The workflow checkpoint schema version is unsupported.");
        if (!Guid.TryParseExact(checkpoint.WorkflowId, "N", out _))
            throw Corrupt("The workflow identifier is invalid.");
        if (string.IsNullOrWhiteSpace(checkpoint.Objective) || checkpoint.Objective.Length > 10_000)
            throw Corrupt("The workflow objective is invalid.");
        if (checkpoint.TotalModelCalls < 0 || checkpoint.TotalToolCalls < 0 || checkpoint.RevisionCount < 0 ||
            checkpoint.ResumeCount < 0 || checkpoint.FencingToken < 0 || checkpoint.TotalDurationMilliseconds < 0)
            throw Corrupt("Workflow counters are invalid.");
        if (checkpoint.RevisionCount > options.MaxRevisionCycles)
            throw Corrupt("Workflow counters exceed configured budgets.");
        if (checkpoint.Events.Count > options.MaxEvents)
            throw Corrupt("Workflow event history exceeds its bound.");
        if (checkpoint.Evidence.Any(item => !IsEvidence(item)) ||
            checkpoint.Evidence.Distinct(StringComparer.Ordinal).Count() != checkpoint.Evidence.Count)
            throw Corrupt("Workflow evidence is invalid.");
        if (checkpoint.Status is WorkflowStatus.Planning or WorkflowStatus.Running &&
            (string.IsNullOrWhiteSpace(checkpoint.ExecutionOwner) || checkpoint.LeaseExpiresAt is null))
            throw Corrupt("An executing workflow is missing ownership.");
        if (checkpoint.Status is not (WorkflowStatus.Planning or WorkflowStatus.Running) &&
            (checkpoint.ExecutionOwner is not null || checkpoint.LeaseExpiresAt is not null))
            throw Corrupt("A non-executing workflow retains ownership.");
        if (checkpoint.IsTerminal && checkpoint.Tasks.Any(task => task.Status == WorkflowTaskStatus.Running))
            throw Corrupt("A terminal workflow contains a running task.");

        ValidatePlanAndTasks(checkpoint);
        ValidateMessages(checkpoint);
        if (JsonSerializer.SerializeToUtf8Bytes(checkpoint).Length > options.MaxCheckpointBytes)
            throw Corrupt("The workflow checkpoint exceeds its configured size.");
    }

    private void ValidatePlanAndTasks(MultiAgentWorkflowCheckpoint checkpoint)
    {
        if (checkpoint.Plan is null)
        {
            if (checkpoint.Tasks.Count != 0)
                throw Corrupt("An unplanned workflow contains tasks.");
            return;
        }

        try
        {
            planValidator.Validate(checkpoint.Plan, checkpoint.Objective);
        }
        catch (AgentOperationException exception)
        {
            throw Corrupt("The persisted workflow plan is invalid.", exception);
        }

        if (checkpoint.Tasks.Select(task => task.TaskId).Distinct(StringComparer.Ordinal).Count() != checkpoint.Tasks.Count)
            throw Corrupt("Workflow task identifiers are duplicated.");
        var supervisor = checkpoint.Tasks.SingleOrDefault(task => task.Role == AgentRole.Supervisor);
        if (supervisor is null || supervisor.TaskId != "supervisor" || supervisor.Status != WorkflowTaskStatus.Completed)
            throw Corrupt("The persisted Supervisor planning task is invalid.");
        var planned = checkpoint.Tasks.Where(task => task.Role != AgentRole.Supervisor).ToArray();
        if (planned.Length != checkpoint.Plan.Tasks.Count)
            throw Corrupt("Workflow tasks do not match the validated plan.");

        foreach (var task in planned)
        {
            var definition = checkpoint.Plan.Tasks.SingleOrDefault(item => item.TaskId == task.TaskId);
            if (definition is null || definition.Role != task.Role || definition.Objective != task.Objective ||
                !definition.DependsOn.SequenceEqual(task.DependsOn, StringComparer.Ordinal))
                throw Corrupt("A workflow task does not match its plan definition.");
            if (task.AttemptCount < 0 || task.AttemptCount > options.MaxTaskAttempts)
                throw Corrupt("A workflow task attempt count is invalid.");
            if (task.Status == WorkflowTaskStatus.Completed && task.Output is null)
                throw Corrupt("A completed workflow task is missing output.");
            if (task.Output is not null && task.Output.Evidence.Any(item => !checkpoint.Evidence.Contains(item, StringComparer.Ordinal)))
                throw Corrupt("A task output cites evidence outside the workflow registry.");
            if ((task.Metrics?.ModelCalls ?? 0) < 0 || (task.Metrics?.ToolCalls ?? 0) < 0)
                throw Corrupt("Task metrics are invalid.");
        }
    }

    private void ValidateMessages(MultiAgentWorkflowCheckpoint checkpoint)
    {
        foreach (var message in checkpoint.Events)
        {
            if (message.SchemaVersion != 1 || message.WorkflowId != checkpoint.WorkflowId ||
                string.IsNullOrWhiteSpace(message.TaskId) || string.IsNullOrWhiteSpace(message.CorrelationId) ||
                message.PayloadSummary.Length > 2_000 ||
                message.Evidence.Any(item => !checkpoint.Evidence.Contains(item, StringComparer.Ordinal)))
                throw Corrupt("A workflow message is invalid.");
            if (message.MessageType is WorkflowMessageType.RevisionRequested or WorkflowMessageType.ReviewCompleted &&
                message.Sender != AgentRole.SecurityReviewer)
                throw Corrupt("A review message has an unauthorized sender.");
            if (message.MessageType == WorkflowMessageType.WorkflowCompleted && message.Sender != AgentRole.Supervisor)
                throw Corrupt("A workflow-completion message has an unauthorized sender.");
        }
    }

    private static bool IsEvidence(string item) =>
        item.StartsWith("knowledge:", StringComparison.Ordinal) || item.StartsWith("repo:", StringComparison.Ordinal);

    private static AgentOperationException Corrupt(string message, Exception? exception = null) =>
        exception is null
            ? new AgentOperationException(AgentFailureKind.CorruptState, message)
            : new AgentOperationException(AgentFailureKind.CorruptState, message, exception);
}
