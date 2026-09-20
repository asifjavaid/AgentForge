namespace AgentForge.Application.Requirements;

public static class RequirementAnalysisSchema
{
    public const string Name = "requirement_analysis";

    public const string Json = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "functionalRequirements": {
              "type": "array",
              "items": { "type": "string" }
            },
            "nonFunctionalRequirements": {
              "type": "array",
              "items": { "type": "string" }
            },
            "technicalConsiderations": {
              "type": "array",
              "items": { "type": "string" }
            },
            "securityRisks": {
              "type": "array",
              "items": { "type": "string" }
            },
            "questions": {
              "type": "array",
              "items": { "type": "string" }
            },
            "complexity": {
              "type": "string",
              "enum": ["Low", "Medium", "High"]
            }
          },
          "required": [
            "summary",
            "functionalRequirements",
            "nonFunctionalRequirements",
            "technicalConsiderations",
            "securityRisks",
            "questions",
            "complexity"
          ],
          "additionalProperties": false
        }
        """;
}
