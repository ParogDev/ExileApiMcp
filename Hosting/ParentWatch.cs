using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExileApiMcp.Hosting;

/// <summary>
/// stdio servers exit when their client goes away. Normally stdin reaching end-of-file does that, but servers were found
/// still running a day after their clients (run.cmd's cmd.exe) had exited: an inherited handle kept the pipe open. So in
/// stdio mode we also watch the process that launched us and stop when it exits, whatever the pipe does.
/// </summary>
public static class ParentWatch
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    /// <summary>Stops the application (via stop) when the parent process exits. No-op when the parent can't be found.</summary>
    public static void Start(Action stop)
    {
        Process? parent;
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return;
            parent = Process.GetProcessById(info.InheritedFromUniqueProcessId.ToInt32());
            // A recycled id: the "parent" started after us, so ours is already gone.
            if (parent.StartTime > Process.GetCurrentProcess().StartTime) { Exit(stop, "the process that launched it is gone"); return; }
        }
        catch (ArgumentException) { Exit(stop, "the process that launched it is gone"); return; }
        catch (Exception) { return; }   // can't tell: rely on stdin as before

        _ = Task.Run(async () =>
        {
            try { await parent.WaitForExitAsync(); } catch { return; }
            Exit(stop, $"its launcher (pid {parent.Id}) exited");
        });
    }

    private static void Exit(Action stop, string why)
    {
        Console.Error.WriteLine($"[ExileApiMcp] stdio server stopping: {why}.");
        stop();
    }
}
