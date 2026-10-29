using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CatClauded.Core.Platform;

/// <summary>
/// Windows: процесс по TCP-соединению (Claude Code или curl.exe из WSL), его предки — десктоп Claude, редактор
/// или терминал, — и окно, которое поднять по «Открыть окно».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcesses : IProcessProbe
{
    public ProcessOwner? Find(int clientPort, int serverPort)
    {
        if (PidByConnection(clientPort, serverPort) is not { } pid) return null;
        var chain = Ancestors(pid);
        nint window = 0;
        ClaudeSource? source = null;
        foreach (var (p, exe) in chain)
        {
            var path = exe.Replace('/', '\\');
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            // Десктоп Claude: Claude.exe из WindowsApps\Claude_… или AnthropicClaude; сам CLI тоже claude.exe, но в другой папке.
            if (name == "claude" && (path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase)))
                source ??= ClaudeSource.Desktop;
            else if (name is "code" or "code - insiders" or "cursor" or "windsurf" or "idea64" or "rider64" or "pycharm64" or "webstorm64" or "devenv")
                source ??= ClaudeSource.Ide;
            if (window == 0 && MainWindow(p) is var w && w != 0) window = w;
        }
        return new ProcessOwner(pid, window, source, chain.Select(c => c.Pid).ToList());
    }

    public bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Поднять окно сессии. Щелчок по коту — последний ввод пользователя, поэтому SetForegroundWindow разрешён.</summary>
    public bool Focus(ProcessOwner owner)
    {
        var window = owner.Window;
        if (window == 0 || !IsWindow(window)) return false;
        if (IsIconic(window)) ShowWindow(window, 9 /* SW_RESTORE */);
        return SetForegroundWindow(window);
    }

    private static nint MainWindow(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    /// <summary>Процесс и его предки (не дальше 12 колен) с путями к exe.</summary>
    private static List<(int Pid, string Exe)> Ancestors(int pid)
    {
        var parents = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1) return [(pid, "")];
        try
        {
            var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e)) parents[e.th32ProcessID] = e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }

        var chain = new List<(int, string)>();
        var seen = new HashSet<int>();
        for (int? p = pid; p is { } cur && cur > 4 && seen.Add(cur) && chain.Count < 12; p = parents.TryGetValue(cur, out var parent) ? parent : null)
            chain.Add((cur, ExePath(cur)));
        return chain;
    }

    private static string ExePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    private static int? PidByConnection(int clientPort, int serverPort)
    {
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, 2 /* AF_INET */, 5 /* TCP_TABLE_OWNER_PID_ALL */, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) != 0) return null;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buffer + 4 + i * rowSize);
                if (Port(row.localPort) == clientPort && Port(row.remotePort) == serverPort && row.owningPid != Environment.ProcessId)
                    return (int)row.owningPid;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int Port(uint networkOrder) => (ushort)IPAddress.NetworkToHostOrder((short)(networkOrder & 0xFFFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state, localAddr, localPort, remoteAddr, remotePort, owningPid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public int dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int af, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")] private static extern bool QueryFullProcessImageName(nint h, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
}
