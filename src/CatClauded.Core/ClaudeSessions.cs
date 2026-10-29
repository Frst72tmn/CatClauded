using System.Text.Json;
using System.Text.Json.Nodes;

namespace CatClauded.Core;

public enum ClaudeSource { Desktop, Terminal, Ide }

public enum ClaudeOs { Windows, Wsl, Linux, Mac }

/// <summary>Где запущен Claude Code: десктоп, терминал или редактор; Windows, WSL или Linux (Host — имя машины).</summary>
public sealed record ClaudeOrigin(ClaudeSource Source, ClaudeOs Os, string Host = "")
{
    public string Label
    {
        get
        {
            var source = Source switch { ClaudeSource.Desktop => "Десктоп", ClaudeSource.Ide => "Редактор", _ => "Терминал" };
            var os = Os switch { ClaudeOs.Wsl => "WSL", ClaudeOs.Linux => "Linux", ClaudeOs.Mac => "macOS", _ => "Windows" };
            return $"{source} · {os}" + (Os is ClaudeOs.Linux or ClaudeOs.Mac && Host.Length > 0 ? $" ({Host})" : "");
        }
    }

    /// <summary>Значок на коте: «▣» десктоп, «›_» терминал, «{}» редактор; у Linux и WSL — пингвин.</summary>
    public string Badge => (Source switch { ClaudeSource.Desktop => "▣", ClaudeSource.Ide => "{}", _ => "›_" }) + (Os is ClaudeOs.Linux or ClaudeOs.Wsl ? " 🐧" : "");
}

/// <summary>
/// Процесс, приславший хук на этом компьютере. Pid — сам Claude Code (жива ли сессия), Window — окно для «Открыть окно» (Windows),
/// Chain — он и его предки (Linux: по ним ищется окно терминала через xdotool или wmctrl).
/// </summary>
public sealed record ProcessOwner(int? Pid, nint Window, ClaudeSource? Source, IReadOnlyList<int> Chain);

/// <summary>Один вызов хука: тело от Claude Code, откуда пришло и кто прислал.</summary>
public sealed record ClaudeHookCall(JsonElement Body, ClaudeOrigin Origin, ProcessOwner? Owner = null)
{
    public string Event => Str(Body, "hook_event_name");
    public string SessionId => Str(Body, "session_id");

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>
/// Сессии Claude Code по событиям его хуков. Каждая сессия — кот: спит, работает, просит разрешения
/// (PermissionRequest ждёт, пока вы нажмёте кнопку у кота) или ждёт вашего ответа (Notification idle_prompt).
/// Ответили в самом Claude — следующее событие сессии снимает просьбу, кот уходит.
/// </summary>
public sealed class ClaudeSessions(Func<int, bool>? isAlive = null)
{
    public const int Coats = 6;

    /// <summary>Сколько молчащая сессия остаётся на лежанке: SessionEnd приходит не всегда (закрыли окно терминала).</summary>
    public static readonly TimeSpan Forget = TimeSpan.FromHours(12);

    /// <summary>Сколько «закончивший» кот потягивается, прежде чем уснуть.</summary>
    public static readonly TimeSpan FinishedFor = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = [];
    private long _nextCat;
    private long _nextAsk;

    /// <summary>Что-то поменялось — котов пора перестроить.</summary>
    public event Action? Changed;

    private sealed class Session(string id, long catId)
    {
        public string Id { get; } = id;
        public long CatId { get; } = catId;
        public ClaudeOrigin Origin { get; set; } = new(ClaudeSource.Terminal, ClaudeOs.Windows);
        public string? Title { get; set; }
        public string Cwd { get; set; } = "";
        public CatState State { get; set; } = CatState.Sleeping;
        public DateTimeOffset LastUtc { get; set; }
        public DateTimeOffset? FinishedUtc { get; set; }
        public ProcessOwner? Owner { get; set; }
        public Pending? Permission { get; set; }
        public CatAsk? Waiting { get; set; }
    }

