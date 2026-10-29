namespace CatClauded.Core.Platform;

/// <summary>Процессы этого компьютера: кто прислал хук, жив ли он, поднять его окно.</summary>
public interface IProcessProbe
{
    /// <summary>Процесс, чьё соединение пришло с порта clientPort на наш serverPort (оба на 127.0.0.1).</summary>
    ProcessOwner? Find(int clientPort, int serverPort);

    bool IsAlive(int pid);

    /// <summary>Поднять окно сессии; false — не нашлось или нечем (на Linux нужен xdotool или wmctrl).</summary>
    bool Focus(ProcessOwner owner);

    public static IProcessProbe? ForThisOs() =>
        OperatingSystem.IsWindows() ? new WindowsProcesses()
        : OperatingSystem.IsLinux() ? new LinuxProcesses()
        : null;
}
