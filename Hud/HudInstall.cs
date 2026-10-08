using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExileApiMcp.Hud;

/// <summary>
/// A HUD install on disk (PoE1 ExileApi or PoE2 ExileCore2), read straight from its folder: logs
/// and source plugins. Works with the game, the HUD and the bridge all closed, which is the point:
/// an agent that just edited a plugin can see whether the HUD compiled it.
/// </summary>
public sealed partial class HudInstall
{
    public string Game { get; }
    public string Root { get; }

    private HudInstall(string game, string root)
    {
        Game = game;
        Root = root;
    }

    public string LogsDir => Path.Combine(Root, "Logs");
    public string SourcePluginsDir => Path.Combine(Root, "Plugins", "Source");

    /// <summary>The HUD folder is the parent of its bridge folder (…\PoeHelper\claude-bridge).</summary>
    public static HudInstall? FromBridgeDir(string game, string bridgeDir)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(bridgeDir.TrimEnd('\\', '/')));
        if (root == null) return null;
        var detected = File.Exists(Path.Combine(root, "ExileCore2.dll")) ? "poe2"
            : File.Exists(Path.Combine(root, "ExileCore.dll")) ? "poe1" : null;
        if (detected == null) return null;
        return new HudInstall(game == "auto" ? detected : game, root);
    }

    // ── Logs ─────────────────────────────────────────────────────────

    public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);

    /// <summary>One HUD run: from its "Start" marker to "Close" (or now, while running).</summary>
    public sealed record Run(DateTimeOffset StartedAt, DateTimeOffset? ClosedAt, List<LogEntry> Entries);

    // 2026-10-08 03:49:24.168 -04:00 [ERR] message (continuation lines follow until the next stamp)
    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d) \[(VRB|DBG|INF|WRN|ERR|FTL)\] ?(.*)$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"=+ Start (?:hud|ExileCore2) at ")]
    private static partial Regex StartMarker();

    [GeneratedRegex(@"=+ Close (?:hud|ExileCore2) at ")]
    private static partial Regex CloseMarker();

    /// <summary>
    /// The latest HUD run, parsed from the newest log files. PoE1 writes Error/Warning/Info/Verbose
    /// files per day and PoE2 only Verbose; Verbose has every level, so it is the one read. A run
    /// can span midnight, so the two newest files are read.
    /// </summary>
    public Run? LatestRun()
    {
        if (!Directory.Exists(LogsDir)) return null;
        var files = new DirectoryInfo(LogsDir).GetFiles("Verbose*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(2).Reverse().ToList();
        if (files.Count == 0) return null;

        var entries = new List<LogEntry>();
        foreach (var f in files) entries.AddRange(Parse(ReadShared(f.FullName)));

        var start = entries.FindLastIndex(e => StartMarker().IsMatch(e.Message));
        var run = start >= 0 ? entries.GetRange(start, entries.Count - start) : entries;
        var close = run.FindIndex(e => CloseMarker().IsMatch(e.Message));
        return new Run(run.Count > 0 ? run[0].Time : DateTimeOffset.MinValue,
            close >= 0 ? run[close].Time : null, run);
    }

    private static IEnumerable<LogEntry> Parse(string text)
    {
        DateTimeOffset time = default;
        string? level = null;
        var msg = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = LogLine().Match(line);
            if (m.Success)
            {
                if (level != null) yield return new LogEntry(time, level, msg.ToString());
                time = DateTimeOffset.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
                level = m.Groups[2].Value;
                msg.Clear().Append(m.Groups[3].Value);
            }
            else if (level != null && line.Length > 0)
            {
                msg.Append('\n').Append(line);
            }
        }
        if (level != null) yield return new LogEntry(time, level, msg.ToString());
    }

    /// <summary>The HUD keeps its logs open; read without blocking its writer.</summary>
    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static int LevelRank(string level) => level switch
    {
        "VRB" => 0, "DBG" => 1, "INF" => 2, "WRN" => 3, "ERR" => 4, "FTL" => 5, _ => 2,
    };

    // ── Source plugins ───────────────────────────────────────────────

    public sealed record SourcePlugin(string Folder, string Path, string? ProjectName);

    public IEnumerable<SourcePlugin> SourcePlugins()
    {
        if (!Directory.Exists(SourcePluginsDir)) yield break;
        foreach (var dir in new DirectoryInfo(SourcePluginsDir).GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? proj = null;
            try { proj = dir.GetFiles("*.csproj").Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Name)).FirstOrDefault(); }
            catch (IOException) { } // broken junction
            catch (UnauthorizedAccessException) { }
            yield return new SourcePlugin(dir.Name, dir.FullName, proj);
        }
    }

    // ── Compaction ───────────────────────────────────────────────────

    [GeneratedRegex(@"^\s+at (System|Microsoft|Internal)\.")]
    private static partial Regex FrameworkFrame();

    /// <summary>
    /// Collapses runs of .NET runtime stack frames ("at System.IO...") into one line: in plugin
    /// exceptions they are most of the trace and none of the signal, and they cost agents tokens.
    /// </summary>
    public static string CompactStackTraces(string text)
    {
        if (!text.Contains("   at ", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder();
        var hidden = 0;
        foreach (var line in text.Split('\n'))
        {
            if (FrameworkFrame().IsMatch(line)) { hidden++; continue; }
            if (hidden > 0) { sb.Append($"   ... {hidden} .NET frame{(hidden == 1 ? "" : "s")}\n"); hidden = 0; }
            sb.Append(line).Append('\n');
        }
        if (hidden > 0) sb.Append($"   ... {hidden} .NET frame{(hidden == 1 ? "" : "s")}\n");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Redact, then compact: what tools return for any log or Errors.txt text.</summary>
    public string ForAgent(string text) => CompactStackTraces(Redact(text));

    // ── Redaction ────────────────────────────────────────────────────

    // A Windows path to a file: drive, any directories (spaces allowed), a file name with an extension.
    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n(]+?\.[A-Za-z0-9]{1,8}(?![A-Za-z0-9])")]
    private static partial Regex WindowsFilePath();

    /// <summary>
    /// Paths outside this HUD folder (for example the HUD's own build tree in stack traces, or an old
    /// install an Errors.txt mentions) are shortened to "&lt;outside HUD&gt;\dir\file". That keeps
    /// the useful part (which file) and keeps machine-specific source locations out of tool results,
    /// which agents tend to copy into commits.
    /// </summary>
    public string Redact(string text) =>
        WindowsFilePath().Replace(text, m =>
        {
            var p = m.Value;
            if (p.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return p;
            var parts = p.Split('\\');
            return parts.Length >= 3 ? $"<outside HUD>\\{parts[^2]}\\{parts[^1]}" : p;
        });
}
