using System.Text.Json;
using AgentForge.Application.Agents;
using AgentForge.Application.HumanApproval;
using AgentForge.Infrastructure.HumanApproval;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests.HumanApproval;

public sealed class HumanApprovalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentforge-hitl-tests", Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-21T10:00:00Z"));

    [Fact]
    public async Task ValidProposalIsPendingAndDoesNotChangeSandbox()
    {
        var fixture = CreateFixture();
        var approval = await fixture.Service.CreateAsync(Proposal(), Requester(), "correlation");
        Assert.Equal(ApprovalStatus.PendingApproval, approval.Status);
        Assert.Equal(ActionRisk.Medium, approval.Risk);
        Assert.Equal(64, approval.ActionDigest.Length);
        Assert.False(File.Exists(Path.Combine(_root, "feature-flags.json")));
        Assert.Equal(4, (await fixture.Service.GetAuditAsync(approval.ApprovalId, Auditor())).Count);
    }

    [Fact]
    public async Task HumanApprovalBindsIdentityAndMakesActionEligible()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        var approved = await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        Assert.Equal(ApprovalStatus.Approved, approved.Status);
        Assert.Equal("reviewer", approved.ApproverId);
        Assert.Equal(created.ActionDigest, approved.ActionDigest);
        Assert.False(File.Exists(Path.Combine(_root, "feature-flags.json")));
    }

    [Fact]
    public async Task RejectionIsTerminalAndCannotExecute()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        var rejected = await fixture.Service.RejectAsync(created.ApprovalId, Reviewer(), "reject");
        Assert.Equal(ApprovalStatus.Rejected, rejected.Status);
        await AssertFailureAsync(AgentFailureKind.Conflict, () =>
            fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute"));
        Assert.False(File.Exists(Path.Combine(_root, "feature-flags.json")));
    }

    [Fact]
    public async Task ExpiredApprovalIsDurablyExpiredAndCannotExecute()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        _clock.Advance(TimeSpan.FromMinutes(31));
        await AssertFailureAsync(AgentFailureKind.Conflict, () =>
            fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute"));
        var stored = await fixture.Store.GetAsync(created.ApprovalId);
        Assert.Equal(ApprovalStatus.Expired, stored!.Status);
        Assert.Contains(stored.AuditEvents, item => item.EventType == ApprovalAuditEventType.ApprovalExpired);
    }

    [Fact]
    public async Task RevokedApprovalCannotExecute()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        var revoked = await fixture.Service.RevokeAsync(created.ApprovalId, Reviewer(), "revoke");
        Assert.Equal(ApprovalStatus.Revoked, revoked.Status);
        await AssertFailureAsync(AgentFailureKind.Conflict, () =>
            fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute"));
    }

    [Fact]
    public async Task AgentCannotApproveItsOwnProposalEvenWithForgedPermission()
    {
        var fixture = CreateFixture();
        var agent = Actor("agent", ApprovalActorType.Agent, ApprovalPermissions.ProposalCreate,
            ApprovalPermissions.ProposalRead, ApprovalPermissions.Approve);
        var created = await fixture.Service.CreateAsync(Proposal(), agent, "create");
        await AssertFailureAsync(AgentFailureKind.AccessDenied, () =>
            fixture.Service.ApproveAsync(created.ApprovalId, agent, "approve"));
    }

    [Fact]
    public async Task FourEyesPolicyRejectsRequesterSelfApproval()
    {
        var fixture = CreateFixture();
        var requester = Actor("same-human", ApprovalActorType.Human, ApprovalPermissions.ProposalCreate,
            ApprovalPermissions.ProposalRead, ApprovalPermissions.Approve);
        var created = await fixture.Service.CreateAsync(Proposal(), requester, "create");
        await AssertFailureAsync(AgentFailureKind.AccessDenied, () =>
            fixture.Service.ApproveAsync(created.ApprovalId, requester, "approve"));
    }

    [Fact]
    public async Task MissingPermissionFailsInsideServiceBoundary()
    {
        var fixture = CreateFixture();
        await AssertFailureAsync(AgentFailureKind.AccessDenied, () =>
            fixture.Service.CreateAsync(Proposal(), Actor("anonymous", ApprovalActorType.Human), "create"));
    }

    [Fact]
    public async Task DuplicateApprovalBySameReviewerIsIdempotent()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        var first = await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve-1");
        var second = await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve-2");
        Assert.Equal(first.DecisionAt, second.DecisionAt);
        Assert.Equal(5, (await fixture.Store.GetAsync(created.ApprovalId))!.AuditEvents.Count);
    }

    [Fact]
    public async Task ConcurrentApprovalAndRejectionCommitOnlyOneDecision()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        var results = await Task.WhenAll(Capture(() => fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "a")),
            Capture(() => fixture.Service.RejectAsync(created.ApprovalId, Reviewer(), "r")));
        Assert.Single(results, item => item.View is not null);
        Assert.Single(results, item => item.Error is AgentOperationException);
        Assert.Contains((await fixture.Store.GetAsync(created.ApprovalId))!.Status,
            new[] { ApprovalStatus.Approved, ApprovalStatus.Rejected });
    }

    [Fact]
    public async Task ApprovedActionExecutesOnceWithReceiptAndAudit()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        var executed = await fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute");
        Assert.Equal(ApprovalStatus.Executed, executed.Status);
        Assert.Equal(ControlledActionOutcome.Executed, executed.Receipt!.Outcome);
        using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "feature-flags.json")));
        Assert.True(artifact.RootElement.GetProperty("flags").GetProperty("enable-demo-feature").GetBoolean());
        var audit = await fixture.Service.GetAuditAsync(created.ApprovalId, Auditor());
        Assert.Contains(audit, item => item.EventType == ApprovalAuditEventType.ExecutionStarted);
        Assert.Contains(audit, item => item.EventType == ApprovalAuditEventType.ExecutionCompleted);
    }

    [Fact]
    public async Task RepeatedExecutionReturnsCommittedReceiptWithoutSecondSideEffect()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        var first = await fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute-1");
        var second = await fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute-2");
        Assert.Equal(first.Receipt, second.Receipt);
        Assert.Equal(1, second.Receipt!.CurrentTargetVersion);
    }

    [Fact]
    public async Task SecondProposalWithStaleTargetVersionCannotOverwriteNewerChange()
    {
        var fixture = CreateFixture();
        var first = await fixture.Service.CreateAsync(Proposal(), Requester(), "first");
        var second = await fixture.Service.CreateAsync(Proposal(enabled: false), Requester(), "second");
        await fixture.Service.ApproveAsync(first.ApprovalId, Reviewer(), "approve-first");
        await fixture.Service.ApproveAsync(second.ApprovalId, Reviewer(), "approve-second");
        await fixture.Service.ExecuteAsync(first.ApprovalId, Executor(), "execute-first");
        var stale = await fixture.Service.ExecuteAsync(second.ApprovalId, Executor(), "execute-second");
        Assert.Equal(ApprovalStatus.ExecutionFailed, stale.Status);
        Assert.Equal("TargetVersionConflict", stale.Receipt!.ReasonCode);
    }

    [Fact]
    public async Task ExecutorUncertainOutcomeStopsAutomaticRetry()
    {
        var uncertainExecutor = new StubExecutor(ControlledActionOutcome.Uncertain);
        var fixture = CreateFixture(uncertainExecutor);
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        var result = await fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute");
        Assert.Equal(ApprovalStatus.ExecutionUncertain, result.Status);
        await AssertFailureAsync(AgentFailureKind.Conflict, () =>
            fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "retry"));
        Assert.Equal(1, uncertainExecutor.Calls);
    }

    [Fact]
    public async Task ModifiedProposalAfterApprovalIsDetectedBeforeExecution()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        var stored = (await fixture.Store.GetAsync(created.ApprovalId))!;
        using var changed = JsonDocument.Parse("{\"flagName\":\"enable-demo-feature\",\"enabled\":false}");
        fixture.Store.Inject(stored with { Proposal = stored.Proposal with { Parameters = changed.RootElement.Clone(), Enabled = false } });
        await AssertFailureAsync(AgentFailureKind.CorruptState, () =>
            fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "execute"));
    }

    [Fact]
    public async Task TenantBoundaryBlocksReadsAndDecisions()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        var foreign = Reviewer() with { TenantId = "other" };
        await AssertFailureAsync(AgentFailureKind.AccessDenied, () =>
            fixture.Service.GetAsync(created.ApprovalId, foreign));
        await AssertFailureAsync(AgentFailureKind.AccessDenied, () =>
            fixture.Service.ApproveAsync(created.ApprovalId, foreign, "approve"));
    }

    [Fact]
    public async Task PolicyVersionChangeInvalidatesExecutionWithoutHidingHistory()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        var updatedOptions = Options("A10-v2");
        var updatedValidator = new ApprovalRecordValidator(updatedOptions);
        var sandbox = new SandboxFeatureFlagExecutor(updatedOptions, _clock);
        var source = new AllowSource();
        var updatedService = new HumanApprovalService(fixture.Store,
            new SandboxActionProposalValidator(source, sandbox, updatedOptions), source,
            new DeterministicActionPolicyEvaluator(updatedOptions), sandbox, updatedValidator,
            updatedOptions, _clock, NullLogger<HumanApprovalService>.Instance);
        var readable = await updatedService.GetAsync(created.ApprovalId, Executor());
        Assert.Equal("A10-v1", readable.PolicyVersion);
        await AssertFailureAsync(AgentFailureKind.Conflict, () =>
            updatedService.ExecuteAsync(created.ApprovalId, Executor(), "execute"));
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReacquiredAndStaleExecutorCannotCommit()
    {
        var blocking = new BlockingExecutor();
        var fixture = CreateFixture(blocking);
        var created = await fixture.Service.CreateAsync(Proposal(), Requester(), "create");
        await fixture.Service.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        var first = Capture(() => fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "first"));
        await blocking.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(31));
        var second = Capture(() => fixture.Service.ExecuteAsync(created.ApprovalId, Executor(), "second"));
        await blocking.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        blocking.Release.TrySetResult();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Single(outcomes, item => item.View?.Status == ApprovalStatus.Executed);
        Assert.Single(outcomes, item => item.Error is AgentOperationException);
        var stored = await fixture.Store.GetAsync(created.ApprovalId);
        Assert.Equal(2, stored!.FencingToken);
        Assert.Equal(ApprovalStatus.Executed, stored.Status);
    }

    private Fixture CreateFixture(IControlledActionExecutor? executor = null)
    {
        Directory.CreateDirectory(_root);
        var options = Options();
        var validator = new ApprovalRecordValidator(options);
        var store = new InMemoryApprovalStore(validator);
        var sandbox = new SandboxFeatureFlagExecutor(options, _clock);
        var source = new AllowSource();
        var proposalValidator = new SandboxActionProposalValidator(source, sandbox, options);
        var service = new HumanApprovalService(store, proposalValidator, source,
            new DeterministicActionPolicyEvaluator(options), executor ?? sandbox, validator, options, _clock,
            NullLogger<HumanApprovalService>.Instance);
        return new Fixture(service, store);
    }

    private HumanApprovalOptions Options(string policyVersion = "A10-v1") => new()
    {
        StoreProvider = "InMemory",
        FilePath = Path.Combine(_root, "approvals"),
        SandboxRoot = _root,
        ApprovalLifetimeMinutes = 30,
        ExecutionLeaseSeconds = 30,
        ExecutionTimeoutSeconds = 10,
        PolicyVersion = policyVersion,
        AllowedFlagNames = ["enable-demo-feature"]
    };

    private static AgentActionProposal Proposal(bool enabled = true)
    {
        using var document = JsonDocument.Parse($"{{\"flagName\":\"enable-demo-feature\",\"enabled\":{enabled.ToString().ToLowerInvariant()}}}");
        return new AgentActionProposal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "report",
            "UpdateSandboxFeatureFlag", "sandbox-feature-flags", document.RootElement.Clone(),
            "Enable the reviewed demo capability.", ["knowledge:authentication.md#chunk-1"]);
    }

    private static ActorContext Requester() => Actor("requester", ApprovalActorType.Human,
        ApprovalPermissions.ProposalCreate, ApprovalPermissions.ProposalRead);
    private static ActorContext Reviewer() => Actor("reviewer", ApprovalActorType.Human,
        ApprovalPermissions.ProposalRead, ApprovalPermissions.Review,
        ApprovalPermissions.Approve, ApprovalPermissions.Reject, ApprovalPermissions.AuditRead);
    private static ActorContext Executor() => Actor("executor", ApprovalActorType.Service,
        ApprovalPermissions.ProposalRead, ApprovalPermissions.Execute, ApprovalPermissions.AuditRead);
    private static ActorContext Auditor() => Actor("auditor", ApprovalActorType.Human,
        ApprovalPermissions.ProposalRead, ApprovalPermissions.AuditRead);
    private static ActorContext Actor(string id, ApprovalActorType type, params string[] permissions) =>
        new(id, type, "local", permissions.ToHashSet(StringComparer.Ordinal));

    private static async Task AssertFailureAsync(AgentFailureKind kind, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<AgentOperationException>(action);
        Assert.Equal(kind, exception.FailureKind);
    }

    private static async Task<(ApprovalView? View, Exception? Error)> Capture(Func<Task<ApprovalView>> action)
    {
        try { return (await action(), null); }
        catch (Exception exception) { return (null, exception); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed record Fixture(HumanApprovalService Service, InMemoryApprovalStore Store);
    private sealed class AllowSource : IActionProposalSourceValidator
    {
        public Task ValidateAsync(string workflowId, string taskId, IReadOnlyList<string> evidenceIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class StubExecutor(ControlledActionOutcome outcome) : IControlledActionExecutor
    {
        public int Calls { get; private set; }
        public Task<ActionExecutionReceipt> ExecuteAsync(ApprovedAction action,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ActionExecutionReceipt(action.OperationId, outcome, action.ActionType,
                action.TargetId, action.ExpectedTargetVersion, action.ExpectedTargetVersion,
                DateTimeOffset.UtcNow, outcome.ToString()));
        }
    }
    private sealed class BlockingExecutor : IControlledActionExecutor
    {
        private int _calls;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ActionExecutionReceipt> ExecuteAsync(ApprovedAction action,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            (call == 1 ? FirstEntered : SecondEntered).TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ActionExecutionReceipt(action.OperationId,
                call == 1 ? ControlledActionOutcome.Executed : ControlledActionOutcome.Reconciled,
                action.ActionType, action.TargetId, action.ExpectedTargetVersion,
                action.ExpectedTargetVersion + 1, DateTimeOffset.UtcNow, "ControlledTest");
        }
    }
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
