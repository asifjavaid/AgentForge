namespace AgentForge.Application.Agents;

public enum AgentFailureKind
{
    InvalidRequest,
    AccessDenied,
    InvalidTool,
    InvalidArguments,
    IterationLimit,
    InvalidResponse,
    Timeout,
    Provider
}
