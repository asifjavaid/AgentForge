using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentForge.Application.Agents;
using AgentForge.Application.SoftwareAgents;
using AgentForge.Domain.SoftwareAgents;
using Microsoft.Extensions.Logging;

namespace AgentForge.Application.DurableAgents;

public sealed partial class DurableAgentRunManager(
    IAgentCheckpointStore store,
    DurableCheckpointValidator validator,
    IToolCallingClient client,
    ISoftwareAgentToolDispatcher dispatcher,
    SoftwareAgentOptions agentOptions,
    DurableAgentOptions durableOptions,
    TimeProvider timeProvider,
    ILogger<DurableAgentRunManager> logger) : IDurableAgentRunManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<DurableAgentRunView> CreateAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 10_000)
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "A bounded question is required.");

        var now = timeProvider.GetUtcNow();
        var checkpoint = new DurableAgentCheckpoint(
            DurableAgentCheckpoint.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            question.Trim(),
            now,
            now,
            DurableAgentRunStatus.Created,
            0,
            0,
            [],
            [],
            [],
            [],
            "Unknown",
            "Unknown",
            null,
            null,
            null,
            0,
            0,
            [],
            null,
            [],
            null,
            null,
            null,
            0,
            0,
            null,
            string.Empty);
        validator.Validate(checkpoint);
        var created = await store.CreateAsync(checkpoint, cancellationToken);
        logger.LogInformation(
            "Durable agent run created. RunId: {RunId}; CheckpointVersion: {CheckpointVersion}; Status: {Status}",
            created.RunId, created.Version, created.Status);
        return ToView(created);
    }

    public async Task<DurableAgentRunView> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await LoadAsync(runId, cancellationToken);
        return ToView(checkpoint);
    }

    public async Task<DurableAgentRunView> ResumeAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await LoadAsync(runId, cancellationToken);
        if (checkpoint.Status is DurableAgentRunStatus.Completed or DurableAgentRunStatus.InsufficientEvidence)
            return ToView(checkpoint);
        if (checkpoint.Status is DurableAgentRunStatus.Failed or DurableAgentRunStatus.Cancelled)
            throw new AgentOperationException(AgentFailureKind.Conflict, "A terminal durable run cannot be resumed.");

        checkpoint = await RecoverExpiredLeaseAsync(checkpoint, cancellationToken);
        if (checkpoint.Status == DurableAgentRunStatus.Running)
            throw new AgentOperationException(AgentFailureKind.Conflict, "The durable run already has an active executor.");

        DurableAgentStateMachine.EnsureTransition(checkpoint.Status, DurableAgentRunStatus.Running);
        var attemptId = Guid.NewGuid().ToString("N");
        var attemptTimer = Stopwatch.StartNew();
        var now = timeProvider.GetUtcNow();
        var acquired = checkpoint with
        {
            Status = DurableAgentRunStatus.Running,
            ExecutionOwner = attemptId,
            LeaseExpiresAt = now + durableOptions.GetLeaseDuration(),
            FencingToken = checkpoint.FencingToken + 1,
            ResumeCount = checkpoint.ResumeCount + 1,
            UpdatedAt = now,
            LastFailureClassification = null
        };
        checkpoint = await UpdateOrConflictAsync(acquired, checkpoint.Version, cancellationToken);
        var fencingToken = checkpoint.FencingToken;
        logger.LogInformation(
            "Durable execution acquired. RunId: {RunId}; AttemptId: {AttemptId}; FencingToken: {FencingToken}; CheckpointVersion: {CheckpointVersion}; ResumeCount: {ResumeCount}",
            runId, attemptId, fencingToken, checkpoint.Version, checkpoint.ResumeCount);

        try
        {
            checkpoint = await ExecuteAsync(checkpoint, attemptId, fencingToken, cancellationToken);
            attemptTimer.Stop();
            checkpoint = await UpdateOrConflictAsync(checkpoint with
            {
                TotalDurationMilliseconds = checkpoint.TotalDurationMilliseconds + attemptTimer.ElapsedMilliseconds,
                UpdatedAt = timeProvider.GetUtcNow()
            }, checkpoint.Version, CancellationToken.None);
            logger.LogInformation(
                "Durable execution attempt ended. RunId: {RunId}; AttemptId: {AttemptId}; Status: {Status}; TerminationReason: {TerminationReason}; ModelSteps: {ModelSteps}; ToolCalls: {ToolCalls}; TotalTokens: {TotalTokens}; AttemptDurationMs: {AttemptDurationMs}",
                checkpoint.RunId, attemptId, checkpoint.Status, checkpoint.TerminationReason,
                checkpoint.ModelStep, checkpoint.ToolCallCount, checkpoint.TotalTokens, attemptTimer.ElapsedMilliseconds);
            return ToView(checkpoint);
        }
        catch (OperationCanceledException)
        {
            attemptTimer.Stop();
            await TryReleaseAsync(
                checkpoint.RunId,
                attemptId,
                fencingToken,
                DurableAgentRunStatus.WaitingToResume,
                "ExecutionAttemptCancelled",
                attemptTimer.ElapsedMilliseconds,
                CancellationToken.None);
            throw;
        }
        catch (AgentOperationException exception) when (
            exception.FailureKind is AgentFailureKind.Provider or AgentFailureKind.Timeout or AgentFailureKind.Storage)
        {
            attemptTimer.Stop();
            var released = await TryReleaseAsync(
                checkpoint.RunId,
                attemptId,
                fencingToken,
                DurableAgentRunStatus.WaitingToResume,
                exception.FailureKind.ToString(),
                attemptTimer.ElapsedMilliseconds,
                CancellationToken.None);
            return ToView(released ?? await LoadAsync(runId, CancellationToken.None));
        }
        catch (AgentOperationException exception) when (
            exception.FailureKind is not AgentFailureKind.Conflict and
            not AgentFailureKind.NotFound and
            not AgentFailureKind.CorruptState)
        {
            attemptTimer.Stop();
            var released = await TryReleaseAsync(
                checkpoint.RunId,
                attemptId,
                fencingToken,
                DurableAgentRunStatus.Failed,
                exception.FailureKind.ToString(),
                attemptTimer.ElapsedMilliseconds,
                CancellationToken.None);
            return ToView(released ?? await LoadAsync(runId, CancellationToken.None));
        }
    }

    public async Task<DurableAgentRunView> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await LoadAsync(runId, cancellationToken);
        if (checkpoint.IsTerminal)
        {
            if (checkpoint.Status == DurableAgentRunStatus.Cancelled)
                return ToView(checkpoint);
            throw new AgentOperationException(AgentFailureKind.Conflict, "A terminal durable run cannot be cancelled.");
        }

        DurableAgentStateMachine.EnsureTransition(checkpoint.Status, DurableAgentRunStatus.Cancelled);
        var cancelled = checkpoint with
        {
            Status = DurableAgentRunStatus.Cancelled,
            ExecutionOwner = null,
            LeaseExpiresAt = null,
            FencingToken = checkpoint.FencingToken + 1,
            TerminationReason = SoftwareAgentTerminationReason.Cancelled,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        var saved = await UpdateOrConflictAsync(cancelled, checkpoint.Version, cancellationToken);
        return ToView(saved);
    }

    private async Task<DurableAgentCheckpoint> ExecuteAsync(
        DurableAgentCheckpoint checkpoint,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        agentOptions.Validate();
        durableOptions.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(agentOptions.GetTimeout());

        checkpoint = await ContinuePendingExecutionsAsync(checkpoint, owner, fencingToken, timeout.Token);
        if (checkpoint.IsTerminal)
            return checkpoint;
        while (checkpoint.ModelStep < agentOptions.MaxSteps)
        {
            checkpoint = await EnsureOwnershipAsync(checkpoint, owner, fencingToken, timeout.Token);
            var messages = new List<AgentMessage>
            {
                new(AgentMessageRole.System, SoftwareAgentPrompt.SystemMessage),
                new(AgentMessageRole.User, checkpoint.Question)
            };
            messages.AddRange(checkpoint.Conversation);
            var response = await client.CompleteAsync(
                new ToolCallingRequest(
                    messages,
                    dispatcher.Definitions,
                    SoftwareAgentSchema.Name,
                    SoftwareAgentSchema.Json),
                timeout.Token);
            checkpoint = AddModelUsage(checkpoint, response);

            if (response.ToolCalls.Count == 0)
            {
                var final = ParseFinal(response.FinalJson);
                var citations = final.Citations.Distinct(StringComparer.Ordinal).ToArray();
                ValidateCitations(final.Answer, citations, checkpoint.Evidence);
                var status = final.InsufficientEvidence
                    ? DurableAgentRunStatus.InsufficientEvidence
                    : DurableAgentRunStatus.Completed;
                DurableAgentStateMachine.EnsureTransition(checkpoint.Status, status);
                var completed = checkpoint with
                {
                    Status = status,
                    FinalAnswer = final.Answer.Trim(),
                    FinalCitations = citations,
                    TerminationReason = final.InsufficientEvidence
                        ? SoftwareAgentTerminationReason.InsufficientEvidence
                        : SoftwareAgentTerminationReason.Completed,
                    ExecutionOwner = null,
                    LeaseExpiresAt = null,
                    UpdatedAt = timeProvider.GetUtcNow()
                };
                return await UpdateOrConflictAsync(completed, checkpoint.Version, timeout.Token);
            }

            if (!string.IsNullOrWhiteSpace(response.FinalJson))
                throw new AgentOperationException(
                    AgentFailureKind.InvalidResponse,
                    "The model returned tool calls and a final answer in the same turn.");

            checkpoint = await RecordPendingCallsAsync(
                checkpoint,
                response.ToolCalls,
                owner,
                fencingToken,
                timeout.Token);
            if (checkpoint.IsTerminal)
                return checkpoint;
            checkpoint = await ContinuePendingExecutionsAsync(checkpoint, owner, fencingToken, timeout.Token);
            if (checkpoint.IsTerminal)
                return checkpoint;
        }

        return await FinishOwnedAsync(
            checkpoint,
            owner,
            fencingToken,
            DurableAgentRunStatus.Failed,
            SoftwareAgentTerminationReason.MaxStepsReached,
            "MaxStepsReached",
            cancellationToken);
    }

    private DurableAgentCheckpoint AddModelUsage(
        DurableAgentCheckpoint checkpoint,
        ToolCallingResponse response) => checkpoint with
        {
            ModelStep = checkpoint.ModelStep + 1,
            Provider = response.Provider,
            Model = response.Model,
            InputTokens = AddNullable(checkpoint.InputTokens, response.Usage.InputTokens),
            OutputTokens = AddNullable(checkpoint.OutputTokens, response.Usage.OutputTokens),
            TotalTokens = AddNullable(checkpoint.TotalTokens, response.Usage.TotalTokens)
        };

    private async Task<DurableAgentCheckpoint> RecordPendingCallsAsync(
        DurableAgentCheckpoint checkpoint,
        IReadOnlyList<AgentToolCall> calls,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var definitions = dispatcher.Definitions.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var executions = checkpoint.ToolExecutions.ToList();
        var fingerprints = checkpoint.ToolCallFingerprints.ToHashSet(StringComparer.Ordinal);
        var toolCallCount = checkpoint.ToolCallCount;
        foreach (var call in calls)
        {
            if (toolCallCount >= agentOptions.MaxToolCalls)
                return await FinishOwnedAsync(
                    checkpoint, owner, fencingToken, DurableAgentRunStatus.Failed,
                    SoftwareAgentTerminationReason.MaxToolCallsReached, "MaxToolCallsReached", cancellationToken);
            if (!definitions.Contains(call.Name))
                return await FinishOwnedAsync(
                    checkpoint, owner, fencingToken, DurableAgentRunStatus.Failed,
                    SoftwareAgentTerminationReason.InvalidToolRequest, "InvalidTool", cancellationToken);
            if (call.Name == "search_code" &&
                executions.Count(item => item.ToolName == "search_code" && item.Status == DurableToolExecutionStatus.Completed) >=
                    agentOptions.MaxRepositorySearchCalls)
                return await FinishOwnedAsync(
                    checkpoint, owner, fencingToken, DurableAgentRunStatus.InsufficientEvidence,
                    SoftwareAgentTerminationReason.InsufficientEvidence, "RepositorySearchBudgetExhausted", cancellationToken);

            toolCallCount++;
            var fingerprint = $"{call.Name}\n{NormalizeJson(call.ArgumentsJson)}";
            if (!fingerprints.Add(fingerprint))
                return await FinishOwnedAsync(
                    checkpoint, owner, fencingToken, DurableAgentRunStatus.Failed,
                    SoftwareAgentTerminationReason.InvalidToolRequest, "RepeatedToolCall", cancellationToken);
            executions.Add(new DurableToolExecution(
                call.Id,
                call.Name,
                call.ArgumentsJson,
                fingerprint,
                checkpoint.ModelStep,
                DurableToolExecutionStatus.Pending,
                null,
                0,
                null,
                [],
                false,
                null));
        }

        var updated = checkpoint with
        {
            ToolCallCount = toolCallCount,
            Conversation = [.. checkpoint.Conversation, new AgentMessage(AgentMessageRole.Assistant, ToolCalls: calls)],
            ToolExecutions = executions,
            ToolCallFingerprints = fingerprints.ToArray()
        };
        return await SaveOwnedAsync(updated, owner, fencingToken, cancellationToken);
    }

    private async Task<DurableAgentCheckpoint> ContinuePendingExecutionsAsync(
        DurableAgentCheckpoint checkpoint,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        foreach (var executionId in checkpoint.ToolExecutions
                     .Where(item => item.Status == DurableToolExecutionStatus.Pending)
                     .Select(item => item.ExecutionId)
                     .ToArray())
        {
            checkpoint = await EnsureOwnershipAsync(checkpoint, owner, fencingToken, cancellationToken);
            var execution = checkpoint.ToolExecutions.Single(item => item.ExecutionId == executionId);
            var timer = Stopwatch.StartNew();
            SoftwareAgentToolResult result;
            try
            {
                result = await dispatcher.DispatchAsync(
                    new AgentToolCall(execution.ExecutionId, execution.ToolName, execution.ArgumentsJson),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentOperationException exception) when (
                exception.FailureKind is AgentFailureKind.InvalidTool or AgentFailureKind.InvalidArguments or
                    AgentFailureKind.AccessDenied or AgentFailureKind.InvalidRequest)
            {
                timer.Stop();
                return await FailPendingAsync(
                    checkpoint, executionId, exception.FailureKind.ToString(), timer.ElapsedMilliseconds,
                    owner, fencingToken, cancellationToken);
            }
            catch
            {
                timer.Stop();
                return await FailPendingAsync(
                    checkpoint, executionId, "ToolFailure", timer.ElapsedMilliseconds,
                    owner, fencingToken, cancellationToken);
            }
            timer.Stop();

            var remaining = agentOptions.MaxTotalToolResultCharacters - checkpoint.TotalToolResultCharacters;
            if (remaining <= 0 || result.Content.Length > remaining)
                return await FinishOwnedAsync(
                    checkpoint, owner, fencingToken, DurableAgentRunStatus.Failed,
                    SoftwareAgentTerminationReason.ToolFailure, "ToolResultBudgetExceeded", cancellationToken);

            var executions = checkpoint.ToolExecutions.Select(item =>
                item.ExecutionId == executionId
                    ? item with
                    {
                        Status = DurableToolExecutionStatus.Completed,
                        CompletedAt = timeProvider.GetUtcNow(),
                        DurationMilliseconds = timer.ElapsedMilliseconds,
                        ResultContent = result.Content,
                        Evidence = result.Evidence
                    }
                    : item).ToArray();
            checkpoint = checkpoint with
            {
                ToolExecutions = executions,
                Evidence = checkpoint.Evidence.Concat(result.Evidence).Distinct(StringComparer.Ordinal).ToArray(),
                TotalToolResultCharacters = checkpoint.TotalToolResultCharacters + result.Content.Length,
                ToolsUsed = checkpoint.ToolsUsed.Append(execution.ToolName).Distinct(StringComparer.Ordinal).ToArray()
            };
            checkpoint = await SaveOwnedAsync(checkpoint, owner, fencingToken, cancellationToken);
        }

        foreach (var executionId in checkpoint.ToolExecutions
                     .Where(item => item.Status == DurableToolExecutionStatus.Completed && !item.ConversationApplied)
                     .Select(item => item.ExecutionId)
                     .ToArray())
        {
            var execution = checkpoint.ToolExecutions.Single(item => item.ExecutionId == executionId);
            var executions = checkpoint.ToolExecutions.Select(item =>
                item.ExecutionId == executionId ? item with { ConversationApplied = true } : item).ToArray();
            checkpoint = checkpoint with
            {
                Conversation =
                [.. checkpoint.Conversation, new AgentMessage(AgentMessageRole.Tool, execution.ResultContent, ToolCallId: executionId)],
                ToolExecutions = executions
            };
            checkpoint = await SaveOwnedAsync(checkpoint, owner, fencingToken, cancellationToken);
        }

        return checkpoint;
    }

    private async Task<DurableAgentCheckpoint> FailPendingAsync(
        DurableAgentCheckpoint checkpoint,
        string executionId,
        string errorCode,
        long durationMilliseconds,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var executions = checkpoint.ToolExecutions.Select(item =>
            item.Status == DurableToolExecutionStatus.Pending
                ? item with
                {
                    Status = DurableToolExecutionStatus.Failed,
                    CompletedAt = timeProvider.GetUtcNow(),
                    DurationMilliseconds = item.ExecutionId == executionId ? durationMilliseconds : 0,
                    ErrorCode = item.ExecutionId == executionId ? errorCode : "AbandonedAfterToolFailure"
                }
                : item).ToArray();
        checkpoint = checkpoint with { ToolExecutions = executions };
        return await FinishOwnedAsync(
            checkpoint,
            owner,
            fencingToken,
            DurableAgentRunStatus.Failed,
            errorCode == "ToolFailure"
                ? SoftwareAgentTerminationReason.ToolFailure
                : SoftwareAgentTerminationReason.InvalidToolRequest,
            errorCode,
            cancellationToken);
    }

    private async Task<DurableAgentCheckpoint> FinishOwnedAsync(
        DurableAgentCheckpoint checkpoint,
        string owner,
        long fencingToken,
        DurableAgentRunStatus status,
        SoftwareAgentTerminationReason terminationReason,
        string failureClassification,
        CancellationToken cancellationToken)
    {
        checkpoint = await EnsureOwnershipAsync(checkpoint, owner, fencingToken, cancellationToken);
        DurableAgentStateMachine.EnsureTransition(checkpoint.Status, status);
        var completed = checkpoint with
        {
            Status = status,
            TerminationReason = terminationReason,
            LastFailureClassification = failureClassification,
            ExecutionOwner = null,
            LeaseExpiresAt = null,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return await UpdateOrConflictAsync(completed, checkpoint.Version, cancellationToken);
    }

    private async Task<DurableAgentCheckpoint> SaveOwnedAsync(
        DurableAgentCheckpoint checkpoint,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        if (checkpoint.ExecutionOwner != owner || checkpoint.FencingToken != fencingToken ||
            checkpoint.Status != DurableAgentRunStatus.Running)
            throw new AgentOperationException(AgentFailureKind.Conflict, "Durable execution ownership was lost.");
        var now = timeProvider.GetUtcNow();
        var updated = checkpoint with
        {
            UpdatedAt = now,
            LeaseExpiresAt = now + durableOptions.GetLeaseDuration()
        };
        return await UpdateOrConflictAsync(updated, checkpoint.Version, cancellationToken);
    }

    private async Task<DurableAgentCheckpoint> EnsureOwnershipAsync(
        DurableAgentCheckpoint checkpoint,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var latest = await LoadAsync(checkpoint.RunId, cancellationToken);
        if (latest.Version != checkpoint.Version || latest.ExecutionOwner != owner ||
            latest.FencingToken != fencingToken || latest.Status != DurableAgentRunStatus.Running ||
            latest.LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw new AgentOperationException(AgentFailureKind.Conflict, "Durable execution ownership was lost.");
        return checkpoint;
    }

    private async Task<DurableAgentCheckpoint> RecoverExpiredLeaseAsync(
        DurableAgentCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Status != DurableAgentRunStatus.Running || checkpoint.LeaseExpiresAt > timeProvider.GetUtcNow())
            return checkpoint;
        DurableAgentStateMachine.EnsureTransition(checkpoint.Status, DurableAgentRunStatus.WaitingToResume);
        var recovered = checkpoint with
        {
            Status = DurableAgentRunStatus.WaitingToResume,
            ExecutionOwner = null,
            LeaseExpiresAt = null,
            FencingToken = checkpoint.FencingToken + 1,
            LastFailureClassification = "ExpiredExecutionOwnership",
            UpdatedAt = timeProvider.GetUtcNow()
        };
        var saved = await UpdateOrConflictAsync(recovered, checkpoint.Version, cancellationToken);
        logger.LogWarning(
            "Expired durable execution ownership recovered. RunId: {RunId}; PreviousFencingToken: {PreviousFencingToken}; CheckpointVersion: {CheckpointVersion}",
            checkpoint.RunId, checkpoint.FencingToken, saved.Version);
        return saved;
    }

    private async Task<DurableAgentCheckpoint?> TryReleaseAsync(
        string runId,
        string owner,
        long fencingToken,
        DurableAgentRunStatus status,
        string failure,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await LoadAsync(runId, cancellationToken);
            if (latest.ExecutionOwner != owner || latest.FencingToken != fencingToken ||
                latest.Status != DurableAgentRunStatus.Running)
                return null;
            DurableAgentStateMachine.EnsureTransition(latest.Status, status);
            var released = latest with
            {
                Status = status,
                ExecutionOwner = null,
                LeaseExpiresAt = null,
                LastFailureClassification = failure,
                TotalDurationMilliseconds = latest.TotalDurationMilliseconds + elapsedMilliseconds,
                UpdatedAt = timeProvider.GetUtcNow()
            };
            var result = await store.TryUpdateAsync(released, latest.Version, cancellationToken);
            return result.Succeeded ? result.Checkpoint : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<DurableAgentCheckpoint> LoadAsync(string runId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(runId, "N", out _))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The durable run identifier is invalid.");
        var timer = Stopwatch.StartNew();
        var checkpoint = await store.GetAsync(runId, cancellationToken);
        timer.Stop();
        logger.LogDebug(
            "Durable checkpoint loaded. RunId: {RunId}; Found: {Found}; DurationMs: {DurationMs}",
            runId, checkpoint is not null, timer.ElapsedMilliseconds);
        if (checkpoint is null)
            throw new AgentOperationException(AgentFailureKind.NotFound, "The durable run was not found.");
        validator.Validate(checkpoint);
        return checkpoint;
    }

    private async Task<DurableAgentCheckpoint> UpdateOrConflictAsync(
        DurableAgentCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        validator.Validate(checkpoint);
        var timer = Stopwatch.StartNew();
        var result = await store.TryUpdateAsync(checkpoint, expectedVersion, cancellationToken);
        timer.Stop();
        if (!result.Succeeded || result.Checkpoint is null)
        {
            logger.LogWarning(
                "Durable checkpoint concurrency conflict. RunId: {RunId}; ExpectedVersion: {ExpectedVersion}; CurrentVersion: {CurrentVersion}",
                checkpoint.RunId, expectedVersion, result.Checkpoint?.Version);
            throw new AgentOperationException(AgentFailureKind.Conflict, "The durable checkpoint was updated by another executor.");
        }
        logger.LogInformation(
            "Durable checkpoint saved. RunId: {RunId}; Status: {Status}; CheckpointVersion: {CheckpointVersion}; ModelStep: {ModelStep}; ToolCalls: {ToolCalls}; DurationMs: {DurationMs}",
            result.Checkpoint.RunId, result.Checkpoint.Status, result.Checkpoint.Version,
            result.Checkpoint.ModelStep, result.Checkpoint.ToolCallCount, timer.ElapsedMilliseconds);
        return result.Checkpoint;
    }

    private DurableAgentRunView ToView(DurableAgentCheckpoint checkpoint)
    {
        var executions = checkpoint.ToolExecutions.Select(item => new SoftwareAgentToolExecution(
            item.ExecutionId,
            item.ToolName,
            item.Status == DurableToolExecutionStatus.Completed,
            item.DurationMilliseconds,
            item.ResultContent?.Length ?? 0,
            item.Evidence,
            item.ErrorCode)).ToArray();
        SoftwareAgentRun? final = null;
        if (checkpoint.Status is DurableAgentRunStatus.Completed or DurableAgentRunStatus.InsufficientEvidence)
        {
            final = new SoftwareAgentRun(
                checkpoint.RunId,
                checkpoint.FinalAnswer ?? "The bounded durable run ended without sufficient evidence.",
                checkpoint.FinalCitations.Select(citation =>
                {
                    var separator = citation.IndexOf(':');
                    return new SoftwareAgentSource(citation[..separator], citation[(separator + 1)..]);
                }).ToArray(),
                executions,
                checkpoint.TerminationReason ?? SoftwareAgentTerminationReason.InsufficientEvidence,
                new SoftwareAgentUsage(
                    checkpoint.Provider,
                    checkpoint.Model,
                    checkpoint.InputTokens,
                    checkpoint.OutputTokens,
                    checkpoint.TotalTokens),
                checkpoint.TotalDurationMilliseconds,
                checkpoint.ModelStep,
                checkpoint.ToolCallCount,
                checkpoint.ToolsUsed);
        }

        return new DurableAgentRunView(
            checkpoint.RunId,
            checkpoint.Status,
            checkpoint.CreatedAt,
            checkpoint.UpdatedAt,
            new DurableAgentProgress(
                checkpoint.ModelStep,
                checkpoint.ToolCallCount,
                checkpoint.ResumeCount,
                checkpoint.ToolExecutions.Count(item => item.Status == DurableToolExecutionStatus.Completed)),
            executions,
            final,
            checkpoint.LastFailureClassification);
    }

    private static FinalAnswer ParseFinal(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AgentOperationException(AgentFailureKind.InvalidResponse, "The model returned no durable agent response.");
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
                "The model returned an invalid durable agent answer.",
                exception);
        }
    }

    private static void ValidateCitations(
        string answer,
        IReadOnlyList<string> citations,
        IReadOnlyList<string> evidence)
    {
        if (citations.Any(citation => !evidence.Contains(citation, StringComparer.Ordinal)))
            throw new AgentOperationException(AgentFailureKind.InvalidResponse, "A durable answer cited unavailable evidence.");
        var inline = CitationPattern().Matches(answer).Select(match => match.Groups[1].Value);
        if (inline.Any(citation => !evidence.Contains(citation, StringComparer.Ordinal)))
            throw new AgentOperationException(AgentFailureKind.InvalidResponse, "A durable answer contained an unverified citation.");
    }

    private static int? AddNullable(int? current, int? addition) =>
        current.HasValue || addition.HasValue ? (current ?? 0) + (addition ?? 0) : null;

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
