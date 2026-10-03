using System.Diagnostics;
using AgentForge.Application.Knowledge;
using AgentForge.Application.Llm;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;

namespace AgentForge.Infrastructure.OpenAI;

public sealed class OpenAiEmbeddingClient(IOptions<OpenAIOptions> options) : IEmbeddingClient
{
    private readonly OpenAIOptions _options = options.Value;

    public Task<EmbeddingBatchResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedBatchAsync([text], cancellationToken);

    public async Task<EmbeddingBatchResult> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(_options.ApiKey) ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : _options.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new LlmOperationException(LlmFailureKind.Configuration, "OpenAI configuration is missing.");
        var timer = Stopwatch.StartNew();
        OpenAIEmbeddingCollection response = await new EmbeddingClient(_options.EmbeddingModel, key)
            .GenerateEmbeddingsAsync(texts, cancellationToken: cancellationToken);
        timer.Stop();
        return new EmbeddingBatchResult(
            response.Select(item => new EmbeddingVector(item.ToFloats().ToArray())).ToArray(),
            "OpenAI", _options.EmbeddingModel, timer.ElapsedMilliseconds, response.Usage?.InputTokenCount);
    }
}
