using AgentForge.Application.Agents;
using AgentForge.Infrastructure.ToolCalling;

namespace AgentForge.Tests.MultiAgent;

public sealed class ChatToolCallingProtocolTests
{
    [Fact]
    public void NoToolRequestDoesNotSendParallelToolCallsOption()
    {
        var request = new ToolCallingRequest([], [], "response", MinimalSchema);

        var options = ChatToolCallingProtocol.CreateOptions(request);

        Assert.Null(options.AllowParallelToolCalls);
        Assert.Empty(options.Tools);
    }

    [Fact]
    public void FinalToolTurnKeepsDefinitionsAndDisablesToolChoice()
    {
        var request = new ToolCallingRequest([],
            [new AgentToolDefinition("read_file", "Read a file.", MinimalSchema)],
            "response", MinimalSchema, RequireFinalResponse: true);

        var options = ChatToolCallingProtocol.CreateOptions(request);

        Assert.False(options.AllowParallelToolCalls);
        Assert.Single(options.Tools);
        Assert.NotNull(options.ToolChoice);
    }

    private const string MinimalSchema = """
        { "type": "object", "properties": {}, "required": [], "additionalProperties": false }
        """;
}
