namespace ExileApiMcp.Bridge;

/// <summary>
/// A source of resources/updated pushes for subscriptions/listen (Hosting/Subscriptions.cs). Each hub owns a URI space,
/// says which URIs it serves, and signals the ones that changed while someone listens. Register a hub as a singleton
/// IResourceHub and the listen handler honours its URIs: no changes to the handler.
/// </summary>
public interface IResourceHub
{
    bool Handles(string uri);
    IDisposable Listen(IEnumerable<string> uris, Action<string> signal);
}
