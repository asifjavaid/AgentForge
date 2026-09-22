using System.ClientModel.Primitives;
using System.Diagnostics;
using AgentForge.Application.Agents;
using AgentForge.Infrastructure.ToolCalling;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AgentForge.Infrastructure.AzureFoundry;

public sealed class AzureFoundryToolCallingClient(
    IOptions<AzureFoundryOptions> options,
    ILogger<AzureFoundryToolCallingClient> logger) : IToolCallingClient
{
    private const string TokenScope = "https://ai.azure.com/.default";
    private readonly AzureFoundryOptions _options = options.Value;

    public async Task<ToolCallingResponse> CompleteAsync(
        ToolCallingRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _options.GetChatEndpoint();
        var deployment = _options.GetDeploymentName();
        var tenantId = _options.GetTenantId();
        var stopwatch = Stopwatch.StartNew();

        try
        {
#pragma warning disable OPENAI001
            var credential = new DefaultAzureCredential(
                new DefaultAzureCredentialOptions { TenantId = tenantId });
            var client = new ChatClient(
                deployment,
                new BearerTokenPolicy(credential, TokenScope),
                new OpenAIClientOptions
                {
                    Endpoint = endpoint,
                    RetryPolicy = new ClientRetryPolicy(0)
                });
#pragma warning restore OPENAI001

            var response = await ChatToolCallingProtocol.CompleteAsync(
                client,
                "AzureFoundry",
                deployment,
                request,
                cancellationToken);

            stopwatch.Stop();
            logger.LogInformation(
                "Agent model turn completed. Provider: AzureFoundry; Model: {Model}; DurationMs: {DurationMs}; ToolCalls: {ToolCalls}; InputTokens: {InputTokens}; OutputTokens: {OutputTokens}; TotalTokens: {TotalTokens}",
                deployment,
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
                "Agent model turn failed. Provider: AzureFoundry; Model: {Model}; DurationMs: {DurationMs}; ExceptionType: {ExceptionType}",
                deployment,
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name);
            throw new AgentOperationException(
                AgentFailureKind.Provider,
                "Azure Foundry repository-agent request failed.",
                exception);
        }
    }
}
