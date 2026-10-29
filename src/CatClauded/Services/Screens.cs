using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using CatClauded.Core.Platform;

namespace CatClauded.Services;

/// <summary>
/// Мониторы и окна для котов (пиксели экрана): где активное окно, не во весь ли экран оно, окна без фокуса.
/// Windows — через user32; Linux — через xdotool, если он есть (иначе — основной монитор).
/// </summary>
public static class Screens
{
    /// <summary>Рабочая область монитора, где активное окно.</summary>
    public static PixelRect ActiveWork(WindowBase any)
    {
        var point = ForegroundCenter();
        var all = any.Screens;
        var screen = (point is { } p ? all.ScreenFromPoint(p) : null) ?? all.Primary ?? all.All.FirstOrDefault();
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
    }

    /// <summary>Рабочая область монитора, на котором точка; если точка ни на каком — ближайшего.</summary>
    public static PixelRect WorkAt(WindowBase any, PixelPoint p) => ScreenAt(any, p)?.WorkingArea ?? ActiveWork(any);

    public static double ScaleAt(WindowBase any, PixelPoint p) => ScreenAt(any, p)?.Scaling ?? 1.0;

    private static Screen? ScreenAt(WindowBase any, PixelPoint p)
    {
        var all = any.Screens;
        return all.ScreenFromPoint(p) ?? all.All.OrderBy(s => Distance(s.Bounds, p)).FirstOrDefault();
    }

    private static double Distance(PixelRect r, PixelPoint p)
    {
        var dx = Math.Max(Math.Max(r.X - p.X, 0), p.X - r.Right);
        var dy = Math.Max(Math.Max(r.Y - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    /// <summary>Размер окна в пикселях экрана.</summary>
    public static PixelSize SizeOf(Window w) => PixelSize.FromSize(w.ClientSize, w.DesktopScaling);

    /// <summary>Окно не забирает фокус и не попадает в Alt+Tab (Windows).</summary>
    public static void NoActivate(Window w)
    {
        if (!OperatingSystem.IsWindows() || w.TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
    }

    /// <summary>Поверх всех окон, не трогая фокус: после чужих «поверх всех» (видеозвонки, панели) коты снова наверху.</summary>
    public static void BringTop(Window w)
    {
        if (OperatingSystem.IsWindows() && w.TryGetPlatformHandle()?.Handle is { } hwnd)
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, NoSizeNoMoveNoActivate);
    }

    /// <summary>Активное окно во весь монитор (презентация, видео, игра) — котам не прыгать. Только Windows.</summary>
    public static bool ForegroundIsFullScreen()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(fg, name, name.Capacity);
        if (name.ToString() is "WorkerW" or "Progman") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(fg, 2), ref info);
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>Середина активного окна; null — не узнать.</summary>
    private static PixelPoint? ForegroundCenter()
    {
        if (OperatingSystem.IsWindows())
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && GetWindowRect(fg, out var r)) return new PixelPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            return GetCursorPos(out var c) ? new PixelPoint(c.X, c.Y) : null;
        }
        if (OperatingSystem.IsLinux() && LinuxProcesses.Which("xdotool"))
        {
            // X=…, Y=…, WIDTH=…, HEIGHT=… — по строке на значение.
            var v = LinuxProcesses.Run("xdotool", "getactivewindow getwindowgeometry --shell").Split('\n')
                .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => int.TryParse(p[1], out var n) ? n : 0);
            if (v.TryGetValue("X", out var x) && v.TryGetValue("Y", out var y))
                return new PixelPoint(x + v.GetValueOrDefault("WIDTH") / 2, y + v.GetValueOrDefault("HEIGHT") / 2);
        }
        return null;
    }

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
    private const uint NoSizeNoMoveNoActivate = 0x0001 | 0x0002 | 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
