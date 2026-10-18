using AgentForge.Application.Agents;
using AgentForge.Application.DurableAgents;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;

namespace AgentForge.Infrastructure.DurableAgents;

public sealed class AzureTableAgentCheckpointStore : IAgentCheckpointStore
{
    private const int SegmentBytes = 48_000;
    private readonly TableClient _table;

    public AzureTableAgentCheckpointStore(AzureTableCheckpointOptions options)
    {
        var credential = new DefaultAzureCredential(
            new DefaultAzureCredentialOptions { TenantId = options.GetTenantId() });
        _table = new TableClient(options.GetEndpoint(), options.GetTableName(), credential);
    }

    public async Task<DurableAgentCheckpoint?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(
                runId, "metadata", cancellationToken: cancellationToken);
            return await ReadPayloadAsync(response.Value, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException exception)
        {
            throw StorageFailure(exception);
        }
    }

    public async Task<DurableAgentCheckpoint> CreateAsync(
        DurableAgentCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(checkpoint.RunId);
        var generation = Guid.NewGuid().ToString("N");
        var payload = CheckpointJson.Serialize(checkpoint with { Version = string.Empty });
        var actions = CreatePayloadActions(checkpoint, generation, payload, ETag.All, create: true);
        try
        {
            await _table.SubmitTransactionAsync(actions, cancellationToken);
            return await GetAsync(checkpoint.RunId, cancellationToken) ??
                throw new AgentOperationException(AgentFailureKind.Storage, "The created durable checkpoint could not be loaded.");
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            throw new AgentOperationException(AgentFailureKind.Conflict, "The durable run already exists.", exception);
        }
        catch (RequestFailedException exception)
        {
            throw StorageFailure(exception);
        }
    }

    public async Task<CheckpointWriteResult> TryUpdateAsync(
        DurableAgentCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var generation = Guid.NewGuid().ToString("N");
        var payload = CheckpointJson.Serialize(checkpoint with { Version = string.Empty });
        var actions = CreatePayloadActions(checkpoint, generation, payload, new ETag(expectedVersion), create: false);
        try
        {
            await _table.SubmitTransactionAsync(actions, cancellationToken);
            var current = await GetAsync(checkpoint.RunId, cancellationToken);
            return new CheckpointWriteResult(current is not null, current);
        }
        catch (RequestFailedException exception) when (exception.Status is 404 or 409 or 412)
        {
            return new CheckpointWriteResult(false, await GetAsync(checkpoint.RunId, cancellationToken));
        }
        catch (RequestFailedException exception)
        {
            throw StorageFailure(exception);
        }
    }

    private List<TableTransactionAction> CreatePayloadActions(
        DurableAgentCheckpoint checkpoint,
        string generation,
        byte[] payload,
        ETag expectedEtag,
        bool create)
    {
        var segments = payload.Chunk(SegmentBytes).Select(chunk => chunk.ToArray()).ToArray();
        if (segments.Length > 99)
            throw new AgentOperationException(AgentFailureKind.Storage, "The durable checkpoint exceeds the Azure Table transaction bound.");
        var metadata = new TableEntity(checkpoint.RunId, "metadata")
        {
            ["Generation"] = generation,
            ["SegmentCount"] = segments.Length,
            ["Checksum"] = CheckpointJson.Checksum(payload),
            ["SchemaVersion"] = checkpoint.SchemaVersion,
            ["Status"] = checkpoint.Status.ToString(),
            ["UpdatedAt"] = checkpoint.UpdatedAt
        };
        var actions = new List<TableTransactionAction>(segments.Length + 1)
        {
            create
                ? new TableTransactionAction(TableTransactionActionType.Add, metadata)
                : new TableTransactionAction(TableTransactionActionType.UpdateReplace, metadata, expectedEtag)
        };
        actions.AddRange(segments.Select((segment, index) =>
            new TableTransactionAction(
                TableTransactionActionType.Add,
                new TableEntity(checkpoint.RunId, $"payload:{generation}:{index:D4}")
                {
                    ["Payload"] = segment
                })));
        return actions;
    }

    private async Task<DurableAgentCheckpoint> ReadPayloadAsync(
        TableEntity metadata,
        CancellationToken cancellationToken)
    {
        var generation = metadata.GetString("Generation") ?? throw Corrupt("Azure Table metadata is missing its payload generation.");
        var segmentCount = metadata.GetInt32("SegmentCount") ?? throw Corrupt("Azure Table metadata is missing its segment count.");
        if (segmentCount is < 1 or > 99) throw Corrupt("Azure Table payload segment count is invalid.");
        await using var stream = new MemoryStream();
        for (var index = 0; index < segmentCount; index++)
        {
            TableEntity segment;
            try
            {
                segment = (await _table.GetEntityAsync<TableEntity>(
                    metadata.PartitionKey,
                    $"payload:{generation}:{index:D4}",
                    cancellationToken: cancellationToken)).Value;
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                throw Corrupt("Azure Table checkpoint payload is incomplete.", exception);
            }
            var bytes = segment.GetBinaryData("Payload")?.ToArray() ??
                throw Corrupt("Azure Table checkpoint payload segment is malformed.");
            await stream.WriteAsync(bytes, cancellationToken);
        }
        var payload = stream.ToArray();
        var checksum = metadata.GetString("Checksum") ?? string.Empty;
        if (!string.Equals(checksum, CheckpointJson.Checksum(payload), StringComparison.Ordinal))
            throw Corrupt("Azure Table checkpoint checksum did not match.");
        return CheckpointJson.Deserialize(payload) with { Version = metadata.ETag.ToString() };
    }

    private static void ValidateRunId(string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The durable run identifier is invalid.");
    }

    private static AgentOperationException StorageFailure(RequestFailedException exception) =>
        new(AgentFailureKind.Storage, "Azure Table checkpoint storage failed.", exception);

    private static AgentOperationException Corrupt(string message, Exception? exception = null) =>
        exception is null
            ? new AgentOperationException(AgentFailureKind.CorruptState, message)
            : new AgentOperationException(AgentFailureKind.CorruptState, message, exception);
}
