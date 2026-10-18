using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.DurableAgents;

namespace AgentForge.Infrastructure.DurableAgents;

internal static class CheckpointJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] Serialize(DurableAgentCheckpoint checkpoint) =>
        JsonSerializer.SerializeToUtf8Bytes(checkpoint, Options);

    internal static DurableAgentCheckpoint Deserialize(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<DurableAgentCheckpoint>(payload, Options)
                ?? throw new JsonException("Checkpoint payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentForge.Application.Agents.AgentOperationException(
                AgentForge.Application.Agents.AgentFailureKind.CorruptState,
                "The durable checkpoint payload is malformed.",
                exception);
        }
    }

    internal static string Checksum(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload));
}
