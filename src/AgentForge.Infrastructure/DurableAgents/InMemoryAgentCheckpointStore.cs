using System.Collections.Concurrent;
using AgentForge.Application.Agents;
using AgentForge.Application.DurableAgents;

namespace AgentForge.Infrastructure.DurableAgents;

public sealed class InMemoryAgentCheckpointStore : IAgentCheckpointStore
{
    private readonly ConcurrentDictionary<string, DurableAgentCheckpoint> _checkpoints = new(StringComparer.Ordinal);

    public Task<DurableAgentCheckpoint?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints.TryGetValue(runId, out var checkpoint);
        return Task.FromResult(checkpoint);
    }

    public Task<DurableAgentCheckpoint> CreateAsync(
        DurableAgentCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = checkpoint with { Version = "1" };
        if (!_checkpoints.TryAdd(checkpoint.RunId, created))
            throw new AgentOperationException(AgentFailureKind.Conflict, "The durable run already exists.");
        return Task.FromResult(created);
    }

    public Task<CheckpointWriteResult> TryUpdateAsync(
        DurableAgentCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_checkpoints.TryGetValue(checkpoint.RunId, out var current))
        {
            if (!string.Equals(current.Version, expectedVersion, StringComparison.Ordinal) ||
                !long.TryParse(current.Version, out var version))
                return Task.FromResult(new CheckpointWriteResult(false, current));
            var updated = checkpoint with { Version = (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (_checkpoints.TryUpdate(checkpoint.RunId, updated, current))
                return Task.FromResult(new CheckpointWriteResult(true, updated));
        }

        return Task.FromResult(new CheckpointWriteResult(false, null));
    }

    internal void Inject(DurableAgentCheckpoint checkpoint) => _checkpoints[checkpoint.RunId] = checkpoint;
}
