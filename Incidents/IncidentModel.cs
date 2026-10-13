using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace ExileApiMcp.Incidents;

// The incident board's file (%LOCALAPPDATA%\Hexile\incidents.json), shared with tools\incident.ps1 and the Claude Code hook
// (tools\incident-hook.*). The contract is fixed: camelCase, ISO UTC times, every field below; unknown fields are kept
// ([JsonExtensionData]) so what the other writers add survives our writes, and we add our own (rev, ownerPid, cause...).

public sealed class IncidentFile
{
    /// <summary>+1 on every write.</summary>
    public int Rev { get; set; }
    public List<Incident> Incidents { get; set; } = [];
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

public sealed class Incident
{
    /// <summary>i-&lt;yyyyMMddHHmmss&gt;-&lt;4 hex&gt;</summary>
    public string Id { get; set; } = "";
    /// <summary>bridge-down | hud-restart | deploy | mcp-down | ports-reserved | outage | custom</summary>
    public string Kind { get; set; } = "custom";
    /// <summary>poe1 | poe2 | machine</summary>
    public string Scope { get; set; } = "machine";
    public string Title { get; set; } = "";
    /// <summary>What broke, named at the broken link.</summary>
    public string? Detail { get; set; }
    /// <summary>What to do / who must act; null if nothing.</summary>
    public string? Fix { get; set; }
    public string RaisedBy { get; set; } = "";
    public DateTime? RaisedAt { get; set; }
    public DateTime? Eta { get; set; }
    /// <summary>open | resolved</summary>
    public string State { get; set; } = "open";
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    /// <summary>Dedupe key: an open incident with the same key is updated, never duplicated.</summary>
    public string Key { get; set; } = "";
    public List<IncidentResponse> Responses { get; set; } = [];
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }

    [JsonIgnore] public bool IsOpen => State == "open";

    /// <summary>An extra (non-contract) field: rev, ownerPid, cause, plugin, lastSeenAt, resolution, updatedAt...</summary>
    public JToken? Get(string name) => Extra != null && Extra.TryGetValue(name, out var v) && v.Type != JTokenType.Null ? v : null;

    public void Set(string name, JToken? value)
    {
        Extra ??= new Dictionary<string, JToken>();
        if (value == null || value.Type == JTokenType.Null) Extra.Remove(name);
        else Extra[name] = value;
    }

    /// <summary>The file rev at this incident's last change (ours; incidents written by other tools may not have it).</summary>
    [JsonIgnore] public int? ChangedRev => Get("rev")?.Type == JTokenType.Integer ? Get("rev")!.Value<int>() : null;
    [JsonIgnore] public int? OwnerPid => Get("ownerPid")?.Type == JTokenType.Integer ? Get("ownerPid")!.Value<int>() : null;
}

public sealed class IncidentResponse
{
    public string Who { get; set; } = "";
    /// <summary>The answering session's id (SessionIdentity.ResponseId; the Claude Code hook writes the Claude session id).
    /// A session's new response replaces the one with the same id; entries without one (older writers) match by Who.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? SessionId { get; set; }
    /// <summary>ok | blocked | waiting</summary>
    public string Status { get; set; } = "ok";
    public string? Note { get; set; }
    public DateTime? Until { get; set; }
    public DateTime? At { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>One server's entry in server-heartbeats.json (keyed by pid).</summary>
public sealed class ServerBeat
{
    public string Label { get; set; } = "";
    /// <summary>mcp | mcp-http.</summary>
    public string? Kind { get; set; }
    /// <summary>The client session the server works for (SessionIdentity.ServerSession), for the deploy same-session rule.
    /// Absent from older servers' beats.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? Session { get; set; }
    public DateTime? At { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>What a raise asks for; the store fills id, times and the raiser.</summary>
public sealed class IncidentDraft
{
    public string Kind { get; set; } = "custom";
    public string Scope { get; set; } = "machine";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public string? Fix { get; set; }
    public DateTime? Eta { get; set; }
    public string? Key { get; set; }
    /// <summary>Extra fields to set (ownerPid, cause, plugin...).</summary>
    public Dictionary<string, JToken?> Fields { get; set; } = [];

    public static readonly string[] Kinds = ["bridge-down", "hud-restart", "deploy", "mcp-down", "ports-reserved", "outage", "custom"];
    public static readonly string[] Scopes = ["poe1", "poe2", "machine"];

    /// <summary>The dedupe key when none is given, as tools\incident-lib.ps1 makes it: kind:scope; custom: custom:&lt;title, lower case&gt;.</summary>
    public string EffectiveKey => !string.IsNullOrWhiteSpace(Key) ? Key.Trim()
        : Kind == "custom" ? $"custom:{Title.ToLowerInvariant()}"
        : $"{Kind}:{Scope}";
}

/// <summary>ISO 8601 UTC ("2026-10-11T12:00:00Z") on write; any ISO form (or a Date token) on read.</summary>
public sealed class IsoUtcConverter : JsonConverter
{
    public override bool CanConvert(Type t) => t == typeof(DateTime) || t == typeof(DateTime?);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        switch (reader.TokenType)
        {
            case JsonToken.Null: return null;
            case JsonToken.Date:
                return reader.Value switch
                {
                    DateTime d => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime(),
                    DateTimeOffset o => o.UtcDateTime,
                    _ => null,
                };
            case JsonToken.String:
                var s = (string?)reader.Value;
                if (string.IsNullOrWhiteSpace(s)) return null;
                return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p)
                    ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : null;   // unreadable time: treat as unknown, never fail the whole file
            default:
                reader.Skip();
                return null;
        }
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is DateTime d) writer.WriteValue(Iso(d));
        else writer.WriteNull();
    }

    public static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

public static class IncidentJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.None,   // times stay strings in Extra; typed fields go through IsoUtcConverter
        Converters = [new IsoUtcConverter()],
        Formatting = Formatting.Indented,
    };

    public static string Serialize(object o) => JsonConvert.SerializeObject(o, Settings);
    public static string SerializeCompact(object o) => JsonConvert.SerializeObject(o, Formatting.None, Settings);
}
