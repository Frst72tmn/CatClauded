using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CatClauded.Core;

namespace CatClauded.Views;

/// <summary>Настройки: каких котов показывать, прыжки, размер, порт и подключение к Claude Code.</summary>
public sealed class SettingsWindow : Window
{
    private readonly App _app;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

    public SettingsWindow(App app)
    {
        _app = app;
        Title = "CatClauded — настройки";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var s = app.Settings.Current;

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 4 };
        panel.Children.Add(Header("Подключение"));
        panel.Children.Add(_status);
        var connect = new WrapPanel();
        connect.Children.Add(Button("Подключить к Claude Code", async () => { await app.InstallAsync(); Refresh(); }, "accent"));
        connect.Children.Add(Button("Отключить", async () => { await app.UninstallAsync(); Refresh(); }));
        panel.Children.Add(connect);
        if (OperatingSystem.IsWindows())
        {
            var wsl = new WrapPanel();
            wsl.Children.Add(Button("Команда для WSL — в буфер", () => app.CopyAsync(this, HookInstaller.ShellCommand(app.Settings.Current.Port, app.Settings.Current.Token, wsl: true), "Команда для WSL")));
            wsl.Children.Add(Button("Отключение в WSL — в буфер", () => app.CopyAsync(this, HookInstaller.ShellCommand(app.Settings.Current.Port, app.Settings.Current.Token, wsl: true, remove: true), "Команда отключения для WSL")));
            panel.Children.Add(wsl);
        }
        var remote = new WrapPanel();
        remote.Children.Add(Button("Команда для Linux по SSH — в буфер", () => app.CopyAsync(this, HookInstaller.ShellCommand(app.Settings.Current.Port, app.Settings.Current.Token, wsl: false), "Команда для удалённого Linux")));
        remote.Children.Add(Button("Отключение на Linux — в буфер", () => app.CopyAsync(this, HookInstaller.ShellCommand(app.Settings.Current.Port, app.Settings.Current.Token, wsl: false, remove: true), "Команда отключения для Linux")));
        panel.Children.Add(remote);
        panel.Children.Add(Note($"Удалённый Linux: подключитесь с туннелем  ssh -R {s.Port}:127.0.0.1:{s.Port} host  и выполните там скопированную команду (нужны python3 и curl)."));

        panel.Children.Add(Header("Коты"));
        panel.Children.Add(Check("Показывать котов", s.Enabled, (c, v) => c.Enabled = v));
        panel.Children.Add(Check("Прыгать к активному окну и ждать кнопку (выключено — решает сам Claude)", s.Jump, (c, v) => c.Jump = v));
        panel.Children.Add(Check("Звать, когда Claude ждёт ответа", s.CallWhenIdle, (c, v) => c.CallWhenIdle = v));
        panel.Children.Add(Check("Звук, когда кот прыгает", s.Sound, (c, v) => c.Sound = v));

        panel.Children.Add(Header("Каких сессий показывать котов"));
        var origins = new WrapPanel();
        origins.Children.Add(Check("Десктоп · Windows", s.DesktopWindows, (c, v) => c.DesktopWindows = v));
        origins.Children.Add(Check("Терминал · Windows", s.TerminalWindows, (c, v) => c.TerminalWindows = v));
        origins.Children.Add(Check("WSL", s.Wsl, (c, v) => c.Wsl = v));
        origins.Children.Add(Check("Linux", s.Linux, (c, v) => c.Linux = v));
        panel.Children.Add(origins);

        panel.Children.Add(Header("Размер"));
        var scale = new Slider { Minimum = 0.75, Maximum = 1.5, Value = s.Scale, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        scale.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) app.Settings.Update(c => c.Scale = Math.Round(scale.Value, 2));
        };
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        size.Children.Add(scale);
        size.Children.Add(Button("Вернуть лежанку на место", () => { app.Park.ResetBed(); return Task.CompletedTask; }));
        panel.Children.Add(size);

        panel.Children.Add(Header("Порт"));
        var port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = s.Port, Increment = 1, FormatString = "0", Width = 160 };
        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        portRow.Children.Add(port);
        portRow.Children.Add(Button("Применить", async () =>
        {
            if (port.Value is not { } v || (int)v == app.Settings.Current.Port) return;
            app.Settings.Update(c => c.Port = (int)v);
            await app.RestartServerAsync();
            Refresh();
            if (HookInstaller.IsInstalled(HookInstaller.SettingsPath))
                await ConfirmWindow.InformAsync("Порт изменён", "Хуки в settings.json смотрят на старый порт. Нажмите «Подключить к Claude Code» ещё раз (и обновите команды в WSL и на Linux).");
        }));
        panel.Children.Add(portRow);
        panel.Children.Add(Note($"По умолчанию {CatSettings.DefaultPort}: 47321 занят MyWorkAssist, обе программы работают одновременно."));
        panel.Children.Add(Note("Настройки: " + app.Settings.Path));

        Content = new ScrollViewer { Content = panel, MaxHeight = 820 };
        Activated += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var installed = HookInstaller.IsInstalled(HookInstaller.SettingsPath);
        _status.Text = (_app.Server.Problem is { } problem ? "Хуки не принимаются: " + problem : $"Принимаю хуки на 127.0.0.1:{_app.Server.Port}.")
            + "\n" + (installed ? "Подключено: " : "Не подключено: ") + HookInstaller.SettingsPath;
    }

    private CheckBox Check(string text, bool value, Action<CatSettings, bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 16, 0) };
        box.IsCheckedChanged += (_, _) => _app.Settings.Update(c => set(c, box.IsChecked == true));
        return box;
    }

    private static Button Button(string text, Func<Task> click, string? cls = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 4, 8, 4) };
        if (cls is not null) b.Classes.Add(cls);
        b.Click += async (_, _) => await click();
        return b;
    }

    private static TextBlock Header(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
}
