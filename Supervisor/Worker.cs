using System.Diagnostics;
using System.Text;

namespace ExileApiMcp.Supervisor;

/// <summary>
/// One server process (dotnet ExileApiMcp.dll) the supervisor runs. starting: launched, not yet answering; active: gets
/// new calls; draining: a newer worker took over, this one finishes the calls it has (then its stdin closes and it
/// exits); exited.
/// </summary>
public sealed class Worker
{
    private static int _seq;
    private readonly object _writeLock = new();
    private StreamWriter? _stdin;

    public int Seq { get; } = Interlocked.Increment(ref _seq);
    public BuildRef Build { get; }
    public Process Proc { get; }
    public string State { get; set; } = "starting";
    public int? Port { get; init; }                                   // http mode: where it listens
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public string? LastStderr { get; private set; }

    /// <summary>stdio mode: client request ids (raw JSON) this worker owes an answer.</summary>
    public HashSet<string> InFlight { get; } = new(StringComparer.Ordinal);
    /// <summary>http mode: connections open to it.</summary>
    public int Connections;
    /// <summary>stdio: the id of the replayed initialize (or the liveness ping) whose answer the supervisor keeps.</summary>
    public string? ProbeKey { get; set; }
    public TaskCompletionSource<string> ProbeReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Worker(BuildRef build, Process proc, int? port) { Build = build; Proc = proc; Port = port; }

    public static Worker Start(BuildRef build, IEnumerable<string> args, IDictionary<string, string> env, int? port,
        Action<Worker, string> onStdout, Action<Worker> onExit)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Environment.CurrentDirectory,   // the session's checkout: SessionIdentity reads its branch from here
        };
        psi.ArgumentList.Add(build.Dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var w = new Worker(build, proc, port);
        proc.Exited += (_, _) => onExit(w);
        if (!proc.Start()) throw new InvalidOperationException($"dotnet {build.Dll} did not start");
        w._stdin = proc.StandardInput;
        w._stdin.AutoFlush = true;
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null) onStdout(w, line);
        });
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                if (line.Length > 0) w.LastStderr = line;
                Console.Error.WriteLine(line);   // the client keeps our stderr as the server log: pass it through as-is
            }
        });
        return w;
    }

    /// <summary>One line to the worker's stdin (stdio: a JSON-RPC message; http: a control word). False when it is gone.</summary>
    public bool Send(string line)
    {
        lock (_writeLock)
        {
            if (_stdin == null) return false;
            try { _stdin.Write(line); _stdin.Write('\n'); return true; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return false; }
        }
    }

    /// <summary>End of input: a stdio server stops when its stdin ends, an http worker drains and stops.</summary>
    public void CloseInput()
    {
        lock (_writeLock)
        {
            try { _stdin?.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            _stdin = null;
        }
    }

    public void Kill()
    {
        try { if (!Proc.HasExited) Proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public int? Pid { get { try { return Proc.Id; } catch (InvalidOperationException) { return null; } } }
}
