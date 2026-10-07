using AgentForge.Application.Agents;
using AgentForge.Domain.SoftwareAgents;

namespace AgentForge.Application.SoftwareAgents;

public sealed record SoftwareAgentToolResult(
    string Content,
    IReadOnlyList<string> Evidence,
    string Category);

public interface ISoftwareAgentToolDispatcher
{
    IReadOnlyList<AgentToolDefinition> Definitions { get; }

    Task<SoftwareAgentToolResult> DispatchAsync(
        AgentToolCall call,
        CancellationToken cancellationToken = default);
}

public interface ISoftwareEngineeringAgent
{
    Task<SoftwareAgentRun> AskAsync(
        string question,
        CancellationToken cancellationToken = default);
}
