using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

[McpServerToolType]
public sealed class EvalTools
{
    private readonly BridgeClient _client;

    public EvalTools(BridgeClient client)
    {
        _client = client;
    }

    [McpServerTool(Name = "eval_path"), Description("Evaluate a dotted-path expression to walk the ExileApi object graph via reflection. Start from 'GameController' and traverse properties, fields, and allowed methods. Examples: 'GameController.Player.GetComponent<Life>().CurHP', 'GameController.IngameState.Data.ServerData.PlayerStashTabs[0].Name'. Read-only, public members only, 150ms timeout.")]
    public async Task<string> EvalPath(
        [Description("Dotted-path expression starting with 'GameController'. Supports property access, GetComponent<T>(), array indexing [N], dictionary lookup [\"key\"], GetChildAtIndex(N), ToString().")] string expression)
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", new JObject { ["type"] = $"eval:{expression}" });
        return result.ToString();
    }

    [McpServerTool(Name = "describe_type"), Description("List all public properties and methods available at a given path in the ExileApi object graph. Use this to discover what data is available before using eval_path. Example: 'GameController.Player' shows all Entity properties.")]
    public async Task<string> DescribeType(
        [Description("Dotted-path expression to describe (e.g. 'GameController', 'GameController.Player', 'GameController.IngameState.Data')")] string expression)
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", new JObject { ["type"] = $"describe:{expression}" });
        return result.ToString();
    }
}
