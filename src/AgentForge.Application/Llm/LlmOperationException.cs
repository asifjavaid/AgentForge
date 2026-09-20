namespace AgentForge.Application.Llm;

public sealed class LlmOperationException : Exception
{
    public LlmOperationException(
        LlmFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public LlmFailureKind FailureKind { get; }
}
