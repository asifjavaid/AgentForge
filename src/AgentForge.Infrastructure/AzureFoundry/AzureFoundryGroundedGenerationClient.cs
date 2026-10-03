using System.ClientModel.Primitives;
using System.ClientModel;
using AgentForge.Application.Knowledge;
using AgentForge.Application.Llm;
using AgentForge.Infrastructure.Knowledge;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AgentForge.Infrastructure.AzureFoundry;

public sealed class AzureFoundryGroundedGenerationClient(IOptions<AzureFoundryOptions> options) : IGroundedGenerationClient
{
    private const string TokenScope = "https://ai.azure.com/.default";
    private readonly AzureFoundryOptions _options = options.Value;

    public async Task<GroundedGenerationResult> GenerateAsync(GroundedGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var model = _options.GetDeploymentName();
#pragma warning disable OPENAI001
        var client = new ChatClient(
            model,
            new BearerTokenPolicy(
                new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = _options.GetTenantId() }), TokenScope),
            new OpenAIClientOptions { Endpoint = _options.GetChatEndpoint(), RetryPolicy = new ClientRetryPolicy(0) });
#pragma warning restore OPENAI001
        try
        {
            return await GroundedChatProtocol.GenerateAsync(client, "AzureFoundry", model, request, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (ClientResultException exception) when (exception.Status is 401 or 403)
        {
            throw new LlmOperationException(LlmFailureKind.Authentication, "Azure Foundry authentication failed.", exception);
        }
        catch (ClientResultException exception) when (exception.Status == 429)
        {
            throw new LlmOperationException(LlmFailureKind.RateLimit, "Azure Foundry throttled the request.", exception);
        }
        catch (ClientResultException exception)
        {
            throw new LlmOperationException(LlmFailureKind.Provider, "Azure Foundry grounded generation failed.", exception);
        }
    }
}
