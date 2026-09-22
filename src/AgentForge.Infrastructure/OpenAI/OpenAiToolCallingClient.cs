using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using AgentForge.Application.Agents;
using AgentForge.Infrastructure.ToolCalling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AgentForge.Infrastructure.OpenAI;

public sealed class OpenAiToolCallingClient(
    IOptions<OpenAIOptions> options,
    ILogger<OpenAiToolCallingClient> logger) : IToolCallingClient
{
    private readonly OpenAIOptions _options = options.Value;

    public async Task<ToolCallingResponse> CompleteAsync(
        ToolCallingRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : _options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new AgentOperationException(AgentFailureKind.Provider, "OpenAI configuration is missing.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = new ChatClient(
                _options.Model,
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(0) });
            var response = await ChatToolCallingProtocol.CompleteAsync(
                client,
                "OpenAI",
                _options.Model,
                request,
                cancellationToken);

            stopwatch.Stop();
            logger.LogInformation(
                "Agent model turn completed. Provider: OpenAI; Model: {Model}; DurationMs: {DurationMs}; ToolCalls: {ToolCalls}; InputTokens: {InputTokens}; OutputTokens: {OutputTokens}; TotalTokens: {TotalTokens}",
                _options.Model,
                stopwatch.ElapsedMilliseconds,
                response.ToolCalls.Count,
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.TotalTokens);
            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger.LogWarning(
                "Agent model turn failed. Provider: OpenAI; Model: {Model}; DurationMs: {DurationMs}; ExceptionType: {ExceptionType}",
                _options.Model,
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name);
            throw new AgentOperationException(
                AgentFailureKind.Provider,
                "OpenAI repository-agent request failed.",
                exception);
        }
    }
}
