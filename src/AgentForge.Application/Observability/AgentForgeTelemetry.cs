using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentForge.Application.Observability;

public static class AgentForgeTelemetry
{
    public const string SourceName = "AgentForge";
    public static readonly ActivitySource Activities = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
    public static readonly Counter<long> ModelCalls = Meter.CreateCounter<long>("agentforge.model.calls");
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("agentforge.tool.calls");
    public static readonly Counter<long> ToolCallsReused = Meter.CreateCounter<long>("agentforge.tool.calls.reused");
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("agentforge.tokens");
    public static readonly Histogram<double> ModelLatency = Meter.CreateHistogram<double>("agentforge.model.duration", "ms");
    public static readonly Histogram<double> ToolLatency = Meter.CreateHistogram<double>("agentforge.tool.duration", "ms");
    public static readonly Histogram<double> CheckpointLatency = Meter.CreateHistogram<double>("agentforge.checkpoint.duration", "ms");
    public static readonly Histogram<long> ContextCharacters = Meter.CreateHistogram<long>("agentforge.context.characters", "characters");
    public static readonly Counter<long> ApprovalRequests = Meter.CreateCounter<long>("agentforge.approval.requests");
    public static readonly Counter<long> ApprovalDecisions = Meter.CreateCounter<long>("agentforge.approval.decisions");
    public static readonly Counter<long> PolicyDenials = Meter.CreateCounter<long>("agentforge.approval.policy.denials");
    public static readonly Counter<long> ApprovalsExpired = Meter.CreateCounter<long>("agentforge.approval.expired");
    public static readonly Counter<long> ActionExecutions = Meter.CreateCounter<long>("agentforge.action.executions");
    public static readonly Counter<long> ActionReconciliations = Meter.CreateCounter<long>("agentforge.action.reconciliations");
    public static readonly Counter<long> ActionUncertain = Meter.CreateCounter<long>("agentforge.action.uncertain");
    public static readonly Histogram<double> ApprovalLatency = Meter.CreateHistogram<double>("agentforge.approval.duration", "ms");
    public static readonly Histogram<double> ActionExecutionDuration = Meter.CreateHistogram<double>("agentforge.action.duration", "ms");

    public static Activity? Start(string name, string workflowId, string? taskId = null, string? role = null)
    {
        var activity = Activities.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag("agentforge.workflow.id", workflowId);
        activity?.SetTag("agentforge.task.id", taskId);
        activity?.SetTag("agentforge.agent.role", role);
        return activity;
    }
}
