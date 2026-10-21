using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Application.Agents;
using AgentForge.Application.HumanApproval;

namespace AgentForge.Infrastructure.HumanApproval;

public sealed class FileApprovalStore : IApprovalStore
{
    private readonly string _root;
    private readonly HumanApprovalOptions _options;
    private readonly ApprovalRecordValidator _validator;

    public FileApprovalStore(HumanApprovalOptions options, ApprovalRecordValidator validator)
    {
        _options = options;
        _validator = validator;
        _root = Path.GetFullPath(options.FilePath);
        Directory.CreateDirectory(_root);
    }

    public async Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        ValidateId(approvalId);
        await using var handle = await AcquireLockAsync(approvalId, cancellationToken);
        var path = Path.Combine(_root, approvalId + ".json");
        return File.Exists(path) ? await ReadAsync(path, approvalId, cancellationToken) : null;
    }

    public async Task<IReadOnlyList<ApprovalRecord>> ListAsync(ApprovalStatus? status, string tenantId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ApprovalRecord>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            var record = await GetAsync(id, cancellationToken);
            if (record is not null && record.TenantId == tenantId && (status is null || record.Status == status))
                results.Add(record);
        }
        return results.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<ApprovalRecord> CreateAsync(ApprovalRecord record,
        CancellationToken cancellationToken = default)
    {
        ValidateId(record.ApprovalId);
        _validator.Validate(record);
        await using var handle = await AcquireLockAsync(record.ApprovalId, cancellationToken);
        var path = Path.Combine(_root, record.ApprovalId + ".json");
        if (File.Exists(path)) throw new AgentOperationException(AgentFailureKind.Conflict, "The approval already exists.");
        var created = record with { Version = "1" };
        await WriteAsync(path, created, cancellationToken);
        return created;
    }

    public async Task<ApprovalWriteResult> TryUpdateAsync(ApprovalRecord record, string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(record.ApprovalId);
        await using var handle = await AcquireLockAsync(record.ApprovalId, cancellationToken);
        var path = Path.Combine(_root, record.ApprovalId + ".json");
        if (!File.Exists(path)) return new ApprovalWriteResult(false, null);
        var current = await ReadAsync(path, record.ApprovalId, cancellationToken);
        if (current.Version != expectedVersion || !long.TryParse(current.Version, out var version))
            return new ApprovalWriteResult(false, current);
        _validator.ValidateUpdate(current, record);
        var updated = record with { Version = (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await WriteAsync(path, updated, cancellationToken);
        return new ApprovalWriteResult(true, updated);
    }

    private async Task<ApprovalRecord> ReadAsync(string path, string id, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length > _options.MaxRecordBytes) throw new JsonException("Approval record exceeds its bound.");
            var envelope = JsonSerializer.Deserialize<ApprovalJson.Envelope>(bytes, ApprovalJson.Options) ??
                throw new JsonException("Approval envelope was empty.");
            var payload = Convert.FromBase64String(envelope.Payload);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(envelope.Checksum),
                Convert.FromHexString(ApprovalJson.Checksum(payload))))
                throw new JsonException("Approval checksum mismatch.");
            var record = ApprovalJson.Deserialize(payload);
            if (record.ApprovalId != id) throw new JsonException("Approval identity mismatch.");
            _validator.Validate(record);
            return record;
        }
        catch (AgentOperationException) { throw; }
        catch (Exception exception) when (exception is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new AgentOperationException(AgentFailureKind.CorruptState, "The approval record is invalid.", exception);
        }
    }

    private async Task WriteAsync(string path, ApprovalRecord record, CancellationToken cancellationToken)
    {
        var payload = ApprovalJson.Serialize(record);
        var envelope = new ApprovalJson.Envelope(ApprovalJson.Checksum(payload), Convert.ToBase64String(payload));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ApprovalJson.Options);
        if (bytes.Length > _options.MaxRecordBytes)
            throw new AgentOperationException(AgentFailureKind.Storage, "The approval record exceeds its configured bound.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<FileStream> AcquireLockAsync(string id, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, id + ".lock");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(25, cancellationToken); }
        }
        throw new AgentOperationException(AgentFailureKind.Conflict, "The approval record is busy.");
    }

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The approval identifier is invalid.");
    }
}
