using System.Text.Json;
using System.Text.Json.Nodes;
using CatClauded.Core;

namespace CatClauded.Tests;

public class ClaudeSessionsTests
{
    private static readonly ClaudeOrigin Desktop = new(ClaudeSource.Desktop, ClaudeOs.Windows);

    internal static ClaudeHookCall Call(string json, ClaudeOrigin? origin = null, ProcessOwner? owner = null) =>
        new(JsonDocument.Parse(json).RootElement.Clone(), origin ?? Desktop, owner);

    internal static string Ev(string name, string extra = "", string session = "s1") =>
        $$"""{"session_id":"{{session}}","cwd":"E:\\Work\\proj","hook_event_name":"{{name}}"{{extra}}}""";

    private static IReadOnlyList<CatInfo> Cats(ClaudeSessions s) => s.Cats(_ => true, callWhenIdle: true);

    [Fact]
    public async Task SessionBecomesCatThatWorksAndFinishes()
    {
        var s = new ClaudeSessions();
        await s.HandleAsync(Call(Ev("SessionStart")), TimeSpan.FromSeconds(1), CancellationToken.None);
        var cat = Assert.Single(Cats(s));
        Assert.Equal("proj", cat.Title);
        Assert.Equal("Десктоп · Windows", cat.Origin.Label);
        Assert.Equal("▣", cat.Origin.Badge);
        Assert.Equal(CatState.Sleeping, cat.State);

        await s.HandleAsync(Call(Ev("UserPromptSubmit", ""","prompt":"Почини сборку" """)), TimeSpan.FromSeconds(1), CancellationToken.None);
        cat = Assert.Single(Cats(s));
        Assert.Equal(CatState.Working, cat.State);
        Assert.Equal("Почини сборку", cat.Title);

        var stopped = DateTimeOffset.UtcNow;
        await s.HandleAsync(Call(Ev("Stop")), TimeSpan.FromSeconds(1), CancellationToken.None, stopped);
        Assert.Equal(CatState.Finished, Assert.Single(s.Cats(_ => true, true, stopped)).State);
        Assert.Equal(CatState.Sleeping, Assert.Single(s.Cats(_ => true, true, stopped + ClaudeSessions.FinishedFor)).State);

        await s.HandleAsync(Call(Ev("SessionEnd")), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Empty(Cats(s));
    }

    [Fact]
    public async Task EachSessionIsItsOwnCatWithStableCoat()
    {
        var s = new ClaudeSessions();
        await s.HandleAsync(Call(Ev("SessionStart", session: "a")), TimeSpan.Zero, CancellationToken.None);
        await s.HandleAsync(Call(Ev("SessionStart", session: "b")), TimeSpan.Zero, CancellationToken.None);
        var cats = Cats(s);
        Assert.Equal(2, cats.Count);
        Assert.NotEqual(cats[0].Coat, cats[1].Coat);
        await s.HandleAsync(Call(Ev("UserPromptSubmit", session: "a")), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(cats[0].Coat, Cats(s)[0].Coat);
    }

    [Fact]
    public async Task PermissionWaitsForTheCatAndAllows()
    {
        var s = new ClaudeSessions();
        var pending = s.HandleAsync(Call(Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"npm test","description":"Run tests"}""")),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        var cat = Assert.Single(Cats(s));
        Assert.Equal(CatState.Asking, cat.State);
        Assert.Equal("Выполнить команду: Run tests", cat.Ask!.Title);
        Assert.Equal("npm test", cat.Ask.Detail);
        Assert.False(cat.Ask.CanAlways);
        Assert.False(pending.IsCompleted);

        Assert.True(s.Decide(cat.Ask.AskId!.Value, allow: true));
        var answer = JsonNode.Parse((await pending)!)!;
        Assert.Equal("allow", answer["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
        Assert.Equal("PermissionRequest", answer["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>());
        Assert.Null(answer["hookSpecificOutput"]!["decision"]!["updatedPermissions"]);
        Assert.Null(Cats(s)[0].Ask);
        Assert.False(s.Decide(cat.Ask.AskId!.Value, allow: false)); // второй раз — уже снято
    }

    [Fact]
    public async Task AlwaysReturnsClaudesSuggestedRule()
    {
        var s = new ClaudeSessions();
        var pending = s.HandleAsync(Call(Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"ls"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"ls"}],"behavior":"allow","destination":"localSettings"}]""")),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        var ask = Cats(s)[0].Ask!;
        Assert.True(ask.CanAlways);
        Assert.True(s.DecideAlways(ask.AskId!.Value));
        var decision = JsonNode.Parse((await pending)!)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", decision["behavior"]!.GetValue<string>());
        Assert.Equal("localSettings", decision["updatedPermissions"]![0]!["destination"]!.GetValue<string>());
        Assert.Equal("ls", decision["updatedPermissions"]![0]!["rules"]![0]!["ruleContent"]!.GetValue<string>());
    }

    [Fact]
    public async Task EmptySuggestionsMeanNoAlwaysButton()
    {
        var s = new ClaudeSessions();
        _ = s.HandleAsync(Call(Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"ls"},"permission_suggestions":[]""")), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(Cats(s)[0].Ask!.CanAlways);
        s.Decide(Cats(s)[0].Ask!.AskId!.Value, true);
    }

    [Fact]
    public async Task DenyTellsClaudeWhy()
    {
        var s = new ClaudeSessions();
        var pending = s.HandleAsync(Call(Ev("PermissionRequest", ""","tool_name":"Write","tool_input":{"file_path":"C:\\a.txt"}""")), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("Записать файл", Cats(s)[0].Ask!.Title);
        s.Decide(Cats(s)[0].Ask!.AskId!.Value, allow: false);
        var decision = JsonNode.Parse((await pending)!)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("deny", decision["behavior"]!.GetValue<string>());
        Assert.Contains("заблокировал", decision["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnsweredInClaudeItselfReleasesTheHook()
    {
        var s = new ClaudeSessions();
        var pending = s.HandleAsync(Call(Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"ls"}""")), TimeSpan.FromSeconds(10), CancellationToken.None);
        await s.HandleAsync(Call(Ev("PostToolUse", ""","tool_name":"Bash" """)), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Null(await pending);
        Assert.Equal(CatState.Working, Cats(s)[0].State);
    }

