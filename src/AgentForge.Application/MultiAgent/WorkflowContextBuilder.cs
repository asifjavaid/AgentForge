using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Agents;

namespace AgentForge.Application.MultiAgent;

public sealed record WorkflowContextBuildResult(
    string Json,
    int OriginalCharacters,
    int OptimizedCharacters,
    IReadOnlyList<string> Evidence);

public static class WorkflowContextBuilder
{
    public const int MaximumContextCharacters = 120_000;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static WorkflowContextBuildResult Build(WorkflowTaskState task, AgentExecutionContext context)
    {
        var original = JsonSerializer.Serialize(new
        {
            context.WorkflowId,
            context.CorrelationId,
            workflowObjective = context.Objective,
            task = new { task.TaskId, role = task.Role.ToString(), task.Objective },
            revisionNumber = context.RevisionNumber,
            budgets = new { context.MaxModelCalls, context.MaxToolCalls, context.MaxOutputCharacters },
            previousOutputs = task.PreviousOutputs,
            dependencyResults = context.DependencyResults.Select(item => new
            {
                item.TaskId,
                role = item.Role.ToString(),
                output = item.Output
            }),
            availableEvidence = context.AvailableEvidence
        }, Options);

        var dependencies = context.DependencyResults.Select(item => new
        {
            item.TaskId,
            role = item.Role.ToString(),
            output = Compact(item.Output)
        }).ToArray();
        var previous = task.PreviousOutputs.Select(Compact).ToArray();
        var referenced = dependencies.SelectMany(item => item.output?.Evidence ?? [])
            .Concat(previous.SelectMany(item => item?.Evidence ?? []))
            .Distinct(StringComparer.Ordinal).ToArray();
        var evidence = task.Role is AgentRole.RequirementsAnalyst or AgentRole.CodeAnalysis
            ? context.AvailableEvidence.Distinct(StringComparer.Ordinal).ToArray()
            : referenced;
        var optimized = JsonSerializer.Serialize(new
        {
            workflowId = context.WorkflowId,
            correlationId = context.CorrelationId,
            objective = context.Objective,
            task = new { id = task.TaskId, role = task.Role.ToString(), objective = task.Objective },
            revision = context.RevisionNumber,
            budgets = new { modelCalls = context.MaxModelCalls, toolCalls = context.MaxToolCalls, outputCharacters = context.MaxOutputCharacters },
            previous = previous.Length == 0 ? null : previous,
            dependencies = dependencies.Length == 0 ? null : dependencies,
            evidence = evidence.Length == 0 ? null : evidence
        }, Options);
        if (optimized.Length > MaximumContextCharacters)
            throw new AgentOperationException(AgentFailureKind.InvalidRequest,
                "The evidence-preserving task context exceeds its configured safety bound.");
        return new WorkflowContextBuildResult(optimized, original.Length, optimized.Length, evidence);
    }

    private static CompactAgentOutput? Compact(AgentTaskOutput? output)
    {
        if (output is null) return null;
        return new CompactAgentOutput(
            output.Summary,
            NonEmpty(output.Findings),
            NonEmpty(output.Recommendations),
            NonEmpty(output.Risks),
            NonEmpty(output.Assumptions),
            NonEmpty(output.Evidence),
            output.InsufficientEvidence ? true : null,
            output.ReviewOutcome == ReviewOutcome.NotApplicable ? null : output.ReviewOutcome,
            output.RevisionTargetTaskId,
            output.RevisionReason);
    }

    private static IReadOnlyList<string>? NonEmpty(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : values.Distinct(StringComparer.Ordinal).ToArray();

    private sealed record CompactAgentOutput(
        string Summary,
        IReadOnlyList<string>? Findings,
        IReadOnlyList<string>? Recommendations,
        IReadOnlyList<string>? Risks,
        IReadOnlyList<string>? Assumptions,
        IReadOnlyList<string>? Evidence,
        bool? InsufficientEvidence,
        ReviewOutcome? ReviewOutcome,
        string? RevisionTargetTaskId,
        string? RevisionReason);
}
