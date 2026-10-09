using System.ComponentModel;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The observer as subscribable resources (typed JSON, the same contracts as the tools). Subscribe through
/// subscriptions/listen and the server pushes notifications/resources/updated when they change (ObserveHub), so an app
/// or agent reacts to what the game and the server did instead of polling.
/// </summary>
[McpServerResourceType]
public static class ObserveResources
{
    [McpServerResource(UriTemplate = "exile://observe/{game}/events", Name = "observe-events", Title = "Observer events", MimeType = "application/json", IconSource = IconSet.TimelineLight)]
    [Description("The latest observer events (ObserveEventsResult: up to 100, newest last; layer, ui, area, level, entity). Subscribable: updated when new events arrive.")]
    public static async Task<string> Events(BridgeRegistry bridges, string game, CancellationToken ct)
    {
        var (_, r) = await bridges.CallAsync(game, "observe.events", new JObject { ["since"] = 0, ["limit"] = 500 }, ct);
        var result = Dto.From<ObserveEventsResult>(r);
        result.Events = result.Events.Select(e => e.Normalized()).TakeLast(100).ToList();
        return JsonSerializer.Serialize(result, Dto.Options);
    }

    [McpServerResource(UriTemplate = "exile://observe/{game}/layers", Name = "observe-layers", Title = "Observer layers", MimeType = "application/json", IconSource = IconSet.TimelineLight)]
    [Description("The observer's layer specs and their status (LayersResult). Subscribable: updated when any layer logs an event.")]
    public static async Task<string> Layers(BridgeRegistry bridges, string game, CancellationToken ct)
    {
        var (_, r) = await bridges.CallAsync(game, "observe.layers", new JObject(), ct);
        return JsonSerializer.Serialize(Dto.From<LayersResult>(r), Dto.Options);
    }

    [McpServerResource(UriTemplate = "exile://observe/{game}/layers/{layer}", Name = "observe-layer-map", Title = "Observer layer map", MimeType = "application/json", IconSource = IconSet.TimelineLight)]
    [Description("Every unit of one layer that changed, with counts and timing (LayerMapResult). Subscribable: updated when that layer logs an event.")]
    public static async Task<string> LayerMap(BridgeRegistry bridges, string game, string layer, CancellationToken ct)
    {
        var (_, r) = await bridges.CallAsync(game, "observe.layer_map", new JObject { ["layer"] = layer, ["limit"] = 200 }, ct);
        return JsonSerializer.Serialize(Dto.From<LayerMapResult>(r), Dto.Options);
    }
}
