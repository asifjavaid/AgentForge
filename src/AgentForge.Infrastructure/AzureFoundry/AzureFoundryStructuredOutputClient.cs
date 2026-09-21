using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Llm;
using AgentForge.Domain.Requirements;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AgentForge.Infrastructure.AzureFoundry;

public sealed class AzureFoundryStructuredOutputClient(
    IOptions<AzureFoundryOptions> options,
    ILogger<AzureFoundryStructuredOutputClient> logger) : IStructuredOutputClient
{
    private const string TokenScope = "https://ai.azure.com/.default";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly AzureFoundryOptions _options = options.Value;

    public async Task<T> GenerateAsync<T>(
        StructuredOutputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = _options.GetChatEndpoint();
        var deploymentName = _options.GetDeploymentName();
        var tenantId = _options.GetTenantId();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
#pragma warning disable OPENAI001
            var credential = new DefaultAzureCredential(
                new DefaultAzureCredentialOptions { TenantId = tenantId });
            var tokenPolicy = new BearerTokenPolicy(credential, TokenScope);
            var client = new ChatClient(
                model: deploymentName,
                authenticationPolicy: tokenPolicy,
                options: new OpenAIClientOptions
                {
                    Endpoint = endpoint,
                    RetryPolicy = new ClientRetryPolicy(0)
                });
#pragma warning restore OPENAI001

            var completionOptions = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    request.SchemaName,
                    BinaryData.FromString(request.JsonSchema),
                    jsonSchemaIsStrict: true)
            };

            ChatCompletion completion = await client.CompleteChatAsync(
                [
                    new SystemChatMessage(request.SystemPrompt),
                    new UserChatMessage(request.UserPrompt)
                ],
                completionOptions,
                timeoutSource.Token);

            if (completion.FinishReason != ChatFinishReason.Stop ||
                !string.IsNullOrWhiteSpace(completion.Refusal) ||
                completion.Content.Count == 0)
            {
                throw new LlmOperationException(
                    LlmFailureKind.InvalidResponse,
                    "Azure Foundry did not return a complete structured response.");
            }

            var result = DeserializeStructuredResponse<T>(completion.Content[0].Text);

            stopwatch.Stop();
            logger.LogInformation(
                "LLM operation completed. Provider: AzureFoundry; Model: {Model}; DurationMs: {DurationMs}; InputTokens: {InputTokens}; OutputTokens: {OutputTokens}; TotalTokens: {TotalTokens}",
                deploymentName,
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
            throw CreateFailure(LlmFailureKind.Timeout, "Azure Foundry request timed out.", exception, stopwatch);
        }
        catch (CredentialUnavailableException exception)
        {
            throw CreateFailure(LlmFailureKind.Authentication, "Azure credential is unavailable.", exception, stopwatch);
        }
        catch (AuthenticationFailedException exception)
        {
            throw CreateFailure(LlmFailureKind.Authentication, "Azure authentication failed.", exception, stopwatch);
        }
        catch (ClientResultException exception) when (exception.Status is 401 or 403)
        {
            throw CreateFailure(LlmFailureKind.Authentication, "Azure Foundry authentication failed.", exception, stopwatch);
        }
        catch (ClientResultException exception) when (exception.Status == 429)
        {
            throw CreateFailure(LlmFailureKind.RateLimit, "Azure Foundry throttled the request.", exception, stopwatch);
        }
        catch (ClientResultException exception) when (exception.Status == 404)
        {
            throw CreateFailure(LlmFailureKind.Unavailable, "Azure Foundry deployment is unavailable.", exception, stopwatch);
        }
        catch (ClientResultException exception) when (
            exception.Status == 400 &&
            exception.Message.Contains("Tenant provided in token does not match resource token", StringComparison.OrdinalIgnoreCase))
        {
            throw CreateFailure(LlmFailureKind.Authentication, "Azure Foundry token belongs to a different tenant.", exception, stopwatch);
        }
        catch (ClientResultException exception)
        {
            throw CreateFailure(LlmFailureKind.Provider, "Azure Foundry request failed.", exception, stopwatch);
        }
        catch (HttpRequestException exception)
        {
            throw CreateFailure(LlmFailureKind.Unavailable, "Azure Foundry could not be reached.", exception, stopwatch);
        }
        catch (JsonException exception)
        {
            throw CreateFailure(LlmFailureKind.InvalidResponse, "Azure Foundry returned invalid structured output.", exception, stopwatch);
        }
        catch (LlmOperationException exception)
        {
            stopwatch.Stop();
            logger.LogWarning(
                "LLM operation failed. Provider: AzureFoundry; Model: {Model}; DurationMs: {DurationMs}; FailureKind: {FailureKind}",
                deploymentName,
                stopwatch.ElapsedMilliseconds,
                exception.FailureKind);
            throw;
        }
        catch (Exception exception)
        {
            throw CreateFailure(LlmFailureKind.Provider, "Azure Foundry request failed.", exception, stopwatch);
        }
    }

    internal static T DeserializeStructuredResponse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new LlmOperationException(
                LlmFailureKind.InvalidResponse,
                "Azure Foundry returned an empty structured response.");
        }

        var result = JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new LlmOperationException(
                LlmFailureKind.InvalidResponse,
                "Azure Foundry returned an empty structured response.");

        if (result is RequirementAnalysis analysis &&
            (string.IsNullOrWhiteSpace(analysis.Summary) ||
             analysis.FunctionalRequirements is null ||
             analysis.NonFunctionalRequirements is null ||
             analysis.TechnicalConsiderations is null ||
             analysis.SecurityRisks is null ||
             analysis.Questions is null ||
             !Enum.IsDefined(analysis.Complexity)))
        {
            throw new LlmOperationException(
                LlmFailureKind.InvalidResponse,
                "Azure Foundry returned an incomplete requirement analysis.");
        }

        return result;
    }

    private LlmOperationException CreateFailure(
        LlmFailureKind failureKind,
        string message,
        Exception innerException,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        logger.LogWarning(
            "LLM operation failed. Provider: AzureFoundry; Model: {Model}; DurationMs: {DurationMs}; FailureKind: {FailureKind}; HttpStatus: {HttpStatus}; ExceptionType: {ExceptionType}",
            _options.DeploymentName,
            stopwatch.ElapsedMilliseconds,
            failureKind,
            (innerException as ClientResultException)?.Status,
            innerException.GetType().Name);

        return new LlmOperationException(failureKind, message, innerException);
    }
}