    private sealed record Pending(long Id, CatAsk Ask, JsonElement? Suggestions)
    {
        public TaskCompletionSource<string?> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Событие хука. Для PermissionRequest ждёт решения у кота (не дольше maxWait) и возвращает JSON для Claude Code;
    /// null — решать будет сам Claude Code, как обычно.
    /// </summary>
    public async Task<string?> HandleAsync(ClaudeHookCall call, TimeSpan maxWait, CancellationToken ct, DateTimeOffset? now = null)
    {
        if (call.SessionId.Length == 0) return null;
        Pending? wait = null;
        lock (_gate)
        {
            var at = now ?? DateTimeOffset.UtcNow;
            if (!_sessions.TryGetValue(call.SessionId, out var s))
            {
                if (call.Event == "SessionEnd") return null;
                _sessions[call.SessionId] = s = new Session(call.SessionId, ++_nextCat);
            }
            s.Origin = call.Origin;
            s.LastUtc = at;
            if (call.Owner is { } owner) s.Owner = owner;
            if (ClaudeHookCall.Str(call.Body, "cwd") is { Length: > 0 } cwd) s.Cwd = cwd;
            if (ClaudeHookCall.Str(call.Body, "session_title") is { Length: > 0 } title) s.Title = Short(title, 60);

            switch (call.Event)
            {
                case "SessionStart":
                    s.State = CatState.Sleeping;
                    break;
                case "UserPromptSubmit":
                    Release(s);
                    s.Waiting = null;
                    s.State = CatState.Working;
                    s.Title ??= Short(ClaudeHookCall.Str(call.Body, "prompt"), 60) is { Length: > 0 } p ? p : null;
                    break;
                case "PermissionRequest":
                    Release(s);
                    s.Waiting = null;
                    var id = ++_nextAsk;
                    var (what, detail) = Describe(call.Body);
                    var suggestions = call.Body.TryGetProperty("permission_suggestions", out var sg) && sg.ValueKind == JsonValueKind.Array && sg.GetArrayLength() > 0
                        ? sg.Clone() : (JsonElement?)null;
                    s.Permission = wait = new Pending(id, new CatAsk(id, what, detail, suggestions is not null, at), suggestions);
                    break;
                case "PostToolUse" or "PostToolUseFailure" or "PermissionDenied":
                    // Инструмент пошёл (или отказан) — значит, на вопрос уже ответили в самом Claude.
                    Release(s);
                    s.Waiting = null;
                    s.State = CatState.Working;
                    break;
                case "Notification":
                    var type = ClaudeHookCall.Str(call.Body, "notification_type");
                    if (type is "idle_prompt" or "agent_needs_input" or "elicitation_dialog" && s.Permission is null)
                        s.Waiting = new CatAsk(null, "Claude ждёт вас: " + (ClaudeHookCall.Str(call.Body, "message") is { Length: > 0 } m ? Short(m, 300) : "нужен ваш ответ"), "", false, at);
                    break;
                case "Stop" or "StopFailure":
                    Release(s);
                    s.State = CatState.Finished;
                    s.FinishedUtc = at;
                    break;
                case "SessionEnd":
                    Release(s);
                    _sessions.Remove(s.Id);
                    break;
            }
        }
        Changed?.Invoke();
        if (wait is null) return null;

        // Ждём кнопку у кота; Claude Code бросил запрос (ответили у него) или вышло время — решает он сам.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(maxWait);
        try { return await wait.Decision.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { return null; }
        finally
        {
            lock (_gate)
                foreach (var s in _sessions.Values.Where(s => s.Permission == wait)) s.Permission = null;
            Changed?.Invoke();
        }
    }

    /// <summary>«Разрешить» / «Заблокировать» у кота. false — просьба уже снята.</summary>
    public bool Decide(long askId, bool allow) =>
        Resolve(askId, p => allow ? Decision("allow") : Decision("deny", message: "Пользователь заблокировал это действие (CatClauded)."));

    /// <summary>«Всегда»: разрешить и добавить правило, которое предложил сам Claude Code.</summary>
    public bool DecideAlways(long askId) => Resolve(askId, p => Decision("allow", p.Suggestions));

    /// <summary>Открыли сессию — «Claude ждёт вас» снимается.</summary>
    public void Seen(long catId)
    {
        lock (_gate)
            foreach (var s in _sessions.Values.Where(s => s.CatId == catId)) s.Waiting = null;
        Changed?.Invoke();
    }

    /// <summary>Кто прислал события сессии — его окно поднимают по «Открыть окно».</summary>
    public ProcessOwner? OwnerOf(long catId)
    {
        lock (_gate)
            return _sessions.Values.FirstOrDefault(s => s.CatId == catId)?.Owner;
    }

    /// <summary>Коты сессий; show — каких показывать (по настройкам).</summary>
    public IReadOnlyList<CatInfo> Cats(Func<ClaudeOrigin, bool> show, bool callWhenIdle, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var dead in _sessions.Values.Where(s => s.Permission is null && (at - s.LastUtc > Forget
                         || s.Owner?.Pid is { } pid && isAlive is not null && !isAlive(pid))).ToList())
                _sessions.Remove(dead.Id);

            return _sessions.Values.Where(s => show(s.Origin)).OrderBy(s => s.CatId).Select(s =>
            {
                var ask = s.Permission?.Ask ?? (callWhenIdle ? s.Waiting : null);
                var state = ask is not null ? CatState.Asking
                    : s.State == CatState.Finished && s.FinishedUtc is { } f && at - f >= FinishedFor ? CatState.Sleeping
                    : s.State;
                return new CatInfo(s.CatId, s.Title ?? Folder(s.Cwd), (int)(s.CatId % Coats), state, ask, new CatOrigin(s.Origin.Label, s.Origin.Badge));
            }).ToList();
        }
    }

