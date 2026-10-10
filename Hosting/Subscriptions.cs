using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ExileApiMcp.Bridge;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Hosting;

/// <summary>
/// subscriptions/listen (2026-07-28, SEP-2575), owned by us: in stateless HTTP there is no session channel, so pushes
/// travel on the listen request's own response stream for as long as the client keeps it open. We honour resource
/// subscriptions to the URI spaces of every registered IResourceHub
/// (exile://observe/..., exile://perf/..., exile://attention/...) and nothing else (no list changes: our tool, resource and prompt lists
/// are static). Contract: one acknowledgement first, listing only what we honour; every notification
/// tagged with the listen request id under _meta; clean up when the request is cancelled.
/// </summary>
public static class Subscriptions
{
    public static async ValueTask<EmptyResult> Listen(RequestContext<SubscriptionsListenRequestParams> ctx, CancellationToken ct)
    {
        var wanted = ctx.Params?.Notifications?.ResourceSubscriptions ?? [];
        var hubs = ctx.Services!.GetServices<IResourceHub>().ToList();
        var honoured = wanted.Where(u => hubs.Any(h => h.Handles(u))).Distinct(StringComparer.Ordinal).ToList();
        var id = JsonSerializer.SerializeToNode(ctx.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions);
        JsonObject Meta() => new() { [MetaKeys.SubscriptionId] = id?.DeepClone() };

        // The SDK's acknowledgement type has no _meta; send the same shape as JSON so it carries the subscription id too.
        var ack = JsonSerializer.SerializeToNode(new SubscriptionsAcknowledgedNotificationParams
        {
            Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = honoured },
        }, McpJsonUtilities.DefaultOptions)!.AsObject();
        ack["_meta"] = Meta();
        await ctx.Server.SendNotificationAsync(NotificationMethods.SubscriptionsAcknowledgedNotification, ack, McpJsonUtilities.DefaultOptions, ct);
        if (honoured.Count == 0) return new EmptyResult();

        // Coalesce: a burst of events becomes one update per URI; the client re-reads the resource.
        var updates = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var listening = hubs.Select(h => h.Listen(honoured.Where(h.Handles), uri => updates.Writer.TryWrite(uri))).ToList();
        try
        {
            while (await updates.Reader.WaitToReadAsync(ct))
            {
                var batch = new HashSet<string>(StringComparer.Ordinal);
                while (updates.Reader.TryRead(out var u)) batch.Add(u);
                foreach (var uri in batch)
                    await ctx.Server.SendNotificationAsync(NotificationMethods.ResourceUpdatedNotification,
                        new ResourceUpdatedNotificationParams { Uri = uri, Meta = Meta() }, McpJsonUtilities.DefaultOptions, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { foreach (var l in listening) l.Dispose(); }
        return new EmptyResult();
    }
}
