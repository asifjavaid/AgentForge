namespace AgentForge.Application.Repositories;

public static class RepositoryAnalysisSchema
{
    public const string Name = "repository_analysis";

    public const string Json = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "architectureComponents": { "type": "array", "items": { "type": "string" } },
            "importantFiles": { "type": "array", "items": { "type": "string" } },
            "findings": { "type": "array", "items": { "type": "string" } },
            "securityConcerns": { "type": "array", "items": { "type": "string" } },
            "recommendations": { "type": "array", "items": { "type": "string" } },
            "unansweredQuestions": { "type": "array", "items": { "type": "string" } }
          },
          "required": [
            "summary",
            "architectureComponents",
            "importantFiles",
            "findings",
            "securityConcerns",
            "recommendations",
            "unansweredQuestions"
          ],
          "additionalProperties": false
        }
        """;
}
