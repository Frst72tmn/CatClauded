using System.Security.Cryptography;
using System.Text.Json;

namespace CatClauded.Core;

/// <summary>Настройки CatClauded — JSON в папке пользователя (%APPDATA%\CatClauded или ~/.config/catclauded).</summary>
public sealed class CatSettings
{
    public const int DefaultPort = 47322;

    /// <summary>Порт приёма хуков на 127.0.0.1. 47321 занят MyWorkAssist — обе программы работают одновременно.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Секрет в заголовке X-CatClauded-Token: без него хук отвергается.</summary>
    public string Token { get; set; } = "";

    /// <summary>Показывать котов вообще.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Просящий кот прыгает на монитор с активным окном (и ждёт кнопку). Выключено — решает сам Claude.</summary>
    public bool Jump { get; set; } = true;

    /// <summary>Звать, когда Claude ждёт ответа (Notification idle_prompt).</summary>
    public bool CallWhenIdle { get; set; } = true;

    public bool Sound { get; set; } = true;

    public double Scale { get; set; } = 1.0;

    public bool DesktopWindows { get; set; } = true;
    public bool TerminalWindows { get; set; } = true;
    public bool Wsl { get; set; } = true;
    public bool Linux { get; set; } = true;

    /// <summary>Место лежанки (левый верхний угол, пиксели экрана); null — по умолчанию.</summary>
    public int? BedX { get; set; }
    public int? BedY { get; set; }

    /// <summary>Показывать ли котов сессий отсюда (галочки «Десктоп · Windows», «Терминал · Windows», «WSL», «Linux»).</summary>
    public bool Shows(ClaudeOrigin o) => Enabled && o switch
    {
        { Os: ClaudeOs.Wsl } => Wsl,
        { Os: ClaudeOs.Linux or ClaudeOs.Mac } => Linux,
        { Source: ClaudeSource.Desktop } => DesktopWindows,
        _ => TerminalWindows,
    };

    public CatSettings Clone() => JsonSerializer.Deserialize<CatSettings>(JsonSerializer.Serialize(this))!;
}

/// <summary>Чтение и запись настроек; Changed — после каждого изменения.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
        Path = System.IO.Path.Combine(Directory, "settings.json");
        Current = Load();
    }

    public string Directory { get; }
    public string Path { get; }
    public CatSettings Current { get; private set; }

    public event Action<CatSettings>? Changed;

    /// <summary>%APPDATA%\CatClauded на Windows, ~/.config/catclauded на Linux; CATCLAUDED_CONFIG_DIR — своя папка.</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("CATCLAUDED_CONFIG_DIR") is { Length: > 0 } dir ? dir
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OperatingSystem.IsWindows() ? "CatClauded" : "catclauded");

    public void Update(Action<CatSettings> change)
    {
        CatSettings copy;
        lock (_gate)
        {
            copy = Current.Clone();
            change(copy);
            Save(copy);
            Current = copy;
        }
        Changed?.Invoke(copy);
    }

    private CatSettings Load()
    {
        CatSettings s;
        try { s = File.Exists(Path) ? JsonSerializer.Deserialize<CatSettings>(File.ReadAllText(Path)) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException) { s = new(); }
        if (s.Port is <= 0 or > 65535) s.Port = CatSettings.DefaultPort;
        if (string.IsNullOrEmpty(s.Token) || !File.Exists(Path))
        {
            if (string.IsNullOrEmpty(s.Token)) s.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            try { Save(s); } catch (IOException) { }
        }
        return s;
    }

    private void Save(CatSettings s)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); // там токен
        File.Move(tmp, Path, overwrite: true);
    }
}
