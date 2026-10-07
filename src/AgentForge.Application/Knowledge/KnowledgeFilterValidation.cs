namespace AgentForge.Application.Knowledge;

public static class KnowledgeFilterValidation
{
    public static string? ValidateSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        source = source.Trim();
        if (source.Length > 128 ||
            source != Path.GetFileName(source) ||
            !(source.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
              source.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                "Source must be a Markdown or text filename without a path.",
                nameof(source));
        return source;
    }
}
