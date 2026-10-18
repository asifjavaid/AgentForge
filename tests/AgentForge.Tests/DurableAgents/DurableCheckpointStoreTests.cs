using AgentForge.Application.Agents;
using AgentForge.Application.DurableAgents;
using AgentForge.Infrastructure.DurableAgents;

namespace AgentForge.Tests.DurableAgents;

public sealed class DurableCheckpointStoreTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "agentforge-durable-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(DurableAgentRunStatus.Created, DurableAgentRunStatus.Running)]
    [InlineData(DurableAgentRunStatus.Created, DurableAgentRunStatus.Cancelled)]
    [InlineData(DurableAgentRunStatus.Running, DurableAgentRunStatus.WaitingToResume)]
    [InlineData(DurableAgentRunStatus.Running, DurableAgentRunStatus.Completed)]
    [InlineData(DurableAgentRunStatus.Running, DurableAgentRunStatus.InsufficientEvidence)]
    [InlineData(DurableAgentRunStatus.Running, DurableAgentRunStatus.Failed)]
    [InlineData(DurableAgentRunStatus.Running, DurableAgentRunStatus.Cancelled)]
    [InlineData(DurableAgentRunStatus.WaitingToResume, DurableAgentRunStatus.Running)]
    [InlineData(DurableAgentRunStatus.WaitingToResume, DurableAgentRunStatus.Cancelled)]
    public void StateMachine_AllowsDocumentedTransitions(
        DurableAgentRunStatus from,
        DurableAgentRunStatus to) => Assert.True(DurableAgentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(DurableAgentRunStatus.Created, DurableAgentRunStatus.Completed)]
    [InlineData(DurableAgentRunStatus.Created, DurableAgentRunStatus.WaitingToResume)]
    [InlineData(DurableAgentRunStatus.Running, DurableAgentRunStatus.Running)]
    [InlineData(DurableAgentRunStatus.WaitingToResume, DurableAgentRunStatus.Completed)]
    [InlineData(DurableAgentRunStatus.Completed, DurableAgentRunStatus.Running)]
    [InlineData(DurableAgentRunStatus.Failed, DurableAgentRunStatus.Running)]
    [InlineData(DurableAgentRunStatus.Cancelled, DurableAgentRunStatus.Running)]
    public void StateMachine_RejectsUnsafeTransitions(
        DurableAgentRunStatus from,
        DurableAgentRunStatus to)
    {
        Assert.False(DurableAgentStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => DurableAgentStateMachine.EnsureTransition(from, to));
    }

    [Fact]
    public async Task InMemoryStore_CreateReadAndCompareExchange_PreserveState()
    {
        var store = new InMemoryAgentCheckpointStore();
        var created = await store.CreateAsync(Checkpoint());
        var updated = created with { Status = DurableAgentRunStatus.Cancelled };

        var write = await store.TryUpdateAsync(updated, created.Version);
        var loaded = await store.GetAsync(created.RunId);

        Assert.True(write.Succeeded);
        Assert.Equal("2", loaded!.Version);
        Assert.Equal(DurableAgentRunStatus.Cancelled, loaded.Status);
    }

    [Fact]
    public async Task InMemoryStore_StaleVersionCannotOverwriteNewerCheckpoint()
    {
        var store = new InMemoryAgentCheckpointStore();
        var created = await store.CreateAsync(Checkpoint());
        var first = await store.TryUpdateAsync(created with { ResumeCount = 1 }, created.Version);
        var stale = await store.TryUpdateAsync(created with { ResumeCount = 99 }, created.Version);

        Assert.True(first.Succeeded);
        Assert.False(stale.Succeeded);
        Assert.Equal(1, (await store.GetAsync(created.RunId))!.ResumeCount);
    }

    [Fact]
    public async Task InMemoryStore_DuplicateCreateIsConflict()
    {
        var store = new InMemoryAgentCheckpointStore();
        var checkpoint = Checkpoint();
        await store.CreateAsync(checkpoint);

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => store.CreateAsync(checkpoint));
        Assert.Equal(AgentFailureKind.Conflict, exception.FailureKind);
    }

    [Fact]
    public async Task FileStore_NewInstanceReadsPersistedCheckpoint()
    {
        var options = FileOptions();
        var checkpoint = Checkpoint();
        var created = await new FileAgentCheckpointStore(options).CreateAsync(checkpoint);

        var loaded = await new FileAgentCheckpointStore(options).GetAsync(checkpoint.RunId);

        Assert.Equal(created.RunId, loaded!.RunId);
        Assert.Equal(created.Version, loaded.Version);
        Assert.Equal(created.Question, loaded.Question);
        Assert.Equal(created.Status, loaded.Status);
        Assert.Empty(loaded.Conversation);
        Assert.Empty(loaded.ToolExecutions);
    }

    [Fact]
    public async Task FileStore_StaleVersionCannotOverwriteNewerCheckpoint()
    {
        var store = new FileAgentCheckpointStore(FileOptions());
        var created = await store.CreateAsync(Checkpoint());
        var first = await store.TryUpdateAsync(created with { ResumeCount = 1 }, created.Version);
        var stale = await store.TryUpdateAsync(created with { ResumeCount = 2 }, created.Version);

        Assert.True(first.Succeeded);
        Assert.False(stale.Succeeded);
        Assert.Equal(1, (await store.GetAsync(created.RunId))!.ResumeCount);
    }

    [Fact]
    public async Task FileStore_CorruptEnvelopeFailsClosed()
    {
        var options = FileOptions();
        var store = new FileAgentCheckpointStore(options);
        var created = await store.CreateAsync(Checkpoint());
        await File.WriteAllTextAsync(Path.Combine(options.FilePath, created.RunId + ".json"), "not-json");

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => store.GetAsync(created.RunId));

        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    [Fact]
    public void SerializationRoundTrip_PreservesCheckpoint()
    {
        var checkpoint = Checkpoint() with
        {
            Evidence = ["knowledge:authentication.md#chunk-1"],
            ToolsUsed = ["search_project_knowledge"]
        };

        var restored = CheckpointJson.Deserialize(CheckpointJson.Serialize(checkpoint));

        Assert.Equal(checkpoint.RunId, restored.RunId);
        Assert.Equal(checkpoint.Question, restored.Question);
        Assert.Equal(checkpoint.Status, restored.Status);
        Assert.Equal(checkpoint.Evidence, restored.Evidence);
        Assert.Equal(checkpoint.ToolsUsed, restored.ToolsUsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Validator_RejectsUnsupportedSchema(int schemaVersion)
    {
        var exception = Assert.Throws<AgentOperationException>(() => Validator().Validate(
            Checkpoint() with { SchemaVersion = schemaVersion }));
        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    [Fact]
    public void Validator_RejectsRunningStateWithoutLeaseOwner() => AssertCorrupt(
        Checkpoint() with { Status = DurableAgentRunStatus.Running });

    [Fact]
    public void Validator_RejectsLeaseOwnerOutsideRunningState() => AssertCorrupt(
        Checkpoint() with { ExecutionOwner = "owner", LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });

    [Fact]
    public void Validator_RejectsFabricatedFinalCitation() => AssertCorrupt(
        Checkpoint() with { FinalCitations = ["repo:missing.cs"] });

    [Fact]
    public void Validator_RejectsUnrecognizedEvidenceScheme() => AssertCorrupt(
        Checkpoint() with { Evidence = ["http:untrusted"] });

    [Fact]
    public void Validator_RejectsSystemMessages() => AssertCorrupt(
        Checkpoint() with { Conversation = [new AgentMessage(AgentMessageRole.System, "secret prompt")] });

    [Fact]
    public void Validator_RejectsDuplicateToolExecutionIds()
    {
        var execution = Execution("same", DurableToolExecutionStatus.Pending);
        AssertCorrupt(Checkpoint() with { ToolExecutions = [execution, execution] });
    }

    [Fact]
    public void Validator_RejectsCompletedExecutionWithoutResult() => AssertCorrupt(
        Checkpoint() with { ToolExecutions = [Execution("call", DurableToolExecutionStatus.Completed)] });

    [Fact]
    public void Validator_RejectsPendingExecutionInTerminalState() => AssertCorrupt(
        Checkpoint() with
        {
            Status = DurableAgentRunStatus.Failed,
            ToolExecutions = [Execution("call", DurableToolExecutionStatus.Pending)]
        });

    [Fact]
    public void DurableOptions_RejectsUnboundedOrUnknownConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => new DurableAgentOptions { StoreProvider = "Unknown" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DurableAgentOptions { LeaseSeconds = 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DurableAgentOptions { MaxCheckpointBytes = 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DurableAgentOptions { MaxConversationMessages = 1 }.Validate());
    }

    [Theory]
    [InlineData("http://account.table.core.windows.net")]
    [InlineData("https://user:secret@account.table.core.windows.net")]
    [InlineData("https://account.table.core.windows.net?key=secret")]
    public void AzureTableOptions_RejectsUnsafeEndpoint(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => new AzureTableCheckpointOptions { Endpoint = endpoint }.GetEndpoint());

    [Theory]
    [InlineData("a")]
    [InlineData("1startsWithNumber")]
    [InlineData("contains-hyphen")]
    public void AzureTableOptions_RejectsInvalidTableName(string tableName) =>
        Assert.Throws<InvalidOperationException>(() => new AzureTableCheckpointOptions { TableName = tableName }.GetTableName());

    [Fact]
    public void AzureTableOptions_AcceptsHttpsEndpointAndOptionalTenant()
    {
        var tenant = Guid.NewGuid();
        var options = new AzureTableCheckpointOptions
        {
            Endpoint = "https://agentforge.table.core.windows.net",
            TableName = "AgentForgeRuns",
            TenantId = tenant.ToString("D")
        };

        Assert.Equal("https://agentforge.table.core.windows.net/", options.GetEndpoint().AbsoluteUri);
        Assert.Equal("AgentForgeRuns", options.GetTableName());
        Assert.Equal(tenant.ToString("D"), options.GetTenantId());
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private DurableAgentOptions FileOptions() => new()
    {
        StoreProvider = "File",
        FilePath = _temporaryDirectory
    };

    private static DurableCheckpointValidator Validator() => new(new DurableAgentOptions());

    private static void AssertCorrupt(DurableAgentCheckpoint checkpoint)
    {
        var exception = Assert.Throws<AgentOperationException>(() => Validator().Validate(checkpoint));
        Assert.Equal(AgentFailureKind.CorruptState, exception.FailureKind);
    }

    private static DurableToolExecution Execution(string id, DurableToolExecutionStatus status) => new(
        id,
        "read_file",
        "{}",
        "read_file\n{}",
        1,
        status,
        status == DurableToolExecutionStatus.Pending ? null : DateTimeOffset.UtcNow,
        1,
        null,
        [],
        false,
        null);

    internal static DurableAgentCheckpoint Checkpoint(
        DurableAgentRunStatus status = DurableAgentRunStatus.Created,
        string? runId = null) => new(
        DurableAgentCheckpoint.CurrentSchemaVersion,
        runId ?? Guid.NewGuid().ToString("N"),
        "What is the documented behavior?",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        status,
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
}
