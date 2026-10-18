using System.Collections.Concurrent;
using AgentForge.Application.Agents;
using AgentForge.Application.MultiAgent;
using AgentForge.Infrastructure.MultiAgent;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests.MultiAgent;

public sealed class MultiAgentWorkflowManagerTests
{
    [Fact]
    public async Task FullWorkflowExecutesAllSixRolesAndProducesReviewedReport()
    {
        var fixture = Fixture(WorkflowPlanAndStoreTests.FullPlan());
        var created = await fixture.Manager.CreateAsync("Full assessment");

        var completed = await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(WorkflowStatus.Completed, completed.Status);
        Assert.Equal(ReviewOutcome.Approved, completed.ReviewOutcome);
        Assert.Equal(6, completed.Tasks.Count);
        Assert.All(completed.Tasks, task => Assert.Equal(WorkflowTaskStatus.Completed, task.Status));
        Assert.Contains("Technical report", completed.FinalReport, StringComparison.Ordinal);
        Assert.Equal(6, completed.Usage.ModelCalls);
        Assert.Equal(2, completed.Usage.ToolCalls);
        Assert.Equal(50, completed.Usage.TotalTokens);
    }

    [Fact]
    public async Task NarrowPlanDoesNotInvokeUnnecessaryAgents()
    {
        var plan = new WorkflowPlan("ignored",
            [WorkflowPlanAndStoreTests.Task("requirements", AgentRole.RequirementsAnalyst)]);
        var fixture = Fixture(plan);
        var created = await fixture.Manager.CreateAsync("Only summarize documented requirements");

        var completed = await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(WorkflowStatus.Completed, completed.Status);
        Assert.Equal([AgentRole.Supervisor, AgentRole.RequirementsAnalyst], completed.Tasks.Select(task => task.Role));
        Assert.Equal(0, fixture.Agents[AgentRole.CodeAnalysis].CallCount);
    }

    [Fact]
    public async Task IndependentTasksActuallyOverlapAndArchitectWaits()
    {
        var tracker = new ConcurrencyTracker();
        var fixture = Fixture(WorkflowPlanAndStoreTests.FullPlan(), tracker: tracker);
        var created = await fixture.Manager.CreateAsync("Full assessment");

        await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(2, tracker.Maximum);
        var requirementsEnd = fixture.Agents[AgentRole.RequirementsAnalyst].LastCompleted;
        var codeEnd = fixture.Agents[AgentRole.CodeAnalysis].LastCompleted;
        var architectStart = fixture.Agents[AgentRole.SolutionArchitect].LastStarted;
        Assert.True(architectStart >= requirementsEnd);
        Assert.True(architectStart >= codeEnd);
    }

    [Fact]
    public async Task ReviewerRequestsSpecificBoundedRevision()
    {
        var fixture = Fixture(
            WorkflowPlanAndStoreTests.FullPlan(),
            reviewOutcomes: [ReviewOutcome.RevisionRequired, ReviewOutcome.Approved]);
        var created = await fixture.Manager.CreateAsync("Full assessment");

        var completed = await fixture.Manager.ResumeAsync(created.WorkflowId);
        var architect = completed.Tasks.Single(task => task.Role == AgentRole.SolutionArchitect);
        var reviewer = completed.Tasks.Single(task => task.Role == AgentRole.SecurityReviewer);

        Assert.Equal(WorkflowStatus.Completed, completed.Status);
        Assert.Equal(1, completed.RevisionCount);
        Assert.Equal(2, architect.AttemptCount);
        Assert.Equal(2, reviewer.AttemptCount);
        Assert.Equal(2, fixture.Agents[AgentRole.SolutionArchitect].CallCount);
    }

    [Fact]
    public async Task RevisionLimitProducesQualifiedResult()
    {
        var options = WorkflowPlanAndStoreTests.Options() with { MaxRevisionCycles = 1 };
        var fixture = Fixture(
            WorkflowPlanAndStoreTests.FullPlan(),
            options,
            reviewOutcomes: [ReviewOutcome.RevisionRequired, ReviewOutcome.RevisionRequired]);
        var created = await fixture.Manager.CreateAsync("Full assessment");

        var completed = await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(WorkflowStatus.CompletedWithConcerns, completed.Status);
        Assert.Equal(WorkflowTerminationReason.CompletedWithConcerns, completed.TerminationReason);
        Assert.Equal(1, completed.RevisionCount);
        Assert.Equal("RevisionLimitReached", completed.LastFailureClassification);
    }

    [Fact]
    public async Task InsufficientEvidenceProducesQualifiedOutcome()
    {
        var fixture = Fixture(WorkflowPlanAndStoreTests.FullPlan(), insufficientRole: AgentRole.RequirementsAnalyst);
        var created = await fixture.Manager.CreateAsync("Unknown project feature");

        var completed = await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(WorkflowStatus.InsufficientEvidence, completed.Status);
        Assert.Equal(WorkflowTerminationReason.InsufficientEvidence, completed.TerminationReason);
    }

