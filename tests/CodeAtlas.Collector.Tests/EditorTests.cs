using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeAtlas.Collector.Tests.Support;

namespace CodeAtlas.Collector.Tests;

public sealed class EditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-editor-" + Guid.NewGuid().ToString("N"));
    private readonly string _recorder;
    private readonly string _argsFile;
    private readonly FixtureAssembly _fixture;

    public EditorTests()
    {
        Directory.CreateDirectory(_root);
        _argsFile = Path.Combine(_root, "editor-args.txt");
        _recorder = Script("editor.sh", $"#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{_argsFile}'\n");
        // The source file name contains shell syntax; it must reach the editor as one argument and run nothing.
        _fixture = FixtureAssembly.Build(Path.Combine(_root, "app"), FixtureAssembly.FixtureText, fileName: "Evil$(touch PWNED);x.cs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, body);
        // The stand-in editors are POSIX shell scripts; these tests run on Linux and macOS only.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private Task<CollectorHost> StartAsync(string? editor = null, params string[] extra) =>
        CollectorHost.StartAsync([$"--source-root={_root}", $"--editor-command={editor ?? _recorder}", .. extra]);

    private object Identity() => new { assembly = _fixture.DllPath, mvid = _fixture.Mvid, token = _fixture.Token("Fixture.Orders", "Count", "String") };

    private static async Task<string> TokenAsync(CollectorHost host) =>
        (await host.Client.GetFromJsonAsync<JsonElement>("api/session")).GetProperty("token").GetString()!;

    private static HttpRequestMessage Open(object body, string? token, string? origin = null, string? host = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/open") { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Add("X-CodeAtlas-Token", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        return request;
    }

    private static int LineOf(string fragment) =>
        FixtureAssembly.FixtureText.Replace("\r\n", "\n").Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains(fragment)).i + 1;

    [Fact]
    public async Task Opens_the_verified_file_at_the_declaration_line_with_one_argument_per_value()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.SendAsync(Open(Identity(), await TokenAsync(host)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["-r", "-g", $"{_fixture.SourcePath}:{LineOf("public int Count(string a)")}"], File.ReadAllLines(_argsFile));
        Assert.Empty(Directory.GetFiles(_root, "PWNED", SearchOption.AllDirectories));
        Assert.False(File.Exists("PWNED"));
    }

    [Fact]
    public async Task Rider_gets_its_own_argument_shape()
    {
        await using var host = await StartAsync(extra: "--editor=rider");

        (await host.Client.SendAsync(Open(Identity(), await TokenAsync(host)))).Dispose();

        Assert.Equal(["--line", LineOf("public int Count(string a)").ToString(), _fixture.SourcePath], File.ReadAllLines(_argsFile));
    }

    [Fact]
    public async Task Without_the_session_token_it_is_401()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.SendAsync(Open(Identity(), token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(File.Exists(_argsFile));
    }

    [Fact]
    public async Task A_foreign_origin_is_403()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.SendAsync(Open(Identity(), await TokenAsync(host), origin: "http://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_public_url_origin_is_accepted_behind_a_path_proxy()
    {
        await using var host = await StartAsync(extra: "--public-url=https://code.example/absproxy/4318/");
        using var session = new HttpRequestMessage(HttpMethod.Get, "absproxy/4318/api/session");
        session.Headers.Host = "code.example";
        var token = (await (await host.Client.SendAsync(session)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var open = Open(Identity(), token, origin: "https://code.example", host: "code.example");
        open.RequestUri = new Uri("absproxy/4318/api/open", UriKind.Relative);

        using var response = await host.Client.SendAsync(open);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Only_json_is_accepted()
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/open") { Content = new StringContent("assembly=x", Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.Add("X-CodeAtlas-Token", await TokenAsync(host));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Paths_in_the_request_are_ignored_and_unknown_identities_are_422()
    {
        await using var host = await StartAsync();
        var body = new { assembly = "/etc/passwd", mvid = Guid.NewGuid(), token = 1, file = "/etc/passwd", line = 1 };

        using var response = await host.Client.SendAsync(Open(body, await TokenAsync(host)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False(File.Exists(_argsFile));
    }

    [Fact]
    public async Task The_session_token_is_not_readable_cross_origin()
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/session");
        request.Headers.Add("Origin", "http://evil.example");

        using var response = await host.Client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_missing_editor_is_reported_not_crashed()
    {
        await using var host = await StartAsync(editor: Path.Combine(_root, "no-such-editor"));

        using var response = await host.Client.SendAsync(Open(Identity(), await TokenAsync(host)));
        var info = await host.Client.GetFromJsonAsync<JsonElement>("api/info");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(info.GetProperty("editor").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task A_hanging_editor_times_out()
    {
        var hanging = Script("hang.sh", "#!/bin/sh\nsleep 30\n");
        await using var host = await StartAsync(editor: hanging, "--CodeAtlas:Editor:TimeoutSeconds=1");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        using var response = await host.Client.SendAsync(Open(Identity(), await TokenAsync(host)));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_editor_that_cannot_be_started_is_reported_not_crashed()
    {
        var notExecutable = Path.Combine(_root, "not-executable.sh");
        File.WriteAllText(notExecutable, "#!/bin/sh\n"); // no execute permission

        await using var host = await StartAsync(editor: notExecutable);
        using var response = await host.Client.SendAsync(Open(Identity(), await TokenAsync(host)));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("could not be started", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true, "code.cmd")]
    [InlineData(false, "code")]
    public void Windows_lookup_uses_PATHEXT_and_never_picks_the_extensionless_script(bool windows, string expected)
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "code"), "");
        File.WriteAllText(Path.Combine(bin, "code.cmd"), "");

        var found = CodeAtlas.Collector.Editor.EditorLauncher.FindOnPath("code", bin, ".COM;.EXE;.BAT;.CMD", windows);

        Assert.Equal(Path.Combine(bin, expected), found);
    }
}
