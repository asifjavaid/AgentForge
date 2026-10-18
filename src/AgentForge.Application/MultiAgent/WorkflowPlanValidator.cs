using AgentForge.Application.Agents;

namespace AgentForge.Application.MultiAgent;

public sealed class WorkflowPlanValidator(MultiAgentWorkflowOptions options)
{
    public WorkflowPlan Validate(WorkflowPlan plan, string originalObjective)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(originalObjective) || originalObjective.Length > 10_000)
            throw Invalid("The workflow objective is invalid.");
        if (plan.Tasks is null || plan.Tasks.Count is < 1 || plan.Tasks.Count > options.MaxPlannedTasks)
            throw Invalid("The workflow plan task count is outside the configured bound.");

        var tasks = plan.Tasks.Select(task => task with
        {
            TaskId = task.TaskId.Trim(),
            Objective = task.Objective.Trim(),
            DependsOn = task.DependsOn.Distinct(StringComparer.Ordinal).ToArray()
        }).ToArray();
        if (tasks.Any(task => string.IsNullOrWhiteSpace(task.TaskId) || task.TaskId.Length > 64 ||
            !task.TaskId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw Invalid("A workflow task identifier is invalid.");
        if (tasks.Any(task => string.IsNullOrWhiteSpace(task.Objective) || task.Objective.Length > 2_000))
            throw Invalid("A workflow task objective is invalid.");
        if (tasks.Any(task => !Enum.IsDefined(task.Role) || task.Role == AgentRole.Supervisor))
            throw Invalid("The workflow plan contains an unknown or non-delegable agent role.");
        if (tasks.Select(task => task.TaskId).Distinct(StringComparer.Ordinal).Count() != tasks.Length)
            throw Invalid("The workflow plan contains duplicate task identifiers.");
        if (tasks.GroupBy(task => task.Role).Any(group => group.Count() > 1))
            throw Invalid("The workflow plan may contain at most one task for each specialist role.");

        var taskIds = tasks.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
        if (tasks.Any(task => task.DependsOn.Any(dependency => !taskIds.Contains(dependency) || dependency == task.TaskId)))
            throw Invalid("The workflow plan contains a missing or self dependency.");

        EnsureAcyclicAndBounded(tasks);
        EnsureReviewPolicy(tasks);
        return new WorkflowPlan(originalObjective.Trim(), tasks);
    }

    private void EnsureAcyclicAndBounded(IReadOnlyList<PlannedAgentTask> tasks)
    {
        var byId = tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        int Visit(string taskId)
        {
            if (visiting.Contains(taskId)) throw Invalid("The workflow dependency graph contains a cycle.");
            if (depths.TryGetValue(taskId, out var knownDepth)) return knownDepth;
            visiting.Add(taskId);
            var depth = 1;
            foreach (var dependency in byId[taskId].DependsOn)
                depth = Math.Max(depth, 1 + Visit(dependency));
            visiting.Remove(taskId);
            depths[taskId] = depth;
            if (depth > options.MaxDependencyDepth)
                throw Invalid("The workflow dependency graph exceeds the configured depth.");
            return depth;
        }

        foreach (var task in tasks) Visit(task.TaskId);
    }

    private static void EnsureReviewPolicy(IReadOnlyList<PlannedAgentTask> tasks)
    {
        var reviewer = tasks.SingleOrDefault(task => task.Role == AgentRole.SecurityReviewer);
        var architect = tasks.SingleOrDefault(task => task.Role == AgentRole.SolutionArchitect);
        var report = tasks.SingleOrDefault(task => task.Role == AgentRole.ReportSynthesis);
        if (report is not null && reviewer is null)
            throw Invalid("A final report cannot bypass Security Reviewer assessment.");
        if (architect is not null && reviewer is null)
            throw Invalid("An architecture recommendation requires Security Reviewer assessment.");
        if (architect is not null && reviewer is not null && !DependsTransitively(reviewer, architect.TaskId, tasks))
            throw Invalid("Security Reviewer must depend on the architecture task.");
        if (report is not null && reviewer is not null && !DependsTransitively(report, reviewer.TaskId, tasks))
            throw Invalid("The report task must depend on Security Reviewer.");
        if (report is not null && tasks.Any(task => task.Role is AgentRole.RequirementsAnalyst or AgentRole.CodeAnalysis) &&
            !DependsTransitively(report, reviewer!.TaskId, tasks))
            throw Invalid("The final report review dependency is invalid.");
    }

    private static bool DependsTransitively(
        PlannedAgentTask task,
        string requiredTaskId,
        IReadOnlyList<PlannedAgentTask> tasks)
    {
        var byId = tasks.ToDictionary(item => item.TaskId, StringComparer.Ordinal);
        var pending = new Stack<string>(task.DependsOn);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (current == requiredTaskId) return true;
            foreach (var dependency in byId[current].DependsOn) pending.Push(dependency);
        }
        return false;
    }

    private static AgentOperationException Invalid(string message) =>
        new(AgentFailureKind.InvalidResponse, message);
}
