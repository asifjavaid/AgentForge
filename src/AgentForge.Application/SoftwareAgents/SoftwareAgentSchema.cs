namespace AgentForge.Application.SoftwareAgents;

public static class SoftwareAgentSchema
{
    public const string Name = "software_agent_answer";

    public const string Json = """
        {
          "type": "object",
          "properties": {
            "answer": { "type": "string" },
            "citations": {
              "type": "array",
              "items": { "type": "string" }
            },
            "insufficientEvidence": { "type": "boolean" }
          },
          "required": ["answer", "citations", "insufficientEvidence"],
          "additionalProperties": false
        }
        """;
}
