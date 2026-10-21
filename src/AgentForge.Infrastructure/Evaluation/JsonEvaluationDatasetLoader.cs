using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Application.Evaluation;
using AgentForge.Application.MultiAgent;

namespace AgentForge.Infrastructure.Evaluation;

public sealed class JsonEvaluationDatasetLoader : IEvaluationDatasetLoader
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        WriteIndented = true
    };

    public async Task<EvaluationDataset> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Dataset path is required.");
        try
        {
            await using var stream = File.OpenRead(Path.GetFullPath(path));
            var dataset = await JsonSerializer.DeserializeAsync<EvaluationDataset>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Evaluation dataset is empty.");
            Validate(dataset);
            return dataset;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Evaluation dataset JSON is invalid.", exception);
        }
    }

    internal static void Validate(EvaluationDataset dataset)
    {
        if (string.IsNullOrWhiteSpace(dataset.DatasetId) || dataset.Version < 1 || dataset.Cases.Count < 1)
            throw new InvalidDataException("Evaluation dataset identity or version is invalid.");
        if (dataset.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != dataset.Cases.Count)
            throw new InvalidDataException("Evaluation dataset contains duplicate case IDs.");
        foreach (var item in dataset.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 80 ||
                !item.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                throw new InvalidDataException("Evaluation case ID is invalid.");
            if (string.IsNullOrWhiteSpace(item.Objective) || string.IsNullOrWhiteSpace(item.Category) ||
                item.Tags.Count == 0 || item.Budget.MaxModelCalls < 0 || item.Budget.MaxToolCalls < 0 ||
                item.Budget.MaxTotalTokens < 0 || item.Budget.MaxRevisions < 0)
                throw new InvalidDataException($"Evaluation case '{item.Id}' is incomplete.");
            if (item.RequiredRoles.Distinct().Count() != item.RequiredRoles.Count ||
                item.RequiredRoles.Any(role => !Enum.IsDefined(role)))
                throw new InvalidDataException($"Evaluation case '{item.Id}' contains invalid roles.");
            if (item.ExpectedEvidence.Any(evidence => !IsEvidence(evidence)))
                throw new InvalidDataException($"Evaluation case '{item.Id}' contains invalid expected evidence.");
            if (item.Fixture.Citations.Any(citation => !IsEvidence(citation)) ||
                item.Fixture.RetrievedEvidence.Any(evidence => !IsEvidence(evidence)))
                throw new InvalidDataException($"Evaluation case '{item.Id}' contains invalid fixture evidence.");
        }
    }

    private static bool IsEvidence(string value) =>
        value.StartsWith("knowledge:", StringComparison.Ordinal) ||
        value.StartsWith("repo:", StringComparison.Ordinal) ||
        value.StartsWith("synthetic:", StringComparison.Ordinal);
}
