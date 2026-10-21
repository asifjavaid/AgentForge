namespace AgentForge.Application.HumanApproval;

public static class ActionProposalSchema
{
    public const string Name = "agentforge_action_proposal";

    public const string Json = """
        {
          "type": "object",
          "properties": {
            "workflowId": { "type": "string", "pattern": "^[a-f0-9]{32}$" },
            "taskId": { "type": "string", "minLength": 1, "maxLength": 80 },
            "actionType": { "type": "string", "enum": ["UpdateSandboxFeatureFlag"] },
            "targetId": { "type": "string", "enum": ["sandbox-feature-flags"] },
            "parameters": {
              "type": "object",
              "properties": {
                "flagName": { "type": "string", "enum": ["enable-demo-feature"] },
                "enabled": { "type": "boolean" }
              },
              "required": ["flagName", "enabled"],
              "additionalProperties": false
            },
            "justification": { "type": "string", "minLength": 1, "maxLength": 2000 },
            "evidenceIds": {
              "type": "array",
              "minItems": 1,
              "maxItems": 32,
              "uniqueItems": true,
              "items": { "type": "string", "maxLength": 300 }
            }
          },
          "required": ["workflowId", "taskId", "actionType", "targetId", "parameters", "justification", "evidenceIds"],
          "additionalProperties": false
        }
        """;
}
