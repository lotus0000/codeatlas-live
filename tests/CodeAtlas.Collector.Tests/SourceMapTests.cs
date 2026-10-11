using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using CodeAtlas.Collector.Source;
using CodeAtlas.Collector.Tests.Support;

namespace CodeAtlas.Collector.Tests;

public sealed class SourceMapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-sourcemap-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureAssembly _fixture;
    private readonly PdbSourceMap _map;

    public SourceMapTests()
    {
        _fixture = FixtureAssembly.Build(Path.Combine(_root, "app"), FixtureAssembly.FixtureText);
        _map = new PdbSourceMap([_root]);
    }

    public void Dispose()
    {
        _map.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static int LineOf(string text, string fragment) =>
        text.Replace("\r\n", "\n").Split('\n').Select((line, i) => (line, i)).First(x => x.line.Contains(fragment)).i + 1;

    private SourceLocation Resolve(int token, Guid? mvid = null, string? dll = null) =>
        _map.Resolve(dll ?? _fixture.DllPath, mvid ?? _fixture.Mvid, token);

    [Theory]
    [InlineData("Count", "Int32", "public int Count(int a)", "method")]
    [InlineData("Count", "String", "public int Count(string a)", "method")]
    [InlineData("Echo", null, "public T Echo<T>(T value)", "method")]
    public void Ordinary_methods_and_each_overload_map_to_their_own_declaration(string method, string? firstParameter, string declaration, string kind)
    {
        var location = Resolve(_fixture.Token("Fixture.Orders", method, firstParameter));

        Assert.True(location.Found, location.Reason);
        Assert.Equal(LineOf(FixtureAssembly.FixtureText, declaration), location.NavigationLine);
        Assert.Equal(location.DisplayStart, location.NavigationLine);
        Assert.Equal(kind, location.Kind);
        Assert.Equal("pdb + roslyn", location.Mapping);
        Assert.Equal("app/Fixture.cs", location.RelativePath);
    }

    [Theory]
    [InlineData("LoadAsync", "public async Task<int> LoadAsync")]
    [InlineData("Numbers", "public IEnumerable<int> Numbers()")]
    public void Async_and_iterator_methods_map_through_their_state_machine(string method, string declaration)
    {
        var location = Resolve(_fixture.Token("Fixture.Orders", method));

        Assert.True(location.Found, location.Reason);
        Assert.True(location.StateMachine);
        Assert.Equal(LineOf(FixtureAssembly.FixtureText, declaration), location.NavigationLine);
        Assert.True(location.DisplayEnd > location.DisplayStart);
    }

    [Fact]
    public void A_lambda_maps_to_the_lambda_itself()
    {
        var location = Resolve(_fixture.LambdaToken("MakeLambda"));

        Assert.True(location.Found, location.Reason);
        Assert.Equal("lambda", location.Kind);
        Assert.Equal(LineOf(FixtureAssembly.FixtureText, "MakeLambda()"), location.NavigationLine);
    }

    [Fact]
    public void A_local_function_maps_to_its_declaration()
    {
        var location = Resolve(_fixture.LocalFunctionToken("Twice"));

        Assert.True(location.Found, location.Reason);
        Assert.Equal("local function", location.Kind);
        Assert.Equal(LineOf(FixtureAssembly.FixtureText, "static int Twice"), location.NavigationLine);
    }

    [Fact]
    public void A_primary_constructor_maps_to_its_type_declaration()
    {
        var location = Resolve(_fixture.ConstructorToken("Fixture.Prices"));

        Assert.True(location.Found, location.Reason);
        Assert.Equal("primary constructor", location.Kind);
        Assert.Equal(LineOf(FixtureAssembly.FixtureText, "public class Prices(decimal rate)"), location.NavigationLine);
    }

    [Fact]
    public void Crlf_and_bom_files_map_to_the_right_lines()
    {
        var other = FixtureAssembly.Build(Path.Combine(_root, "crlf"), FixtureAssembly.FixtureText, crlf: true, bom: true, assemblyName: "Crlf");

        var location = _map.Resolve(other.DllPath, other.Mvid, other.Token("Fixture.Orders", "Count", "String"));

        Assert.True(location.Found, location.Reason);
        Assert.Equal(LineOf(FixtureAssembly.FixtureText, "public int Count(string a)"), location.NavigationLine);
    }

    [Fact]
    public void An_interface_method_has_no_source_and_says_so()
    {
        var location = Resolve(_fixture.Token("Fixture.IOrders", "Save"));

        Assert.False(location.Found);
        Assert.Contains("no sequence points", location.Reason);
    }

    [Fact]
    public void A_source_file_changed_after_the_build_is_refused()
    {
        File.AppendAllText(_fixture.SourcePath, "\n// edited\n");

        var location = Resolve(_fixture.Token("Fixture.Orders", "Count", "Int32"));

        Assert.False(location.Found);
        Assert.Contains("changed after the build", location.Reason);
    }

    [Fact]
    public void A_rebuilt_assembly_is_refused()
    {
        var location = Resolve(_fixture.Token("Fixture.Orders", "Count", "Int32"), mvid: Guid.NewGuid());

        Assert.False(location.Found);
        Assert.Contains("rebuilt after this span was recorded", location.Reason);
    }

    [Fact]
    public void A_pdb_from_another_build_is_refused()
    {
        var other = FixtureAssembly.Build(Path.Combine(_root, "other"), FixtureAssembly.FixtureText + "\n// different\n");
        File.Copy(other.DllPath.Replace(".dll", ".pdb"), _fixture.DllPath.Replace(".dll", ".pdb"), overwrite: true);

        var location = Resolve(_fixture.Token("Fixture.Orders", "Count", "Int32"));

        Assert.False(location.Found);
        Assert.Contains("PDB", location.Reason);
    }

    [Fact]
    public void Assemblies_outside_the_source_roots_are_refused()
    {
        using var narrow = new PdbSourceMap([Path.Combine(_root, "elsewhere")]);

        var location = narrow.Resolve(_fixture.DllPath, _fixture.Mvid, _fixture.Token("Fixture.Orders", "Count", "Int32"));

        Assert.False(location.Found);
        Assert.Contains("outside the configured source roots", location.Reason);
    }

    [Fact]
    public void A_symlink_inside_a_root_pointing_outside_is_refused()
    {
        var outside = FixtureAssembly.Build(Path.Combine(Path.GetTempPath(), "codeatlas-outside-" + Guid.NewGuid().ToString("N")), FixtureAssembly.FixtureText);
        var link = Path.Combine(_root, "link.dll");
        File.CreateSymbolicLink(link, outside.DllPath);

        var location = _map.Resolve(link, outside.Mvid, outside.Token("Fixture.Orders", "Count", "Int32"));

        Assert.False(location.Found);
        Assert.Contains("outside the configured source roots", location.Reason);
    }

    [Fact]
    public void A_symlinked_directory_inside_a_root_pointing_outside_is_refused()
    {
        var outside = FixtureAssembly.Build(Path.Combine(Path.GetTempPath(), "codeatlas-outside-" + Guid.NewGuid().ToString("N")), FixtureAssembly.FixtureText);
        var linkedDirectory = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, Path.GetDirectoryName(outside.DllPath)!);

        var location = _map.Resolve(Path.Combine(linkedDirectory, "Fixture.dll"), outside.Mvid, outside.Token("Fixture.Orders", "Count", "Int32"));

        // Refused before anything outside the root (the assembly and its PDB) is opened.
        Assert.False(location.Found);
        Assert.Equal("The assembly is outside the configured source roots.", location.Reason);
    }

    [Fact]
    public void Code_from_a_source_generator_is_refused_with_that_reason()
    {
        var generated = FixtureAssembly.Build(Path.Combine(_root, "gen"), FixtureAssembly.FixtureText, assemblyName: "Gen",
            sourcePathOverride: Path.Combine(_root, "gen", "obj", "Generated.g.cs"));

        var location = _map.Resolve(generated.DllPath, generated.Mvid, generated.Token("Fixture.Orders", "Count", "Int32"));

        Assert.False(location.Found);
        Assert.Contains("source generator", location.Reason);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("out-of-range")]
    public void Tokens_that_are_not_methods_of_this_assembly_are_refused(string kind)
    {
        var token = kind == "type" ? _fixture.TypeToken("Fixture.Orders") : 0x06FFFFFF;

        var location = Resolve(token);

        Assert.False(location.Found);
    }

    [Fact]
    public void No_file_handles_stay_open_after_a_lookup()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Reads /proc/self/fd.");
        Assert.True(Resolve(_fixture.Token("Fixture.Orders", "Count", "Int32")).Found);

        var open = Directory.GetFiles("/proc/self/fd")
            .Select(fd => { try { return new FileInfo(fd).LinkTarget; } catch (IOException) { return null; } })
            .Where(target => target is not null && target.StartsWith(_root, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(open);
    }

    [Fact]
    public async Task Lookups_while_the_assembly_is_rebuilt_never_throw()
    {
        var directory = Path.Combine(_root, "rebuild");
        var first = FixtureAssembly.Build(directory, FixtureAssembly.FixtureText, assemblyName: "Rebuilt");
        var token = first.Token("Fixture.Orders", "Count", "Int32");
        var errors = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { _map.Resolve(first.DllPath, first.Mvid, token); }
                catch (Exception e) { errors.Enqueue(e); }
            }
        })).ToArray();

        for (var i = 0; i < 15; i++)
        {
            FixtureAssembly.Build(directory, FixtureAssembly.FixtureText + $"\n// build {i}\n", assemblyName: "Rebuilt");
            await Task.Delay(10);
        }
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task The_source_endpoint_returns_the_verified_file()
    {
        await using var host = await CollectorHost.StartAsync($"--source-root={_root}");
        var query = $"assembly={Uri.EscapeDataString(_fixture.DllPath)}&mvid={_fixture.Mvid}&token={_fixture.Token("Fixture.Orders", "Count", "Int32")}";

        var result = await host.Client.GetFromJsonAsync<JsonElement>($"api/source?{query}");

        Assert.True(result.GetProperty("location").GetProperty("found").GetBoolean());
        Assert.Equal(File.ReadAllText(_fixture.SourcePath), result.GetProperty("text").GetString());
    }
}
