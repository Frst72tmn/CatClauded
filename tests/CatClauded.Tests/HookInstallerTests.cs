using System.Text.Json.Nodes;
using CatClauded.Core;

namespace CatClauded.Tests;

public class HookInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "catclauded-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void MergeReplacesOnlyOurHooks()
    {
        var settings = JsonNode.Parse("""
            {"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"say done"}]},{"hooks":[{"type":"http","url":"x","headers":{"X-CatClauded-Token":"old"}}]},
              {"hooks":[{"type":"http","url":"y","headers":{"X-MWA-Token":"mwa"}}]}],
             "SessionEnd":[{"hooks":[{"type":"http","headers":{"X-CatClauded-Token":"old"}}]}]}}
            """)!.AsObject();
        var ours = JsonNode.Parse("""{"Stop":[{"hooks":[{"type":"http","headers":{"X-CatClauded-Token":"new"}}]}]}""")!.AsObject();
        var merged = HookInstaller.Merge(settings, ours).ToJsonString();
        Assert.Contains("say done", merged);
        Assert.Contains("\"new\"", merged);
        Assert.Contains("X-MWA-Token", merged); // хуки MyWorkAssist — чужие, не трогаем
        Assert.DoesNotContain("\"old\"", merged);
        Assert.DoesNotContain("SessionEnd", merged);
        Assert.Contains("opus", merged);

        var removed = HookInstaller.Merge(JsonNode.Parse(merged)!.AsObject(), null).ToJsonString();
        Assert.DoesNotContain("X-CatClauded-Token", removed);
        Assert.Contains("say done", removed);
        Assert.Contains("X-MWA-Token", removed);
    }

    [Fact]
    public void RemovingFromSettingsWithOnlyOurHooksDropsHooksKey()
    {
        var settings = new JsonObject { ["hooks"] = HookInstaller.HooksFor("windows", "pc", 47322, "t") };
        var removed = HookInstaller.Merge(settings, null);
        Assert.Null(removed["hooks"]);
    }

    [Fact]
    public void WindowsHooksAreHttpWithMarker()
    {
        var hooks = HookInstaller.HooksFor("windows", "PC", 47322, "secret");
        Assert.Equal(HookInstaller.Events.Length, hooks.Count);
        var permission = hooks["PermissionRequest"]![0]!["hooks"]![0]!;
        Assert.Equal("http", permission["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:47322/claude-hook", permission["url"]!.GetValue<string>());
        Assert.Equal(3600, permission["timeout"]!.GetValue<int>());
        Assert.Equal("secret", permission["headers"]!["X-CatClauded-Token"]!.GetValue<string>());
        Assert.Equal("$CLAUDE_CODE_ENTRYPOINT", permission["headers"]!["X-CatClauded-Entry"]!.GetValue<string>());
        Assert.Equal("CLAUDE_CODE_ENTRYPOINT", permission["allowedEnvVars"]![0]!.GetValue<string>());
        Assert.Equal("$MWA_AGENT", permission["headers"]!["X-CatClauded-Agent"]!.GetValue<string>());
        Assert.Equal("MWA_AGENT", permission["allowedEnvVars"]![1]!.GetValue<string>());
        Assert.Equal("$CLAUDE_CODE_HOST_SESSION_ID", permission["headers"]!["X-CatClauded-Desktop-Session"]!.GetValue<string>());
        Assert.Equal("CLAUDE_CODE_HOST_SESSION_ID", permission["allowedEnvVars"]![2]!.GetValue<string>());
        Assert.Equal(10, hooks["Stop"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void LinuxHooksAreQuietCurl()
    {
        var hooks = HookInstaller.HooksFor("linux", "devbox", 47322, "secret");
        var hook = hooks["SessionStart"]![0]!["hooks"]![0]!;
        Assert.Equal("command", hook["type"]!.GetValue<string>());
        var command = hook["command"]!.GetValue<string>();
        Assert.StartsWith("curl -s -m 10 ", command);
        Assert.EndsWith("http://127.0.0.1:47322/claude-hook || true", command);
        Assert.Contains("X-CatClauded-Token: secret", command);
        Assert.Contains("X-CatClauded-OS: linux", command);
        Assert.Contains("X-CatClauded-Host: devbox", command);
        Assert.Contains("X-CatClauded-Agent: $MWA_AGENT", command);

        var wsl = HookInstaller.HooksFor("linux", "@HOST@", 47322, "secret", wsl: true)["PermissionRequest"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.StartsWith("/mnt/c/Windows/System32/curl.exe -s -m 3600 ", wsl);
        Assert.Contains("X-CatClauded-OS: wsl", wsl);
    }

    [Fact]
    public void WriteKeepsBackupAndForeignSettingsAndIsIdempotent()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {
              // комментарий пользователя
              "model": "opus",
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "say done" } ] } ] },
            }
            """);
        var original = File.ReadAllText(path);

        Assert.Equal(path + ".mwa-backup", HookInstaller.Write(path, HookInstaller.HooksFor("windows", "PC", 47322, "t1")));
        Assert.Equal(original, File.ReadAllText(path + ".mwa-backup"));
        Assert.True(HookInstaller.IsInstalled(path));

        Assert.Null(HookInstaller.Write(path, HookInstaller.HooksFor("windows", "PC", 47323, "t2"))); // копия уже есть
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count); // чужой + наш, без дублей
        Assert.DoesNotContain("t1", root.ToJsonString());
        Assert.Contains("47323", root.ToJsonString());
        foreach (var e in HookInstaller.Events) Assert.Single(root["hooks"]![e]!.AsArray(), g => g!.ToJsonString().Contains(HookInstaller.Marker));

        HookInstaller.Write(path, null);
        Assert.False(HookInstaller.IsInstalled(path));
        root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Contains("say done", root.ToJsonString());
        Assert.Single(root["hooks"]!.AsObject());
    }

    [Fact]
    public void WriteCreatesMissingSettings()
    {
        var path = Path.Combine(_dir, "new", "settings.json");
        Assert.Null(HookInstaller.Write(path, HookInstaller.HooksFor("linux", "h", 47322, "t")));
        Assert.True(HookInstaller.IsInstalled(path));
        Assert.False(File.Exists(path + ".mwa-backup"));
    }

    [Fact]
    public void ShellCommandCarriesHooksAsBase64()
    {
        var cmd = HookInstaller.ShellCommand(47322, "tok", wsl: true);
        Assert.StartsWith("python3 - <<'CATCLAUDED'\n", cmd);
        Assert.EndsWith("\nCATCLAUDED", cmd);
        Assert.Contains("X-CatClauded-Token", cmd);
        Assert.Contains(".mwa-backup", cmd);
        var b64 = cmd.Split("b64decode('")[1].Split("')")[0];
        var hooks = JsonNode.Parse(Convert.FromBase64String(b64))!;
        Assert.Contains("curl.exe", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Contains("@HOST@", hooks.ToJsonString());

        var remove = HookInstaller.ShellCommand(47322, "tok", wsl: false, remove: true);
        Assert.Contains(Convert.ToBase64String("{}"u8.ToArray()), remove);
        Assert.Contains("убраны", remove);
    }
}
