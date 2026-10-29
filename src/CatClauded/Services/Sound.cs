using System.Diagnostics;
using System.Runtime.InteropServices;
using CatClauded.Core.Platform;

namespace CatClauded.Services;

/// <summary>Короткий звук, когда кот прыгает: системный на Windows, freedesktop-звук на Linux (если есть чем играть).</summary>
public static class Sound
{
    private const string LinuxSound = "/usr/share/sounds/freedesktop/stereo/message.oga";

    public static void Chime()
    {
        try
        {
            if (OperatingSystem.IsWindows()) MessageBeep(0x40 /* MB_ICONASTERISK */);
            else if (OperatingSystem.IsLinux())
            {
                if (LinuxProcesses.Which("canberra-gtk-play")) Start("canberra-gtk-play", "-i message");
                else if (File.Exists(LinuxSound) && LinuxProcesses.Which("paplay")) Start("paplay", LinuxSound);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void Start(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true })?.Dispose();

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
}
