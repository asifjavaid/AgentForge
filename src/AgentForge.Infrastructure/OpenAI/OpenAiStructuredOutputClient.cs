using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using System.ClientModel;

namespace AgentForge.Infrastructure.OpenAI;

public sealed class OpenAiStructuredOutputClient(
    IOptions<OpenAIOptions> options,
    ILogger<OpenAiStructuredOutputClient> logger) : IStructuredOutputClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly OpenAIOptions _options = options.Value;

    public async Task<T> GenerateAsync<T>(
        StructuredOutputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : _options.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new LlmOperationException(
                LlmFailureKind.Configuration,
                "OpenAI configuration is missing.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = new ChatClient(_options.Model, apiKey);
            var completionOptions = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    request.SchemaName,
                    BinaryData.FromString(request.JsonSchema),
                    jsonSchemaIsStrict: true),
                Temperature = 0.1f
            };

            ChatCompletion completion = await client.CompleteChatAsync(
                [
                    new SystemChatMessage(request.SystemPrompt),
                    new UserChatMessage(request.UserPrompt)
                ],
                completionOptions,
                timeoutSource.Token);

            if (completion.FinishReason is ChatFinishReason.Length or ChatFinishReason.ContentFilter ||
                !string.IsNullOrWhiteSpace(completion.Refusal) ||
                completion.Content.Count == 0)
            {
                throw new LlmOperationException(
                    LlmFailureKind.InvalidResponse,
                    "OpenAI did not return a complete structured response.");
            }

            var result = JsonSerializer.Deserialize<T>(
                completion.Content[0].Text,
                SerializerOptions);

            if (result is null)
            {
                throw new LlmOperationException(
                    LlmFailureKind.InvalidResponse,
                    "OpenAI returned an empty structured response.");
            }

            stopwatch.Stop();
            logger.LogInformation(
                "LLM operation completed. Model: {Model}; DurationMs: {DurationMs}; InputTokens: {InputTokens}; OutputTokens: {OutputTokens}; TotalTokens: {TotalTokens}",
                _options.Model,
                stopwatch.ElapsedMilliseconds,
                completion.Usage?.InputTokenCount,
                completion.Usage?.OutputTokenCount,
                completion.Usage?.TotalTokenCount);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw CreateFailure(LlmFailureKind.Timeout, "OpenAI request timed out.", exception, stopwatch);
        }
        catch (JsonException exception)
        {
            throw CreateFailure(LlmFailureKind.InvalidResponse, "OpenAI returned invalid structured output.", exception, stopwatch);
        }
        catch (ClientResultException exception) when (exception.Status is 401 or 403)
        {
            throw CreateFailure(LlmFailureKind.Authentication, "OpenAI authentication failed.", exception, stopwatch);
        }
        catch (ClientResultException exception) when (exception.Status == 429)
        {
            throw CreateFailure(LlmFailureKind.RateLimit, "OpenAI rate limit was exceeded.", exception, stopwatch);
        }
        catch (ClientResultException exception)
        {
            throw CreateFailure(LlmFailureKind.Provider, "OpenAI request failed.", exception, stopwatch);
        }
        catch (LlmOperationException exception)
        {
            stopwatch.Stop();
            logger.LogWarning(
                "LLM operation failed. Model: {Model}; DurationMs: {DurationMs}; FailureKind: {FailureKind}",
                _options.Model,
                stopwatch.ElapsedMilliseconds,
                exception.FailureKind);
            throw;
        }
        catch (Exception exception)
        {
            throw CreateFailure(LlmFailureKind.Provider, "OpenAI request failed.", exception, stopwatch);
        }
    }

    private LlmOperationException CreateFailure(
        LlmFailureKind failureKind,
        string message,
        Exception innerException,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        logger.LogWarning(
            "LLM operation failed. Model: {Model}; DurationMs: {DurationMs}; FailureKind: {FailureKind}",
            _options.Model,
            stopwatch.ElapsedMilliseconds,
            failureKind);

        return new LlmOperationException(failureKind, message, innerException);
    }
}
