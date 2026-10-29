using Avalonia;
using CatClauded.Core;

namespace CatClauded;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // --print-hooks: напечатать {"hooks": …} для этого компьютера — для проверки через claude --settings файл.
        if (args.Contains("--print-hooks"))
        {
            var s = new SettingsStore().Current;
            Console.Out.Write(new System.Text.Json.Nodes.JsonObject { ["hooks"] = HookInstaller.LocalHooks(s.Port, s.Token) }
                .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
