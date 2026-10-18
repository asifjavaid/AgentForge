using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Application.Agents;
using AgentForge.Application.MultiAgent;

namespace AgentForge.Infrastructure.MultiAgent;

public sealed class FileWorkflowCheckpointStore : IWorkflowCheckpointStore
{
    private readonly string _root;

    public FileWorkflowCheckpointStore(MultiAgentWorkflowOptions options)
    {
        _root = Path.GetFullPath(options.FilePath);
        Directory.CreateDirectory(_root);
    }

    public async Task<MultiAgentWorkflowCheckpoint?> GetAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(workflowId);
        await using var handle = await AcquireLockAsync(workflowId, cancellationToken);
        var path = GetPath(workflowId);
        return File.Exists(path) ? await ReadAsync(path, workflowId, cancellationToken) : null;
    }

    public async Task<MultiAgentWorkflowCheckpoint> CreateAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateId(checkpoint.WorkflowId);
        await using var handle = await AcquireLockAsync(checkpoint.WorkflowId, cancellationToken);
        var path = GetPath(checkpoint.WorkflowId);
        if (File.Exists(path)) throw new AgentOperationException(AgentFailureKind.Conflict, "The workflow already exists.");
        var created = checkpoint with { Version = "1" };
        await WriteAtomicAsync(path, created, cancellationToken);
        return created;
    }

    public async Task<WorkflowCheckpointWriteResult> TryUpdateAsync(
        MultiAgentWorkflowCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(checkpoint.WorkflowId);
        await using var handle = await AcquireLockAsync(checkpoint.WorkflowId, cancellationToken);
        var path = GetPath(checkpoint.WorkflowId);
        if (!File.Exists(path)) return new WorkflowCheckpointWriteResult(false, null);
        var current = await ReadAsync(path, checkpoint.WorkflowId, cancellationToken);
        if (current.Version != expectedVersion || !long.TryParse(current.Version, out var version))
            return new WorkflowCheckpointWriteResult(false, current);
        var updated = checkpoint with { Version = (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await WriteAtomicAsync(path, updated, cancellationToken);
        return new WorkflowCheckpointWriteResult(true, updated);
    }

    private static async Task<MultiAgentWorkflowCheckpoint> ReadAsync(
        string path,
        string workflowId,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, WorkflowCheckpointJson.Options)
                ?? throw new JsonException("Workflow envelope was empty.");
            var payload = Convert.FromBase64String(envelope.Payload);
            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(envelope.Checksum),
                Convert.FromHexString(WorkflowCheckpointJson.Checksum(payload))))
                throw new JsonException("Workflow checkpoint checksum did not match.");
            var checkpoint = WorkflowCheckpointJson.Deserialize(payload);
            if (checkpoint.WorkflowId != workflowId) throw new JsonException("Workflow checkpoint identity mismatch.");
            return checkpoint;
        }
        catch (AgentOperationException) { throw; }
        catch (Exception exception) when (exception is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new AgentOperationException(AgentFailureKind.CorruptState, "The workflow checkpoint file is invalid.", exception);
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        MultiAgentWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var payload = WorkflowCheckpointJson.Serialize(checkpoint);
        var envelope = new Envelope(WorkflowCheckpointJson.Checksum(payload), Convert.ToBase64String(payload));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, WorkflowCheckpointJson.Options);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<FileStream> AcquireLockAsync(string workflowId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, workflowId + ".lock");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(25, cancellationToken); }
        }
        throw new AgentOperationException(AgentFailureKind.Conflict, "The workflow checkpoint is busy.");
    }

    private string GetPath(string workflowId) => Path.Combine(_root, workflowId + ".json");

    private static void ValidateId(string workflowId)
    {
        if (!Guid.TryParseExact(workflowId, "N", out _))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The workflow identifier is invalid.");
    }

    private sealed record Envelope(string Checksum, string Payload);
}
