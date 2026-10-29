using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using CatClauded.Core;
using CatClauded.Core.Platform;
using CatClauded.Services;
using CatClauded.Views;

namespace CatClauded;

/// <summary>
/// CatClauded: иконка в трее, сервер хуков на 127.0.0.1, лежанка и просящие коты.
/// Окон в панели задач нет — всё управление через меню иконки.
/// </summary>
public sealed class App : Application
{
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;

    public SettingsStore Settings { get; } = new();
    public IProcessProbe? Probe { get; } = IProcessProbe.ForThisOs();
    public ClaudeSessions Sessions { get; private set; } = null!;
    public HookServer Server { get; private set; } = null!;
    public CatPark Park { get; private set; } = null!;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        Name = "CatClauded";
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Log.FilePath = Path.Combine(Settings.Directory, "catclauded.log");
        Log.Write($"CatClauded {typeof(App).Assembly.GetName().Version} запущен");

        Sessions = new ClaudeSessions(Probe is null ? null : Probe.IsAlive);
        Server = new HookServer(Sessions, () => Settings.Current, Probe);
        Park = new CatPark(Sessions, Settings, Probe);
        CreateTray(desktop);
        Park.Start();
        desktop.ShutdownRequested += (_, _) => Server.StopAsync().GetAwaiter().GetResult();
        base.OnFrameworkInitializationCompleted();

        await Server.StartAsync();
        UpdateTrayTip();
        if (Server.Problem is { } problem)
            await ConfirmWindow.InformAsync("CatClauded не принимает хуки", $"Не удалось открыть {problem}. Смените порт в настройках.");
        else if (!HookInstaller.IsInstalled(HookInstaller.SettingsPath))
            await InstallAsync(firstRun: true);
        else if (HookInstaller.IsOutdated(HookInstaller.SettingsPath, HookInstaller.LocalHooks(Settings.Current.Port, Settings.Current.Token)))
            await InstallAsync(outdated: true);
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("Подключить к Claude Code", () => _ = InstallAsync()));
        menu.Items.Add(Item("Отключить", () => _ = UninstallAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Настройки", ShowSettings));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Выход", () => desktop.Shutdown()));
        _tray = new TrayIcon { Icon = AppIcon.Create(), ToolTipText = "CatClauded", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowSettings();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private static NativeMenuItem Item(string header, Action click)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => click();
        return item;
    }

    private void UpdateTrayTip()
    {
        if (_tray is null) return;
        _tray.ToolTipText = Server.Problem is { } p ? "CatClauded — " + p : $"CatClauded — котики Claude Code (порт {Server.Port})";
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Записать хуки в ~/.claude/settings.json — только после подтверждения в окне.</summary>
    public async Task InstallAsync(bool firstRun = false, bool outdated = false)
    {
        var path = HookInstaller.SettingsPath;
        var how = OperatingSystem.IsWindows()
            ? $"HTTP-хуки на 127.0.0.1:{Settings.Current.Port}"
            : $"команды curl на 127.0.0.1:{Settings.Current.Port} (молчат, когда CatClauded закрыт)";
        var text = (firstRun ? "Котики ещё не подключены к Claude Code.\n\n"
                : outdated ? "Хуки CatClauded в Claude Code устарели: их ставила прежняя версия или они смотрят на другой порт. "
                    + "Без обновления часть возможностей не работает — например, щелчок по коту не откроет нужную сессию десктопа.\n\n" : "")
            + $"CatClauded добавит свои хуки ({how}) в файл:\n{path}\n\n"
            + (File.Exists(path) && !File.Exists(path + HookInstaller.BackupSuffix) ? $"Перед правкой будет сделана копия:\n{path}{HookInstaller.BackupSuffix}\n\n" : "")
            + "Ваши настройки и чужие хуки останутся как есть. Прежние хуки CatClauded заменятся новыми.\n"
            + "Хуки начнут работать в новых сессиях Claude Code (уже открытые нужно перезапустить).";
        if (!await ConfirmWindow.AskAsync(outdated ? "Обновить хуки CatClauded?" : "Подключить к Claude Code?", text, outdated ? "Обновить" : "Подключить")) return;
        try
        {
            var backup = HookInstaller.Write(path, HookInstaller.LocalHooks(Settings.Current.Port, Settings.Current.Token));
            Log.Write($"Хуки записаны в {path}" + (backup is null ? "" : $", копия {backup}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            await ConfirmWindow.InformAsync("Не получилось", $"Не удалось записать {path}:\n{e.Message}");
        }
    }

    /// <summary>Убрать только свои хуки.</summary>
    public async Task UninstallAsync()
    {
        var path = HookInstaller.SettingsPath;
        if (!HookInstaller.IsInstalled(path))
        {
            await ConfirmWindow.InformAsync("Отключить", $"Хуков CatClauded в {path} нет.");
            return;
        }
        if (!await ConfirmWindow.AskAsync("Отключить от Claude Code?", $"CatClauded уберёт из {path} только свои хуки (с заголовком {HookInstaller.Marker}). Остальное останется.", "Отключить")) return;
        try
        {
            HookInstaller.Write(path, null);
            Log.Write($"Хуки убраны из {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            await ConfirmWindow.InformAsync("Не получилось", $"Не удалось записать {path}:\n{e.Message}");
        }
    }

    public async Task RestartServerAsync()
    {
        await Server.StartAsync();
        UpdateTrayTip();
    }

    public async Task CopyAsync(TopLevel from, string text, string what)
    {
        if (from.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        await ConfirmWindow.InformAsync("Скопировано", $"{what} — в буфере обмена. Вставьте её в терминал и нажмите Enter. Нужны python3 и curl.");
    }
}

/// <summary>Иконка трея: сидящий кот, нарисованный тем же CatView.</summary>
internal static class AppIcon
{
    public static WindowIcon Create()
    {
        const int size = 64;
        var cat = new CatView(0, CatState.Asking, animate: false);
        var dpi = 96.0 * size / CatView.SittingHeight;
        cat.Measure(new Size(CatView.SittingWidth, CatView.SittingHeight));
        cat.Arrange(new Rect(0, 0, CatView.SittingWidth, CatView.SittingHeight));
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(dpi, dpi));
        bitmap.Render(cat);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