    private bool Resolve(long askId, Func<Pending, string> decision)
    {
        Pending? p;
        lock (_gate)
        {
            p = _sessions.Values.Select(s => s.Permission).FirstOrDefault(x => x?.Id == askId);
            if (p is null) return false;
            foreach (var s in _sessions.Values.Where(s => s.Permission == p))
            {
                s.Permission = null;
                s.State = CatState.Working;
            }
        }
        var ok = p.Decision.TrySetResult(decision(p));
        Changed?.Invoke();
        return ok;
    }

    private static void Release(Session s)
    {
        s.Permission?.Decision.TrySetResult(null);
        s.Permission = null;
    }

    /// <summary>Ответ Claude Code на PermissionRequest (hookSpecificOutput.decision).</summary>
    public static string Decision(string behavior, JsonElement? permissions = null, string? message = null)
    {
        var decision = new JsonObject { ["behavior"] = behavior };
        if (permissions is { } p) decision["updatedPermissions"] = JsonNode.Parse(p.GetRawText());
        if (message is not null) decision["message"] = message;
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        }.ToJsonString();
    }

    /// <summary>Что просит Claude, по-человечески: заголовок и подробность (команда, файл, адрес).</summary>
    public static (string Title, string Detail) Describe(JsonElement body)
    {
        var tool = ClaudeHookCall.Str(body, "tool_name");
        var input = body.TryGetProperty("tool_input", out var i) ? i : default;
        string In(string name) => ClaudeHookCall.Str(input, name);
        return tool switch
        {
            "Bash" => ("Выполнить команду" + (In("description") is { Length: > 0 } d ? ": " + Short(d, 120) : ""), In("command")),
            "PowerShell" => ("Выполнить PowerShell" + (In("description") is { Length: > 0 } d ? ": " + Short(d, 120) : ""), In("command")),
            "Edit" or "MultiEdit" => ("Изменить файл", In("file_path")),
            "Write" => ("Записать файл", In("file_path")),
            "NotebookEdit" => ("Изменить блокнот", In("notebook_path")),
            "Read" => ("Прочитать файл", In("file_path")),
            "WebFetch" => ("Открыть страницу", In("url")),
            "WebSearch" => ("Поиск в интернете", In("query")),
            "Glob" or "Grep" => ("Искать в файлах", In("pattern") + (In("path") is { Length: > 0 } p ? "  в " + p : "")),
            _ => (tool.StartsWith("mcp__", StringComparison.Ordinal) ? "Инструмент " + tool[5..].Replace("__", ": ") : tool.Length > 0 ? tool : "Действие",
                input.ValueKind == JsonValueKind.Object ? Short(input.GetRawText(), 400) : ""),
        };
    }

    private static string Folder(string cwd)
    {
        var name = cwd.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
        return string.IsNullOrEmpty(name) ? "Claude Code" : name;
    }

    private static string Short(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
