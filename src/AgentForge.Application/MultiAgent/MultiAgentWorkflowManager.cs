using System.Diagnostics;
using AgentForge.Application.Agents;
using AgentForge.Application.Llm;
using Microsoft.Extensions.Logging;

namespace AgentForge.Application.MultiAgent;

public sealed class MultiAgentWorkflowManager(
    IWorkflowCheckpointStore store,
    IWorkflowPlanner planner,
    IEnumerable<ISpecialistAgent> specialists,
    WorkflowCheckpointValidator validator,
    MultiAgentWorkflowOptions options,
    TimeProvider timeProvider,
    ILogger<MultiAgentWorkflowManager> logger) : IMultiAgentWorkflowManager
{
    private readonly IReadOnlyDictionary<AgentRole, ISpecialistAgent> _specialists = specialists
        .ToDictionary(agent => agent.Role);

    public async Task<MultiAgentWorkflowView> CreateAsync(
        string objective,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > 10_000)
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "A bounded workflow objective is required.");
        var now = timeProvider.GetUtcNow();
        var checkpoint = new MultiAgentWorkflowCheckpoint(
            MultiAgentWorkflowCheckpoint.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            objective.Trim(),
            now,
            now,
            WorkflowStatus.Created,
            null,
            [],
            [],
            [],
            0,
            0,
            null,
            null,
            null,
            0,
            0,
            0,
            null,
            null,
            0,
            ReviewOutcome.NotApplicable,
            null,
            null,
            null,
            string.Empty);
        validator.Validate(checkpoint);
        var created = await store.CreateAsync(checkpoint, cancellationToken);
        logger.LogInformation("Multi-agent workflow created. WorkflowId: {WorkflowId}; Version: {Version}",
            created.WorkflowId, created.Version);
        return ToView(created);
    }

    public async Task<MultiAgentWorkflowView> GetAsync(
        string workflowId,
        CancellationToken cancellationToken = default) => ToView(await LoadAsync(workflowId, cancellationToken));

    public async Task<string> GetReportAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await LoadAsync(workflowId, cancellationToken);
        if (checkpoint.FinalReport is null)
            throw new AgentOperationException(AgentFailureKind.Conflict, "The workflow does not yet have a final report.");
        return checkpoint.FinalReport;
    }

    public async Task<MultiAgentWorkflowView> ResumeAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await LoadAsync(workflowId, cancellationToken);
        if (checkpoint.Status is WorkflowStatus.Completed or WorkflowStatus.CompletedWithConcerns or
            WorkflowStatus.InsufficientEvidence)
            return ToView(checkpoint);
        if (checkpoint.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
            throw new AgentOperationException(AgentFailureKind.Conflict, "A terminal workflow cannot be resumed.");

        checkpoint = await RecoverExpiredOwnershipAsync(checkpoint, cancellationToken);
        if (checkpoint.Status is WorkflowStatus.Planning or WorkflowStatus.Running)
            throw new AgentOperationException(AgentFailureKind.Conflict, "The workflow already has an active executor.");

        var owner = Guid.NewGuid().ToString("N");
        var fence = checkpoint.FencingToken + 1;
        var now = timeProvider.GetUtcNow();
        var executingStatus = checkpoint.Plan is null ? WorkflowStatus.Planning : WorkflowStatus.Running;
        WorkflowStateMachine.EnsureTransition(checkpoint.Status, executingStatus);
        checkpoint = await UpdateAsync(checkpoint with
        {
            Status = executingStatus,
            ExecutionOwner = owner,
            LeaseExpiresAt = now + options.GetLeaseDuration(),
            FencingToken = fence,
            ResumeCount = checkpoint.ResumeCount + 1,
            UpdatedAt = now,
            LastFailureClassification = null
        }, checkpoint.Version, cancellationToken);

        var attemptId = owner;
        var timer = Stopwatch.StartNew();
        logger.LogInformation(
            "Workflow execution acquired. WorkflowId: {WorkflowId}; AttemptId: {AttemptId}; Fence: {Fence}; ResumeCount: {ResumeCount}",
            workflowId, attemptId, fence, checkpoint.ResumeCount);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.GetAttemptTimeout());

        try
        {
            if (checkpoint.Plan is null)
                checkpoint = await PlanAsync(checkpoint, owner, fence, timeout.Token);
            if (!checkpoint.IsTerminal)
                checkpoint = await ExecutePlanAsync(checkpoint, owner, fence, attemptId, timeout.Token);
            timer.Stop();
            checkpoint = await UpdateAsync(checkpoint with
            {
                TotalDurationMilliseconds = checkpoint.TotalDurationMilliseconds + timer.ElapsedMilliseconds,
                UpdatedAt = timeProvider.GetUtcNow()
            }, checkpoint.Version, CancellationToken.None);
            return ToView(checkpoint);
        }
        catch (OperationCanceledException)
        {
            timer.Stop();
            await TryReleaseAsync(workflowId, owner, fence, "ExecutionAttemptCancelled", timer.ElapsedMilliseconds);
            throw;
        }
        catch (LlmOperationException exception)
        {
            timer.Stop();
            var status = exception.FailureKind == LlmFailureKind.InvalidResponse
                ? WorkflowStatus.Failed
                : WorkflowStatus.WaitingToResume;
            var released = await TryReleaseAsync(workflowId, owner, fence, exception.FailureKind.ToString(),
                timer.ElapsedMilliseconds, status);
            return ToView(released ?? await LoadAsync(workflowId, CancellationToken.None));
        }
        catch (AgentOperationException exception) when (
            exception.FailureKind is AgentFailureKind.Provider or AgentFailureKind.Timeout or AgentFailureKind.Storage)
        {
            timer.Stop();
            var released = await TryReleaseAsync(workflowId, owner, fence, exception.FailureKind.ToString(),
                timer.ElapsedMilliseconds);
            return ToView(released ?? await LoadAsync(workflowId, CancellationToken.None));
        }
        catch (AgentOperationException exception) when (
            exception.FailureKind is not AgentFailureKind.Conflict and not AgentFailureKind.CorruptState and
            not AgentFailureKind.NotFound)
        {
            timer.Stop();
            var released = await TryReleaseAsync(workflowId, owner, fence, exception.FailureKind.ToString(),
                timer.ElapsedMilliseconds, WorkflowStatus.Failed);
            return ToView(released ?? await LoadAsync(workflowId, CancellationToken.None));
        }
    }

    public async Task<MultiAgentWorkflowView> CancelAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await LoadAsync(workflowId, cancellationToken);
        if (checkpoint.IsTerminal)
        {
            if (checkpoint.Status == WorkflowStatus.Cancelled) return ToView(checkpoint);
            throw new AgentOperationException(AgentFailureKind.Conflict, "A terminal workflow cannot be cancelled.");
        }
        WorkflowStateMachine.EnsureTransition(checkpoint.Status, WorkflowStatus.Cancelled);
        var tasks = checkpoint.Tasks.Select(task => task.Status == WorkflowTaskStatus.Running
            ? task with { Status = WorkflowTaskStatus.Cancelled, CompletedAt = timeProvider.GetUtcNow() }
            : task).ToArray();
        var cancelled = await UpdateAsync(checkpoint with
        {
            Status = WorkflowStatus.Cancelled,
            Tasks = tasks,
            ExecutionOwner = null,
            LeaseExpiresAt = null,
            FencingToken = checkpoint.FencingToken + 1,
            TerminationReason = WorkflowTerminationReason.Cancelled,
            UpdatedAt = timeProvider.GetUtcNow()
        }, checkpoint.Version, cancellationToken);
        return ToView(cancelled);
    }

    private async Task<MultiAgentWorkflowCheckpoint> PlanAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string owner,
        long fence,
        CancellationToken cancellationToken)
    {
        var planTimer = Stopwatch.StartNew();
        var plan = await planner.CreatePlanAsync(checkpoint.Objective, cancellationToken);
        planTimer.Stop();
        var now = timeProvider.GetUtcNow();
        var supervisorOutput = new AgentTaskOutput(
            $"Supervisor produced and application validation accepted a {plan.Tasks.Count}-task plan.",
            ["The application validated role registration, dependencies, DAG depth, and mandatory review policy."],
            [], [], [], [], false, ReviewOutcome.NotApplicable, null, null, null);
        var supervisor = new WorkflowTaskState(
            "supervisor", AgentRole.Supervisor, "Plan and coordinate the workflow.", [],
            WorkflowTaskStatus.Completed, 1, checkpoint.UpdatedAt, now, supervisorOutput, [],
            new AgentTaskMetrics("Configured", "SupervisorPlanner", 1, 0, null, null, null,
                planTimer.ElapsedMilliseconds, []), null);
        var tasks = plan.Tasks.Select(task => new WorkflowTaskState(
            task.TaskId, task.Role, task.Objective, task.DependsOn, WorkflowTaskStatus.Pending,
            0, null, null, null, [], null, null)).Prepend(supervisor).ToArray();
        var events = AppendEvent(checkpoint, new WorkflowMessage(
            1, checkpoint.WorkflowId, "supervisor", AgentRole.Supervisor, AgentRole.Supervisor,
            WorkflowMessageType.TaskCompleted, Guid.NewGuid().ToString("N"),
            $"Validated {plan.Tasks.Count}-task workflow plan.", [], now));
        WorkflowStateMachine.EnsureTransition(checkpoint.Status, WorkflowStatus.Running);
        return await SaveOwnedAsync(checkpoint with
        {
            Plan = plan,
            Tasks = tasks,
            Events = events,
            Status = WorkflowStatus.Running,
            TotalModelCalls = checkpoint.TotalModelCalls + 1
        }, owner, fence, cancellationToken);
    }

    private async Task<MultiAgentWorkflowCheckpoint> ExecutePlanAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string owner,
        long fence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        while (!checkpoint.IsTerminal)
        {
            checkpoint = await EnsureOwnershipAsync(checkpoint, owner, fence, cancellationToken);
            if (AllPlannedTasksCompleted(checkpoint))
                return await FinishAsync(checkpoint, owner, fence, cancellationToken);

            if (BudgetExhausted(checkpoint))
                return await FinishFailedAsync(checkpoint, owner, fence, WorkflowTerminationReason.MaxBudgetReached,
                    "MaxBudgetReached", cancellationToken);

            var completedIds = checkpoint.Tasks
                .Where(task => task.Status == WorkflowTaskStatus.Completed)
                .Select(task => task.TaskId)
                .ToHashSet(StringComparer.Ordinal);
            var ready = checkpoint.Tasks
                .Where(task => task.Role != AgentRole.Supervisor && task.Status == WorkflowTaskStatus.Pending &&
                    task.DependsOn.All(completedIds.Contains))
                .Take(options.MaxConcurrency)
                .ToArray();
            if (ready.Length == 0)
                return await FinishFailedAsync(checkpoint, owner, fence, WorkflowTerminationReason.TaskFailure,
                    "NoSchedulableTasks", cancellationToken);
            if (ready.Any(task => task.AttemptCount >= options.MaxTaskAttempts))
                return await FinishFailedAsync(checkpoint, owner, fence, WorkflowTerminationReason.TaskFailure,
                    "TaskAttemptLimitReached", cancellationToken);

            var now = timeProvider.GetUtcNow();
            var runningIds = ready.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
            var runningTasks = checkpoint.Tasks.Select(task => runningIds.Contains(task.TaskId)
                ? task with
                {
                    Status = WorkflowTaskStatus.Running,
                    AttemptCount = task.AttemptCount + 1,
                    StartedAt = now,
                    CompletedAt = null,
                    FailureClassification = null
                }
                : task).ToArray();
            var events = checkpoint.Events.ToList();
            foreach (var task in ready)
                events.Add(Message(checkpoint, task.TaskId, AgentRole.Supervisor, task.Role,
                    WorkflowMessageType.TaskStarted, correlationId, $"Started {task.Role} task.", []));
            checkpoint = await SaveOwnedAsync(checkpoint with { Tasks = runningTasks, Events = events },
                owner, fence, cancellationToken);

            var remainingModelCalls = options.MaxTotalModelCalls - checkpoint.TotalModelCalls;
            var remainingToolCalls = options.MaxTotalToolCalls - checkpoint.TotalToolCalls;
            var modelAllocation = Math.Max(1, Math.Min(options.MaxModelCallsPerSpecialist,
                remainingModelCalls / ready.Length));
            var toolAllocation = Math.Max(0, remainingToolCalls / ready.Length);
            var executions = ready.Select(task => ExecuteTaskCapturedAsync(
                checkpoint,
                checkpoint.Tasks.Single(item => item.TaskId == task.TaskId),
                correlationId,
                modelAllocation,
                toolAllocation,
                cancellationToken)).ToList();
            var recoverableFailure = false;

            while (executions.Count > 0)
            {
                var completedExecution = await Task.WhenAny(executions);
                executions.Remove(completedExecution);
                var outcome = await completedExecution;
                checkpoint = await ApplyTaskOutcomeAsync(checkpoint, outcome, owner, fence, correlationId, cancellationToken);
                recoverableFailure |= IsRecoverable(outcome.Exception);
            }

            if (recoverableFailure)
                throw new AgentOperationException(AgentFailureKind.Provider,
                    "A specialist dependency failed transiently after completed peer results were checkpointed.");

            if (checkpoint.Tasks.Any(task => task.Status == WorkflowTaskStatus.Failed))
                return await FinishFailedAsync(checkpoint, owner, fence, WorkflowTerminationReason.TaskFailure,
                    "SpecialistTaskFailed", cancellationToken);

            checkpoint = await ApplyReviewDecisionAsync(checkpoint, owner, fence, correlationId, cancellationToken);
        }
        return checkpoint;
    }

    private async Task<TaskOutcome> ExecuteTaskCapturedAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        WorkflowTaskState task,
        string correlationId,
        int modelAllocation,
        int toolAllocation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_specialists.TryGetValue(task.Role, out var specialist))
                throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The planned specialist is not registered.");
            var dependencies = task.DependsOn
                .Select(id => checkpoint.Tasks.Single(item => item.TaskId == id))
                .ToArray();
            var context = new AgentExecutionContext(
                checkpoint.WorkflowId,
                correlationId,
                checkpoint.Objective,
                dependencies,
                checkpoint.Evidence,
                modelAllocation,
                toolAllocation,
                options.MaxTaskOutputCharacters,
                checkpoint.RevisionCount);
            var result = await specialist.ExecuteAsync(task, context, cancellationToken);
            return new TaskOutcome(task.TaskId, result, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new TaskOutcome(task.TaskId, null, exception);
        }
    }

    private async Task<MultiAgentWorkflowCheckpoint> ApplyTaskOutcomeAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        TaskOutcome outcome,
        string owner,
        long fence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var task = checkpoint.Tasks.Single(item => item.TaskId == outcome.TaskId);
        WorkflowTaskState updated;
        var events = checkpoint.Events.ToList();
        var evidence = checkpoint.Evidence;
        var modelCalls = checkpoint.TotalModelCalls;
        var toolCalls = checkpoint.TotalToolCalls;
        var input = checkpoint.InputTokens;
        var output = checkpoint.OutputTokens;
        var total = checkpoint.TotalTokens;
        if (outcome.Result is not null)
        {
            evidence = checkpoint.Evidence.Concat(outcome.Result.ProducedEvidence)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (outcome.Result.Output.Evidence.Any(item => !evidence.Contains(item, StringComparer.Ordinal)))
                throw new AgentOperationException(AgentFailureKind.InvalidResponse,
                    "A specialist output cited evidence outside the workflow registry.");
            updated = task with
            {
                Status = WorkflowTaskStatus.Completed,
                CompletedAt = now,
                Output = outcome.Result.Output,
                Metrics = outcome.Result.Metrics
            };
            modelCalls += outcome.Result.Metrics.ModelCalls;
            toolCalls += outcome.Result.Metrics.ToolCalls;
            input = AddNullable(input, outcome.Result.Metrics.InputTokens);
            output = AddNullable(output, outcome.Result.Metrics.OutputTokens);
            total = AddNullable(total, outcome.Result.Metrics.TotalTokens);
            var type = task.Role == AgentRole.SecurityReviewer
                ? WorkflowMessageType.ReviewCompleted
                : WorkflowMessageType.TaskCompleted;
            events.Add(Message(checkpoint, task.TaskId, task.Role, AgentRole.Supervisor, type,
                correlationId, $"{task.Role} completed.", outcome.Result.Output.Evidence));
        }
        else
        {
            var classification = Classify(outcome.Exception!);
            var failedMetrics = AgentTaskAttemptTelemetry.GetMetrics(outcome.Exception!);
            var failedEvidence = AgentTaskAttemptTelemetry.GetEvidence(outcome.Exception!);
            if (failedMetrics is not null)
            {
                evidence = checkpoint.Evidence.Concat(failedEvidence)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                modelCalls += failedMetrics.ModelCalls;
                toolCalls += failedMetrics.ToolCalls;
                input = AddNullable(input, failedMetrics.InputTokens);
                output = AddNullable(output, failedMetrics.OutputTokens);
                total = AddNullable(total, failedMetrics.TotalTokens);
            }
            updated = task with
            {
                Status = IsRecoverable(outcome.Exception) ? WorkflowTaskStatus.Pending : WorkflowTaskStatus.Failed,
                CompletedAt = now,
                Metrics = failedMetrics,
                FailureClassification = classification
            };
            logger.LogWarning(
                "Specialist task failed. WorkflowId: {WorkflowId}; TaskId: {TaskId}; Role: {Role}; Classification: {Classification}; ExceptionType: {ExceptionType}; Failure: {Failure}",
                checkpoint.WorkflowId, task.TaskId, task.Role, classification,
                outcome.Exception!.GetType().Name, outcome.Exception.Message);
            events.Add(Message(checkpoint, task.TaskId, task.Role, AgentRole.Supervisor,
                WorkflowMessageType.TaskFailed, correlationId, $"{task.Role} failed: {classification}.", []));
        }

        var tasks = checkpoint.Tasks.Select(item => item.TaskId == task.TaskId ? updated : item).ToArray();
        return await SaveOwnedAsync(checkpoint with
        {
            Tasks = tasks,
            Events = events,
            Evidence = evidence,
            TotalModelCalls = modelCalls,
            TotalToolCalls = toolCalls,
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total
        }, owner, fence, cancellationToken);
    }

    private async Task<MultiAgentWorkflowCheckpoint> ApplyReviewDecisionAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string owner,
        long fence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var reviewer = checkpoint.Tasks.SingleOrDefault(task => task.Role == AgentRole.SecurityReviewer);
        if (reviewer?.Status != WorkflowTaskStatus.Completed || reviewer.Output is null ||
            checkpoint.ReviewOutcome != ReviewOutcome.NotApplicable)
            return checkpoint;

        var outcome = reviewer.Output.ReviewOutcome;
        if (outcome != ReviewOutcome.RevisionRequired)
            return await SaveOwnedAsync(checkpoint with { ReviewOutcome = outcome }, owner, fence, cancellationToken);

        var targetId = reviewer.Output.RevisionTargetTaskId!;
        var target = checkpoint.Tasks.SingleOrDefault(task => task.TaskId == targetId);
        if (target is null || !reviewer.DependsOn.Contains(targetId, StringComparer.Ordinal) || target.Role == AgentRole.Supervisor)
            throw new AgentOperationException(AgentFailureKind.InvalidResponse,
                "Security Reviewer requested revision of an unauthorized task.");

        if (checkpoint.RevisionCount >= options.MaxRevisionCycles)
        {
            var qualifiedReviewer = reviewer with
            {
                Output = reviewer.Output with { ReviewOutcome = ReviewOutcome.ApprovedWithConcerns }
            };
            var tasksAtLimit = checkpoint.Tasks.Select(task => task.TaskId == reviewer.TaskId ? qualifiedReviewer : task).ToArray();
            return await SaveOwnedAsync(checkpoint with
            {
                Tasks = tasksAtLimit,
                ReviewOutcome = ReviewOutcome.ApprovedWithConcerns,
                LastFailureClassification = "RevisionLimitReached"
            }, owner, fence, cancellationToken);
        }

        var revisedTarget = target with
        {
            Status = WorkflowTaskStatus.Pending,
            Output = null,
            Metrics = null,
            CompletedAt = null,
            PreviousOutputs = target.Output is null
                ? target.PreviousOutputs
                : target.PreviousOutputs.Append(target.Output).ToArray()
        };
        var pendingReviewer = reviewer with
        {
            Status = WorkflowTaskStatus.Pending,
            Output = null,
            Metrics = null,
            CompletedAt = null,
            PreviousOutputs = reviewer.PreviousOutputs.Append(reviewer.Output).ToArray()
        };
        var tasks = checkpoint.Tasks.Select(task => task.TaskId == targetId
            ? revisedTarget
            : task.TaskId == reviewer.TaskId
                ? pendingReviewer
                : task.Role == AgentRole.ReportSynthesis
                    ? task with { Status = WorkflowTaskStatus.Pending, Output = null, Metrics = null, CompletedAt = null }
                    : task).ToArray();
        var events = checkpoint.Events.Append(Message(
            checkpoint, reviewer.TaskId, AgentRole.SecurityReviewer, target.Role,
            WorkflowMessageType.RevisionRequested, correlationId,
            reviewer.Output.RevisionReason!, reviewer.Output.Evidence)).ToArray();
        return await SaveOwnedAsync(checkpoint with
        {
            Tasks = tasks,
            Events = events,
            RevisionCount = checkpoint.RevisionCount + 1,
            ReviewOutcome = ReviewOutcome.NotApplicable
        }, owner, fence, cancellationToken);
    }

    private async Task<MultiAgentWorkflowCheckpoint> FinishAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string owner,
        long fence,
        CancellationToken cancellationToken)
    {
        var reportTask = checkpoint.Tasks.SingleOrDefault(task => task.Role == AgentRole.ReportSynthesis);
        var review = checkpoint.ReviewOutcome;
        var insufficient = review == ReviewOutcome.InsufficientEvidence ||
            checkpoint.Tasks.Any(task => task.Output?.InsufficientEvidence == true);
        var status = insufficient
            ? WorkflowStatus.InsufficientEvidence
            : review == ReviewOutcome.ApprovedWithConcerns
                ? WorkflowStatus.CompletedWithConcerns
                : WorkflowStatus.Completed;
        var reason = status switch
        {
            WorkflowStatus.InsufficientEvidence => WorkflowTerminationReason.InsufficientEvidence,
            WorkflowStatus.CompletedWithConcerns => WorkflowTerminationReason.CompletedWithConcerns,
            _ => WorkflowTerminationReason.Completed
        };
        WorkflowStateMachine.EnsureTransition(checkpoint.Status, status);
        var events = checkpoint.Events.Append(Message(checkpoint, "supervisor", AgentRole.Supervisor,
            AgentRole.Supervisor, WorkflowMessageType.WorkflowCompleted, Guid.NewGuid().ToString("N"),
            $"Workflow ended with {reason}.", checkpoint.Evidence)).ToArray();
        return await UpdateAsync(checkpoint with
        {
            Status = status,
            TerminationReason = reason,
            FinalReport = reportTask?.Output?.ReportMarkdown,
            Events = events,
            ExecutionOwner = null,
            LeaseExpiresAt = null,
            UpdatedAt = timeProvider.GetUtcNow()
        }, checkpoint.Version, cancellationToken);
    }

    private Task<MultiAgentWorkflowCheckpoint> FinishFailedAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string owner,
        long fence,
        WorkflowTerminationReason reason,
        string failure,
        CancellationToken cancellationToken)
    {
        WorkflowStateMachine.EnsureTransition(checkpoint.Status, WorkflowStatus.Failed);
        return UpdateAsync(checkpoint with
        {
            Status = WorkflowStatus.Failed,
            TerminationReason = reason,
            LastFailureClassification = failure,
            Tasks = checkpoint.Tasks.Select(task => task.Status == WorkflowTaskStatus.Running
                ? task with { Status = WorkflowTaskStatus.Failed, FailureClassification = failure }
                : task).ToArray(),
            ExecutionOwner = null,
            LeaseExpiresAt = null,
            UpdatedAt = timeProvider.GetUtcNow()
        }, checkpoint.Version, cancellationToken);
    }

    private async Task<MultiAgentWorkflowCheckpoint> RecoverExpiredOwnershipAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Status is not (WorkflowStatus.Planning or WorkflowStatus.Running) ||
            checkpoint.LeaseExpiresAt > timeProvider.GetUtcNow())
            return checkpoint;
        WorkflowStateMachine.EnsureTransition(checkpoint.Status, WorkflowStatus.WaitingToResume);
        var recovered = checkpoint with
        {
            Status = WorkflowStatus.WaitingToResume,
            Tasks = checkpoint.Tasks.Select(task => task.Status == WorkflowTaskStatus.Running
                ? task with { Status = WorkflowTaskStatus.Pending, StartedAt = null }
                : task).ToArray(),
            ExecutionOwner = null,
            LeaseExpiresAt = null,
            FencingToken = checkpoint.FencingToken + 1,
            LastFailureClassification = "ExpiredExecutionOwnership",
            UpdatedAt = timeProvider.GetUtcNow()
        };
        var saved = await UpdateAsync(recovered, checkpoint.Version, cancellationToken);
        logger.LogWarning("Expired workflow owner recovered. WorkflowId: {WorkflowId}; Version: {Version}",
            checkpoint.WorkflowId, saved.Version);
        return saved;
    }

    private async Task<MultiAgentWorkflowCheckpoint> EnsureOwnershipAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string owner,
        long fence,
        CancellationToken cancellationToken)
    {
        var current = await LoadAsync(checkpoint.WorkflowId, cancellationToken);
        if (current.Version != checkpoint.Version || current.ExecutionOwner != owner ||
            current.FencingToken != fence || current.Status is not (WorkflowStatus.Planning or WorkflowStatus.Running) ||
            current.LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw new AgentOperationException(AgentFailureKind.Conflict, "Workflow execution ownership was lost.");
        return checkpoint;
    }

    private async Task<MultiAgentWorkflowCheckpoint> SaveOwnedAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string owner,
        long fence,
        CancellationToken cancellationToken)
    {
        checkpoint = await EnsureOwnershipAsync(checkpoint, owner, fence, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return await UpdateAsync(checkpoint with
        {
            UpdatedAt = now,
            LeaseExpiresAt = now + options.GetLeaseDuration()
        }, checkpoint.Version, cancellationToken);
    }

    private async Task<MultiAgentWorkflowCheckpoint?> TryReleaseAsync(
        string workflowId,
        string owner,
        long fence,
        string failure,
        long elapsed,
        WorkflowStatus status = WorkflowStatus.WaitingToResume)
    {
        try
        {
            var current = await LoadAsync(workflowId, CancellationToken.None);
            if (current.ExecutionOwner != owner || current.FencingToken != fence ||
                current.Status is not (WorkflowStatus.Planning or WorkflowStatus.Running)) return null;
            WorkflowStateMachine.EnsureTransition(current.Status, status);
            var tasks = current.Tasks.Select(task => task.Status == WorkflowTaskStatus.Running
                ? task with
                {
                    Status = status == WorkflowStatus.Failed ? WorkflowTaskStatus.Failed : WorkflowTaskStatus.Pending,
                    StartedAt = status == WorkflowStatus.Failed ? task.StartedAt : null,
                    FailureClassification = status == WorkflowStatus.Failed ? failure : task.FailureClassification
                }
                : task).ToArray();
            var result = await store.TryUpdateAsync(current with
            {
                Status = status,
                Tasks = tasks,
                ExecutionOwner = null,
                LeaseExpiresAt = null,
                LastFailureClassification = failure,
                TerminationReason = status == WorkflowStatus.Failed ? WorkflowTerminationReason.TaskFailure : null,
                TotalDurationMilliseconds = current.TotalDurationMilliseconds + elapsed,
                UpdatedAt = timeProvider.GetUtcNow()
            }, current.Version, CancellationToken.None);
            return result.Succeeded ? result.Checkpoint : null;
        }
        catch { return null; }
    }

    private async Task<MultiAgentWorkflowCheckpoint> LoadAsync(string workflowId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(workflowId, "N", out _))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The workflow identifier is invalid.");
        var checkpoint = await store.GetAsync(workflowId, cancellationToken) ??
            throw new AgentOperationException(AgentFailureKind.NotFound, "The workflow was not found.");
        validator.Validate(checkpoint);
        return checkpoint;
    }

    private async Task<MultiAgentWorkflowCheckpoint> UpdateAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        validator.Validate(checkpoint);
        var result = await store.TryUpdateAsync(checkpoint, expectedVersion, cancellationToken);
        if (!result.Succeeded || result.Checkpoint is null)
            throw new AgentOperationException(AgentFailureKind.Conflict, "The workflow checkpoint was updated by another executor.");
        logger.LogInformation(
            "Workflow checkpoint saved. WorkflowId: {WorkflowId}; Status: {Status}; Version: {Version}; CompletedTasks: {CompletedTasks}; ModelCalls: {ModelCalls}; ToolCalls: {ToolCalls}",
            checkpoint.WorkflowId, result.Checkpoint.Status, result.Checkpoint.Version,
            result.Checkpoint.Tasks.Count(task => task.Status == WorkflowTaskStatus.Completed),
            result.Checkpoint.TotalModelCalls, result.Checkpoint.TotalToolCalls);
        return result.Checkpoint;
    }

    private static bool AllPlannedTasksCompleted(MultiAgentWorkflowCheckpoint checkpoint) =>
        checkpoint.Tasks.Where(task => task.Role != AgentRole.Supervisor)
            .All(task => task.Status == WorkflowTaskStatus.Completed);

    private bool BudgetExhausted(MultiAgentWorkflowCheckpoint checkpoint) =>
        checkpoint.TotalModelCalls >= options.MaxTotalModelCalls ||
        checkpoint.TotalToolCalls > options.MaxTotalToolCalls ||
        checkpoint.TotalTokens > options.MaxTotalTokens;

    private IReadOnlyList<WorkflowMessage> AppendEvent(
        MultiAgentWorkflowCheckpoint checkpoint,
        WorkflowMessage message) => checkpoint.Events.Append(message).ToArray();

    private WorkflowMessage Message(
        MultiAgentWorkflowCheckpoint checkpoint,
        string taskId,
        AgentRole sender,
        AgentRole recipient,
        WorkflowMessageType type,
        string correlationId,
        string summary,
        IReadOnlyList<string> evidence) => new(
        1, checkpoint.WorkflowId, taskId, sender, recipient, type, correlationId,
        summary.Length <= 2_000 ? summary : summary[..2_000], evidence, timeProvider.GetUtcNow());

    private static int? AddNullable(int? current, int? addition) =>
        current.HasValue || addition.HasValue ? (current ?? 0) + (addition ?? 0) : null;

    private static string Classify(Exception exception) => exception switch
    {
        AgentOperationException agent => agent.FailureKind.ToString(),
        LlmOperationException llm => llm.FailureKind.ToString(),
        _ => "TaskFailure"
    };

    private static bool IsRecoverable(Exception? exception) => exception switch
    {
        AgentOperationException agent => agent.FailureKind is AgentFailureKind.Provider or
            AgentFailureKind.Timeout or AgentFailureKind.Storage,
        LlmOperationException llm => llm.FailureKind is LlmFailureKind.Provider or
            LlmFailureKind.Timeout or LlmFailureKind.RateLimit or LlmFailureKind.Unavailable,
        _ => false
    };

    private static MultiAgentWorkflowView ToView(MultiAgentWorkflowCheckpoint checkpoint)
    {
        var tasks = checkpoint.Tasks.Select(task => new WorkflowTaskView(
            task.TaskId,
            task.Role,
            task.Status,
            task.AttemptCount,
            task.StartedAt,
            task.CompletedAt,
            task.Metrics?.ModelCalls ?? 0,
            task.Metrics?.ToolCalls ?? 0,
            task.Metrics?.TotalTokens,
            task.Metrics?.DurationMilliseconds ?? 0,
            task.Output?.Evidence ?? [],
            task.FailureClassification)).ToArray();
        var stage = checkpoint.IsTerminal
            ? checkpoint.Status.ToString()
            : tasks.FirstOrDefault(task => task.Status == WorkflowTaskStatus.Running)?.Role.ToString()
                ?? tasks.FirstOrDefault(task => task.Status == WorkflowTaskStatus.Pending)?.Role.ToString()
                ?? checkpoint.Status.ToString();
        return new MultiAgentWorkflowView(
            checkpoint.WorkflowId,
            checkpoint.Status,
            stage,
            checkpoint.CreatedAt,
            checkpoint.UpdatedAt,
            checkpoint.ResumeCount,
            checkpoint.RevisionCount,
            tasks,
            new WorkflowUsageView(
                checkpoint.TotalModelCalls,
                checkpoint.TotalToolCalls,
                checkpoint.InputTokens,
                checkpoint.OutputTokens,
                checkpoint.TotalTokens,
                checkpoint.TotalDurationMilliseconds),
            checkpoint.ReviewOutcome,
            checkpoint.TerminationReason,
            checkpoint.FinalReport,
            checkpoint.LastFailureClassification);
    }

    private sealed record TaskOutcome(
        string TaskId,
        AgentTaskExecutionResult? Result,
        Exception? Exception);
}
