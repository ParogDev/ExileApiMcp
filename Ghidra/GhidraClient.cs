using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Ghidra;

/// <summary>
/// Minimal client for the GhidraMCP headless server (scaffolding: tools/ghidra-headless.ps1), used for static analysis
/// of the game exe's analysed COPY in the Ghidra project. Nothing here touches the running game.
///   GHIDRA_MCP_URL (default http://127.0.0.1:8089), GHIDRA_MCP_AUTH_TOKEN (bearer; process or user env).
///   POE1_EXE / POE2_EXE override the installed exe paths (used to pick the matching snapshot by SHA-256).
/// Program-wide instruction searches are slow (~30 s on PoE1) and immutable per exe snapshot, so they are cached on
/// disk under %LOCALAPPDATA%\ExileApiMcp\ghidra-cache\&lt;program&gt;\.
/// </summary>
internal sealed class GhidraClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly string _base;
    private readonly string? _token;

    public string Program { get; private set; } = "";

    private GhidraClient(string baseUrl, string? token) { _base = baseUrl.TrimEnd('/'); _token = token; }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    /// <summary>Connect and make sure the analysed snapshot of this game's installed exe is the loaded program.</summary>
    public static async Task<GhidraClient> ForGameAsync(string game, CancellationToken ct)
    {
        var c = new GhidraClient(Env("GHIDRA_MCP_URL") ?? "http://127.0.0.1:8089", Env("GHIDRA_MCP_AUTH_TOKEN"));
        try { await c.GetAsync("check_connection", ct); }
        catch (HttpRequestException)
        {
            throw new McpException("Ghidra isn't running. Start it in the background from the scaffolding repo: " +
                                   "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\ghidra-headless.ps1 (wait for 'running on port 8089').");
        }
        var (programPath, label) = SnapshotFor(game);
        c.Program = Path.GetFileName(programPath);
        var info = await c.TryGetJsonAsync("get_current_program_info", ct);
        if (info?["name"]?.ToString() != c.Program)
        {
            var project = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ghidra", "projects", "PathOfExile.gpr");
            await c.PostAsync("open_project", new JObject { ["path"] = project }, ct);
            var r = await c.PostAsync("load_program_from_project", new JObject { ["path"] = programPath }, ct);
            if (r["success"]?.Value<bool>() != true)
                throw new McpException($"Ghidra couldn't load {programPath}: {r["error"] ?? r}. Is snapshot {label} imported? (tools/ghidra-analyze.ps1 -Game {game})");
        }
        return c;
    }

    /// <summary>The project path of the snapshot whose SHA-256 matches the installed exe (tools/ghidra-analyze.ps1 naming).</summary>
    private static (string programPath, string label) SnapshotFor(string game)
    {
        var exe = Env(game == "poe2" ? "POE2_EXE" : "POE1_EXE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Grinding Gear Games",
            game == "poe2" ? "Path of Exile 2" : "Path of Exile", "PathOfExile.exe");
        if (!File.Exists(exe)) throw new McpException($"Game exe not found at {exe}; set {(game == "poe2" ? "POE2_EXE" : "POE1_EXE")}.");
        string sha;
        using (var fs = File.OpenRead(exe)) sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        var label = $"{File.GetLastWriteTime(exe):yyyyMMdd}-{sha[..8]}";
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ghidra", "binaries", game, label);
        if (!Directory.Exists(bin))
            throw new McpException($"No Ghidra snapshot of the installed {game} exe ({label}): the game was patched since the last analysis. " +
                                   $"Run tools/ghidra-analyze.ps1 -Game {game} (scaffolding repo; takes a while), then retry.");
        return ($"/{game}/{label}/PathOfExile_{game}_{label}.exe", label);
    }

    /// <summary>
    /// Program-wide instruction search by operand substring, cached on disk per program. Null when the pattern is too
    /// common to be useful (the search hit its 50000 cap); that verdict is cached too.
    /// </summary>
    public async Task<JArray?> SearchOperandAsync(string operandPattern, CancellationToken ct)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp", "ghidra-cache", Program);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "ops-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operandPattern)))[..16] + ".json");
        if (File.Exists(file))
        {
            var cached = JToken.Parse(await File.ReadAllTextAsync(file, ct));
            return cached is JArray a ? a : null; // {"tooCommon": true}
        }
        var r = await GetJsonAsync($"search_instructions?operand_pattern={Uri.EscapeDataString(operandPattern)}&limit=50000", ct);
        if (r["truncated"]?.Value<bool>() == true)
        {
            await File.WriteAllTextAsync(file, """{"tooCommon":true}""", ct);
            return null;
        }
        var matches = r["matches"] as JArray ?? [];
        await File.WriteAllTextAsync(file, matches.ToString(Formatting.None), ct);
        return matches;
    }

    /// <summary>Decompiled pseudocode of the function containing an address, cached on disk per program (it never changes).</summary>
    public async Task<string> DecompileAsync(string address, CancellationToken ct)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp", "ghidra-cache", Program);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "dec-" + new string(address.Where(char.IsLetterOrDigit).ToArray()) + ".c");
        if (File.Exists(file)) return await File.ReadAllTextAsync(file, ct);
        var code = await GetAsync($"decompile_function?address={Uri.EscapeDataString(address)}&timeout=60", ct);
        if (code.Contains('(') && !code.StartsWith("{\"error", StringComparison.Ordinal)) await File.WriteAllTextAsync(file, code, ct);
        return code;
    }

    public async Task<JObject?> TryGetJsonAsync(string path, CancellationToken ct)
    {
        try { return JObject.Parse(await GetAsync(path, ct)); } catch { return null; }
    }

    private async Task<JObject> GetJsonAsync(string path, CancellationToken ct) => JObject.Parse(await GetAsync(path, ct));

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/{path}");
        return await SendAsync(req, ct);
    }

    private async Task<JObject> PostAsync(string path, JObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/{path}")
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
        };
        var text = await SendAsync(req, ct);
        try { return JObject.Parse(text); } catch { return new JObject { ["text"] = text }; }
    }

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new McpException($"Ghidra {req.RequestUri!.AbsolutePath}: HTTP {(int)res.StatusCode} {text[..Math.Min(300, text.Length)]}");
        return text;
    }
}
