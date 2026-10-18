using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Agents;
using AgentForge.Application.MultiAgent;

namespace AgentForge.Infrastructure.MultiAgent;

internal static class WorkflowCheckpointJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] Serialize(MultiAgentWorkflowCheckpoint checkpoint) =>
        JsonSerializer.SerializeToUtf8Bytes(checkpoint, Options);

    internal static MultiAgentWorkflowCheckpoint Deserialize(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<MultiAgentWorkflowCheckpoint>(payload, Options)
                ?? throw new JsonException("Workflow checkpoint payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentOperationException(AgentFailureKind.CorruptState, "The workflow checkpoint payload is malformed.", exception);
        }
    }

    internal static string Checksum(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));
}
