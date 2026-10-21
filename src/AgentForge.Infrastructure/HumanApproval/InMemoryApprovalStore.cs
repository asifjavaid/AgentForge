using System.Collections.Concurrent;
using AgentForge.Application.Agents;
using AgentForge.Application.HumanApproval;

namespace AgentForge.Infrastructure.HumanApproval;

public sealed class InMemoryApprovalStore(ApprovalRecordValidator validator) : IApprovalStore
{
    private readonly ConcurrentDictionary<string, ApprovalRecord> _items = new(StringComparer.Ordinal);

    public Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryGetValue(approvalId, out var record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<ApprovalRecord>> ListAsync(ApprovalStatus? status, string tenantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ApprovalRecord> result = _items.Values
            .Where(item => item.TenantId == tenantId && (status is null || item.Status == status))
            .OrderByDescending(item => item.CreatedAt).ToArray();
        return Task.FromResult(result);
    }

    public Task<ApprovalRecord> CreateAsync(ApprovalRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        validator.Validate(record);
        var created = record with { Version = "1" };
        if (!_items.TryAdd(record.ApprovalId, created))
            throw new AgentOperationException(AgentFailureKind.Conflict, "The approval already exists.");
        return Task.FromResult(created);
    }

    public Task<ApprovalWriteResult> TryUpdateAsync(ApprovalRecord record, string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_items.TryGetValue(record.ApprovalId, out var current))
        {
            if (current.Version != expectedVersion || !long.TryParse(current.Version, out var version))
                return Task.FromResult(new ApprovalWriteResult(false, current));
            validator.ValidateUpdate(current, record);
            var updated = record with { Version = (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (_items.TryUpdate(record.ApprovalId, updated, current))
                return Task.FromResult(new ApprovalWriteResult(true, updated));
        }
        return Task.FromResult(new ApprovalWriteResult(false, null));
    }

    internal void Inject(ApprovalRecord record) => _items[record.ApprovalId] = record;
}
