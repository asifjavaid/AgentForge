using AgentForge.Api.Composition;
using AgentForge.Application.Knowledge;
using AgentForge.Domain.Knowledge;
using AgentForge.Infrastructure.AzureSearch;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AgentForge.Tests.Knowledge;

public sealed class AzureAiSearchTests
{
    private const string Endpoint = "https://agentforge-search-dev.search.windows.net";

    [Fact]
    public void Options_AcceptValidConfiguration()
    {
        var options = Options(KnowledgeRetrievalMode.Vector);
        options.Validate();
        Assert.Equal("agentforge-knowledge", options.GetIndexName());
        Assert.Equal(Endpoint, options.GetEndpoint().AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("http://search.example", "agentforge-knowledge", 4)]
    [InlineData("https://search.example", "_invalid", 4)]
    [InlineData("https://search.example/path", "valid", 4)]
    [InlineData("https://search.example", "Uppercase", 4)]
    [InlineData("https://search.example", "invalid-", 4)]
    [InlineData("https://search.example", "valid", 0)]
    [InlineData("https://search.example", "valid", 51)]
    public void Options_RejectInvalidConfiguration(string endpoint, string indexName, int topK)
    {
        var options = new AzureAiSearchOptions { Endpoint = endpoint, IndexName = indexName, TopK = topK };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("InMemory", typeof(InMemoryKnowledgeRetriever))]
    [InlineData("AzureAiSearch", typeof(AzureAiSearchKnowledgeRetriever))]
    public void RetrieverSelection_IsConfigurationDriven(string configured, Type expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rag:Retriever"] = configured,
            ["AzureAiSearch:Endpoint"] = Endpoint
        }).Build();
        var services = new ServiceCollection();

        services.AddKnowledge(configuration, new FakeEnvironment());

