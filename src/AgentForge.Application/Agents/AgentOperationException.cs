namespace AgentForge.Application.Agents;

public sealed class AgentOperationException : Exception
{
    public AgentOperationException(AgentFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public AgentOperationException(AgentFailureKind failureKind, string message, Exception innerException)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public AgentFailureKind FailureKind { get; }
}
