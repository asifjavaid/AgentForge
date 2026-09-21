namespace AgentForge.Application.Llm;

public enum LlmFailureKind
{
    Configuration,
    Authentication,
    RateLimit,
    Timeout,
    Unavailable,
    InvalidResponse,
    Provider
}
