using System.ClientModel.Primitives;
using System.ClientModel;
using System.Diagnostics;
using AgentForge.Application.Knowledge;
using AgentForge.Application.Llm;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;

namespace AgentForge.Infrastructure.AzureFoundry;

public sealed class AzureFoundryEmbeddingClient(
    IOptions<AzureFoundryOptions> options,
    ILogger<AzureFoundryEmbeddingClient> logger) : IEmbeddingClient
{
    private const string TokenScope = "https://ai.azure.com/.default";
    private readonly AzureFoundryOptions _options = options.Value;

    public Task<EmbeddingBatchResult> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedBatchAsync([text], cancellationToken);

    public async Task<EmbeddingBatchResult> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0 || texts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Embedding input must not be empty.", nameof(texts));
        var model = _options.GetEmbeddingDeploymentName();
        var timer = Stopwatch.StartNew();
        try
        {
#pragma warning disable OPENAI001
            var client = new EmbeddingClient(
                model,
                new BearerTokenPolicy(
                    new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = _options.GetTenantId() }), TokenScope),
                new OpenAIClientOptions { Endpoint = _options.GetEmbeddingEndpoint(), RetryPolicy = new ClientRetryPolicy(0) });
#pragma warning restore OPENAI001
            OpenAIEmbeddingCollection response = await client.GenerateEmbeddingsAsync(texts, cancellationToken: cancellationToken);
            timer.Stop();
            var vectors = response.Select(item => new EmbeddingVector(item.ToFloats().ToArray())).ToArray();
            logger.LogInformation(
                "Embedding operation completed. Provider: AzureFoundry; Model: {Model}; Inputs: {Inputs}; Dimensions: {Dimensions}; DurationMs: {DurationMs}; InputTokens: {InputTokens}",
                model, texts.Count, vectors.FirstOrDefault()?.Values.Count ?? 0, timer.ElapsedMilliseconds, response.Usage?.InputTokenCount);
            return new EmbeddingBatchResult(vectors, "AzureFoundry", model, timer.ElapsedMilliseconds, response.Usage?.InputTokenCount);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Embedding operation failed. Provider: AzureFoundry; Model: {Model}; DurationMs: {DurationMs}; HttpStatus: {HttpStatus}; ExceptionType: {ExceptionType}",
                model, timer.ElapsedMilliseconds, (exception as ClientResultException)?.Status, exception.GetType().Name);
            throw new LlmOperationException(LlmFailureKind.Provider, "Azure Foundry embedding request failed.", exception);
        }
    }
}
