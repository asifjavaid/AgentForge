using Azure.Search.Documents.Indexes.Models;

namespace AgentForge.Infrastructure.AzureSearch;

internal static class AzureSearchIndexDefinition
{
    public const int VectorDimensions = 1536;
    public const string VectorField = "contentVector";
    public const string VectorAlgorithm = "knowledge-hnsw";
    public const string VectorProfile = "knowledge-vector-profile";
    public const string SemanticConfiguration = "knowledge-semantic";

    public static SearchIndex Create(string indexName)
    {
        var index = new SearchIndex(indexName)
        {
            VectorSearch = new VectorSearch(),
            SemanticSearch = new SemanticSearch()
        };

        index.Fields.Add(new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true });
        index.Fields.Add(new SimpleField("documentId", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true });
        index.Fields.Add(new SimpleField("source", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true });
        index.Fields.Add(new SimpleField("chunkId", SearchFieldDataType.String) { IsFilterable = true });
        index.Fields.Add(new SimpleField("position", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true });
        index.Fields.Add(new SearchableField("content"));
        index.Fields.Add(new VectorSearchField(VectorField, VectorDimensions, VectorProfile) { IsHidden = true });

        index.VectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration(VectorAlgorithm)
        {
            Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine }
        });
        index.VectorSearch.Profiles.Add(new VectorSearchProfile(VectorProfile, VectorAlgorithm));

        var prioritized = new SemanticPrioritizedFields();
        prioritized.ContentFields.Add(new SemanticField("content"));
        index.SemanticSearch.Configurations.Add(new SemanticConfiguration(SemanticConfiguration, prioritized));
        index.SemanticSearch.DefaultConfigurationName = SemanticConfiguration;
        return index;
    }

    public static void Validate(SearchIndex index)
    {
        var required = new[] { "id", "documentId", "source", "chunkId", "position", "content", VectorField };
        foreach (var name in required)
            if (index.Fields.All(field => !field.Name.Equals(name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Azure AI Search index is missing required field '{name}'.");

        var id = index.Fields.Single(field => field.Name == "id");
        var documentId = index.Fields.Single(field => field.Name == "documentId");
        var source = index.Fields.Single(field => field.Name == "source");
        var chunkId = index.Fields.Single(field => field.Name == "chunkId");
        var position = index.Fields.Single(field => field.Name == "position");
        var content = index.Fields.Single(field => field.Name == "content");
        var vector = index.Fields.Single(field => field.Name == VectorField);
        if (id.IsKey != true || id.IsFilterable != true || id.Type != SearchFieldDataType.String ||
            documentId.IsFilterable != true || source.IsFilterable != true || chunkId.IsFilterable != true ||
            position.IsFilterable != true || content.IsSearchable != true)
            throw new InvalidOperationException("Azure AI Search index field capabilities are incompatible.");
        if (vector.VectorSearchDimensions != VectorDimensions || vector.VectorSearchProfileName != VectorProfile)
            throw new InvalidOperationException("Azure AI Search vector field is incompatible with the configured embedding model.");
        var profile = index.VectorSearch?.Profiles.SingleOrDefault(item => item.Name == VectorProfile);
        var algorithm = index.VectorSearch?.Algorithms
            .OfType<HnswAlgorithmConfiguration>()
            .SingleOrDefault(item => item.Name == VectorAlgorithm);
        if (profile?.AlgorithmConfigurationName != VectorAlgorithm ||
            algorithm?.Parameters.Metric != VectorSearchAlgorithmMetric.Cosine)
            throw new InvalidOperationException("Azure AI Search HNSW cosine configuration is incompatible.");
    }
}
