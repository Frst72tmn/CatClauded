using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CatClauded.Core;

/// <summary>
/// Хуки Claude Code для котиков. На этом компьютере — в ~/.claude/settings.json (его читают и терминал, и десктоп Claude):
/// на Windows HTTP-хуки на 127.0.0.1, на Linux — command-хуки с curl. Для WSL и удалённого Linux — команда, которую
/// пользователь сам запускает там. Свои хуки узнаём по заголовку X-CatClauded-Token: повторная установка их заменяет,
/// удаление убирает только их. Перед первой правкой — копия settings.json.mwa-backup.
/// </summary>
public static class HookInstaller
{
    public const string Marker = Hook.TokenHeader;
    public const string BackupSuffix = ".mwa-backup";

    /// <summary>События, на которые нужен кот. PermissionRequest ждёт решения у кота, остальные отвечают сразу.</summary>
    public static readonly string[] Events = ["SessionStart", "UserPromptSubmit", "PermissionRequest", "PostToolUse", "PostToolUseFailure", "PermissionDenied", "Notification", "Stop", "StopFailure", "SessionEnd"];

    /// <summary>~/.claude/settings.json (или $CLAUDE_CONFIG_DIR/settings.json).</summary>
    public static string SettingsPath =>
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");

    public static bool IsInstalled(string path)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    /// <summary>Хуки для этого компьютера: Windows — HTTP, Linux и macOS — curl.</summary>
    public static JsonObject LocalHooks(int port, string token) =>
        OperatingSystem.IsWindows() ? HooksFor("windows", Environment.MachineName, port, token)
        : HooksFor(OperatingSystem.IsMacOS() ? "mac" : "linux", Environment.MachineName, port, token);

    /// <summary>Записать хуки (null — убрать наши); возвращает путь к копии, если она сделана сейчас.</summary>
    public static string? Write(string path, JsonObject? hooks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var root = text.Trim().Length == 0 ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException("settings.json — не объект JSON");
        var merged = Merge(root, hooks);
        string? backup = null;
        if (text.Length > 0 && !File.Exists(path + BackupSuffix))
        {
            File.Copy(path, path + BackupSuffix);
            backup = path + BackupSuffix;
        }
        var tmp = path + ".catclauded-tmp";
        File.WriteAllText(tmp, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return backup;
    }

    /// <summary>Убрать наши хуки (узнаём по маркеру) и добавить новые; чужие хуки и прочие настройки не трогаем.</summary>
    public static JsonObject Merge(JsonObject settings, JsonObject? ours)
    {
        if (settings["hooks"] is JsonObject hooks)
        {
            foreach (var (name, value) in hooks.ToList())
            {
                if (value is not JsonArray groups) continue;
                foreach (var g in groups.Where(g => g?.ToJsonString().Contains(Marker, StringComparison.Ordinal) == true).ToList()) groups.Remove(g);
                if (groups.Count == 0) hooks.Remove(name);
            }
            if (hooks.Count == 0 && ours is null) settings.Remove("hooks");
        }
        if (ours is null) return settings;
        if (settings["hooks"] is not JsonObject target) settings["hooks"] = target = new JsonObject();
        foreach (var (name, value) in ours)
        {
            if (target[name] is not JsonArray list) target[name] = list = new JsonArray();
            foreach (var g in ((JsonArray)value!).ToList()) list.Add(g!.DeepClone());
        }
        return settings;
    }

    /// <summary>
    /// Хуки для settings.json. Windows — HTTP прямо в программу. WSL и Linux — команда с curl, которая молчит, если связи нет
    /// (программа закрыта, туннель не поднят): Claude тогда спрашивает как обычно, без сообщений «hook failed».
    /// WSL берёт curl.exe Windows — так событие попадает на 127.0.0.1 этого компьютера при любой сети WSL.
    /// </summary>
    public static JsonObject HooksFor(string os, string host, int port, string token, bool wsl = false)
    {
        var result = new JsonObject();
        foreach (var e in Events)
        {
            var timeout = e == "PermissionRequest" ? Hook.HookTimeoutSeconds : 10;
            JsonObject hook = os != "windows"
                ? new JsonObject { ["type"] = "command", ["command"] = CurlCommand(timeout, wsl ? "/mnt/c/Windows/System32/curl.exe" : "curl", wsl ? "wsl" : os, host, port, token), ["timeout"] = timeout }
                : new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = Hook.Url(port),
                    ["timeout"] = timeout,
                    ["headers"] = new JsonObject
                    {
                        [Marker] = token, [Hook.OsHeader] = os, [Hook.HostHeader] = host, [Hook.EntryHeader] = "$CLAUDE_CODE_ENTRYPOINT",
                        [Hook.AgentHeader] = "$MWA_AGENT",
                        [Hook.DesktopSessionHeader] = "$CLAUDE_CODE_HOST_SESSION_ID",
                    },
                    ["allowedEnvVars"] = new JsonArray("CLAUDE_CODE_ENTRYPOINT", "MWA_AGENT", "CLAUDE_CODE_HOST_SESSION_ID"),
                };
            result[e] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(hook) });
        }
        return result;
    }

    private static string CurlCommand(int timeout, string curl, string os, string host, int port, string token) =>
        $"{curl} -s -m {timeout} --data-binary @- -H \"Content-Type: application/json\" -H \"{Marker}: {token}\" -H \"{Hook.OsHeader}: {os}\" -H \"{Hook.HostHeader}: {host}\" " +
        $"-H \"{Hook.EntryHeader}: $CLAUDE_CODE_ENTRYPOINT\" -H \"{Hook.AgentHeader}: $MWA_AGENT\" {Hook.Url(port)} || true";

    /// <summary>
    /// Команда для терминала WSL или удалённого Linux: дописывает (remove — убирает) наши хуки в ~/.claude/settings.json там.
    /// Имя машины подставляется там же (@HOST@).
    /// </summary>
    public static string ShellCommand(int port, string token, bool wsl, bool remove = false)
    {
        var hooks = remove ? "{}" : HooksFor("linux", "@HOST@", port, token, wsl).ToJsonString();
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(hooks));
        return $$"""
            python3 - <<'CATCLAUDED'
            import json, os, base64, shutil, socket
            d = os.environ.get('CLAUDE_CONFIG_DIR') or os.path.expanduser('~/.claude')
            p = os.path.join(d, 'settings.json'); os.makedirs(d, exist_ok=True)
            s = json.load(open(p, encoding='utf-8')) if os.path.exists(p) and os.path.getsize(p) else {}
            if os.path.exists(p) and not os.path.exists(p + '{{BackupSuffix}}'): shutil.copy(p, p + '{{BackupSuffix}}')
            new = json.loads(base64.b64decode('{{b64}}').decode().replace('@HOST@', socket.gethostname()))
            h = s.setdefault('hooks', {})
            for ev in list(h):
                h[ev] = [g for g in h[ev] if '{{Marker}}' not in json.dumps(g)]
                if not h[ev]: del h[ev]
            for ev, groups in new.items(): h.setdefault(ev, []).extend(groups)
            if not h: del s['hooks']
            json.dump(s, open(p, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
            print('CatClauded: {{(remove ? "хуки котиков убраны из" : "котики подключены,")}}', p)
            CATCLAUDED
            """.ReplaceLineEndings("\n");
    }
}