    [Fact]
    public async Task TimeoutAndAbortFallBackToClaude()
    {
        var s = new ClaudeSessions();
        Assert.Null(await s.HandleAsync(Call(Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{}""")), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.Null(Cats(s)[0].Ask);

        using var cts = new CancellationTokenSource();
        var pending = s.HandleAsync(Call(Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{}""")), TimeSpan.FromSeconds(10), cts.Token);
        cts.Cancel();
        Assert.Null(await pending);
    }

    [Fact]
    public async Task IdlePromptCallsUnlessTurnedOffAndClearsWhenSeen()
    {
        var s = new ClaudeSessions();
        await s.HandleAsync(Call(Ev("Notification", ""","notification_type":"idle_prompt","message":"Claude is waiting for your input" """)), TimeSpan.FromSeconds(1), CancellationToken.None);
        var cat = Assert.Single(Cats(s));
        Assert.True(cat.Ask!.IsQuestion);
        Assert.Equal(CatState.Asking, cat.State);
        Assert.Null(s.Cats(_ => true, callWhenIdle: false)[0].Ask);
        s.Seen(cat.Id);
        Assert.Null(Cats(s)[0].Ask);
    }

    [Fact]
    public async Task FiltersByOriginAndForgetsDeadSessions()
    {
        var s = new ClaudeSessions(isAlive: pid => pid != 42);
        await s.HandleAsync(Call(Ev("SessionStart"), new ClaudeOrigin(ClaudeSource.Terminal, ClaudeOs.Linux, "devbox")), TimeSpan.FromSeconds(1), CancellationToken.None);
        await s.HandleAsync(Call("""{"session_id":"s2","hook_event_name":"SessionStart","cwd":"C:\\x"}""", Desktop, new ProcessOwner(42, 0, null, [42])),
            TimeSpan.FromSeconds(1), CancellationToken.None);
        var linux = Assert.Single(s.Cats(o => o.Os == ClaudeOs.Linux, true));
        Assert.Equal("Терминал · Linux (devbox)", linux.Origin.Label);
        Assert.Equal("›_ 🐧", linux.Origin.Badge);
        Assert.Single(Cats(s)); // процесс 42 закрыт — его кот ушёл
    }