    [Fact]
    public async Task CompletedWorkflowIsIdempotent()
    {
        var fixture = Fixture(WorkflowPlanAndStoreTests.FullPlan());
        var created = await fixture.Manager.CreateAsync("Full assessment");
        var first = await fixture.Manager.ResumeAsync(created.WorkflowId);
        var calls = fixture.Agents.Values.Sum(agent => agent.CallCount);

        var second = await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(first.WorkflowId, second.WorkflowId);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Usage, second.Usage);
        Assert.Equal(first.FinalReport, second.FinalReport);
        Assert.Equal(calls, fixture.Agents.Values.Sum(agent => agent.CallCount));
    }

    [Fact]
    public async Task TransientPeerFailurePreservesCompletedSpecialistAndResumes()
    {
        var plan = new WorkflowPlan("ignored",
        [
            WorkflowPlanAndStoreTests.Task("requirements", AgentRole.RequirementsAnalyst),
            WorkflowPlanAndStoreTests.Task("code", AgentRole.CodeAnalysis),
            WorkflowPlanAndStoreTests.Task("review", AgentRole.SecurityReviewer, "requirements", "code")
        ]);
        var fixture = Fixture(plan, transientRole: AgentRole.CodeAnalysis);
        var created = await fixture.Manager.CreateAsync("Assess evidence");

        var waiting = await fixture.Manager.ResumeAsync(created.WorkflowId);
        var completed = await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(WorkflowStatus.WaitingToResume, waiting.Status);
        Assert.Equal(WorkflowStatus.Completed, completed.Status);
        Assert.Equal(1, fixture.Agents[AgentRole.RequirementsAnalyst].CallCount);
        Assert.Equal(2, fixture.Agents[AgentRole.CodeAnalysis].CallCount);
    }

    [Fact]
    public async Task WorkflowWideModelBudgetTerminatesSafely()
    {
        var options = WorkflowPlanAndStoreTests.Options() with { MaxTotalModelCalls = 2 };
        var fixture = Fixture(WorkflowPlanAndStoreTests.FullPlan(), options);
        var created = await fixture.Manager.CreateAsync("Full assessment");

        var failed = await fixture.Manager.ResumeAsync(created.WorkflowId);

        Assert.Equal(WorkflowStatus.Failed, failed.Status);
        Assert.Equal(WorkflowTerminationReason.MaxBudgetReached, failed.TerminationReason);
    }

    [Fact]
    public async Task ActiveExecutorRejectsConcurrentResume()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = new WorkflowPlan("ignored",
            [WorkflowPlanAndStoreTests.Task("requirements", AgentRole.RequirementsAnalyst)]);
        var fixture = Fixture(plan, blockedRole: AgentRole.RequirementsAnalyst, blocker: blocker, entered: entered);
        var created = await fixture.Manager.CreateAsync("Question");
        var first = fixture.Manager.ResumeAsync(created.WorkflowId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.Manager.ResumeAsync(created.WorkflowId));
        blocker.SetResult();
        await first;

        Assert.Equal(AgentFailureKind.Conflict, exception.FailureKind);
    }

    [Fact]
    public async Task ExplicitCancellationFencesActiveExecutor()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = new WorkflowPlan("ignored",
            [WorkflowPlanAndStoreTests.Task("requirements", AgentRole.RequirementsAnalyst)]);
        var fixture = Fixture(plan, blockedRole: AgentRole.RequirementsAnalyst, blocker: blocker, entered: entered);
        var created = await fixture.Manager.CreateAsync("Question");
        var executing = fixture.Manager.ResumeAsync(created.WorkflowId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelled = await fixture.Manager.CancelAsync(created.WorkflowId);
        blocker.SetResult();
        await Assert.ThrowsAsync<AgentOperationException>(() => executing);

        Assert.Equal(WorkflowStatus.Cancelled, cancelled.Status);
        Assert.Equal(WorkflowTerminationReason.Cancelled, cancelled.TerminationReason);
    }

    [Fact]
    public async Task FinalReportEndpointDataPreservesReviewerConcern()
    {
        var fixture = Fixture(
            WorkflowPlanAndStoreTests.FullPlan(),
            reviewOutcomes: [ReviewOutcome.ApprovedWithConcerns]);
        var created = await fixture.Manager.CreateAsync("Full assessment");
        await fixture.Manager.ResumeAsync(created.WorkflowId);

        var report = await fixture.Manager.GetReportAsync(created.WorkflowId);

        Assert.Contains("Reviewer concerns preserved", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportBeforeCompletionIsConflict()
    {
        var fixture = Fixture(WorkflowPlanAndStoreTests.FullPlan());
        var created = await fixture.Manager.CreateAsync("Full assessment");
        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.Manager.GetReportAsync(created.WorkflowId));
        Assert.Equal(AgentFailureKind.Conflict, exception.FailureKind);
    }

    private static FixtureState Fixture(
        WorkflowPlan plan,
        MultiAgentWorkflowOptions? options = null,
        IReadOnlyList<ReviewOutcome>? reviewOutcomes = null,
        AgentRole? insufficientRole = null,
        AgentRole? transientRole = null,
        ConcurrencyTracker? tracker = null,
        AgentRole? blockedRole = null,
        TaskCompletionSource? blocker = null,
        TaskCompletionSource? entered = null)
    {
        options ??= WorkflowPlanAndStoreTests.Options();
        var store = new InMemoryWorkflowCheckpointStore();
        var agents = Enum.GetValues<AgentRole>()
            .Where(role => role != AgentRole.Supervisor)
            .ToDictionary(
                role => role,
                role => new FakeSpecialist(
                    role,
                    role == AgentRole.SecurityReviewer ? reviewOutcomes : null,
                    insufficientRole == role,
                    transientRole == role,
                    role is AgentRole.RequirementsAnalyst or AgentRole.CodeAnalysis ? tracker : null,
                    blockedRole == role ? blocker : null,
                    blockedRole == role ? entered : null));
        var planValidator = new WorkflowPlanValidator(options);
        var manager = new MultiAgentWorkflowManager(
            store,
            new FakePlanner(plan, planValidator),
            agents.Values,
            new WorkflowCheckpointValidator(options, planValidator),
            options,
            TimeProvider.System,
            NullLogger<MultiAgentWorkflowManager>.Instance);
        return new FixtureState(manager, store, agents);
    }

    private sealed record FixtureState(
        MultiAgentWorkflowManager Manager,
        InMemoryWorkflowCheckpointStore Store,
        IReadOnlyDictionary<AgentRole, FakeSpecialist> Agents);

    private sealed class FakePlanner(WorkflowPlan plan, WorkflowPlanValidator validator) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(string objective, CancellationToken cancellationToken = default) =>
            Task.FromResult(validator.Validate(plan, objective));
    }

    private sealed class FakeSpecialist(
        AgentRole role,
        IReadOnlyList<ReviewOutcome>? reviewOutcomes,
        bool insufficient,
        bool transientOnce,
        ConcurrencyTracker? tracker,
        TaskCompletionSource? blocker,
        TaskCompletionSource? entered) : ISpecialistAgent
    {
        private int _calls;
        public AgentRole Role => role;
        public int CallCount => _calls;
        public DateTimeOffset LastStarted { get; private set; }
        public DateTimeOffset LastCompleted { get; private set; }

        public async Task<AgentTaskExecutionResult> ExecuteAsync(
            WorkflowTaskState task,
            AgentExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            LastStarted = DateTimeOffset.UtcNow;
            if (entered is not null) entered.TrySetResult();
            if (blocker is not null) await blocker.Task.WaitAsync(cancellationToken);
            if (tracker is not null)
            {
                tracker.Enter();
                try { await Task.Delay(75, cancellationToken); }
                finally { tracker.Exit(); }
            }
            if (transientOnce && call == 1)
                throw new AgentOperationException(AgentFailureKind.Provider, "Controlled transient failure.");

            var produced = role switch
            {
                AgentRole.RequirementsAnalyst => new[] { "knowledge:authentication.md#chunk-1" },
                AgentRole.CodeAnalysis => new[] { "repo:src/AuthService.cs" },
                _ => []
            };
            var evidence = context.AvailableEvidence.Concat(produced)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var review = role == AgentRole.SecurityReviewer
                ? reviewOutcomes is not null && call <= reviewOutcomes.Count
                    ? reviewOutcomes[call - 1]
                    : ReviewOutcome.Approved
                : ReviewOutcome.NotApplicable;
            var output = new AgentTaskOutput(
                $"{role} summary",
                [$"{role} finding"],
                role == AgentRole.SolutionArchitect ? ["Use bounded architecture."] : [],
                role == AgentRole.SecurityReviewer ? ["Reviewer concerns preserved."] : [],
                [],
                evidence,
                insufficient,
                review,
                review == ReviewOutcome.RevisionRequired ? "architect" : null,
                review == ReviewOutcome.RevisionRequired ? "Add a missing security boundary." : null,
                role == AgentRole.ReportSynthesis
                    ? "# Technical report\n\nReviewer concerns preserved."
                    : null);
            LastCompleted = DateTimeOffset.UtcNow;
            return new AgentTaskExecutionResult(
                output,
                new AgentTaskMetrics("Fake", "fake", 1,
                    role is AgentRole.RequirementsAnalyst or AgentRole.CodeAnalysis ? 1 : 0,
                    6, 4, 10, 1, role switch
                    {
                        AgentRole.RequirementsAnalyst => ["search_project_knowledge"],
                        AgentRole.CodeAnalysis => ["search_code"],
                        _ => []
                    }),
                produced);
        }
    }

    private sealed class ConcurrencyTracker
    {
        private int _current;
        private int _maximum;
        public int Maximum => _maximum;

        public void Enter()
        {
            var current = Interlocked.Increment(ref _current);
            int observed;
            do
            {
                observed = _maximum;
                if (current <= observed) return;
            } while (Interlocked.CompareExchange(ref _maximum, current, observed) != observed);
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }
}
