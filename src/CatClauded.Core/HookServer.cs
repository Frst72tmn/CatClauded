using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CatClauded.Core.Platform;

namespace CatClauded.Core;

/// <summary>Заголовки и адрес хуков CatClauded. Свои хуки в settings.json узнаём по TokenHeader.</summary>
public static class Hook
{
    public const string Path = "/claude-hook";
    public const string TokenHeader = "X-CatClauded-Token";
    /// <summary>windows | wsl | linux | mac.</summary>
    public const string OsHeader = "X-CatClauded-OS";
    public const string HostHeader = "X-CatClauded-Host";
    /// <summary>$CLAUDE_CODE_ENTRYPOINT: claude-desktop — десктоп, cli и sdk-cli — терминал.</summary>
    public const string EntryHeader = "X-CatClauded-Entry";
    /// <summary>$MWA_AGENT: сессии агентов MyWorkAssist — у него свои коты, здесь их не показываем.</summary>
    public const string AgentHeader = "X-CatClauded-Agent";
    /// <summary>$CLAUDE_CODE_HOST_SESSION_ID: id сессии в десктопе Claude (local_…) — по нему щелчок по коту открывает именно её.</summary>
    public const string DesktopSessionHeader = "X-CatClauded-Desktop-Session";

    /// <summary>Сколько ждать кнопку у кота; хук в settings.json ждёт дольше (HookTimeoutSeconds), чтобы ответ успел дойти.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(55);
    public const int HookTimeoutSeconds = 3600;

    public static string Url(int port) => $"http://127.0.0.1:{port}{Path}";
}

/// <summary>
/// Приём хуков Claude Code: POST /claude-hook на 127.0.0.1:Port (постоянный — на него смотрят хуки в ~/.claude/settings.json,
/// туннели SSH с Linux и curl.exe из WSL). Без верного X-CatClauded-Token запрос отвергается.
/// </summary>
public sealed class HookServer(ClaudeSessions sessions, Func<CatSettings> settings, IProcessProbe? probe = null) : IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }

    /// <summary>Почему не слушает (порт занят и т. п.); null — работает.</summary>
    public string? Problem { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await StopAsync();
        Port = settings().Port;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, Port));
        var app = builder.Build();
        app.MapPost(Hook.Path, HandleAsync);
        try
        {
            await app.StartAsync(ct);
            _app = app;
            Problem = null;
            Log.Write($"Слушаю хуки на 127.0.0.1:{Port}");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Problem = $"порт {Port} занят — другой программой или второй копией CatClauded";
            Log.Write($"Порт {Port} не открыть: {ex.Message}");
            await app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext http)
    {
        var s = settings();
        var h = http.Request.Headers;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(h[Hook.TokenHeader].ToString()), Encoding.UTF8.GetBytes(s.Token)))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!s.Enabled || h[Hook.AgentHeader].ToString().Length > 0) return;

        JsonElement body;
        try { body = (await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted)).RootElement.Clone(); }
        catch (JsonException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var os = ParseOs(h[Hook.OsHeader].ToString());
        // Кто прислал на этом компьютере (Claude Code, curl.exe из WSL, curl на Linux) — его окно и жив ли он.
        ProcessOwner? owner = null;
        if (probe is not null && http.Connection.RemotePort > 0)
            try { owner = probe.Find(http.Connection.RemotePort, Port); }
            catch (Exception ex) { Log.Write("Не найти процесс хука: " + ex.Message); }
        // WSL шлёт через curl.exe — он живёт миг, поэтому по нему не судим, жива ли сессия; окно его терминала годится.
        if (os == ClaudeOs.Wsl && owner is not null) owner = owner with { Pid = null };
        var source = h[Hook.EntryHeader].ToString() switch
        {
            "claude-desktop" => ClaudeSource.Desktop,
            var e when e.Contains("vscode", StringComparison.OrdinalIgnoreCase) || e.Contains("jetbrains", StringComparison.OrdinalIgnoreCase) => ClaudeSource.Ide,
            "cli" or "sdk-cli" => ClaudeSource.Terminal,
            _ => owner?.Source ?? ClaudeSource.Terminal,
        };
        var desktopSession = h[Hook.DesktopSessionHeader].ToString() is var d && ClaudeSessions.IsDesktopSessionId(d) ? d : null;
        var call = new ClaudeHookCall(body, new ClaudeOrigin(source, os, h[Hook.HostHeader].ToString()), owner, desktopSession);

        if (call.Event is "SessionStart" or "SessionEnd" or "PermissionRequest")
            Log.Write($"{call.Event} · {call.Origin.Label} · pid {owner?.Pid}" + (desktopSession is null ? "" : " · " + desktopSession));
        // Ждать кнопку можно, только если кот с кнопками появится: коты и прыжки включены, эта сессия не скрыта.
        // Иначе решает сам Claude — не держим его, пока никто не может ответить.
        var canAsk = s.Enabled && s.Jump && s.Shows(call.Origin);
        string? answer;
        try { answer = await sessions.HandleAsync(call, canAsk ? Hook.MaxWait : TimeSpan.Zero, http.RequestAborted); }
        catch (Exception ex)
        {
            Log.Write($"Хук {call.Event} упал: {ex.Message}");
            return; // пустой ответ — Claude Code решает сам
        }
        if (call.Event == "PermissionRequest")
            Log.Write("PermissionRequest: " + (answer is null ? "Claude решает сам" : answer.Contains("\"deny\"") ? "заблокировано у кота" : "разрешено у кота"));
        if (answer is null || http.RequestAborted.IsCancellationRequested) return;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(answer, http.RequestAborted);
    }

    public static ClaudeOs ParseOs(string header) => header.ToLowerInvariant() switch
    {
        "wsl" => ClaudeOs.Wsl,
        "linux" => ClaudeOs.Linux,
        "mac" or "macos" => ClaudeOs.Mac,
        _ => ClaudeOs.Windows,
    };

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(cts.Token); }
        catch (OperationCanceledException) { }
        await app.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
