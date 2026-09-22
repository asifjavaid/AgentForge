using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Agents;
using AgentForge.Domain.Repositories;

namespace AgentForge.Application.Repositories;

public sealed class RepositoryAnalysisAgent(
    IToolCallingClient client,
    IRepositoryToolDispatcher dispatcher,
    IRepositoryBoundary boundary,
    RepositoryAgentOptions options) : IRepositoryAnalysisAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<RepositoryAnalysisRun> AnalyzeAsync(
        string repositoryPath,
        string request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(request))
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidRequest,
                "Repository path and analysis request are required.");
        }

        var repository = boundary.ResolveRepository(repositoryPath);
        var messages = new List<AgentMessage>
        {
            new(AgentMessageRole.System, RepositoryAnalysisPrompt.SystemMessage),
            new(
                AgentMessageRole.User,
                $"Repository identifier: {repository.DisplayName}\nAnalysis request:\n{request.Trim()}")
        };

        var toolNames = new List<string>();
        var inputTokens = 0;
        var outputTokens = 0;
        var totalTokens = 0;
        var hasInputUsage = false;
        var hasOutputUsage = false;
        var hasTotalUsage = false;
        var provider = "Unknown";
        var model = "Unknown";
        var stopwatch = Stopwatch.StartNew();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.GetTimeout());

        try
        {
            for (var iteration = 1; iteration <= options.GetMaxIterations(); iteration++)
            {
                var response = await client.CompleteAsync(
                    new ToolCallingRequest(
                        messages,
                        dispatcher.Definitions,
                        RepositoryAnalysisSchema.Name,
                        RepositoryAnalysisSchema.Json),
                    timeoutSource.Token);

                provider = response.Provider;
                model = response.Model;
                AddUsage(response.Usage);

                if (response.ToolCalls.Count > 0)
                {
                    messages.Add(new AgentMessage(
                        AgentMessageRole.Assistant,
                        ToolCalls: response.ToolCalls));

                    foreach (var call in response.ToolCalls)
                    {
                        RepositoryToolResult toolResult;
                        try
                        {
                            toolResult = await dispatcher.DispatchAsync(
                                repository,
                                call,
                                timeoutSource.Token);
                            toolNames.Add(call.Name);
                        }
                        catch (AgentOperationException exception) when (
                            exception.FailureKind is AgentFailureKind.AccessDenied or
                            AgentFailureKind.InvalidArguments or
                            AgentFailureKind.InvalidTool)
                        {
                            toolNames.Add($"{call.Name} (rejected)");
                            toolResult = new RepositoryToolResult(JsonSerializer.Serialize(new
                            {
                                error = "tool_request_rejected",
                                reason = exception.FailureKind switch
                                {
                                    AgentFailureKind.AccessDenied => "The requested read is outside the permitted repository policy.",
                                    AgentFailureKind.InvalidTool => "The requested tool is not registered or permitted.",
                                    _ => "The tool arguments are invalid."
                                }
                            }));
                        }

                        messages.Add(new AgentMessage(
                            AgentMessageRole.Tool,
                            toolResult.Content,
                            ToolCallId: call.Id));
                    }

                    continue;
                }

                var analysis = DeserializeAnalysis(response.FinalJson);
                stopwatch.Stop();
                return new RepositoryAnalysisRun(
                    analysis,
                    new RepositoryAnalysisMetadata(
                        provider,
                        model,
                        iteration,
                        toolNames.AsReadOnly(),
                        stopwatch.ElapsedMilliseconds,
                        hasInputUsage ? inputTokens : null,
                        hasOutputUsage ? outputTokens : null,
                        hasTotalUsage ? totalTokens : null));
            }

            throw new AgentOperationException(
                AgentFailureKind.IterationLimit,
                "Repository analysis reached the configured iteration limit.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new AgentOperationException(
                AgentFailureKind.Timeout,
                "Repository analysis timed out.",
                exception);
        }

        void AddUsage(AgentTokenUsage usage)
        {
            if (usage.InputTokens is int currentInput)
            {
                inputTokens += currentInput;
                hasInputUsage = true;
            }

            if (usage.OutputTokens is int currentOutput)
            {
                outputTokens += currentOutput;
                hasOutputUsage = true;
            }

            if (usage.TotalTokens is int currentTotal)
            {
                totalTokens += currentTotal;
                hasTotalUsage = true;
            }
        }
    }

    internal static RepositoryAnalysis DeserializeAnalysis(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidResponse,
                "The model returned neither tool calls nor a final analysis.");
        }

        try
        {
            var analysis = JsonSerializer.Deserialize<RepositoryAnalysis>(json, SerializerOptions)
                ?? throw new JsonException("The response was empty.");

            if (string.IsNullOrWhiteSpace(analysis.Summary) ||
                analysis.ArchitectureComponents is null ||
                analysis.ImportantFiles is null ||
                analysis.Findings is null ||
                analysis.SecurityConcerns is null ||
                analysis.Recommendations is null ||
                analysis.UnansweredQuestions is null)
            {
                throw new JsonException("The response was incomplete.");
            }

            return analysis;
        }
        catch (JsonException exception)
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidResponse,
                "The model returned an invalid repository analysis.",
                exception);
        }
    }
}
