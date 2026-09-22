using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Agents;

namespace AgentForge.Infrastructure.Repositories;

internal static class RepositoryToolJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static T Deserialize<T>(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(argumentsJson, Options)
                ?? throw new JsonException("Arguments were empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentOperationException(
                AgentFailureKind.InvalidArguments,
                "Tool arguments did not match the registered schema.",
                exception);
        }
    }

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
