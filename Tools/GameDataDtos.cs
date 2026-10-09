using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of game_data and find_in_game_data: the bridge's data.* replies, passed through.

/// <summary>One data table row: index, decoded text fields and foreign keys ("+slot:..."), first int32s and hex.</summary>
public sealed class GameDataRow
{
    /// <summary>Row index: usually the id the game stores.</summary>
    public int Index { get; set; }
    public List<string> Strings { get; set; } = [];
    /// <summary>Slots pointing at a row of another table: "+slot:File[row] \"first text\"".</summary>
    public List<string> Refs { get; set; } = [];
    /// <summary>"+0:n +4:n ..." over the first 64 bytes.</summary>
    public string? Ints { get; set; }
    public string? Hex { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// game_data. Without file: the data files whose name contains filter (filter, total, files). With file: its rows
/// (file, count, recordLength, offset, rows, note, find, truncated).
/// </summary>
public sealed class GameDataResult
{
    public string? Filter { get; set; }
    public int? Total { get; set; }
    public List<string>? Files { get; set; }
    public string? File { get; set; }
    /// <summary>Rows in the table.</summary>
    public int? Count { get; set; }
    public int? RecordLength { get; set; }
    public int? Offset { get; set; }
    public string? Find { get; set; }
    public List<GameDataRow>? Rows { get; set; }
    public string? Truncated { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GameDataMatch
{
    /// <summary>The value looked for (as the bridge parsed it).</summary>
    public JsonElement Value { get; set; }
    public int Row { get; set; }
    /// <summary>The row's first text field.</summary>
    public string? Label { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A table column holding several of the values.</summary>
public sealed class GameDataColumn
{
    public string File { get; set; } = "";
    /// <summary>Byte offset of the column within a row.</summary>
    public int Offset { get; set; }
    public int Size { get; set; }
    /// <summary>Distinct values found in the column.</summary>
    public int Found { get; set; }
    public int RowsMatched { get; set; }
    public int RowCount { get; set; }
    public int RecordLength { get; set; }
    public List<GameDataMatch> Matches { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>find_in_game_data: the columns that hold the values, best first.</summary>
public sealed class FindInGameDataResult
{
    public string Id { get; set; } = "";
    /// <summary>done (other states come back as the bridge's reply).</summary>
    public string Status { get; set; } = "";
    /// <summary>How many values were looked for.</summary>
    public int Values { get; set; }
    public int Size { get; set; }
    public int MinHits { get; set; }
    public int Tables { get; set; }
    public long Ms { get; set; }
    public List<GameDataColumn> Columns { get; set; } = [];
    /// <summary>Values not in the top column.</summary>
    public List<JsonElement> Missing { get; set; } = [];
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
