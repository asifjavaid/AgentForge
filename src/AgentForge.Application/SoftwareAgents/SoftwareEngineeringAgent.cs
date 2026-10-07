using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentForge.Application.Agents;
using AgentForge.Domain.SoftwareAgents;

namespace AgentForge.Application.SoftwareAgents;

public sealed partial class SoftwareEngineeringAgent(
    IToolCallingClient client,
    ISoftwareAgentToolDispatcher dispatcher,
    SoftwareAgentOptions options) : ISoftwareEngineeringAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<SoftwareAgentRun> AskAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "Question is required.");

        var runId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        var messages = new List<AgentMessage>
        {
            new(AgentMessageRole.System, SoftwareAgentPrompt.SystemMessage),
            new(AgentMessageRole.User, question.Trim())
        };
        var executions = new List<SoftwareAgentToolExecution>();
        var evidence = new HashSet<string>(StringComparer.Ordinal);
        var callFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var toolsUsed = new List<string>();
        var modelTurns = 0;
        var toolCallCount = 0;
        var totalToolCharacters = 0;
        var inputTokens = 0;
        var outputTokens = 0;
        var totalTokens = 0;
        var hasInput = false;
        var hasOutput = false;
        var hasTotal = false;
        var provider = "Unknown";
        var model = "Unknown";

        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.GetTimeout());

        try
        {
            for (var step = 1; step <= options.MaxSteps; step++)
            {
                var response = await client.CompleteAsync(
                    new ToolCallingRequest(
                        messages,
                        dispatcher.Definitions,
                        SoftwareAgentSchema.Name,
                        SoftwareAgentSchema.Json),
                    timeout.Token);
                modelTurns++;
                provider = response.Provider;
                model = response.Model;
                AddUsage(response.Usage);

                if (response.ToolCalls.Count == 0)
                {
                    var final = ParseFinal(response.FinalJson);
                    var citations = final.Citations.Distinct(StringComparer.Ordinal).ToArray();
                    ValidateCitations(final.Answer, citations, evidence);
                    timer.Stop();
                    return BuildRun(
                        final.Answer.Trim(),
                        citations,
                        final.InsufficientEvidence
                            ? SoftwareAgentTerminationReason.InsufficientEvidence
                            : SoftwareAgentTerminationReason.Completed);
                }

                if (!string.IsNullOrWhiteSpace(response.FinalJson))
                    throw new AgentOperationException(
                        AgentFailureKind.InvalidResponse,
                        "The model returned tool calls and a final answer in the same turn.");

                messages.Add(new AgentMessage(AgentMessageRole.Assistant, ToolCalls: response.ToolCalls));
                foreach (var call in response.ToolCalls)
                {
                    if (toolCallCount >= options.MaxToolCalls)
                    {
                        timer.Stop();
                        return BuildRun(
                            "The agent stopped after reaching its tool-call limit.",
                            [],
                            SoftwareAgentTerminationReason.MaxToolCallsReached);
                    }

                    toolCallCount++;
                    var fingerprint = $"{call.Name}\n{NormalizeJson(call.ArgumentsJson)}";
                    if (!callFingerprints.Add(fingerprint))
                    {
                        executions.Add(new SoftwareAgentToolExecution(
                            call.Id, call.Name, false, 0, 0, [], "repeated_tool_call"));
                        timer.Stop();
                        return BuildRun(
                            "The agent stopped because it repeated an identical tool request.",
                            [],
                            SoftwareAgentTerminationReason.InvalidToolRequest);
                    }

                    var toolTimer = Stopwatch.StartNew();
                    try
                    {
                        var result = await dispatcher.DispatchAsync(call, timeout.Token);
                        toolTimer.Stop();
                        var remaining = options.MaxTotalToolResultCharacters - totalToolCharacters;
                        var resultContent = result.Content;
                        var resultEvidence = result.Evidence;
                        if (remaining <= 0)
                        {
                            timer.Stop();
                            return BuildRun(
                                "The agent stopped after reaching its total tool-result limit.",
                                [],
                                SoftwareAgentTerminationReason.ToolFailure);
                        }
                        if (resultContent.Length > remaining)
                        {
                            resultContent = "{\"truncated\":true}";
                            if (resultContent.Length > remaining)
                            {
                                timer.Stop();
                                return BuildRun(
                                    "The agent stopped after reaching its total tool-result limit.",
                                    [],
                                    SoftwareAgentTerminationReason.ToolFailure);
                            }
                            resultEvidence = [];
                        }

                        totalToolCharacters += resultContent.Length;
                        foreach (var item in resultEvidence) evidence.Add(item);
                        toolsUsed.Add(call.Name);
                        executions.Add(new SoftwareAgentToolExecution(
                            call.Id,
                            call.Name,
                            true,
                            toolTimer.ElapsedMilliseconds,
                            resultContent.Length,
                            resultEvidence,
                            null));
                        messages.Add(new AgentMessage(
                            AgentMessageRole.Tool,
                            resultContent,
                            ToolCallId: call.Id));
                    }
                    catch (AgentOperationException exception) when (
                        exception.FailureKind is AgentFailureKind.InvalidTool or
                            AgentFailureKind.InvalidArguments or
                            AgentFailureKind.AccessDenied or
                            AgentFailureKind.InvalidRequest)
                    {
                        toolTimer.Stop();
                        executions.Add(new SoftwareAgentToolExecution(
                            call.Id,
                            call.Name,
                            false,
                            toolTimer.ElapsedMilliseconds,
                            0,
                            [],
                            exception.FailureKind.ToString()));
                        timer.Stop();
                        return BuildRun(
                            "The proposed tool request was rejected by application validation.",
                            [],
                            SoftwareAgentTerminationReason.InvalidToolRequest);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        toolTimer.Stop();
                        executions.Add(new SoftwareAgentToolExecution(
                            call.Id, call.Name, false, toolTimer.ElapsedMilliseconds, 0, [], "tool_failure"));
                        timer.Stop();
                        return BuildRun(
                            "A required read-only tool failed, so the agent could not safely complete the request.",
                            [],
                            SoftwareAgentTerminationReason.ToolFailure);
                    }
                }
            }

            timer.Stop();
            return BuildRun(
                "The agent stopped after reaching its model-step limit.",
                [],
                SoftwareAgentTerminationReason.MaxStepsReached);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new AgentOperationException(
                AgentFailureKind.Timeout,
                "The software agent timed out.",
                exception);
        }

        SoftwareAgentRun BuildRun(
            string answer,
            IReadOnlyList<string> citations,
            SoftwareAgentTerminationReason reason)
        {
            var sources = citations.Select(citation =>
            {
                var separator = citation.IndexOf(':');
                return new SoftwareAgentSource(citation[..separator], citation[(separator + 1)..]);
            }).ToArray();
            return new SoftwareAgentRun(
                runId,
                answer,
                sources,
                executions.AsReadOnly(),
                reason,
                new SoftwareAgentUsage(
                    provider,
                    model,
                    hasInput ? inputTokens : null,
                    hasOutput ? outputTokens : null,
                    hasTotal ? totalTokens : null),
                timer.ElapsedMilliseconds,
                modelTurns,
                toolCallCount,
                toolsUsed.Distinct(StringComparer.Ordinal).ToArray());
        }

        void AddUsage(AgentTokenUsage usage)
        {
            if (usage.InputTokens is int input) { inputTokens += input; hasInput = true; }
            if (usage.OutputTokens is int output) { outputTokens += output; hasOutput = true; }
            if (usage.TotalTokens is int total) { totalTokens += total; hasTotal = true; }
        }
    }

    private static FinalAnswer ParseFinal(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AgentOperationException(
                AgentFailureKind.InvalidResponse,
                "The model returned neither a tool request nor a final answer.");
        try
        {
            var answer = JsonSerializer.Deserialize<FinalAnswer>(json, SerializerOptions)
                ?? throw new JsonException("The response was empty.");
            if (string.IsNullOrWhiteSpace(answer.Answer) || answer.Citations is null)
                throw new JsonException("The response was incomplete.");
            return answer;
        }
        catch (JsonException exception)
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidResponse,
                "The model returned an invalid software-agent answer.",
                exception);
        }
    }

    private static void ValidateCitations(
        string answer,
        IReadOnlyList<string> citations,
        IReadOnlySet<string> evidence)
    {
        if (citations.Any(citation => !evidence.Contains(citation)))
            throw new AgentOperationException(
                AgentFailureKind.InvalidResponse,
                "The final answer cited evidence that was not returned during this run.");
        var inline = CitationPattern().Matches(answer).Select(match => match.Groups[1].Value);
        if (inline.Any(citation => !evidence.Contains(citation)))
            throw new AgentOperationException(
                AgentFailureKind.InvalidResponse,
                "The final answer contained an unverified inline citation.");
    }

    private static string NormalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private sealed record FinalAnswer(
        string Answer,
        IReadOnlyList<string> Citations,
        bool InsufficientEvidence);

    [GeneratedRegex(@"\[((?:knowledge|repo):[^\[\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex CitationPattern();
}