    [Fact]
    public async Task DesktopSessionOpensByClaudeLink()
    {
        var s = new ClaudeSessions();
        await s.HandleAsync(Call(Ev("SessionStart", session: "t")), TimeSpan.Zero, CancellationToken.None);
        await s.HandleAsync(Call(Ev("SessionStart"), Desktop) with { DesktopSession = "local_61dd7372-e5bf-4302-82d5-c3bd2fc55844" }, TimeSpan.Zero, CancellationToken.None);
        var cats = Cats(s);
        Assert.Null(s.DesktopLinkOf(cats[0].Id)); // терминал — без ссылки, поднимаем окно
        Assert.Equal("claude://code/continue?session=local_61dd7372-e5bf-4302-82d5-c3bd2fc55844", s.DesktopLinkOf(cats[1].Id));
        Assert.True(ClaudeSessions.IsDesktopSessionId("local_abc-123"));
        Assert.False(ClaudeSessions.IsDesktopSessionId("local_a&b"));
        Assert.False(ClaudeSessions.IsDesktopSessionId("6e2c3d1a-0000"));
        Assert.False(ClaudeSessions.IsDesktopSessionId(""));
    }

    [Fact]
    public async Task SilentSessionsAreForgotten()
    {
        var s = new ClaudeSessions();
        var at = DateTimeOffset.UtcNow;
        await s.HandleAsync(Call(Ev("SessionStart")), TimeSpan.Zero, CancellationToken.None, at);
        Assert.Single(s.Cats(_ => true, true, at + TimeSpan.FromHours(1)));
        Assert.Empty(s.Cats(_ => true, true, at + ClaudeSessions.Forget + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SettingsShowCatsByOrigin()
    {
        var c = new CatSettings { DesktopWindows = false, Wsl = false };
        Assert.False(c.Shows(new ClaudeOrigin(ClaudeSource.Desktop, ClaudeOs.Windows)));
        Assert.True(c.Shows(new ClaudeOrigin(ClaudeSource.Terminal, ClaudeOs.Windows)));
        Assert.True(c.Shows(new ClaudeOrigin(ClaudeSource.Ide, ClaudeOs.Windows)));
        Assert.False(c.Shows(new ClaudeOrigin(ClaudeSource.Terminal, ClaudeOs.Wsl)));
        Assert.True(c.Shows(new ClaudeOrigin(ClaudeSource.Terminal, ClaudeOs.Linux)));
        c.Enabled = false;
        Assert.False(c.Shows(new ClaudeOrigin(ClaudeSource.Terminal, ClaudeOs.Linux)));
    }

    [Theory]
    [InlineData("""{"tool_name":"Edit","tool_input":{"file_path":"a.cs"}}""", "Изменить файл", "a.cs")]
    [InlineData("""{"tool_name":"WebFetch","tool_input":{"url":"https://x"}}""", "Открыть страницу", "https://x")]
    [InlineData("""{"tool_name":"Grep","tool_input":{"pattern":"foo","path":"src"}}""", "Искать в файлах", "foo  в src")]
    [InlineData("""{"tool_name":"mcp__github__create_issue","tool_input":{}}""", "Инструмент github: create_issue", "{}")]
    public void DescribesToolsInRussian(string body, string title, string detail)
    {
        var (t, d) = ClaudeSessions.Describe(JsonDocument.Parse(body).RootElement);
        Assert.Equal(title, t);
        Assert.Equal(detail, d);
    }
}