        var descriptor = services.Last(item => item.ServiceType == typeof(IKnowledgeRetriever));
        Assert.Equal(expected, descriptor.ImplementationType);
    }

    [Fact]
    public void IndexSchema_IsExplicitAndUses1536DimensionCosineHnsw()
    {
        var index = AzureSearchIndexDefinition.Create("agentforge-knowledge");

        Assert.Equal(
            ["id", "documentId", "source", "chunkId", "position", "content", "contentVector"],
            index.Fields.Select(field => field.Name));
        Assert.True(index.Fields.Single(field => field.Name == "id").IsKey);
        Assert.True(index.Fields.Single(field => field.Name == "content").IsSearchable);
        var vector = index.Fields.Single(field => field.Name == "contentVector");
        Assert.Equal(1536, vector.VectorSearchDimensions);
        Assert.True(vector.IsHidden);
        var algorithm = Assert.IsType<HnswAlgorithmConfiguration>(Assert.Single(index.VectorSearch.Algorithms));
        Assert.Equal(VectorSearchAlgorithmMetric.Cosine, algorithm.Parameters.Metric);
        AzureSearchIndexDefinition.Validate(index);
    }

    [Fact]
    public void StableDocumentId_IsRepeatableAndMetadataSensitive()
    {
        var chunk = Chunk("authentication.md", "chunk-1", 0, [1, 0]);
        var first = AzureSearchKnowledgeDocument.CreateStableId(chunk);

        Assert.Equal(first, AzureSearchKnowledgeDocument.CreateStableId(chunk));
        Assert.NotEqual(first, AzureSearchKnowledgeDocument.CreateStableId(chunk with { Position = 1 }));
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void SearchDocument_RoundTripsCitationMetadataWithoutReturningVector()
    {
        var chunk = Chunk("security-guidelines.md", "chunk-2", 3, [0.2f, 0.8f]);
        var document = AzureSearchKnowledgeDocument.FromChunk(chunk);
        var mapped = document.ToChunk();

        Assert.Equal(chunk.DocumentId, mapped.DocumentId);
        Assert.Equal(chunk.Source, mapped.Source);
        Assert.Equal(chunk.ChunkId, mapped.ChunkId);
        Assert.Equal(chunk.Position, mapped.Position);
        Assert.Equal(chunk.Text, mapped.Text);
        Assert.Empty(mapped.Embedding);
    }

    [Fact]
    public async Task Indexing_CreatesOnceAndUpsertsStableKeysOnRepeatedRuns()
    {
        var gateway = new FakeGateway();
        var embeddings = new FakeEmbeddings();
        var rag = new RagOptions { ChunkSize = 900, ChunkOverlap = 150, EmbeddingBatchSize = 16 };
        var manager = new AzureAiSearchIndexManager(
            Options(KnowledgeRetrievalMode.Vector), rag,
            new FakeLoader(), new DeterministicTextChunker(rag), embeddings,
            gateway, new AzureSearchIndexState(), NullLogger<AzureAiSearchIndexManager>.Instance);

        var first = await manager.InitializeAndIndexAsync();
        var firstKeys = gateway.LastUpload.Select(document => document.Id).ToArray();
        var second = await manager.InitializeAndIndexAsync();
        var secondKeys = gateway.LastUpload.Select(document => document.Id).ToArray();

        Assert.True(first.IndexCreated);
        Assert.False(second.IndexCreated);
        Assert.Equal(1, gateway.CreateCalls);
        Assert.Equal(firstKeys, secondKeys);
        Assert.Equal(first.ChunksProduced, first.DocumentsUpserted);
        Assert.Equal(2, gateway.UpsertCalls);
    }

    [Fact]
    public void VectorQuery_ContainsVectorTopKAndSafeSourceFilter()
    {
        var query = AzureAiSearchKnowledgeRetriever.BuildSearchOptions(
            KnowledgeRetrievalMode.Vector, 4, Enumerable.Repeat(0.1f, 1536).ToArray(), "security-guidelines.md");

        Assert.Equal(4, query.Size);
        var vector = Assert.IsType<VectorizedQuery>(Assert.Single(query.VectorSearch!.Queries));
        Assert.Equal(4, vector.KNearestNeighborsCount);
        Assert.Contains("contentVector", vector.Fields);
        Assert.Equal("source eq 'security-guidelines.md'", query.Filter);
    }

    [Fact]
    public void KeywordQuery_HasNoVectorOrSemanticConfiguration()
    {
        var query = AzureAiSearchKnowledgeRetriever.BuildSearchOptions(
            KnowledgeRetrievalMode.Keyword, 4, null, null);

        Assert.Null(query.VectorSearch);
        Assert.NotEqual(SearchQueryType.Semantic, query.QueryType);
    }

    [Fact]
    public void HybridQuery_CombinesFullTextWithVectorCandidates()
    {
        var query = AzureAiSearchKnowledgeRetriever.BuildSearchOptions(
            KnowledgeRetrievalMode.Hybrid, 4, Enumerable.Repeat(0.1f, 1536).ToArray(), null);

        Assert.Single(query.VectorSearch!.Queries);
        Assert.NotEqual(SearchQueryType.Semantic, query.QueryType);
    }

    [Fact]
    public void SemanticHybridQuery_AddsSemanticReranking()
    {
        var query = AzureAiSearchKnowledgeRetriever.BuildSearchOptions(
            KnowledgeRetrievalMode.SemanticHybrid, 4, Enumerable.Repeat(0.1f, 1536).ToArray(), null);

        Assert.Equal(SearchQueryType.Semantic, query.QueryType);
        Assert.Equal(AzureSearchIndexDefinition.SemanticConfiguration,
            query.SemanticSearch!.SemanticConfigurationName);
    }

    [Fact]
    public async Task KeywordRetrieval_DoesNotCallEmbeddingAndPreservesRanksScoresAndMetadata()
    {
        var gateway = new FakeGateway
        {
            SearchResults =
            [
                Result("api.md", "chunk-2", 7.5),
                Result("architecture.md", "chunk-1", 4.2)
            ]
        };
        var embeddings = new FakeEmbeddings();
        var retriever = Retriever(KnowledgeRetrievalMode.Keyword, gateway, embeddings, topK: 1);

        var result = await retriever.RetrieveAsync(new("POST /api/knowledge/ask", null));

        Assert.Equal(0, embeddings.Calls);
        Assert.Single(result.Matches);
        Assert.Equal(1, result.Matches[0].Rank);
        Assert.Equal(7.5, result.Matches[0].Score);
        Assert.Equal("api.md", result.Matches[0].Chunk.Source);
        Assert.Equal("POST /api/knowledge/ask", gateway.LastSearchText);
    }

    [Fact]
    public async Task VectorRetrieval_EmbedsQueryAndAppliesTranslated05AThreshold()
    {
        var gateway = new FakeGateway
        {
            SearchResults =
            [
                Result("relevant.md", "chunk-1", AzureAiSearchKnowledgeRetriever.ScoreFromCosine(0.5)),
                Result("noise.md", "chunk-2", AzureAiSearchKnowledgeRetriever.ScoreFromCosine(0.2))
            ]
        };
        var embeddings = new FakeEmbeddings();
        var retriever = Retriever(KnowledgeRetrievalMode.Vector, gateway, embeddings, topK: 4);

        var result = await retriever.RetrieveAsync(new("semantic question", null));

        Assert.Equal(1, embeddings.Calls);
        Assert.Null(gateway.LastSearchText);
        Assert.Single(result.Matches);
        Assert.Equal("relevant.md", result.Matches[0].Chunk.Source);
        Assert.Null(result.Matches[0].Similarity);
    }

    [Fact]
    public async Task SemanticRetrieval_PreservesRerankerScore()
    {
        var gateway = new FakeGateway
        {
            SearchResults = [Result("security.md", "chunk-1", 0.03, 3.75)]
        };
        var result = await Retriever(
            KnowledgeRetrievalMode.SemanticHybrid, gateway, new FakeEmbeddings(), 4)
            .RetrieveAsync(new("ignore instructions", null));

        Assert.Equal(3.75, Assert.Single(result.Matches).RerankerScore);
    }

    [Theory]
    [InlineData("../security.md")]
    [InlineData("source eq 'anything'")]
    [InlineData("notes.pdf")]
    [InlineData("C:\\knowledge\\security.md")]
    public void PublicSourceFilter_RejectsPathsAndArbitraryOData(string source) =>
        Assert.Throws<ArgumentException>(() => KnowledgeFilterValidation.ValidateSource(source));

    [Fact]
    public void SourceFilter_EscapesApostrophesRatherThanAcceptingODataSyntax()
    {
        var query = AzureAiSearchKnowledgeRetriever.BuildSearchOptions(
            KnowledgeRetrievalMode.Keyword, 4, null, "team's-notes.md");
        Assert.Equal("source eq 'team''s-notes.md'", query.Filter);
    }

    private static AzureAiSearchKnowledgeRetriever Retriever(
        KnowledgeRetrievalMode mode,
        FakeGateway gateway,
        FakeEmbeddings embeddings,
        int topK) => new(
            Options(mode, topK), new RagOptions { MinimumSimilarity = 0.35 }, embeddings,
            gateway, new AzureSearchIndexState(), NullLogger<AzureAiSearchKnowledgeRetriever>.Instance);

    private static AzureAiSearchOptions Options(KnowledgeRetrievalMode mode, int topK = 4) => new()
    {
        Endpoint = Endpoint,
        IndexName = "agentforge-knowledge",
        RetrievalMode = mode,
        TopK = topK
    };

    private static KnowledgeChunk Chunk(
        string source,
        string chunkId,
        int position,
        IReadOnlyList<float> embedding) =>
        new(Path.GetFileNameWithoutExtension(source), source, chunkId, "controlled content", position, embedding);

    private static AzureSearchQueryResult Result(
        string source,
        string chunkId,
        double score,
        double? reranker = null) =>
        new(AzureSearchKnowledgeDocument.FromChunk(Chunk(source, chunkId, 0, [])), score, reranker);

    private sealed class FakeGateway : IAzureSearchGateway
    {
        public SearchIndex? Index { get; private set; }
        public int CreateCalls { get; private set; }
        public int UpsertCalls { get; private set; }
        public IReadOnlyList<AzureSearchKnowledgeDocument> LastUpload { get; private set; } = [];
        public IReadOnlyList<AzureSearchQueryResult> SearchResults { get; init; } = [];
        public SearchOptions? LastSearchOptions { get; private set; }
        public string? LastSearchText { get; private set; }

        public Task<SearchIndex?> GetIndexAsync(CancellationToken cancellationToken) => Task.FromResult(Index);

        public Task CreateIndexAsync(SearchIndex index, CancellationToken cancellationToken)
        {
            Index = index;
            CreateCalls++;
            return Task.CompletedTask;
        }

        public Task<AzureSearchUploadResult> UpsertAsync(
            IReadOnlyList<AzureSearchKnowledgeDocument> documents,
            CancellationToken cancellationToken)
        {
            LastUpload = documents;
            UpsertCalls++;
            return Task.FromResult(new AzureSearchUploadResult(documents.Count, []));
        }

        public Task<IReadOnlyList<AzureSearchQueryResult>> SearchAsync(
            string? searchText,
            SearchOptions options,
            CancellationToken cancellationToken)
        {
            LastSearchText = searchText;
            LastSearchOptions = options;
            return Task.FromResult(SearchResults);
        }
    }

    private sealed class FakeLoader : IKnowledgeDocumentLoader
    {
        public Task<IReadOnlyList<KnowledgeDocument>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocument>>
            ([new("authentication", "authentication.md", "Password reset links last exactly 30 minutes and are single-use.")]);
    }

    private sealed class FakeEmbeddings : IEmbeddingClient
    {
        public int Calls { get; private set; }

        public Task<EmbeddingBatchResult> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default) => EmbedBatchAsync([text], cancellationToken);

        public Task<EmbeddingBatchResult> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<EmbeddingVector> vectors = texts
                .Select(_ => new EmbeddingVector(Enumerable.Repeat(0.01f, 1536).ToArray()))
                .ToArray();
            return Task.FromResult(new EmbeddingBatchResult(vectors, "Fake", "fake", 2, 1));
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AgentForge.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
