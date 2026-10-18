using System.Collections.Concurrent;
using AgentForge.Application.Agents;
using AgentForge.Application.MultiAgent;

namespace AgentForge.Infrastructure.MultiAgent;

public sealed class InMemoryWorkflowCheckpointStore : IWorkflowCheckpointStore
{
    private readonly ConcurrentDictionary<string, MultiAgentWorkflowCheckpoint> _items = new(StringComparer.Ordinal);

    public Task<MultiAgentWorkflowCheckpoint?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryGetValue(workflowId, out var checkpoint);
        return Task.FromResult(checkpoint);
    }

    public Task<MultiAgentWorkflowCheckpoint> CreateAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = checkpoint with { Version = "1" };
        if (!_items.TryAdd(checkpoint.WorkflowId, created))
            throw new AgentOperationException(AgentFailureKind.Conflict, "The workflow already exists.");
        return Task.FromResult(created);
    }

    public Task<WorkflowCheckpointWriteResult> TryUpdateAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_items.TryGetValue(checkpoint.WorkflowId, out var current))
        {
            if (current.Version != expectedVersion || !long.TryParse(current.Version, out var version))
                return Task.FromResult(new WorkflowCheckpointWriteResult(false, current));
            var updated = checkpoint with { Version = (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (_items.TryUpdate(checkpoint.WorkflowId, updated, current))
                return Task.FromResult(new WorkflowCheckpointWriteResult(true, updated));
        }
        return Task.FromResult(new WorkflowCheckpointWriteResult(false, null));
    }

    internal void Inject(MultiAgentWorkflowCheckpoint checkpoint) => _items[checkpoint.WorkflowId] = checkpoint;
}
