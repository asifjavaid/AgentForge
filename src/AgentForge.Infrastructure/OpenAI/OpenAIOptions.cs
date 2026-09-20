namespace AgentForge.Infrastructure.OpenAI;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    public string? ApiKey { get; init; }

    public string Model { get; init; } = "gpt-4.1-mini";

    public int TimeoutSeconds { get; init; } = 30;
}
