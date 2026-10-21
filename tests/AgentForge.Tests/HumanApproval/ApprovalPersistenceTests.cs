using System.Text.Json;
using AgentForge.Application.Agents;
using AgentForge.Application.HumanApproval;
using AgentForge.Infrastructure.HumanApproval;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests.HumanApproval;

public sealed class ApprovalPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentforge-approval-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FileStoreSurvivesProviderRecreation()
    {
        var options = Options();
        var validator = new ApprovalRecordValidator(options);
        var created = await new FileApprovalStore(options, validator).CreateAsync(Record(options));
        var loaded = await new FileApprovalStore(options, validator).GetAsync(created.ApprovalId);
        Assert.Equal(created.ActionDigest, loaded!.ActionDigest);
        Assert.Equal(ApprovalStatus.PendingApproval, loaded.Status);
        Assert.Equal(4, loaded.AuditEvents.Count);
    }

    [Fact]
    public async Task FileStoreOptimisticConcurrencyAllowsOneDecision()
    {
        var options = Options();
        var validator = new ApprovalRecordValidator(options);
        var store = new FileApprovalStore(options, validator);
        var created = await store.CreateAsync(Record(options));
        var approved = Approved(created);
        var first = await store.TryUpdateAsync(approved, created.Version);
        var second = await store.TryUpdateAsync(approved, created.Version);
        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(ApprovalStatus.Approved, second.Record!.Status);
    }

    [Fact]
    public async Task FileStoreDetectsCorruptEnvelope()
    {
        var options = Options();
        Directory.CreateDirectory(options.FilePath);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(options.FilePath, id + ".json"), "{ broken }");
        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            new FileApprovalStore(options, new ApprovalRecordValidator(options)).GetAsync(id));
        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    [Fact]
    public async Task FileStoreRejectsUnsupportedSchemaVersion()
    {
        var options = Options();
        Directory.CreateDirectory(options.FilePath);
        var record = Record(options) with { SchemaVersion = 99 };
        var payload = ApprovalJson.Serialize(record);
        var envelope = new ApprovalJson.Envelope(ApprovalJson.Checksum(payload), Convert.ToBase64String(payload));
        await File.WriteAllBytesAsync(Path.Combine(options.FilePath, record.ApprovalId + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(envelope, ApprovalJson.Options));
        var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
            new FileApprovalStore(options, new ApprovalRecordValidator(options)).GetAsync(record.ApprovalId));
        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    [Fact]
    public async Task StoreRejectsMutationOfApprovedActionAndAuditPrefix()
    {
        var options = Options();
        var validator = new ApprovalRecordValidator(options);
        var store = new InMemoryApprovalStore(validator);
        var created = await store.CreateAsync(Record(options));
        var modifiedTarget = created with { Proposal = created.Proposal with { TargetId = "production" } };
        await Assert.ThrowsAsync<AgentOperationException>(() => store.TryUpdateAsync(modifiedTarget, created.Version));
        var modifiedAudit = created with { AuditEvents = created.AuditEvents.Skip(1).ToArray() };
        await Assert.ThrowsAsync<AgentOperationException>(() => store.TryUpdateAsync(modifiedAudit, created.Version));
    }

    [Fact]
    public async Task ListIsTenantAndStatusScoped()
    {
        var options = Options();
        var store = new InMemoryApprovalStore(new ApprovalRecordValidator(options));
        await store.CreateAsync(Record(options));
        var other = Record(options) with
        {
            ApprovalId = Guid.NewGuid().ToString("N"),
            OperationId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            TenantId = "other"
        };
        other = other with
        {
            ActionDigest = ActionApprovalDigest.Compute(other.Proposal, other.PolicyVersion,
                options.EnvironmentName, other.TenantId),
            AuditEvents = []
        };
        other = other with { AuditEvents = Audit(other) };
        await store.CreateAsync(other);
        Assert.Single(await store.ListAsync(ApprovalStatus.PendingApproval, "local"));
        Assert.Single(await store.ListAsync(null, "other"));
        Assert.Empty(await store.ListAsync(ApprovalStatus.Approved, "local"));
    }

    [Fact]
    public async Task CrashAfterSideEffectIsReconciledAfterDurableServiceReconstruction()
    {
        var options = Options();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-21T10:00:00Z"));
        var sandbox = new SandboxFeatureFlagExecutor(options, clock);
        var source = new AllowSource();
        var validator = new ApprovalRecordValidator(options);
        var store = new FileApprovalStore(options, validator);
        var crashingService = Service(store, new CrashAfterSideEffectExecutor(sandbox), sandbox,
            source, validator, options, clock);
        var created = await crashingService.CreateAsync(ActionProposal(), Requester(), "create");
        await crashingService.ApproveAsync(created.ApprovalId, Reviewer(), "approve");
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            crashingService.ExecuteAsync(created.ApprovalId, ExecutorActor(), "execute-before-crash"));
        var interrupted = await store.GetAsync(created.ApprovalId);
        Assert.Equal(ApprovalStatus.Executing, interrupted!.Status);
        Assert.Equal(1, await sandbox.GetVersionAsync("sandbox-feature-flags"));

        clock.Advance(TimeSpan.FromSeconds(31));
        var restartedSandbox = new SandboxFeatureFlagExecutor(options, clock);
        var restartedStore = new FileApprovalStore(options, new ApprovalRecordValidator(options));
        var restarted = Service(restartedStore, restartedSandbox, restartedSandbox, source,
            new ApprovalRecordValidator(options), options, clock);
        var recovered = await restarted.ExecuteAsync(created.ApprovalId, ExecutorActor(), "restart-reconcile");
        Assert.Equal(ApprovalStatus.Executed, recovered.Status);
        Assert.Equal(ControlledActionOutcome.Reconciled, recovered.Receipt!.Outcome);
        Assert.Equal(2, (await restartedStore.GetAsync(created.ApprovalId))!.FencingToken);
    }

    private HumanApprovalOptions Options() => new()
    {
        StoreProvider = "File",
        FilePath = Path.Combine(_root, "approvals"),
        SandboxRoot = Path.Combine(_root, "sandbox"),
        AllowedFlagNames = ["enable-demo-feature"]
    };

    private static ApprovalRecord Record(HumanApprovalOptions options)
    {
        using var document = JsonDocument.Parse("{\"flagName\":\"enable-demo-feature\",\"enabled\":true}");
        var proposal = new ValidatedActionProposal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "report",
            "UpdateSandboxFeatureFlag", "sandbox-feature-flags", document.RootElement.Clone(), "reviewed",
            ["knowledge:authentication.md#chunk-1"], "enable-demo-feature", true, 0);
        var id = Guid.NewGuid().ToString("N");
        var digest = ActionApprovalDigest.Compute(proposal, options.PolicyVersion, options.EnvironmentName, "local");
        var record = new ApprovalRecord(1, id, proposal, "requester", ApprovalActorType.Human, "local",
            options.EnvironmentName,
            ActionRisk.Medium, "HumanFourEyes", options.PolicyVersion, digest, ApprovalStatus.PendingApproval,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(30), null, null, null,
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 0, null, null, 0, null, [], "");
        return record with { AuditEvents = Audit(record) };
    }

    private static ApprovalRecord Approved(ApprovalRecord record)
    {
        var audit = record.AuditEvents.Append(new ApprovalAuditEvent(Guid.NewGuid().ToString("N"),
            record.ApprovalId, "reviewer", ApprovalActorType.Human, DateTimeOffset.UnixEpoch.AddMinutes(1),
            ApprovalAuditEventType.ApprovalGranted, ApprovalStatus.PendingApproval, ApprovalStatus.Approved,
            "approve", record.PolicyVersion, record.ActionDigest, "HumanApproval")).ToArray();
        return record with
        {
            Status = ApprovalStatus.Approved,
            ApproverId = "reviewer",
            DecisionAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
            DecisionReasonCode = "HumanApproval",
            AuditEvents = audit
        };
    }

    private static IReadOnlyList<ApprovalAuditEvent> Audit(ApprovalRecord record) =>
        new[]
        {
            ApprovalAuditEventType.ProposalCreated,
            ApprovalAuditEventType.ProposalValidated,
            ApprovalAuditEventType.PolicyEvaluated,
            ApprovalAuditEventType.ApprovalRequested
        }.Select(type => new ApprovalAuditEvent(Guid.NewGuid().ToString("N"), record.ApprovalId,
            record.RequesterId, record.RequesterType, record.CreatedAt, type, null,
            ApprovalStatus.PendingApproval, "create", record.PolicyVersion, record.ActionDigest,
            type.ToString())).ToArray();

    private static HumanApprovalService Service(IApprovalStore store, IControlledActionExecutor executor,
        IActionTargetVersionProvider versionProvider, IActionProposalSourceValidator source,
        ApprovalRecordValidator validator, HumanApprovalOptions options, TimeProvider clock)
    {
        var proposalValidator = new SandboxActionProposalValidator(source, versionProvider, options);
        return new HumanApprovalService(store, proposalValidator, source,
            new DeterministicActionPolicyEvaluator(options), executor, validator, options, clock,
            NullLogger<HumanApprovalService>.Instance);
    }

    private static AgentActionProposal ActionProposal()
    {
        using var document = JsonDocument.Parse("{\"flagName\":\"enable-demo-feature\",\"enabled\":true}");
        return new AgentActionProposal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "report",
            "UpdateSandboxFeatureFlag", "sandbox-feature-flags", document.RootElement.Clone(),
            "Approved sandbox recovery test.", ["knowledge:authentication.md#chunk-1"]);
    }

    private static ActorContext Requester() => Actor("requester", ApprovalActorType.Human,
        ApprovalPermissions.ProposalCreate, ApprovalPermissions.ProposalRead);
    private static ActorContext Reviewer() => Actor("reviewer", ApprovalActorType.Human,
        ApprovalPermissions.ProposalRead, ApprovalPermissions.Approve, ApprovalPermissions.Reject);
    private static ActorContext ExecutorActor() => Actor("executor", ApprovalActorType.Service,
        ApprovalPermissions.ProposalRead, ApprovalPermissions.Execute);
    private static ActorContext Actor(string id, ApprovalActorType type, params string[] permissions) =>
        new(id, type, "local", permissions.ToHashSet(StringComparer.Ordinal));

    private sealed class AllowSource : IActionProposalSourceValidator
    {
        public Task ValidateAsync(string workflowId, string taskId, IReadOnlyList<string> evidenceIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CrashAfterSideEffectExecutor(IControlledActionExecutor inner) : IControlledActionExecutor
    {
        public async Task<ActionExecutionReceipt> ExecuteAsync(ApprovedAction action,
            CancellationToken cancellationToken = default)
        {
            await inner.ExecuteAsync(action, cancellationToken);
            throw new OperationCanceledException("Synthetic process interruption after atomic side effect.");
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
