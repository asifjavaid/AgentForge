using System.Text.Json;
using System.Security.Cryptography;
using AgentForge.Application.Agents;
using AgentForge.Application.DurableAgents;

namespace AgentForge.Infrastructure.DurableAgents;

public sealed class FileAgentCheckpointStore : IAgentCheckpointStore
{
    private readonly string _root;

    public FileAgentCheckpointStore(DurableAgentOptions options)
    {
        _root = Path.GetFullPath(options.FilePath);
        Directory.CreateDirectory(_root);
    }

    public async Task<DurableAgentCheckpoint?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        await using var handle = await AcquireLockAsync(runId, cancellationToken);
        var path = GetPath(runId);
        return File.Exists(path) ? await ReadAsync(path, runId, cancellationToken) : null;
    }

    public async Task<DurableAgentCheckpoint> CreateAsync(
        DurableAgentCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(checkpoint.RunId);
        await using var handle = await AcquireLockAsync(checkpoint.RunId, cancellationToken);
        var path = GetPath(checkpoint.RunId);
        if (File.Exists(path))
            throw new AgentOperationException(AgentFailureKind.Conflict, "The durable run already exists.");
        var created = checkpoint with { Version = "1" };
        await WriteAtomicAsync(path, created, cancellationToken);
        return created;
    }

    public async Task<CheckpointWriteResult> TryUpdateAsync(
        DurableAgentCheckpoint checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(checkpoint.RunId);
        await using var handle = await AcquireLockAsync(checkpoint.RunId, cancellationToken);
        var path = GetPath(checkpoint.RunId);
        if (!File.Exists(path))
            return new CheckpointWriteResult(false, null);
        var current = await ReadAsync(path, checkpoint.RunId, cancellationToken);
        if (!string.Equals(current.Version, expectedVersion, StringComparison.Ordinal) ||
            !long.TryParse(current.Version, out var version))
            return new CheckpointWriteResult(false, current);
        var updated = checkpoint with { Version = (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await WriteAtomicAsync(path, updated, cancellationToken);
        return new CheckpointWriteResult(true, updated);
    }

    private async Task<DurableAgentCheckpoint> ReadAsync(
        string path,
        string expectedRunId,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, CheckpointJson.Options)
                ?? throw new JsonException("Checkpoint envelope was empty.");
            var payload = Convert.FromBase64String(envelope.Payload);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(envelope.Checksum),
                    Convert.FromHexString(CheckpointJson.Checksum(payload))))
                throw new JsonException("Checkpoint checksum did not match.");
            var checkpoint = CheckpointJson.Deserialize(payload);
            if (!string.Equals(checkpoint.RunId, expectedRunId, StringComparison.Ordinal))
                throw new JsonException("Checkpoint identity did not match its file name.");
            return checkpoint;
        }
        catch (AgentOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new AgentOperationException(
                AgentFailureKind.CorruptState,
                "The durable checkpoint file is invalid or unavailable.",
                exception);
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        DurableAgentCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var payload = CheckpointJson.Serialize(checkpoint);
        var envelope = new Envelope(CheckpointJson.Checksum(payload), Convert.ToBase64String(payload));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, CheckpointJson.Options);
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

    private async Task<FileStream> AcquireLockAsync(string runId, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_root, runId + ".lock");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 199)
            {
                await Task.Delay(25, cancellationToken);
            }
        }

        throw new AgentOperationException(AgentFailureKind.Conflict, "The durable checkpoint is busy.");
    }

    private string GetPath(string runId) => Path.Combine(_root, runId + ".json");

    private static void ValidateRunId(string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The durable run identifier is invalid.");
    }

    private sealed record Envelope(string Checksum, string Payload);
}
