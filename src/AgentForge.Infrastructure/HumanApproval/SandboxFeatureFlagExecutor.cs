using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Agents;
using AgentForge.Application.HumanApproval;

namespace AgentForge.Infrastructure.HumanApproval;

public sealed class SandboxFeatureFlagExecutor : IControlledActionExecutor, IActionTargetVersionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private readonly string _root;
    private readonly string _artifactPath;
    private readonly string _lockPath;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlySet<string> _allowedFlags;

    public SandboxFeatureFlagExecutor(HumanApprovalOptions options, TimeProvider timeProvider)
    {
        _root = Path.GetFullPath(options.SandboxRoot);
        _timeProvider = timeProvider;
        _allowedFlags = options.AllowedFlagNames.ToHashSet(StringComparer.Ordinal);
        Directory.CreateDirectory(_root);
        EnsureSafePath(_root, directory: true);
        _artifactPath = Path.Combine(_root, "feature-flags.json");
        _lockPath = Path.Combine(_root, "feature-flags.lock");
    }

    public async Task<long> GetVersionAsync(string targetId, CancellationToken cancellationToken = default)
    {
        ValidateTarget(targetId);
        await using var handle = await AcquireLockAsync(cancellationToken);
        return (await LoadAsync(cancellationToken)).Version;
    }

    public async Task<ActionExecutionReceipt> ExecuteAsync(ApprovedAction action,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(action.TargetId);
        if (action.ActionType != "UpdateSandboxFeatureFlag" ||
            !_allowedFlags.Contains(action.FlagName))
            throw new AgentOperationException(AgentFailureKind.InvalidRequest, "The approved action is invalid.");
        await using var handle = await AcquireLockAsync(cancellationToken);
        var artifact = await LoadAsync(cancellationToken);
        var applied = artifact.AppliedOperations.SingleOrDefault(item => item.OperationId == action.OperationId);
        if (applied is not null)
        {
            if (applied.ActionDigest != action.ActionDigest)
                return Receipt(action, ControlledActionOutcome.Uncertain, applied.PreviousVersion,
                    artifact.Version, "OperationDigestMismatch");
            return Receipt(action, ControlledActionOutcome.Reconciled, applied.PreviousVersion,
                applied.AppliedVersion, "AlreadyAppliedReconciled");
        }
        if (artifact.Version != action.ExpectedTargetVersion)
            return action.FencingToken > 1
                ? Receipt(action, ControlledActionOutcome.Uncertain, artifact.Version,
                    artifact.Version, "RecoveryMarkerMissing")
                : Receipt(action, ControlledActionOutcome.Rejected, artifact.Version,
                    artifact.Version, "TargetVersionConflict");
        var nextVersion = artifact.Version + 1;
        var flags = new Dictionary<string, bool>(artifact.Flags, StringComparer.Ordinal)
        {
            [action.FlagName] = action.Enabled
        };
        var marker = new AppliedOperation(action.OperationId, action.ActionDigest, artifact.Version,
            nextVersion, _timeProvider.GetUtcNow());
        var updated = new SandboxFeatureFlags(1, nextVersion, flags,
            artifact.AppliedOperations.Append(marker).TakeLast(100).ToArray());
        await WriteAtomicAsync(updated, cancellationToken);
        return Receipt(action, ControlledActionOutcome.Executed, artifact.Version, nextVersion, "AppliedAtomically");
    }

    private ActionExecutionReceipt Receipt(ApprovedAction action, ControlledActionOutcome outcome,
        long previous, long current, string reason) => new(action.OperationId, outcome, action.ActionType,
        action.TargetId, previous, current, _timeProvider.GetUtcNow(), reason);

    private async Task<SandboxFeatureFlags> LoadAsync(CancellationToken cancellationToken)
    {
        EnsureSafePath(_root, directory: true);
        if (!File.Exists(_artifactPath)) return new SandboxFeatureFlags(1, 0,
            new Dictionary<string, bool>(StringComparer.Ordinal), []);
        EnsureSafePath(_artifactPath, directory: false);
        try
        {
            var bytes = await File.ReadAllBytesAsync(_artifactPath, cancellationToken);
            if (bytes.Length > 131_072) throw new JsonException("Sandbox artifact is too large.");
            var result = JsonSerializer.Deserialize<SandboxFeatureFlags>(bytes, JsonOptions) ??
                throw new JsonException("Sandbox artifact is empty.");
            if (result.SchemaVersion != 1 || result.Version < 0 || result.Flags.Count > 100 ||
                result.AppliedOperations.Count > 100 ||
                result.AppliedOperations.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() != result.AppliedOperations.Count)
                throw new JsonException("Sandbox artifact invariants failed.");
            return result;
        }
        catch (JsonException exception)
        {
            throw new AgentOperationException(AgentFailureKind.CorruptState, "The sandbox artifact is invalid.", exception);
        }
    }

    private async Task WriteAtomicAsync(SandboxFeatureFlags artifact, CancellationToken cancellationToken)
    {
        EnsureSafePath(_root, directory: true);
        if (File.Exists(_artifactPath)) EnsureSafePath(_artifactPath, directory: false);
        var temporary = Path.Combine(_root, $"feature-flags.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions), cancellationToken);
            File.Move(temporary, _artifactPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        EnsureSafePath(_root, directory: true);
        if (File.Exists(_lockPath)) EnsureSafePath(_lockPath, directory: false);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(25, cancellationToken); }
        }
        throw new AgentOperationException(AgentFailureKind.Conflict, "The sandbox artifact is busy.");
    }

    private static void EnsureSafePath(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            directory && (attributes & FileAttributes.Directory) == 0 ||
            !directory && (attributes & FileAttributes.Directory) != 0)
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "The sandbox path is unsafe.");
        var ancestor = new DirectoryInfo(directory ? path : Path.GetDirectoryName(path)!);
        while (ancestor is not null)
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AgentOperationException(AgentFailureKind.AccessDenied,
                    "The sandbox path crosses a symbolic link or junction.");
            ancestor = ancestor.Parent;
        }
    }

    private static void ValidateTarget(string targetId)
    {
        if (targetId != "sandbox-feature-flags")
            throw new AgentOperationException(AgentFailureKind.AccessDenied, "The action target is not registered.");
    }

    private sealed record SandboxFeatureFlags(
        int SchemaVersion,
        long Version,
        IReadOnlyDictionary<string, bool> Flags,
        IReadOnlyList<AppliedOperation> AppliedOperations);

    private sealed record AppliedOperation(
        string OperationId,
        string ActionDigest,
        long PreviousVersion,
        long AppliedVersion,
        DateTimeOffset AppliedAt);
}
