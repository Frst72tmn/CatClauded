using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CatClauded.Core;
using CatClauded.Core.Platform;

namespace CatClauded.Tests;

/// <summary>Сервер хуков живьём на 127.0.0.1: токен, ответ на PermissionRequest, пустой ответ, когда кот не может спросить.</summary>
public class HookServerTests : IAsyncLifetime
{
    private readonly ClaudeSessions _sessions = new();
    private readonly CatSettings _settings = new() { Token = "tok", Port = FreePort() };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private HookServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new HookServer(_sessions, () => _settings);
        await _server.StartAsync();
        Assert.Null(_server.Problem);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _http.Dispose();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private Task<HttpResponseMessage> Post(string body, string token = "tok", string os = "windows", string entry = "cli")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Hook.Url(_settings.Port)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(Hook.TokenHeader, token);
        request.Headers.Add(Hook.OsHeader, os);
        request.Headers.Add(Hook.HostHeader, "devbox");
        request.Headers.Add(Hook.EntryHeader, entry);
        return _http.SendAsync(request);
    }

    [Fact]
    public async Task WrongTokenIsRejected()
    {
        var r = await Post(ClaudeSessionsTests.Ev("SessionStart"), token: "bad");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Empty(_sessions.Cats(_ => true, true));
    }

    [Fact]
    public async Task EventsAnswerEmptyAndMakeCats()
    {
        var r = await Post(ClaudeSessionsTests.Ev("SessionStart"), os: "linux", entry: "sdk-cli");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync());
        var cat = Assert.Single(_sessions.Cats(_ => true, true));
        Assert.Equal("Терминал · Linux (devbox)", cat.Origin.Label);

        await Post(ClaudeSessionsTests.Ev("SessionStart", session: "d"), entry: "claude-desktop");
        Assert.Contains(_sessions.Cats(_ => true, true), c => c.Origin.Label == "Десктоп · Windows");
    }

    [Fact]
    public async Task PermissionRequestWaitsForButton()
    {
        var pending = Post(ClaudeSessionsTests.Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"ls"}"""));
        CatAsk? ask = null;
        for (var i = 0; i < 100 && ask is null; i++)
        {
            await Task.Delay(50);
            ask = _sessions.Cats(_ => true, true).FirstOrDefault()?.Ask;
        }
        Assert.NotNull(ask);
        Assert.False(pending.IsCompleted);
        _sessions.Decide(ask.AskId!.Value, allow: false);
        var r = await pending;
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
        var json = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.Equal("deny", json["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoJumpMeansClaudeDecidesAtOnce()
    {
        _settings.Jump = false;
        var r = await Post(ClaudeSessionsTests.Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"ls"}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HiddenOriginMeansClaudeDecidesAtOnce()
    {
        _settings.Wsl = false;
        var r = await Post(ClaudeSessionsTests.Ev("PermissionRequest", ""","tool_name":"Bash","tool_input":{"command":"ls"}"""), os: "wsl");
        Assert.Equal("", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BusyPortIsReportedNotThrown()
    {
        var second = new HookServer(new ClaudeSessions(), () => _settings);
        await second.StartAsync();
        Assert.Contains(_settings.Port.ToString(), second.Problem);
        await second.DisposeAsync();
    }
}

public class PlatformParsingTests
{
    [Fact]
    public void ParsesProcStatWithParensInName()
    {
        Assert.Equal(1200, LinuxProcesses.ParentFromStat("1234 (my (odd) name) S 1200 1234 1234 0 -1"));
        Assert.Null(LinuxProcesses.ParentFromStat("garbage"));
    }

    [Fact]
    public void FindsSocketInodeInProcNetTcp()
    {
        // 127.0.0.1:51000 → 127.0.0.1:47322 (0xC738 → 0xB8DA)
        const string line = "   3: 0100007F:C738 0100007F:B8DA 01 00000000:00000000 00:00000000 00000000  1000        0 987654 1 0000000000000000 20 4 30 10 -1";
        Assert.Equal(987654, LinuxProcesses.InodeFromTcpLine(line, 51000, 47322));
        Assert.Null(LinuxProcesses.InodeFromTcpLine(line, 47322, 51000));
    }

    [Fact]
    public void SettingsStoreCreatesTokenAndPersists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "catclauded-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            Assert.Equal(48, store.Current.Token.Length);
            Assert.Equal(47322, store.Current.Port);
            store.Update(s => { s.Port = 50000; s.BedX = 10; });
            var again = new SettingsStore(dir);
            Assert.Equal(50000, again.Current.Port);
            Assert.Equal(10, again.Current.BedX);
            Assert.Equal(store.Current.Token, again.Current.Token);
        }
        finally { Directory.Delete(dir, true); }
    }
}
