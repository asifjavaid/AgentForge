using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Agents;
using AgentForge.Application.HumanApproval;

namespace AgentForge.Infrastructure.HumanApproval;

internal static class ApprovalJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal static byte[] Serialize(ApprovalRecord record) => JsonSerializer.SerializeToUtf8Bytes(record, Options);

    internal static ApprovalRecord Deserialize(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ApprovalRecord>(payload, Options) ??
                throw new JsonException("Approval payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentOperationException(AgentFailureKind.CorruptState,
                "The approval payload is malformed.", exception);
        }
    }

    internal static string Checksum(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));

    internal sealed record Envelope(string Checksum, string Payload);
}
