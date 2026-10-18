using AgentForge.Application.Agents;
using AgentForge.Application.MultiAgent;
using AgentForge.Infrastructure.MultiAgent;

namespace AgentForge.Tests.MultiAgent;

public sealed class WorkflowPlanAndStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agentforge-workflows", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FullPlanIsAccepted()
    {
        var validated = Validator().Validate(FullPlan(), "objective");
        Assert.Equal(5, validated.Tasks.Count);
    }

    [Fact]
    public void NarrowRequirementsPlanIsAccepted()
    {
        var plan = new WorkflowPlan("ignored", [Task("requirements", AgentRole.RequirementsAnalyst)]);
        var validated = Validator().Validate(plan, "Find documented requirements.");
        Assert.Single(validated.Tasks);
        Assert.Equal("Find documented requirements.", validated.Objective);
    }

    [Fact]
    public void NarrowReportPlanWithReviewIsAccepted()
    {
        var plan = new WorkflowPlan("ignored",
        [
            Task("code", AgentRole.CodeAnalysis),
            Task("review", AgentRole.SecurityReviewer, "code"),
            Task("report", AgentRole.ReportSynthesis, "review")
        ]);
        Assert.Equal(3, Validator().Validate(plan, "Review code.").Tasks.Count);
    }

    [Fact]
    public void DuplicateTaskIdsAreRejected() => AssertInvalid(new WorkflowPlan("x",
        [Task("same", AgentRole.RequirementsAnalyst), Task("same", AgentRole.CodeAnalysis)]));

    [Fact]
    public void DuplicateRolesAreRejected() => AssertInvalid(new WorkflowPlan("x",
        [Task("one", AgentRole.CodeAnalysis), Task("two", AgentRole.CodeAnalysis)]));

    [Fact]
    public void MissingDependencyIsRejected() => AssertInvalid(new WorkflowPlan("x",
        [Task("code", AgentRole.CodeAnalysis, "missing")]));

    [Fact]
    public void SelfDependencyIsRejected() => AssertInvalid(new WorkflowPlan("x",
        [Task("code", AgentRole.CodeAnalysis, "code")]));

    [Fact]
    public void CyclicDependenciesAreRejected() => AssertInvalid(new WorkflowPlan("x",
        [Task("one", AgentRole.RequirementsAnalyst, "two"), Task("two", AgentRole.CodeAnalysis, "one")]));

    [Fact]
    public void ExcessiveDepthIsRejected()
    {
        var options = Options() with { MaxDependencyDepth = 2 };
        var validator = new WorkflowPlanValidator(options);
        var plan = new WorkflowPlan("x",
        [
            Task("requirements", AgentRole.RequirementsAnalyst),
            Task("architect", AgentRole.SolutionArchitect, "requirements"),
            Task("review", AgentRole.SecurityReviewer, "architect")
        ]);
        Assert.Throws<AgentOperationException>(() => validator.Validate(plan, "objective"));
    }

    [Fact]
    public void SupervisorCannotDelegateToSupervisor() => AssertInvalid(new WorkflowPlan("x",
        [Task("supervisor2", AgentRole.Supervisor)]));

    [Fact]
    public void UndefinedAgentRoleIsRejected() => AssertInvalid(new WorkflowPlan("x",
        [Task("unknown", (AgentRole)999)]));

    [Fact]
    public void ArchitectureCannotBypassReview() => AssertInvalid(new WorkflowPlan("x",
        [Task("architect", AgentRole.SolutionArchitect)]));

    [Fact]
    public void ReviewerMustDependOnArchitecture() => AssertInvalid(new WorkflowPlan("x",
        [Task("architect", AgentRole.SolutionArchitect), Task("review", AgentRole.SecurityReviewer)]));

    [Fact]
    public void ReportCannotBypassReview() => AssertInvalid(new WorkflowPlan("x",
        [Task("report", AgentRole.ReportSynthesis)]));

    [Fact]
    public void ReportMustDependOnReviewer() => AssertInvalid(new WorkflowPlan("x",
        [Task("review", AgentRole.SecurityReviewer), Task("report", AgentRole.ReportSynthesis)]));

    [Fact]
    public void TaskCountBoundIsEnforced()
    {
        var options = Options() with { MaxPlannedTasks = 2 };
        var validator = new WorkflowPlanValidator(options);
        Assert.Throws<AgentOperationException>(() => validator.Validate(FullPlan(), "objective"));
    }

    [Theory]
    [InlineData(WorkflowStatus.Created, WorkflowStatus.Planning)]
    [InlineData(WorkflowStatus.Planning, WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.WaitingToResume)]
    [InlineData(WorkflowStatus.WaitingToResume, WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.Completed)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.CompletedWithConcerns)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.InsufficientEvidence)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.Failed)]
    public void StateMachineAllowsDocumentedTransitions(WorkflowStatus from, WorkflowStatus to) =>
        Assert.True(WorkflowStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(WorkflowStatus.Created, WorkflowStatus.Completed)]
    [InlineData(WorkflowStatus.Completed, WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.Cancelled, WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.WaitingToResume, WorkflowStatus.Completed)]
    public void StateMachineRejectsUnsafeTransitions(WorkflowStatus from, WorkflowStatus to) =>
        Assert.False(WorkflowStateMachine.CanTransition(from, to));

    [Fact]
    public async Task InMemoryStoreEnforcesCompareAndSwap()
    {
        var store = new InMemoryWorkflowCheckpointStore();
        var created = await store.CreateAsync(Checkpoint());
        var first = await store.TryUpdateAsync(created with { ResumeCount = 1 }, created.Version);
        var stale = await store.TryUpdateAsync(created with { ResumeCount = 2 }, created.Version);
        Assert.True(first.Succeeded);
        Assert.False(stale.Succeeded);
        Assert.Equal(1, (await store.GetAsync(created.WorkflowId))!.ResumeCount);
    }

    [Fact]
    public async Task FileStoreSurvivesFreshInstance()
    {
        var options = Options() with { FilePath = _directory };
        var created = await new FileWorkflowCheckpointStore(options).CreateAsync(Checkpoint());
        var loaded = await new FileWorkflowCheckpointStore(options).GetAsync(created.WorkflowId);
        Assert.Equal(created.WorkflowId, loaded!.WorkflowId);
        Assert.Equal(created.Version, loaded.Version);
    }

    [Fact]
    public async Task FileStoreRejectsCorruption()
    {
        var options = Options() with { FilePath = _directory };
        var store = new FileWorkflowCheckpointStore(options);
        var created = await store.CreateAsync(Checkpoint());
        await File.WriteAllTextAsync(Path.Combine(_directory, created.WorkflowId + ".json"), "invalid");
        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => store.GetAsync(created.WorkflowId));
        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    [Fact]
    public void CheckpointValidatorRejectsUnsupportedSchema()
    {
        var exception = Assert.Throws<AgentOperationException>(() => CheckpointValidator().Validate(
            Checkpoint() with { SchemaVersion = 99 }));
        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    [Fact]
    public void CheckpointValidatorRejectsRunningWithoutOwner()
    {
        var exception = Assert.Throws<AgentOperationException>(() => CheckpointValidator().Validate(
            Checkpoint() with { Status = WorkflowStatus.Running }));
        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    [Fact]
    public void CheckpointValidatorSeparatesTaskAttemptsFromRevisionCycles()
    {
        var checkpoint = Checkpoint() with
        {
            Plan = new WorkflowPlan("Assess the project.", [Task("requirements", AgentRole.RequirementsAnalyst)]),
            Tasks =
            [
                new WorkflowTaskState("supervisor", AgentRole.Supervisor, "Plan.", [],
                    WorkflowTaskStatus.Completed, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new AgentTaskOutput("planned", [], [], [], [], [], false, ReviewOutcome.NotApplicable,
                        null, null, null), [], null, null),
                new WorkflowTaskState("requirements", AgentRole.RequirementsAnalyst,
                    "Execute RequirementsAnalyst work.", [], WorkflowTaskStatus.Pending, 5,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, [], null, "Provider")
            ]
        };

        CheckpointValidator().Validate(checkpoint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    internal static MultiAgentWorkflowOptions Options() => new();

    internal static WorkflowPlan FullPlan() => new("ignored",
    [
        Task("requirements", AgentRole.RequirementsAnalyst),
        Task("code", AgentRole.CodeAnalysis),
        Task("architect", AgentRole.SolutionArchitect, "requirements", "code"),
        Task("review", AgentRole.SecurityReviewer, "architect"),
        Task("report", AgentRole.ReportSynthesis, "review")
    ]);

    internal static PlannedAgentTask Task(string id, AgentRole role, params string[] dependencies) =>
        new(id, role, $"Execute {role} work.", dependencies);

    internal static MultiAgentWorkflowCheckpoint Checkpoint() => new(
        MultiAgentWorkflowCheckpoint.CurrentSchemaVersion,
        Guid.NewGuid().ToString("N"),
        "Assess the project.",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        WorkflowStatus.Created,
        null, [], [], [], 0, 0, null, null, null, 0, 0, 0, null, null, 0,
        ReviewOutcome.NotApplicable, null, null, null, string.Empty);

    private static WorkflowPlanValidator Validator() => new(Options());
    private static WorkflowCheckpointValidator CheckpointValidator() => new(Options(), Validator());

    private static void AssertInvalid(WorkflowPlan plan) =>
        Assert.Throws<AgentOperationException>(() => Validator().Validate(plan, "objective"));
}
