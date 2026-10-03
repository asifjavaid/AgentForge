using System.Text;
using AgentForge.Application.Knowledge;
using AgentForge.Domain.Knowledge;

namespace AgentForge.Infrastructure.Knowledge;

public sealed class MarkdownKnowledgeDocumentLoader(RagOptions options) : IKnowledgeDocumentLoader
{
    public async Task<IReadOnlyList<KnowledgeDocument>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.CorpusPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The configured knowledge corpus does not exist.");

        var documents = new List<KnowledgeDocument>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                     .Where(path =>
                         Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > options.MaxDocumentBytes)
                throw new InvalidDataException($"Knowledge document '{info.Name}' exceeds the configured size limit.");

            var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), cancellationToken);
            if (text.IndexOf('\0') >= 0) continue;
            documents.Add(new KnowledgeDocument(Path.GetFileNameWithoutExtension(info.Name), info.Name, text));
        }

        return documents;
    }
}
