using AgentForge.Application.Knowledge;
using AgentForge.Application.Llm;
using AgentForge.Infrastructure.Knowledge;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace AgentForge.Infrastructure.OpenAI;

public sealed class OpenAiGroundedGenerationClient(IOptions<OpenAIOptions> options) : IGroundedGenerationClient
{
    private readonly OpenAIOptions _options = options.Value;

    public Task<GroundedGenerationResult> GenerateAsync(GroundedGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(_options.ApiKey) ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : _options.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new LlmOperationException(LlmFailureKind.Configuration, "OpenAI configuration is missing.");
        return GroundedChatProtocol.GenerateAsync(
            new ChatClient(_options.Model, key), "OpenAI", _options.Model, request, cancellationToken);
    }
}
