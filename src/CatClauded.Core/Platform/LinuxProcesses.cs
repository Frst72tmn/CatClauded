using System.Diagnostics;
using System.Globalization;

namespace CatClauded.Core.Platform;

/// <summary>
/// Linux: кто прислал хук — по /proc/net/tcp (сокет curl) и /proc/*/fd, его предки по /proc/PID/stat.
/// Сессия жива, пока жив её процесс claude. Окно терминала поднимается через xdotool или wmctrl (X11), если они есть.
/// Хук через SSH-туннель приходит от ssh — тогда поднимается окно терминала с ssh.
/// </summary>
public sealed class LinuxProcesses : IProcessProbe
{
    public ProcessOwner? Find(int clientPort, int serverPort)
    {
        var inode = SocketInode(clientPort, serverPort);
        if (inode is null || PidBySocket(inode.Value) is not { } pid) return null;
        var chain = new List<int>();
        int? claude = null;
        for (int? p = pid; p is { } cur && cur > 1 && !chain.Contains(cur) && chain.Count < 16; p = Parent(cur))
        {
            chain.Add(cur);
            if (claude is null && IsClaude(cur)) claude = cur;
        }
        return new ProcessOwner(claude, 0, ClaudeSource.Terminal, chain);
    }

    public bool IsAlive(int pid) => Directory.Exists($"/proc/{pid}");

    public bool Focus(ProcessOwner owner)
    {
        if (Which("xdotool"))
        {
            foreach (var pid in owner.Chain)
                foreach (var wid in Run("xdotool", $"search --onlyvisible --pid {pid}").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (RunOk("xdotool", $"windowactivate {wid}")) return true;
        }
        if (Which("wmctrl"))
        {
            // wmctrl -lp: «0x03a00007  0 4321   host  Заголовок»
            var windows = Run("wmctrl", "-lp").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(c => c.Length >= 3).ToList();
            foreach (var pid in owner.Chain)
                foreach (var w in windows.Where(c => c[2] == pid.ToString(CultureInfo.InvariantCulture)))
                    if (RunOk("wmctrl", $"-ia {w[0]}")) return true;
        }
        return false;
    }

    /// <summary>Есть ли программа в PATH.</summary>
    public static bool Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, tool)));

    public static string Run(string file, string args, int timeoutMs = 2000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (InvalidOperationException) { } return ""; }
            return output.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }

    private static bool RunOk(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            return p is not null && p.WaitForExit(2000) && p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private static bool IsClaude(int pid)
    {
        try
        {
            var comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            if (comm == "claude") return true;
            return comm is "node" or "bun" && File.ReadAllText($"/proc/{pid}/cmdline").Contains("claude", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static int? Parent(int pid)
    {
        try { return ParentFromStat(File.ReadAllText($"/proc/{pid}/stat")); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>«1234 (имя (с) скобками) S 1200 …» → 1200: имя может содержать пробелы и скобки, поэтому от последней «)».</summary>
    internal static int? ParentFromStat(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], out var ppid) ? ppid : null;
    }

    private static long? SocketInode(int clientPort, int serverPort)
    {
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            string[] lines;
            try { lines = File.ReadAllLines(table); }
            catch (IOException) { continue; }
            foreach (var line in lines.Skip(1))
                if (InodeFromTcpLine(line, clientPort, serverPort) is { } inode) return inode;
        }
        return null;
    }

    /// <summary>Строка /proc/net/tcp: «sl local:port rem:port st … inode» (порты — hex). Своё соединение — по обоим портам.</summary>
    internal static long? InodeFromTcpLine(string line, int localPort, int remotePort)
    {
        var c = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (c.Length < 10) return null;
        static int PortOf(string address) => int.TryParse(address[(address.LastIndexOf(':') + 1)..], NumberStyles.HexNumber, null, out var p) ? p : -1;
        return PortOf(c[1]) == localPort && PortOf(c[2]) == remotePort && long.TryParse(c[9], out var inode) && inode > 0 ? inode : null;
    }

    private static int? PidBySocket(long inode)
    {
        var target = $"socket:[{inode}]";
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                    if (new FileInfo(fd).LinkTarget == target) return pid;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
