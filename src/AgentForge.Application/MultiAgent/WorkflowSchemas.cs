namespace AgentForge.Application.MultiAgent;

internal static class WorkflowSchemas
{
    internal const string PlanName = "agentforge_workflow_plan";
    internal const string TaskOutputName = "agentforge_specialist_output";

    internal const string PlanJson = """
        {
          "type": "object",
          "properties": {
            "objective": { "type": "string" },
            "tasks": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "taskId": { "type": "string" },
                  "role": { "type": "string", "enum": ["RequirementsAnalyst", "CodeAnalysis", "SolutionArchitect", "SecurityReviewer", "ReportSynthesis"] },
                  "objective": { "type": "string" },
                  "dependsOn": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["taskId", "role", "objective", "dependsOn"],
                "additionalProperties": false
              }
            }
          },
          "required": ["objective", "tasks"],
          "additionalProperties": false
        }
        """;

    private const string TaskOutputJson = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "findings": { "type": "array", "items": { "type": "string" } },
            "recommendations": { "type": "array", "items": { "type": "string" } },
            "risks": { "type": "array", "items": { "type": "string" } },
            "assumptions": { "type": "array", "items": { "type": "string" } },
            "evidence": { "type": "array", "items": { "type": "string" } },
            "insufficientEvidence": { "type": "boolean" },
            "reviewOutcome": { "type": "string", "enum": ["NotApplicable", "Approved", "ApprovedWithConcerns", "RevisionRequired", "InsufficientEvidence"] },
            "revisionTargetTaskId": { "type": ["string", "null"] },
            "revisionReason": { "type": ["string", "null"] },
            "reportMarkdown": { "type": ["string", "null"] }
          },
          "required": ["summary", "findings", "recommendations", "risks", "assumptions", "evidence", "insufficientEvidence", "reviewOutcome", "revisionTargetTaskId", "revisionReason", "reportMarkdown"],
          "additionalProperties": false
        }
        """;

    internal static string TaskOutputFor(AgentRole role)
    {
        var schema = TaskOutputJson;
        if (role == AgentRole.SecurityReviewer)
        {
            return schema
                .Replace(
                    "[\"NotApplicable\", \"Approved\", \"ApprovedWithConcerns\", \"RevisionRequired\", \"InsufficientEvidence\"]",
                    "[\"Approved\", \"ApprovedWithConcerns\", \"RevisionRequired\", \"InsufficientEvidence\"]",
                    StringComparison.Ordinal)
                .Replace("\"reportMarkdown\": { \"type\": [\"string\", \"null\"] }",
                    "\"reportMarkdown\": { \"type\": \"null\" }", StringComparison.Ordinal);
        }

        schema = schema
            .Replace(
                "[\"NotApplicable\", \"Approved\", \"ApprovedWithConcerns\", \"RevisionRequired\", \"InsufficientEvidence\"]",
                "[\"NotApplicable\"]",
                StringComparison.Ordinal)
            .Replace("\"revisionTargetTaskId\": { \"type\": [\"string\", \"null\"] }",
                "\"revisionTargetTaskId\": { \"type\": \"null\" }", StringComparison.Ordinal)
            .Replace("\"revisionReason\": { \"type\": [\"string\", \"null\"] }",
                "\"revisionReason\": { \"type\": \"null\" }", StringComparison.Ordinal);
        return role == AgentRole.ReportSynthesis
            ? schema.Replace("\"reportMarkdown\": { \"type\": [\"string\", \"null\"] }",
                "\"reportMarkdown\": { \"type\": \"string\" }", StringComparison.Ordinal)
            : schema.Replace("\"reportMarkdown\": { \"type\": [\"string\", \"null\"] }",
                "\"reportMarkdown\": { \"type\": \"null\" }", StringComparison.Ordinal);
    }
}
