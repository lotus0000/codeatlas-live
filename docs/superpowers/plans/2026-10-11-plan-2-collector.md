# Plan 2 — Collector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `CodeAtlas.Collector`, one loopback-only process that receives official OTLP traces, keeps them in a bounded in-memory store, pushes live updates over SignalR, turns raw spans into an analyzed execution tree with evidence kinds, gaps and a deterministic explanation, maps method identities to verified source through Portable PDBs and Roslyn, and opens that source in the developer's editor through a guarded endpoint.

**Architecture:** `CollectorApp.Create(args)` builds a Minimal API app. Ingest parses OTLP protobuf/JSON into `ExportTraceServiceRequest` (`CodeAtlas.Otlp`), the `TraceStore` upserts spans by id, and `LiveNotifier` pushes trace summaries to `/live`. All interpretation lives in `Analysis/` (tree, evidence, gaps, explanation) so the viewer only renders. `Source/PdbSourceMap` resolves `assembly + MVID + token` and refuses with a reason on any mismatch. `Editor/EditorLauncher` starts a fixed executable with an argument list. Security is layered: loopback binding, host filtering, same-origin + session token for `/api/open`, CORS only on `/v1/traces`.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs + SignalR, `CodeAtlas.Otlp` (Plan 1), System.Reflection.Metadata, Microsoft.CodeAnalysis.CSharp 5.9.0, xunit.v3 4.0.1 on Microsoft.Testing.Platform, Microsoft.AspNetCore.SignalR.Client 10.0.12.

**Spec:** `docs/superpowers/specs/2026-10-10-codeatlas-live-mvp-design.md` (§4, §7, §8 Explain, §10, §11)

## Global Constraints

- Binds only to loopback (`127.0.0.1`, `localhost`, `[::1]`); any other address in `Urls` stops start-up with a message. Default `http://127.0.0.1:4318`.
- OTLP schema: `CodeAtlas.Otlp` (opentelemetry-proto v1.5.0). `POST /v1/traces` accepts `application/x-protobuf` and `application/json`, optional `Content-Encoding: gzip`, and answers in the request's encoding. Decompressed bodies over 16 MiB → 413.
- Store limits (defaults): 500 traces, 2,000 spans per trace, attribute strings cut at 4,096 characters.
- JSON: camelCase; nanosecond timestamps are **strings**.
- All viewer-facing paths are relative to the app root: `api/…`, `live`, `v1/traces`; `--public-url` adds a path base.
- Exact gap texts (spec §7.2):
  - `Parent span not received yet: it may still be in an export batch, sampled out, or its export failed.`
  - `No server span received for this request. The backend may not be instrumented, the span may be sampled out, or its export was delayed or failed.` (HTTP client spans only, ≥ 5 s after they ended)
  - `{N} ms of this span is not covered by any child span. That time is unexplained by telemetry; it does not mean nothing ran.` (N with one decimal, only when ≥ 1 ms)
  - `The handler's execution was not recorded: there is no handler or action span. A filter such as validation may have ended the request first, or WithCodeAtlasHandlerSpan is not enabled for this endpoint.`
- Evidence kinds exposed to the viewer: node `evidence` is always `executed` (a span exists); a node's `code.evidence` is `executed` or `routing`; `correlation` is `explicit`, `heuristic` or absent; gaps are listed separately. Explanation sentences never state anything that is not in a span.
- `/api/open` accepts only `{ assembly, mvid, token }`; the editor executable is resolved once at start-up and started with `ProcessStartInfo.ArgumentList` (no shell), 10 s timeout.
- No outbound network calls.
- Tests: xunit.v3 4.0.1, `OutputType Exe`, `NoWarn xUnit1051`; collector hosts run on real Kestrel at `127.0.0.1:0`.

## Review Focus

- Malicious or broken parent links (a span that is its own parent, two spans parenting each other) — the analyzer must terminate and show them as roots with a gap → Task 4 test `Parent_cycles_terminate_and_are_reported`.
- Huge or compressed-bomb OTLP bodies — must answer 413 without exhausting memory → Task 2 test `A_gzip_bomb_is_rejected_with_413`.
- Ingest and reads at the same time from several threads — no exceptions, no lost spans → Task 3 test `Concurrent_ingest_and_reads_are_safe`.
- Source files with CRLF line endings or a UTF-8 BOM — lines must still match the PDB → Task 6 test `Crlf_and_bom_files_map_to_the_right_lines`.
- An editor command that never exits — the request must fail after the timeout, not hang → Task 7 test `A_hanging_editor_times_out`.

## File Structure

```
src/CodeAtlas.Collector/
  CodeAtlas.Collector.csproj      Web SDK, PackAsTool (command `codeatlas`)
  Program.cs                      CollectorApp.Create(args).Run()
  CollectorApp.cs                 configuration, security, service and endpoint wiring
  CollectorOptions.cs             validated settings
  Ingest/OtlpReader.cs            request body → ExportTraceServiceRequest
  Ingest/IngestEndpoints.cs       POST /v1/traces
  Store/SpanRecord.cs             stored span DTOs
  Store/TraceStore.cs             bounded upsert store
  Live/LiveHub.cs                 hub + LiveNotifier
  Analysis/TraceViews.cs          TraceView / NodeView / Gap / TraceSummary DTOs
  Analysis/TraceAnalyzer.cs       tree, kinds, evidence, gaps, summaries
  Analysis/Explainer.cs           deterministic explanation
  Api/TraceEndpoints.cs           GET api/info, api/traces, api/traces/{id}
  Source/PdbSourceMap.cs          identity → verified source location
  Source/SourceEndpoints.cs       GET api/source
  Editor/EditorLauncher.cs        fixed executable, argument list, timeout
  Editor/EditorEndpoints.cs       GET api/session, POST api/open
  wwwroot/index.html              placeholder until Plan 4 builds the viewer here
tests/CodeAtlas.Collector.Tests/
  CodeAtlas.Collector.Tests.csproj
  AssemblyInfo.cs
  Support/CollectorHost.cs        start a collector on 127.0.0.1:0
  Support/Otlp.cs                 build OTLP requests in tests
  Support/FixtureAssembly.cs      compile source with a Portable PDB, look up tokens
  Fixtures/otlp/js-export.json    OTLP/JSON as the browser package sends it
  Fixtures/sourcemap/Fixture.cs.txt
  StartupTests.cs  IngestTests.cs  RealExporterTests.cs  StoreTests.cs  LiveTests.cs
  AnalyzerTests.cs  ExplainerTests.cs  SourceMapTests.cs  EditorTests.cs
scripts/verify-tool.sh
```

---

### Task 1: Collector skeleton, loopback-only start, host filtering, path base

**Files:**
- Create: `src/CodeAtlas.Collector/CodeAtlas.Collector.csproj`, `Program.cs`, `CollectorApp.cs`, `CollectorOptions.cs`, `Api/TraceEndpoints.cs` (info only for now), `wwwroot/index.html`
- Test: `tests/CodeAtlas.Collector.Tests/CodeAtlas.Collector.Tests.csproj`, `AssemblyInfo.cs`, `Support/CollectorHost.cs`, `StartupTests.cs`

**Interfaces:**
- Produces:
  - `public static class CollectorApp { static readonly IReadOnlyDictionary<string,string> Switches; static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null); }`
  - `public sealed record CollectorOptions(IReadOnlyList<Uri> Urls, IReadOnlyList<string> SourceRoots, IReadOnlyList<string> AllowedOrigins, IReadOnlyList<string> AllowedHosts, Uri? PublicUrl, string EditorKind, string? EditorCommand, StoreLimits Limits) { static CollectorOptions From(IConfiguration configuration, string currentDirectory); string? PathBase { get; } }`
  - `public sealed record StoreLimits(int MaxTraces = 500, int MaxSpansPerTrace = 2000, int MaxAttributeLength = 4096)`
  - `GET api/info` → `{ version, sourceRoots, editor: { kind, available } }` (editor filled in Task 7; until then `available = false`)
  - test helper `CollectorHost.StartAsync(params string[] args)` with `App`, `Client`, `BaseUrl`

- [ ] **Step 1: Create the projects**

`src/CodeAtlas.Collector/CodeAtlas.Collector.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>codeatlas</ToolCommandName>
    <PackageId>CodeAtlas.Collector</PackageId>
    <Version>0.1.0</Version>
    <Description>CodeAtlas Live collector and viewer (local development tool).</Description>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.9.0" />
    <ProjectReference Include="../CodeAtlas.Otlp/CodeAtlas.Otlp.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Update="wwwroot/**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="CodeAtlas.Collector.Tests" />
  </ItemGroup>
</Project>
```

`src/CodeAtlas.Collector/wwwroot/index.html`:
```html
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>CodeAtlas Live</title></head>
<body><p>CodeAtlas collector is running. The viewer is built into this folder by Plan 4.</p></body>
</html>
```

`tests/CodeAtlas.Collector.Tests/CodeAtlas.Collector.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <NoWarn>$(NoWarn);xUnit1051</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.12" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.12" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.9.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/CodeAtlas.Collector/CodeAtlas.Collector.csproj" />
    <ProjectReference Include="../../src/CodeAtlas.AspNetCore/CodeAtlas.AspNetCore.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="Fixtures/**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`tests/CodeAtlas.Collector.Tests/AssemblyInfo.cs`:
```csharp
// One test class runs a real CodeAtlas.AspNetCore app in-process; its ActivityListener is process-wide.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
```

```bash
dotnet sln add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests
```

- [ ] **Step 2: Write the test host**

`tests/CodeAtlas.Collector.Tests/Support/CollectorHost.cs`:
```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.Collector.Tests.Support;

/// <summary>A real collector on 127.0.0.1 with a free port.</summary>
internal sealed class CollectorHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required string BaseUrl { get; init; }

    public static async Task<CollectorHost> StartAsync(params string[] args)
    {
        var app = CollectorApp.Create(["--urls=http://127.0.0.1:0", .. args], b => b.Logging.ClearProviders());
        await app.StartAsync();
        var baseUrl = app.Urls.First().TrimEnd('/');
        return new CollectorHost { App = app, Client = new HttpClient { BaseAddress = new Uri(baseUrl + "/") }, BaseUrl = baseUrl };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
```

- [ ] **Step 3: Write the failing start-up tests**

`tests/CodeAtlas.Collector.Tests/StartupTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeAtlas.Collector.Tests.Support;

namespace CodeAtlas.Collector.Tests;

public class StartupTests
{
    [Theory]
    [InlineData("http://0.0.0.0:4318")]
    [InlineData("http://192.168.1.10:4318")]
    [InlineData("http://example.com:4318")]
    public void Refuses_to_listen_anywhere_but_loopback(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CollectorApp.Create([$"--urls={url}"]));
        Assert.Contains("loopback", error.Message);
    }

    [Fact]
    public async Task Answers_info_on_loopback()
    {
        await using var host = await CollectorHost.StartAsync("--source-root=/tmp");

        var info = await host.Client.GetFromJsonAsync<JsonElement>("api/info");

        Assert.Equal("0.1.0", info.GetProperty("version").GetString());
        Assert.Equal(["/tmp"], info.GetProperty("sourceRoots").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Rejects_requests_for_other_host_names()
    {
        await using var host = await CollectorHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/info");
        request.Headers.Host = "evil.example";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_public_url_adds_its_host_and_path_base()
    {
        await using var host = await CollectorHost.StartAsync("--public-url=https://code.example/absproxy/4318/");
        using var request = new HttpRequestMessage(HttpMethod.Get, "absproxy/4318/api/info");
        request.Headers.Host = "code.example";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Serves_the_viewer_folder()
    {
        await using var host = await CollectorHost.StartAsync();

        var html = await host.Client.GetStringAsync("");

        Assert.Contains("CodeAtlas", html);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: build error `The name 'CollectorApp' does not exist in the current context`.

- [ ] **Step 5: Implement options, app and info endpoint**

`src/CodeAtlas.Collector/CollectorOptions.cs`:
```csharp
using Microsoft.Extensions.Configuration;

namespace CodeAtlas.Collector;

public sealed record StoreLimits(int MaxTraces = 500, int MaxSpansPerTrace = 2000, int MaxAttributeLength = 4096);

/// <summary>Validated settings. Throws on anything that would weaken the loopback-only guarantee.</summary>
public sealed record CollectorOptions(
    IReadOnlyList<Uri> Urls,
    IReadOnlyList<string> SourceRoots,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AllowedHosts,
    Uri? PublicUrl,
    string EditorKind,
    string? EditorCommand,
    StoreLimits Limits)
{
    private static readonly HashSet<string> LoopbackHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

    /// <summary>Path prefix the app is served under when behind a path proxy, e.g. "/absproxy/4318".</summary>
    public string? PathBase => PublicUrl is null || PublicUrl.AbsolutePath == "/" ? null : PublicUrl.AbsolutePath.TrimEnd('/');

    public static CollectorOptions From(IConfiguration configuration, string currentDirectory)
    {
        var urls = (configuration["urls"] ?? "http://127.0.0.1:4318")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : throw new InvalidOperationException($"'{u}' is not a URL."))
            .ToList();
        foreach (var url in urls)
            if (!LoopbackHosts.Contains(url.Host))
                throw new InvalidOperationException(
                    $"The CodeAtlas collector only listens on loopback addresses (127.0.0.1, localhost, [::1]); '{url}' is not one. " +
                    "To reach it from another machine, use a proxy you control, such as code-server's /absproxy/, and pass --public-url.");

        var publicUrl = configuration["CodeAtlas:PublicUrl"] is { Length: > 0 } raw
            ? Uri.TryCreate(raw, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"
                ? parsed
                : throw new InvalidOperationException($"--public-url '{raw}' is not an absolute http(s) URL.")
            : null;

        var roots = List(configuration["CodeAtlas:SourceRoots"]);
        var hosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };
        if (publicUrl is not null) hosts.Add(publicUrl.Host);

        return new CollectorOptions(
            urls,
            roots.Count > 0 ? roots.Select(Path.GetFullPath).ToList() : [Path.GetFullPath(currentDirectory)],
            List(configuration["CodeAtlas:AllowedOrigins"] ?? "http://localhost:5173,http://127.0.0.1:5173"),
            hosts,
            publicUrl,
            configuration["CodeAtlas:Editor:Kind"] ?? "code-server",
            configuration["CodeAtlas:Editor:Command"],
            new StoreLimits());
    }

    private static List<string> List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
```

`src/CodeAtlas.Collector/CollectorApp.cs`:
```csharp
using CodeAtlas.Collector.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.Collector;

public static class CollectorApp
{
    public const string Version = "0.1.0";
    public const string IngestCors = "otlp-ingest";

    public static readonly IReadOnlyDictionary<string, string> Switches = new Dictionary<string, string>
    {
        ["--source-root"] = "CodeAtlas:SourceRoots",
        ["--public-url"] = "CodeAtlas:PublicUrl",
        ["--allowed-origin"] = "CodeAtlas:AllowedOrigins",
        ["--editor"] = "CodeAtlas:Editor:Kind",
        ["--editor-command"] = "CodeAtlas:Editor:Command",
    };

    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Configuration.AddCommandLine(args, Switches.ToDictionary());
        configure?.Invoke(builder);

        var options = CollectorOptions.From(builder.Configuration, Directory.GetCurrentDirectory());
        builder.WebHost.UseUrls(options.Urls.Select(u => u.ToString()).ToArray());
        // Host filtering (on by default in WebApplication) blocks DNS-rebinding pages that resolve to 127.0.0.1.
        builder.Configuration["AllowedHosts"] = string.Join(';', options.AllowedHosts);
        builder.Services.AddSingleton(options);
        builder.Services.AddCors(cors => cors.AddPolicy(IngestCors, policy => policy
            .WithOrigins([.. options.AllowedOrigins]).WithMethods("POST").WithHeaders("content-type", "content-encoding")));
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        if (options.PathBase is { } pathBase) app.UsePathBase(pathBase);
        // WebApplication adds routing at the very start unless it is added explicitly; it must run after the path base.
        app.UseRouting();
        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        TraceEndpoints.Map(app);
        return app;
    }
}
```

`src/CodeAtlas.Collector/Api/TraceEndpoints.cs`:
```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.Collector.Api;

internal static class TraceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/info", (CollectorOptions options) => Results.Ok(new
        {
            version = CollectorApp.Version,
            sourceRoots = options.SourceRoots,
            editor = new { kind = options.EditorKind, available = false },
        }));
    }
}
```

`src/CodeAtlas.Collector/Program.cs`:
```csharp
using CodeAtlas.Collector;

CollectorApp.Create(args).Run();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: `total: 7, failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests CodeAtlas.Live.slnx
git commit -m "feat(collector): loopback-only app with host filtering, path base and info endpoint"
```

---

### Task 2: OTLP ingest

**Files:**
- Create: `src/CodeAtlas.Collector/Ingest/OtlpReader.cs`, `src/CodeAtlas.Collector/Ingest/IngestEndpoints.cs`, `src/CodeAtlas.Collector/Store/SpanRecord.cs`, `src/CodeAtlas.Collector/Store/TraceStore.cs` (upsert, limits, most-detailed-copy merge)
- Modify: `src/CodeAtlas.Collector/CollectorApp.cs` (register store, map ingest, add `api/traces/{id}/spans` for tests)
- Test: `tests/CodeAtlas.Collector.Tests/Support/Otlp.cs`, `tests/CodeAtlas.Collector.Tests/Fixtures/otlp/js-export.json`, `tests/CodeAtlas.Collector.Tests/IngestTests.cs`, `tests/CodeAtlas.Collector.Tests/StoreTests.cs`, `tests/CodeAtlas.Collector.Tests/RealExporterTests.cs`

**Interfaces:**
- Consumes: `CodeAtlas.Otlp` types and `OtlpJson.ParseTraces` (Plan 1).
- Produces:
  - `internal static class OtlpReader { const long MaxBody = 16 * 1024 * 1024; static Task<ExportTraceServiceRequest> ReadAsync(HttpRequest request); }` throwing `BadHttpRequestException(status 400|413|415)`
  - `public sealed record SpanEventRecord(string Name, [string] ulong TimeUnixNano, IReadOnlyDictionary<string, object?> Attributes)`
  - `public sealed record SpanRecord(string TraceId, string SpanId, string? ParentSpanId, string Name, string Kind, string Service, string Scope, [string] ulong StartUnixNano, [string] ulong EndUnixNano, string Status, string? StatusMessage, IReadOnlyDictionary<string, object?> Attributes, IReadOnlyList<SpanEventRecord> Events, IReadOnlyDictionary<string, object?> Resource)` — `Kind` ∈ `server|client|internal|producer|consumer|unspecified`, `Status` ∈ `unset|ok|error`
  - `public sealed class TraceStore(StoreLimits limits) { IReadOnlyList<string> Add(ExportTraceServiceRequest request); IReadOnlyList<SpanRecord>? Get(string traceId); IReadOnlyList<string> RecentTraceIds(int limit); bool IsTruncated(string traceId); }`
  - `GET api/traces/{traceId}/spans` → `SpanRecord[]` (raw; also used by the viewer's inspector)
  - test helper `Otlp.Span(...)`, `Otlp.Request(...)`, `Otlp.PostAsync(...)`

- [ ] **Step 1: Write the test support and the browser fixture**

`tests/CodeAtlas.Collector.Tests/Support/Otlp.cs`:
```csharp
using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.Collector.Tests.Support;

/// <summary>Builds OTLP requests. Times are milliseconds after a fixed epoch so tests read easily.</summary>
internal static class Otlp
{
    public const ulong Epoch = 1_700_000_000_000_000_000;

    public static Span Span(string traceId, string spanId, string? parent, string name, Span.Types.SpanKind kind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind.Internal,
        double startMs = 0, double endMs = 1, params (string Key, object Value)[] attributes)
    {
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Convert.FromHexString(traceId)),
            SpanId = ByteString.CopyFrom(Convert.FromHexString(spanId)),
            ParentSpanId = parent is null ? ByteString.Empty : ByteString.CopyFrom(Convert.FromHexString(parent)),
            Name = name,
            Kind = kind,
            StartTimeUnixNano = Epoch + (ulong)(startMs * 1_000_000),
            EndTimeUnixNano = Epoch + (ulong)(endMs * 1_000_000),
        };
        foreach (var (key, value) in attributes) span.Attributes.Add(Kv(key, value));
        return span;
    }

    public static KeyValue Kv(string key, object value) => new()
    {
        Key = key,
        Value = value switch
        {
            string s => new AnyValue { StringValue = s },
            bool b => new AnyValue { BoolValue = b },
            int or long => new AnyValue { IntValue = Convert.ToInt64(value) },
            double d => new AnyValue { DoubleValue = d },
            _ => new AnyValue { StringValue = value.ToString() },
        },
    };

    public static ExportTraceServiceRequest Request(string service, string scope, IEnumerable<Span> spans, params (string Key, object Value)[] resource)
    {
        var resourceSpans = new ResourceSpans { Resource = new Resource { Attributes = { Kv("service.name", service) } } };
        foreach (var (key, value) in resource) resourceSpans.Resource.Attributes.Add(Kv(key, value));
        var scopeSpans = new ScopeSpans { Scope = new InstrumentationScope { Name = scope } };
        scopeSpans.Spans.AddRange(spans);
        resourceSpans.ScopeSpans.Add(scopeSpans);
        return new ExportTraceServiceRequest { ResourceSpans = { resourceSpans } };
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, ExportTraceServiceRequest request)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return await client.PostAsync("v1/traces", content);
    }

    public static string Id(int n, int bytes) => n.ToString("x").PadLeft(bytes * 2, '0');
    public static string TraceId(int n) => Id(n, 16);
    public static string SpanId(int n) => Id(n, 8);
}
```

`tests/CodeAtlas.Collector.Tests/Fixtures/otlp/js-export.json` (the shape `@opentelemetry/exporter-trace-otlp-http` sends; Plan 3 adds a test that keeps this file in sync with the real serializer):
```json
{
  "resourceSpans": [
    {
      "resource": {
        "attributes": [
          { "key": "service.name", "value": { "stringValue": "sample-web" } },
          { "key": "telemetry.sdk.language", "value": { "stringValue": "webjs" } }
        ],
        "droppedAttributesCount": 0
      },
      "scopeSpans": [
        {
          "scope": { "name": "codeatlas-web" },
          "spans": [
            {
              "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
              "spanId": "00f067aa0ba902b7",
              "name": "interaction: Create order",
              "kind": 1,
              "startTimeUnixNano": "1700000000000000000",
              "endTimeUnixNano": "1700000000120000000",
              "attributes": [
                { "key": "codeatlas.interaction.name", "value": { "stringValue": "Create order" } },
                { "key": "codeatlas.correlation", "value": { "stringValue": "explicit" } }
              ],
              "droppedAttributesCount": 0,
              "events": [],
              "droppedEventsCount": 0,
              "status": { "code": 0 },
              "links": [],
              "droppedLinksCount": 0
            },
            {
              "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
              "spanId": "b7ad6b7169203331",
              "parentSpanId": "00f067aa0ba902b7",
              "name": "POST /api/orders",
              "kind": 3,
              "startTimeUnixNano": "1700000000010000000",
              "endTimeUnixNano": "1700000000110000000",
              "attributes": [
                { "key": "http.request.method", "value": { "stringValue": "POST" } },
                { "key": "url.path", "value": { "stringValue": "/api/orders" } },
                { "key": "http.response.status_code", "value": { "intValue": 201 } },
                { "key": "codeatlas.correlation", "value": { "stringValue": "explicit" } }
              ],
              "droppedAttributesCount": 0,
              "events": [],
              "droppedEventsCount": 0,
              "status": { "code": 0 },
              "links": [],
              "droppedLinksCount": 0
            }
          ]
        }
      ]
    }
  ]
}
```

- [ ] **Step 2: Write the failing ingest and store tests**

`tests/CodeAtlas.Collector.Tests/IngestTests.cs`:
```csharp
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

public class IngestTests
{
    private static ExportTraceServiceRequest OneSpan(int trace = 1) =>
        Otlp.Request("api", "Microsoft.AspNetCore", [Otlp.Span(Otlp.TraceId(trace), Otlp.SpanId(1), null, "GET /ping", SpanKind.Server, 0, 5, ("http.route", "/ping"))]);

    [Fact]
    public async Task Protobuf_is_stored_and_answered_in_protobuf()
    {
        await using var host = await CollectorHost.StartAsync();

        using var response = await Otlp.PostAsync(host.Client, OneSpan());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        ExportTraceServiceResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        var spans = await host.Client.GetFromJsonAsync<List<SpanRecord>>($"api/traces/{Otlp.TraceId(1)}/spans");
        var span = Assert.Single(spans!);
        Assert.Equal("server", span.Kind);
        Assert.Equal("api", span.Service);
        Assert.Equal("/ping", span.Attributes["http.route"]?.ToString());
        Assert.Equal(Otlp.Epoch + 5_000_000, span.EndUnixNano);
    }

    [Fact]
    public async Task Timestamps_are_strings_in_json()
    {
        await using var host = await CollectorHost.StartAsync();
        (await Otlp.PostAsync(host.Client, OneSpan())).Dispose();

        var json = await host.Client.GetStringAsync($"api/traces/{Otlp.TraceId(1)}/spans");

        Assert.Contains($"\"startUnixNano\":\"{Otlp.Epoch}\"", json);
    }

    [Fact]
    public async Task Browser_json_is_stored_and_answered_in_json()
    {
        await using var host = await CollectorHost.StartAsync();
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/otlp/js-export.json"));

        using var response = await host.Client.PostAsync("v1/traces", new StringContent(fixture, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var spans = await host.Client.GetFromJsonAsync<List<SpanRecord>>("api/traces/4bf92f3577b34da6a3ce929d0e0e4736/spans");
        Assert.Equal(2, spans!.Count);
        Assert.Equal("00f067aa0ba902b7", spans.Single(s => s.Kind == "client").ParentSpanId);
        Assert.Equal("sample-web", spans[0].Service);
    }

    [Fact]
    public async Task Gzip_bodies_are_accepted()
    {
        await using var host = await CollectorHost.StartAsync();
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(OneSpan().ToByteArray());
        using var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");

        using var response = await host.Client.PostAsync("v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await host.Client.GetFromJsonAsync<List<SpanRecord>>($"api/traces/{Otlp.TraceId(1)}/spans"));
    }

    [Fact]
    public async Task A_gzip_bomb_is_rejected_with_413()
    {
        await using var host = await CollectorHost.StartAsync();
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            for (var i = 0; i < 20; i++) gzip.Write(new byte[1024 * 1024]);
        using var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");

        using var response = await host.Client.PostAsync("v1/traces", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("application/x-protobuf", "not protobuf at all \u0001\u0002")]
    [InlineData("application/json", "{ not json")]
    [InlineData("application/json", """{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"zz"}]}]}]}""")]
    public async Task Malformed_bodies_get_400(string contentType, string body)
    {
        await using var host = await CollectorHost.StartAsync();

        using var response = await host.Client.PostAsync("v1/traces", new StringContent(body, Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Other_content_types_get_415()
    {
        await using var host = await CollectorHost.StartAsync();

        using var response = await host.Client.PostAsync("v1/traces", new StringContent("x", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://evil.example", false)]
    public async Task Browsers_may_post_only_from_allowed_origins(string origin, bool allowed)
    {
        await using var host = await CollectorHost.StartAsync();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "v1/traces");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await host.Client.SendAsync(preflight);

        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
```

`tests/CodeAtlas.Collector.Tests/StoreTests.cs`:
```csharp
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

public class StoreTests
{
    private static TraceStore Store(int maxTraces = 500, int maxSpans = 2000, int maxLength = 4096) =>
        new(new StoreLimits(maxTraces, maxSpans, maxLength));

    [Fact]
    public void Children_before_parents_are_kept_and_joined_by_id()
    {
        var store = Store();
        var t = Otlp.TraceId(1);

        store.Add(Otlp.Request("api", "s", [Otlp.Span(t, Otlp.SpanId(2), Otlp.SpanId(1), "child")]));
        store.Add(Otlp.Request("api", "s", [Otlp.Span(t, Otlp.SpanId(1), null, "parent", SpanKind.Server)]));

        var spans = store.Get(t)!;
        Assert.Equal(2, spans.Count);
        Assert.Equal(Otlp.SpanId(1), spans.Single(s => s.Name == "child").ParentSpanId);
    }

    [Fact]
    public void The_oldest_trace_is_evicted_beyond_the_limit()
    {
        var store = Store(maxTraces: 3);

        for (var i = 1; i <= 4; i++) store.Add(Otlp.Request("api", "s", [Otlp.Span(Otlp.TraceId(i), Otlp.SpanId(1), null, $"t{i}")]));

        Assert.Null(store.Get(Otlp.TraceId(1)));
        Assert.Equal([Otlp.TraceId(4), Otlp.TraceId(3), Otlp.TraceId(2)], store.RecentTraceIds(10));
    }

    [Fact]
    public void Spans_beyond_the_per_trace_limit_are_dropped_and_marked()
    {
        var store = Store(maxSpans: 2);
        var t = Otlp.TraceId(1);

        store.Add(Otlp.Request("api", "s", Enumerable.Range(1, 3).Select(i => Otlp.Span(t, Otlp.SpanId(i), null, $"s{i}"))));

        Assert.Equal(2, store.Get(t)!.Count);
        Assert.True(store.IsTruncated(t));
    }

    [Fact]
    public void Long_attribute_values_are_cut()
    {
        var store = Store(maxLength: 10);
        var t = Otlp.TraceId(1);

        store.Add(Otlp.Request("api", "s", [Otlp.Span(t, Otlp.SpanId(1), null, "x", attributes: ("big", new string('a', 50)))]));

        Assert.Equal("aaaaaaaaaa…", store.Get(t)![0].Attributes["big"]);
    }

    [Fact]
    public void A_more_detailed_copy_of_a_span_wins_over_a_poorer_one()
    {
        var store = Store();
        var t = Otlp.TraceId(1);
        var rich = Otlp.Span(t, Otlp.SpanId(1), null, "GET /orders", SpanKind.Server, 0, 1, ("http.route", "/orders"), ("http.response.status_code", 200));
        var poor = Otlp.Span(t, Otlp.SpanId(1), null, "Microsoft.AspNetCore.Hosting.HttpRequestIn", SpanKind.Server);

        store.Add(Otlp.Request("api", "s", [rich]));
        store.Add(Otlp.Request("api", "s", [poor]));

        Assert.Equal("GET /orders", store.Get(t)!.Single().Name);
    }

    [Fact]
    public async Task Concurrent_ingest_and_reads_are_safe()
    {
        var store = Store(maxTraces: 50);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var t = Otlp.TraceId(worker * 1000 + i % 60);
                store.Add(Otlp.Request("api", "s", [Otlp.Span(t, Otlp.SpanId(i + 1), null, "x")]));
                foreach (var id in store.RecentTraceIds(20)) _ = store.Get(id)?.Count;
            }
        })));

        Assert.Equal(50, store.RecentTraceIds(1000).Count);
    }
}
```

`tests/CodeAtlas.Collector.Tests/RealExporterTests.cs`:
```csharp
using System.Net.Http.Json;
using CodeAtlas.AspNetCore;
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.Collector.Tests;

/// <summary>The real Plan 1 exporter talking to the real collector over HTTP.</summary>
public class RealExporterTests
{
    [Fact]
    public async Task Spans_from_CodeAtlas_AspNetCore_arrive_intact()
    {
        await using var collector = await CollectorHost.StartAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development, ApplicationName = "TargetApp" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddCodeAtlas(o => o.Endpoint = $"{collector.BaseUrl}/v1/traces");
        await using var target = builder.Build();
        target.MapGet("/orders/{id:int}", (int id) => id);
        await target.StartAsync();
        var traceId = Otlp.TraceId(0xabc);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/7");
        request.Headers.Add("traceparent", $"00-{traceId}-{Otlp.SpanId(9)}-01");
        (await target.GetTestClient().SendAsync(request)).Dispose();

        List<SpanRecord>? spans = null;
        for (var i = 0; i < 50 && spans is not { Count: > 0 }; i++)
        {
            await Task.Delay(100);
            using var response = await collector.Client.GetAsync($"api/traces/{traceId}/spans");
            if (response.IsSuccessStatusCode) spans = await response.Content.ReadFromJsonAsync<List<SpanRecord>>();
        }
        var server = Assert.Single(spans!, s => s.Kind == "server");
        Assert.Equal("GET /orders/{id:int}", server.Name);
        Assert.Equal("TargetApp", server.Service);
        Assert.Equal("codeatlas", server.Resource["codeatlas.sampling"]?.ToString());
        Assert.Contains(spans!, s => s.Attributes.ContainsKey("codeatlas.code.token"));
        await target.StopAsync();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: build error `The type or namespace name 'Store' does not exist in the namespace 'CodeAtlas.Collector'`.

- [ ] **Step 4: Implement records, reader, store and endpoint**

`src/CodeAtlas.Collector/Store/SpanRecord.cs`:
```csharp
using System.Text.Json.Serialization;

namespace CodeAtlas.Collector.Store;

public sealed record SpanEventRecord(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong TimeUnixNano,
    IReadOnlyDictionary<string, object?> Attributes);

/// <summary>One stored span. Nanosecond times travel as strings: they exceed JavaScript's exact integer range.</summary>
public sealed record SpanRecord(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    string Service,
    string Scope,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong StartUnixNano,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong EndUnixNano,
    string Status,
    string? StatusMessage,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<SpanEventRecord> Events,
    IReadOnlyDictionary<string, object?> Resource)
{
    public double DurationMs => (EndUnixNano - StartUnixNano) / 1_000_000.0;
}
```

`src/CodeAtlas.Collector/Ingest/OtlpReader.cs`:
```csharp
using System.IO.Compression;
using CodeAtlas.Otlp;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.Collector.Ingest;

/// <summary>Reads an OTLP/HTTP trace export (official schema), refusing oversized, malformed or unknown bodies.</summary>
internal static class OtlpReader
{
    public const long MaxBody = 16 * 1024 * 1024;

    public static async Task<ExportTraceServiceRequest> ReadAsync(HttpRequest request)
    {
        var contentType = request.ContentType ?? "";
        var isProtobuf = contentType.StartsWith("application/x-protobuf", StringComparison.OrdinalIgnoreCase);
        var isJson = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
        if (!isProtobuf && !isJson) throw new BadHttpRequestException($"Unsupported content type '{contentType}'.", StatusCodes.Status415UnsupportedMediaType);

        Stream body = request.Body;
        if (string.Equals(request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
            body = new GZipStream(body, CompressionMode.Decompress);
        var bytes = await ReadLimitedAsync(body);

        try
        {
            return isProtobuf
                ? ExportTraceServiceRequest.Parser.ParseFrom(bytes)
                : OtlpJson.ParseTraces(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or InvalidJsonException or System.Text.Json.JsonException or FormatException)
        {
            throw new BadHttpRequestException($"Malformed OTLP body: {e.Message}", StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream body)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        try
        {
            while ((read = await body.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxBody)
                    throw new BadHttpRequestException("OTLP body exceeds 16 MiB.", StatusCodes.Status413PayloadTooLarge);
                buffer.Write(chunk, 0, read);
            }
        }
        catch (InvalidDataException e)
        {
            throw new BadHttpRequestException($"Invalid gzip body: {e.Message}", StatusCodes.Status400BadRequest);
        }
        return buffer.ToArray();
    }
}
```

`src/CodeAtlas.Collector/Store/TraceStore.cs`:
```csharp
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace CodeAtlas.Collector.Store;

/// <summary>
/// Bounded in-memory store. Spans are upserted by id, so batching, retries and out-of-order arrival are normal.
/// Thread-safe; all public members take the same lock.
/// </summary>
public sealed class TraceStore(StoreLimits limits)
{
    private readonly Dictionary<string, Dictionary<string, SpanRecord>> _traces = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
    private readonly HashSet<string> _truncated = new();
    private readonly Lock _lock = new();

    /// <summary>Adds every span of the request; returns the ids of the traces it touched.</summary>
    public IReadOnlyList<string> Add(ExportTraceServiceRequest request)
    {
        var records = new List<SpanRecord>();
        foreach (var resourceSpans in request.ResourceSpans)
        {
            var resource = Attributes(resourceSpans.Resource?.Attributes ?? []);
            var service = resource.GetValueOrDefault("service.name") as string ?? "unknown";
            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            foreach (var span in scopeSpans.Spans)
                records.Add(ToRecord(span, service, scopeSpans.Scope?.Name ?? "", resource));
        }

        lock (_lock)
        {
            var touched = new List<string>();
            foreach (var record in records)
            {
                if (!_traces.TryGetValue(record.TraceId, out var spans))
                {
                    _traces[record.TraceId] = spans = new Dictionary<string, SpanRecord>();
                    _nodes[record.TraceId] = _order.AddFirst(record.TraceId);
                    while (_order.Count > limits.MaxTraces) Evict(_order.Last!.Value);
                }
                if (spans.TryGetValue(record.SpanId, out var existing))
                {
                    // Several hosts in one process may export the same span; keep the most detailed copy.
                    if (record.Attributes.Count >= existing.Attributes.Count) spans[record.SpanId] = record;
                }
                else if (spans.Count >= limits.MaxSpansPerTrace) _truncated.Add(record.TraceId);
                else spans[record.SpanId] = record;
                if (!touched.Contains(record.TraceId)) touched.Add(record.TraceId);
            }
            return touched;
        }
    }

    public IReadOnlyList<SpanRecord>? Get(string traceId)
    {
        lock (_lock) return _traces.TryGetValue(traceId, out var spans) ? spans.Values.ToList() : null;
    }

    public IReadOnlyList<string> RecentTraceIds(int limit)
    {
        lock (_lock) return _order.Take(limit).ToList();
    }

    public bool IsTruncated(string traceId)
    {
        lock (_lock) return _truncated.Contains(traceId);
    }

    private void Evict(string traceId)
    {
        _traces.Remove(traceId);
        _truncated.Remove(traceId);
        if (_nodes.Remove(traceId, out var node)) _order.Remove(node);
    }

    private SpanRecord ToRecord(ProtoSpan span, string service, string scope, IReadOnlyDictionary<string, object?> resource) => new(
        Hex(span.TraceId),
        Hex(span.SpanId),
        span.ParentSpanId.IsEmpty ? null : Hex(span.ParentSpanId),
        span.Name,
        span.Kind switch
        {
            ProtoSpan.Types.SpanKind.Server => "server",
            ProtoSpan.Types.SpanKind.Client => "client",
            ProtoSpan.Types.SpanKind.Internal => "internal",
            ProtoSpan.Types.SpanKind.Producer => "producer",
            ProtoSpan.Types.SpanKind.Consumer => "consumer",
            _ => "unspecified",
        },
        service,
        scope,
        span.StartTimeUnixNano,
        Math.Max(span.EndTimeUnixNano, span.StartTimeUnixNano),
        span.Status?.Code switch
        {
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error => "error",
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Ok => "ok",
            _ => "unset",
        },
        string.IsNullOrEmpty(span.Status?.Message) ? null : span.Status.Message,
        Attributes(span.Attributes),
        span.Events.Select(e => new SpanEventRecord(e.Name, e.TimeUnixNano, Attributes(e.Attributes))).ToList(),
        resource);

    private Dictionary<string, object?> Attributes(IEnumerable<KeyValue> values)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kv in values) result[kv.Key] = Value(kv.Value);
        return result;
    }

    private object? Value(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => Cut(value.StringValue),
        AnyValue.ValueOneofCase.IntValue => value.IntValue,
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
        AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(Value).ToList(),
        AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.Span),
        _ => null,
    };

    private string Cut(string s) => s.Length <= limits.MaxAttributeLength ? s : s[..limits.MaxAttributeLength] + "…";

    private static string Hex(ByteString bytes) => Convert.ToHexStringLower(bytes.Span);
}
```

`src/CodeAtlas.Collector/Ingest/IngestEndpoints.cs`:
```csharp
using CodeAtlas.Collector.Store;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.Collector.Ingest;

internal static class IngestEndpoints
{
    private static readonly byte[] EmptyResponse = new ExportTraceServiceResponse().ToByteArray();

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/traces", async (HttpRequest request, TraceStore store) =>
        {
            ExportTraceServiceRequest export;
            try { export = await OtlpReader.ReadAsync(request); }
            catch (BadHttpRequestException e) { return Results.Text(e.Message, statusCode: e.StatusCode); }

            store.Add(export);
            return request.ContentType!.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                ? Results.Text("{}", "application/json")
                : Results.Bytes(EmptyResponse, "application/x-protobuf");
        }).RequireCors(CollectorApp.IngestCors);

        app.MapGet("/api/traces/{traceId}/spans", (string traceId, TraceStore store) =>
            store.Get(traceId) is { } spans ? Results.Ok(spans.OrderBy(s => s.StartUnixNano)) : Results.NotFound());
    }
}
```

Modify `src/CodeAtlas.Collector/CollectorApp.cs` — register the store after `AddSingleton(options)` and map ingest after `TraceEndpoints.Map(app);`:
```csharp
        builder.Services.AddSingleton(new Store.TraceStore(options.Limits));
```
```csharp
        Ingest.IngestEndpoints.Map(app);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: `total: 25, failed: 0`.

If `Spans_from_CodeAtlas_AspNetCore_arrive_intact` times out, print the collector's stored trace ids (`store.RecentTraceIds`) before changing anything: the target app may have sent the span under a different trace id, which would be a Plan 1 bug to report, not a test to loosen.

- [ ] **Step 6: Commit**

```bash
git add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests
git commit -m "feat(collector): OTLP/HTTP ingest (protobuf, JSON, gzip) into an upsert store"
```

---

### Task 3: Live push and configurable limits

**Files:**
- Create: `src/CodeAtlas.Collector/Live/LiveHub.cs` (hub + notifier)
- Modify: `src/CodeAtlas.Collector/Ingest/IngestEndpoints.cs` (notify after storing), `src/CodeAtlas.Collector/CollectorApp.cs` (SignalR), `src/CodeAtlas.Collector/CollectorOptions.cs` (read `CodeAtlas:MaxTraces`, `CodeAtlas:MaxSpansPerTrace`)
- Test: `tests/CodeAtlas.Collector.Tests/LiveTests.cs`

**Interfaces:**
- Consumes: `TraceStore`, `SpanRecord` (Task 2); `TraceAnalyzer.Summarize` arrives in Task 4, so the hub pushes trace **ids** now and summaries from Task 4 on.
- Produces:
  - `public sealed class LiveHub : Hub`, mapped at `/live`
  - `internal sealed class LiveNotifier(IHubContext<LiveHub> hub, TraceStore store) { Task NotifyAsync(IReadOnlyList<string> traceIds); }` sending event `traces-updated` with `string[]` of trace ids
  - configuration keys `CodeAtlas:MaxTraces`, `CodeAtlas:MaxSpansPerTrace`

- [ ] **Step 1: Write the failing live and configuration tests**

`tests/CodeAtlas.Collector.Tests/LiveTests.cs`:
```csharp
using CodeAtlas.Collector.Tests.Support;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodeAtlas.Collector.Tests;

public class LiveTests
{
    [Fact]
    public async Task Viewers_are_told_which_traces_changed()
    {
        await using var host = await CollectorHost.StartAsync();
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder().WithUrl($"{host.BaseUrl}/live").Build();
        connection.On<string[]>("traces-updated", ids => received.TrySetResult(ids));
        await connection.StartAsync();

        (await Otlp.PostAsync(host.Client, Otlp.Request("api", "s", [Otlp.Span(Otlp.TraceId(7), Otlp.SpanId(1), null, "x")]))).Dispose();

        Assert.Equal([Otlp.TraceId(7)], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Store_limits_come_from_configuration()
    {
        await using var host = await CollectorHost.StartAsync("--CodeAtlas:MaxTraces=2");

        for (var i = 1; i <= 3; i++)
            (await Otlp.PostAsync(host.Client, Otlp.Request("api", "s", [Otlp.Span(Otlp.TraceId(i), Otlp.SpanId(1), null, "x")]))).Dispose();

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await host.Client.GetAsync($"api/traces/{Otlp.TraceId(1)}/spans")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests -- --filter-class CodeAtlas.Collector.Tests.LiveTests`
Expected: both FAIL — `Viewers_are_told_which_traces_changed` on a 404 from the `/live` negotiation, `Store_limits_come_from_configuration` because trace 1 is still stored (default limit 500).

- [ ] **Step 3: Implement the hub, notifier, notification on ingest and configurable limits**

`src/CodeAtlas.Collector/Live/LiveHub.cs`:
```csharp
using Microsoft.AspNetCore.SignalR;

namespace CodeAtlas.Collector.Live;

/// <summary>Viewers connect here; the server only pushes.</summary>
public sealed class LiveHub : Hub;

internal sealed class LiveNotifier(IHubContext<LiveHub> hub)
{
    public Task NotifyAsync(IReadOnlyList<string> traceIds) =>
        traceIds.Count == 0 ? Task.CompletedTask : hub.Clients.All.SendAsync("traces-updated", traceIds.ToArray());
}
```

Modify `src/CodeAtlas.Collector/Ingest/IngestEndpoints.cs` — the ingest handler takes the notifier and announces the touched traces:
```csharp
        app.MapPost("/v1/traces", async (HttpRequest request, TraceStore store, Live.LiveNotifier live) =>
        {
            ExportTraceServiceRequest export;
            try { export = await OtlpReader.ReadAsync(request); }
            catch (BadHttpRequestException e) { return Results.Text(e.Message, statusCode: e.StatusCode); }

            await live.NotifyAsync(store.Add(export));
            return request.ContentType!.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                ? Results.Text("{}", "application/json")
                : Results.Bytes(EmptyResponse, "application/x-protobuf");
        }).RequireCors(CollectorApp.IngestCors);
```

Modify `src/CodeAtlas.Collector/CollectorOptions.cs` — replace `new StoreLimits()` in `From` with:
```csharp
            new StoreLimits(
                configuration.GetValue("CodeAtlas:MaxTraces", 500),
                configuration.GetValue("CodeAtlas:MaxSpansPerTrace", 2000)));
```

Modify `src/CodeAtlas.Collector/CollectorApp.cs` — after the store registration:
```csharp
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<Live.LiveNotifier>();
```
and after `Ingest.IngestEndpoints.Map(app);`:
```csharp
        app.MapHub<Live.LiveHub>("/live");
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: `total: 27, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests
git commit -m "feat(collector): SignalR live updates and configurable store limits"
```

---

### Task 4: Trace analysis — tree, kinds, evidence, gaps, summaries

**Files:**
- Create: `src/CodeAtlas.Collector/Analysis/TraceViews.cs`, `src/CodeAtlas.Collector/Analysis/TraceAnalyzer.cs`
- Modify: `src/CodeAtlas.Collector/Api/TraceEndpoints.cs` (`api/traces`, `api/traces/{id}`), `src/CodeAtlas.Collector/Live/LiveHub.cs` (push summaries)
- Modify: `tests/CodeAtlas.Collector.Tests/LiveTests.cs` (expect summaries)
- Test: `tests/CodeAtlas.Collector.Tests/AnalyzerTests.cs`

**Interfaces:**
- Consumes: `SpanRecord`, `TraceStore` (Tasks 2–3).
- Produces:
  - `public sealed record CodeRef(string Assembly, string Mvid, long Token, string Function, string Evidence)` — `Evidence` ∈ `executed|routing`
  - `public sealed record Gap(string Kind, string Message)` — `Kind` ∈ `missing-parent|no-server-span|uncovered-time|handler-not-recorded`
  - `public sealed record NodeView(SpanRecord Span, string Kind, string Evidence, string? Correlation, CodeRef? Code, IReadOnlyList<Gap> Gaps, IReadOnlyList<NodeView> Children)` — `Kind` ∈ `interaction|browser-request|server|endpoint|action|handler|database|http-client|internal`
  - `public sealed record TraceSummary(string TraceId, [string] ulong StartUnixNano, double DurationMs, string? Interaction, string? Correlation, string? Method, string? Route, long? StatusCode, bool HasError, int SpanCount, string Sampling)`
  - `public sealed record TraceView(TraceSummary Summary, IReadOnlyList<NodeView> Roots, bool Truncated, IReadOnlyList<ExplainLine> Explanation)` — `Explanation` is filled in Task 5 (empty list until then)
  - `public sealed record ExplainLine(string Kind, string Text, IReadOnlyList<string> SpanIds)`
  - `public static class TraceAnalyzer { static TraceView Analyze(string traceId, IReadOnlyList<SpanRecord> spans, bool truncated, DateTimeOffset now); static TraceSummary Summarize(string traceId, IReadOnlyList<SpanRecord> spans); }`
  - `GET api/traces?limit=` → `TraceSummary[]` newest first; `GET api/traces/{id}` → `TraceView` or 404
  - live event `traces-updated` now carries `TraceSummary[]`

- [ ] **Step 1: Write the failing analyzer tests**

`tests/CodeAtlas.Collector.Tests/AnalyzerTests.cs`:
```csharp
using CodeAtlas.Collector.Analysis;
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

public class AnalyzerTests
{
    private static readonly string T = Otlp.TraceId(1);
    private static readonly DateTimeOffset Later = DateTimeOffset.FromUnixTimeMilliseconds((long)(Otlp.Epoch / 1_000_000) + 60_000);

    private static IReadOnlyList<SpanRecord> Spans(params (string Service, string Scope, OpenTelemetry.Proto.Trace.V1.Span Span)[] spans)
    {
        var store = new TraceStore(new StoreLimits());
        foreach (var (service, scope, span) in spans) store.Add(Otlp.Request(service, scope, [span]));
        return store.Get(T)!;
    }

    private static IEnumerable<NodeView> All(IEnumerable<NodeView> nodes) => nodes.SelectMany(n => All(n.Children).Prepend(n));

    // A browser interaction → fetch → server → endpoint (routing) → handler (executed) → database.
    private static IReadOnlyList<SpanRecord> CreateOrder(bool withHandler = true) => Spans(
        ("web", "codeatlas-web", Otlp.Span(T, Otlp.SpanId(1), null, "interaction: Create order", SpanKind.Internal, 0, 0,
            ("codeatlas.interaction.name", "Create order"), ("codeatlas.correlation", "explicit"))),
        ("web", "codeatlas-web", Otlp.Span(T, Otlp.SpanId(2), Otlp.SpanId(1), "POST /api/orders", SpanKind.Client, 1, 50,
            ("http.request.method", "POST"), ("codeatlas.correlation", "explicit"), ("http.response.status_code", 201))),
        ("api", "Microsoft.AspNetCore", Otlp.Span(T, Otlp.SpanId(3), Otlp.SpanId(2), "POST /api/orders", SpanKind.Server, 2, 48,
            ("http.request.method", "POST"), ("http.route", "/api/orders"), ("http.response.status_code", 201))),
        ("api", "CodeAtlas.Components", Otlp.Span(T, Otlp.SpanId(4), Otlp.SpanId(3), "endpoint POST /api/orders", SpanKind.Internal, 3, 47,
            ("codeatlas.component", "endpoint"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.mvid", "m"),
            ("codeatlas.code.token", 100663300), ("codeatlas.code.assembly_path", "/app/Shop.dll"), ("codeatlas.code.evidence", "routing"))),
        withHandler
            ? ("api", "CodeAtlas.Components", Otlp.Span(T, Otlp.SpanId(5), Otlp.SpanId(4), "handler OrderEndpoints.Create", SpanKind.Internal, 4, 46,
                ("codeatlas.component", "handler"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.mvid", "m"),
                ("codeatlas.code.token", 100663300), ("codeatlas.code.assembly_path", "/app/Shop.dll"), ("codeatlas.code.evidence", "executed")))
            : ("api", "Microsoft.AspNetCore", Otlp.Span(T, Otlp.SpanId(9), Otlp.SpanId(4), "unused", SpanKind.Internal, 4, 4)),
        ("api", "Npgsql", Otlp.Span(T, Otlp.SpanId(6), withHandler ? Otlp.SpanId(5) : Otlp.SpanId(4), "INSERT orders", SpanKind.Client, 10, 30,
            ("db.system.name", "postgresql"), ("db.query.summary", "INSERT orders"))));

    [Fact]
    public void Builds_the_tree_from_span_ids_with_kinds()
    {
        var view = TraceAnalyzer.Analyze(T, CreateOrder(), truncated: false, Later);

        var root = Assert.Single(view.Roots);
        Assert.Equal("interaction", root.Kind);
        Assert.Equal(["interaction", "browser-request", "server", "endpoint", "handler", "database"],
            All(view.Roots).Select(n => n.Kind));
        Assert.All(All(view.Roots), n => Assert.Equal("executed", n.Evidence));
    }

    [Fact]
    public void Method_identity_keeps_its_evidence_kind_and_correlation_is_exposed()
    {
        var nodes = All(TraceAnalyzer.Analyze(T, CreateOrder(), false, Later).Roots).ToList();

        Assert.Equal("routing", nodes.Single(n => n.Kind == "endpoint").Code!.Evidence);
        Assert.Equal("executed", nodes.Single(n => n.Kind == "handler").Code!.Evidence);
        Assert.Equal(100663300, nodes.Single(n => n.Kind == "handler").Code!.Token);
        Assert.Equal("explicit", nodes.Single(n => n.Kind == "browser-request").Correlation);
        Assert.Null(nodes.Single(n => n.Kind == "database").Code);
    }

    [Fact]
    public void Summary_names_the_interaction_request_and_sampling()
    {
        var summary = TraceAnalyzer.Summarize(T, CreateOrder());

        Assert.Equal("Create order", summary.Interaction);
        Assert.Equal("explicit", summary.Correlation);
        Assert.Equal(("POST", "/api/orders", 201L), (summary.Method, summary.Route, summary.StatusCode));
        Assert.Equal(50, summary.DurationMs);
        Assert.False(summary.HasError);
        Assert.Equal(6, summary.SpanCount);
        Assert.Equal("unknown", summary.Sampling); // these test spans carry no codeatlas.sampling resource attribute
    }

    [Fact]
    public void A_missing_parent_is_a_root_with_a_gap()
    {
        var view = TraceAnalyzer.Analyze(T, Spans(("api", "s", Otlp.Span(T, Otlp.SpanId(2), Otlp.SpanId(1), "orphan"))), false, Later);

        var gap = Assert.Single(Assert.Single(view.Roots).Gaps);
        Assert.Equal("missing-parent", gap.Kind);
        Assert.Equal("Parent span not received yet: it may still be in an export batch, sampled out, or its export failed.", gap.Message);
    }

    [Fact]
    public void Uncovered_time_is_reported_with_honest_wording()
    {
        var view = TraceAnalyzer.Analyze(T, CreateOrder(), false, Later);

        var handler = All(view.Roots).Single(n => n.Kind == "handler");
        var gap = Assert.Single(handler.Gaps);
        Assert.Equal("uncovered-time", gap.Kind);
        Assert.Equal("22.0 ms of this span is not covered by any child span. That time is unexplained by telemetry; it does not mean nothing ran.", gap.Message);
    }

    [Fact]
    public void An_http_client_span_without_a_server_child_gets_a_gap_only_after_five_seconds()
    {
        var spans = Spans(("web", "codeatlas-web", Otlp.Span(T, Otlp.SpanId(1), null, "GET /x", SpanKind.Client, 0, 10, ("http.request.method", "GET"))));
        var justEnded = DateTimeOffset.FromUnixTimeMilliseconds((long)(Otlp.Epoch / 1_000_000) + 1_000);

        Assert.Empty(TraceAnalyzer.Analyze(T, spans, false, justEnded).Roots[0].Gaps);
        var gap = Assert.Single(TraceAnalyzer.Analyze(T, spans, false, Later).Roots[0].Gaps);
        Assert.Equal("No server span received for this request. The backend may not be instrumented, the span may be sampled out, or its export was delayed or failed.", gap.Message);
    }

    [Fact]
    public void Database_client_spans_never_get_the_no_server_span_gap()
    {
        var spans = Spans(("api", "Npgsql", Otlp.Span(T, Otlp.SpanId(1), null, "SELECT t", SpanKind.Client, 0, 1, ("db.system.name", "postgresql"))));

        Assert.Empty(TraceAnalyzer.Analyze(T, spans, false, Later).Roots[0].Gaps);
    }

    [Fact]
    public void An_endpoint_without_a_handler_or_action_says_the_handler_was_not_recorded()
    {
        var view = TraceAnalyzer.Analyze(T, CreateOrder(withHandler: false), false, Later);

        var endpoint = All(view.Roots).Single(n => n.Kind == "endpoint");
        Assert.Contains(endpoint.Gaps, g => g.Kind == "handler-not-recorded" && g.Message.StartsWith("The handler's execution was not recorded"));
    }

    [Fact]
    public void Parent_cycles_terminate_and_are_reported()
    {
        var spans = Spans(
            ("api", "s", Otlp.Span(T, Otlp.SpanId(1), Otlp.SpanId(1), "self")),
            ("api", "s", Otlp.Span(T, Otlp.SpanId(2), Otlp.SpanId(3), "a")),
            ("api", "s", Otlp.Span(T, Otlp.SpanId(3), Otlp.SpanId(2), "b")));

        var view = TraceAnalyzer.Analyze(T, spans, false, Later);

        Assert.Equal(3, All(view.Roots).Count());
        Assert.All(view.Roots, r => Assert.Contains(r.Gaps, g => g.Kind == "missing-parent"));
    }

    [Fact]
    public void App_sampling_is_reported_from_the_resource()
    {
        var spans = Spans(("api", "Microsoft.AspNetCore", Otlp.Span(T, Otlp.SpanId(1), null, "GET /", SpanKind.Server)));
        var withSampling = new TraceStore(new StoreLimits());
        withSampling.Add(Otlp.Request("api", "Microsoft.AspNetCore", [Otlp.Span(T, Otlp.SpanId(1), null, "GET /", SpanKind.Server)], ("codeatlas.sampling", "app")));

        Assert.Equal("app", TraceAnalyzer.Summarize(T, withSampling.Get(T)!).Sampling);
        Assert.Equal("unknown", TraceAnalyzer.Summarize(T, spans).Sampling);
    }
}
```

Update `tests/CodeAtlas.Collector.Tests/LiveTests.cs` to expect summaries — replace the handler and assertion:
```csharp
        var received = new TaskCompletionSource<CodeAtlas.Collector.Analysis.TraceSummary[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder().WithUrl($"{host.BaseUrl}/live").Build();
        connection.On<CodeAtlas.Collector.Analysis.TraceSummary[]>("traces-updated", s => received.TrySetResult(s));
```
```csharp
        Assert.Equal([Otlp.TraceId(7)], (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Select(s => s.TraceId));
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: build error `The type or namespace name 'Analysis' does not exist`.

- [ ] **Step 3: Implement the views and the analyzer**

`src/CodeAtlas.Collector/Analysis/TraceViews.cs`:
```csharp
using System.Text.Json.Serialization;
using CodeAtlas.Collector.Store;

namespace CodeAtlas.Collector.Analysis;

public sealed record CodeRef(string Assembly, string Mvid, long Token, string Function, string Evidence);

public sealed record Gap(string Kind, string Message);

public sealed record NodeView(
    SpanRecord Span, string Kind, string Evidence, string? Correlation, CodeRef? Code,
    IReadOnlyList<Gap> Gaps, IReadOnlyList<NodeView> Children);

public sealed record TraceSummary(
    string TraceId,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] ulong StartUnixNano,
    double DurationMs, string? Interaction, string? Correlation, string? Method, string? Route, long? StatusCode,
    bool HasError, int SpanCount, string Sampling);

public sealed record ExplainLine(string Kind, string Text, IReadOnlyList<string> SpanIds);

public sealed record TraceView(TraceSummary Summary, IReadOnlyList<NodeView> Roots, bool Truncated, IReadOnlyList<ExplainLine> Explanation);
```

`src/CodeAtlas.Collector/Analysis/TraceAnalyzer.cs`:
```csharp
using System.Globalization;
using CodeAtlas.Collector.Store;

namespace CodeAtlas.Collector.Analysis;

/// <summary>
/// Turns stored spans into what the viewer shows. Everything here is derived from span data only; anything that cannot
/// be shown from evidence becomes a <see cref="Gap"/> worded as unknown.
/// </summary>
public static class TraceAnalyzer
{
    public const string MissingParent = "Parent span not received yet: it may still be in an export batch, sampled out, or its export failed.";
    public const string NoServerSpan = "No server span received for this request. The backend may not be instrumented, the span may be sampled out, or its export was delayed or failed.";
    public const string HandlerNotRecorded = "The handler's execution was not recorded: there is no handler or action span. A filter such as validation may have ended the request first, or WithCodeAtlasHandlerSpan is not enabled for this endpoint.";
    private static readonly TimeSpan ServerSpanGrace = TimeSpan.FromSeconds(5);

    public static TraceView Analyze(string traceId, IReadOnlyList<SpanRecord> spans, bool truncated, DateTimeOffset now)
    {
        var byId = spans.ToDictionary(s => s.SpanId);
        var children = spans
            .Where(s => s.ParentSpanId is { } p && byId.ContainsKey(p) && !InCycle(s, byId))
            .GroupBy(s => s.ParentSpanId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartUnixNano).ToList());
        var roots = spans.Where(s => s.ParentSpanId is not { } p || !byId.ContainsKey(p) || InCycle(s, byId)).OrderBy(s => s.StartUnixNano);
        var nowNanos = (ulong)now.ToUnixTimeMilliseconds() * 1_000_000;

        NodeView Build(SpanRecord span)
        {
            var kids = children.GetValueOrDefault(span.SpanId)?.Select(Build).ToList() ?? [];
            var kind = KindOf(span);
            var gaps = new List<Gap>();
            if (span.ParentSpanId is { } parent && (!byId.ContainsKey(parent) || InCycle(span, byId))) gaps.Add(new("missing-parent", MissingParent));
            if (kind is "browser-request" or "http-client" && !Descendants(kids).Any(k => k.Kind == "server") && nowNanos > span.EndUnixNano + (ulong)ServerSpanGrace.Ticks * 100)
                gaps.Add(new("no-server-span", NoServerSpan));
            if (kids.Count > 0 && Uncovered(span, kids) is var ms and >= 1)
                gaps.Add(new("uncovered-time", $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms of this span is not covered by any child span. That time is unexplained by telemetry; it does not mean nothing ran."));
            var code = CodeOf(span);
            if (kind == "endpoint" && code?.Evidence == "routing" && !Descendants(kids).Any(k => k.Kind is "handler" or "action"))
                gaps.Add(new("handler-not-recorded", HandlerNotRecorded));
            return new NodeView(span, kind, "executed", span.Attributes.GetValueOrDefault("codeatlas.correlation") as string, code, gaps, kids);
        }

        return new TraceView(Summarize(traceId, spans), roots.Select(Build).ToList(), truncated, []);
    }

    public static TraceSummary Summarize(string traceId, IReadOnlyList<SpanRecord> spans)
    {
        var interaction = spans.FirstOrDefault(s => s.Attributes.ContainsKey("codeatlas.interaction.name"));
        var server = spans.Where(s => s.Kind == "server").OrderBy(s => s.StartUnixNano).FirstOrDefault();
        var start = spans.Min(s => s.StartUnixNano);
        var end = spans.Max(s => s.EndUnixNano);
        var sampling = spans.Select(s => s.Resource.GetValueOrDefault("codeatlas.sampling") as string).FirstOrDefault(s => s is not null) ?? "unknown";
        return new TraceSummary(
            traceId, start, (end - start) / 1_000_000.0,
            interaction?.Attributes["codeatlas.interaction.name"] as string,
            interaction?.Attributes.GetValueOrDefault("codeatlas.correlation") as string,
            server?.Attributes.GetValueOrDefault("http.request.method") as string,
            server?.Attributes.GetValueOrDefault("http.route") as string,
            server?.Attributes.GetValueOrDefault("http.response.status_code") as long?,
            spans.Any(s => s.Status == "error"),
            spans.Count,
            sampling);
    }

    internal static string KindOf(SpanRecord span) => span.Attributes switch
    {
        var a when a.ContainsKey("codeatlas.interaction.name") => "interaction",
        var a when a.GetValueOrDefault("codeatlas.component") is "endpoint" or "action" or "handler" => (string)a["codeatlas.component"]!,
        var a when a.ContainsKey("db.system.name") || a.ContainsKey("db.system") => "database",
        _ when span.Kind == "server" => "server",
        var a when span.Kind == "client" && a.ContainsKey("http.request.method") && span.Scope == "codeatlas-web" => "browser-request",
        var a when span.Kind == "client" && a.ContainsKey("http.request.method") => "http-client",
        _ => "internal",
    };

    private static CodeRef? CodeOf(SpanRecord span)
    {
        var a = span.Attributes;
        if (a.GetValueOrDefault("codeatlas.code.token") is not long token) return null;
        return new CodeRef(
            a.GetValueOrDefault("codeatlas.code.assembly_path") as string ?? "",
            a.GetValueOrDefault("codeatlas.code.mvid") as string ?? "",
            token,
            a.GetValueOrDefault("codeatlas.code.function") as string ?? "",
            a.GetValueOrDefault("codeatlas.code.evidence") as string ?? "executed");
    }

    private static bool InCycle(SpanRecord span, IReadOnlyDictionary<string, SpanRecord> byId)
    {
        var seen = new HashSet<string> { span.SpanId };
        for (var current = span; current.ParentSpanId is { } parent && byId.TryGetValue(parent, out var next); current = next)
            if (!seen.Add(next.SpanId)) return true;
        return false;
    }

    private static IEnumerable<NodeView> Descendants(IEnumerable<NodeView> nodes) => nodes.SelectMany(n => Descendants(n.Children).Prepend(n));

    private static double Uncovered(SpanRecord span, IReadOnlyList<NodeView> children)
    {
        ulong covered = 0, curStart = 0, curEnd = 0;
        var started = false;
        foreach (var child in children.OrderBy(c => c.Span.StartUnixNano))
        {
            var s = Math.Max(child.Span.StartUnixNano, span.StartUnixNano);
            var e = Math.Min(child.Span.EndUnixNano, span.EndUnixNano);
            if (e <= s) continue;
            if (!started || s > curEnd)
            {
                if (started) covered += curEnd - curStart;
                (curStart, curEnd, started) = (s, e, true);
            }
            else curEnd = Math.Max(curEnd, e);
        }
        if (started) covered += curEnd - curStart;
        return (span.EndUnixNano - span.StartUnixNano - covered) / 1_000_000.0;
    }
}
```

Modify `src/CodeAtlas.Collector/Api/TraceEndpoints.cs` — add inside `Map`:
```csharp
        app.MapGet("/api/traces", (Store.TraceStore store, int? limit) =>
            store.RecentTraceIds(Math.Clamp(limit ?? 50, 1, 500))
                .Select(id => store.Get(id) is { Count: > 0 } spans ? Analysis.TraceAnalyzer.Summarize(id, spans) : null)
                .OfType<Analysis.TraceSummary>());

        app.MapGet("/api/traces/{traceId}", (string traceId, Store.TraceStore store) =>
            store.Get(traceId) is { Count: > 0 } spans
                ? Results.Ok(Analysis.TraceAnalyzer.Analyze(traceId, spans, store.IsTruncated(traceId), DateTimeOffset.UtcNow))
                : Results.NotFound());
```

Replace `LiveNotifier` in `src/CodeAtlas.Collector/Live/LiveHub.cs`:
```csharp
internal sealed class LiveNotifier(IHubContext<LiveHub> hub, Store.TraceStore store)
{
    public Task NotifyAsync(IReadOnlyList<string> traceIds)
    {
        var summaries = traceIds
            .Select(id => store.Get(id) is { Count: > 0 } spans ? Analysis.TraceAnalyzer.Summarize(id, spans) : null)
            .OfType<Analysis.TraceSummary>()
            .ToArray();
        return summaries.Length == 0 ? Task.CompletedTask : hub.Clients.All.SendAsync("traces-updated", summaries);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: `total: 37, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests
git commit -m "feat(collector): trace analysis with kinds, evidence, honest gaps and summaries"
```

---

### Task 5: Explain Execution

**Files:**
- Create: `src/CodeAtlas.Collector/Analysis/Explainer.cs`
- Modify: `src/CodeAtlas.Collector/Analysis/TraceAnalyzer.cs` (fill `Explanation`)
- Test: `tests/CodeAtlas.Collector.Tests/ExplainerTests.cs`

**Interfaces:**
- Consumes: `TraceView`, `NodeView`, `ExplainLine` (Task 4).
- Produces: `public static class Explainer { static IReadOnlyList<ExplainLine> Explain(TraceSummary summary, IReadOnlyList<NodeView> roots); }` with line kinds `entry|request|executed|routing|database|http|slowest|exception|visibility|sampling`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeAtlas.Collector.Tests/ExplainerTests.cs`:
```csharp
using CodeAtlas.Collector.Analysis;
using CodeAtlas.Collector.Store;
using CodeAtlas.Collector.Tests.Support;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

public class ExplainerTests
{
    private static readonly string T = Otlp.TraceId(1);
    private static readonly DateTimeOffset Later = DateTimeOffset.FromUnixTimeMilliseconds((long)(Otlp.Epoch / 1_000_000) + 60_000);

    private static TraceView View(string correlation = "explicit", bool fail = false, string sampling = "codeatlas")
    {
        var store = new TraceStore(new StoreLimits());
        void Add(string service, string scope, OpenTelemetry.Proto.Trace.V1.Span span) =>
            store.Add(Otlp.Request(service, scope, [span], ("codeatlas.sampling", sampling)));
        Add("web", "codeatlas-web", Otlp.Span(T, Otlp.SpanId(1), null, "interaction: Create order", SpanKind.Internal, 0, 0,
            ("codeatlas.interaction.name", "Create order"), ("codeatlas.correlation", correlation)));
        Add("web", "codeatlas-web", Otlp.Span(T, Otlp.SpanId(2), Otlp.SpanId(1), "POST /api/orders", SpanKind.Client, 1, 60,
            ("http.request.method", "POST"), ("codeatlas.correlation", correlation)));
        Add("api", "Microsoft.AspNetCore", Otlp.Span(T, Otlp.SpanId(3), Otlp.SpanId(2), "POST /api/orders", SpanKind.Server, 2, 58,
            ("http.request.method", "POST"), ("http.route", "/api/orders"), ("http.response.status_code", fail ? 500 : 201)));
        Add("api", "CodeAtlas.Components", Otlp.Span(T, Otlp.SpanId(4), Otlp.SpanId(3), "endpoint POST /api/orders", SpanKind.Internal, 3, 57,
            ("codeatlas.component", "endpoint"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.token", 1), ("codeatlas.code.evidence", "routing")));
        var handler = Otlp.Span(T, Otlp.SpanId(5), Otlp.SpanId(4), "handler OrderEndpoints.Create", SpanKind.Internal, 4, 56,
            ("codeatlas.component", "handler"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.token", 1), ("codeatlas.code.evidence", "executed"));
        if (fail)
        {
            handler.Status = new OpenTelemetry.Proto.Trace.V1.Status { Code = OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error, Message = "Out of stock" };
            handler.Events.Add(new OpenTelemetry.Proto.Trace.V1.Span.Types.Event
            {
                Name = "exception",
                Attributes = { Otlp.Kv("exception.type", "System.InvalidOperationException"), Otlp.Kv("exception.message", "Out of stock") },
            });
        }
        Add("api", "CodeAtlas.Components", handler);
        Add("api", "Npgsql", Otlp.Span(T, Otlp.SpanId(6), Otlp.SpanId(5), "SELECT products", SpanKind.Client, 5, 15, ("db.system.name", "postgresql")));
        Add("api", "Npgsql", Otlp.Span(T, Otlp.SpanId(7), Otlp.SpanId(5), "INSERT orders", SpanKind.Client, 16, 40, ("db.system.name", "postgresql")));
        Add("api", "Npgsql", Otlp.Span(T, Otlp.SpanId(8), Otlp.SpanId(5), "SELECT products", SpanKind.Client, 41, 45, ("db.system.name", "postgresql")));
        Add("api", "System.Net.Http", Otlp.Span(T, Otlp.SpanId(9), Otlp.SpanId(5), "GET", SpanKind.Client, 46, 55,
            ("http.request.method", "GET"), ("url.full", "https://stock.example/api/check"), ("http.response.status_code", 200)));
        return TraceAnalyzer.Analyze(T, store.Get(T)!, false, Later);
    }

    private static string Text(TraceView view, string kind) => string.Join(" | ", view.Explanation.Where(l => l.Kind == kind).Select(l => l.Text));

    [Fact]
    public void Explains_a_successful_request_from_its_spans()
    {
        var view = View();

        Assert.Equal("The interaction \"Create order\" triggered this request (explicit link).", Text(view, "entry"));
        Assert.Equal("POST /api/orders returned 201 in 56.0 ms.", Text(view, "request"));
        Assert.Equal("Executed Shop.OrderEndpoints.Create (handler).", Text(view, "executed"));
        Assert.Equal("3 database operations: SELECT products ×2, INSERT orders ×1.", Text(view, "database"));
        Assert.Equal("1 outgoing HTTP call: GET https://stock.example/api/check → 200.", Text(view, "http"));
        Assert.Equal("Slowest recorded operations: POST /api/orders (59.0 ms), POST /api/orders (56.0 ms), endpoint POST /api/orders (54.0 ms).", Text(view, "slowest"));
        Assert.Equal("", Text(view, "routing"));
        Assert.Equal("", Text(view, "exception"));
    }

    [Fact]
    public void Every_line_points_at_the_spans_it_is_based_on()
    {
        var view = View();

        Assert.Equal([Otlp.SpanId(5)], view.Explanation.Single(l => l.Kind == "executed").SpanIds);
        Assert.Equal([Otlp.SpanId(6), Otlp.SpanId(7), Otlp.SpanId(8)], view.Explanation.Single(l => l.Kind == "database").SpanIds.Order());
    }

    [Fact]
    public void A_heuristic_link_is_worded_as_likely_not_proven()
    {
        Assert.Equal("The interaction \"Create order\" likely triggered this request (linked by timing, not proven).", Text(View("heuristic"), "entry"));
    }

    [Fact]
    public void Failures_name_the_recorded_exception()
    {
        var view = View(fail: true);

        Assert.Equal("POST /api/orders returned 500 in 56.0 ms.", Text(view, "request"));
        Assert.Equal("handler OrderEndpoints.Create recorded System.InvalidOperationException: Out of stock.", Text(view, "exception"));
    }

    [Fact]
    public void Routing_without_execution_says_it_is_not_recorded()
    {
        var store = new TraceStore(new StoreLimits());
        store.Add(Otlp.Request("api", "CodeAtlas.Components", [
            Otlp.Span(T, Otlp.SpanId(4), null, "endpoint POST /api/orders", SpanKind.Internal, 0, 5,
                ("codeatlas.component", "endpoint"), ("codeatlas.code.function", "Shop.OrderEndpoints.Create"), ("codeatlas.code.token", 1), ("codeatlas.code.evidence", "routing")),
        ]));

        var view = TraceAnalyzer.Analyze(T, store.Get(T)!, false, Later);

        Assert.Equal("Routed to Shop.OrderEndpoints.Create; whether it ran is not recorded.", Text(view, "routing"));
        Assert.Contains("No browser interaction was captured for this trace.", Text(view, "entry"));
    }

    [Fact]
    public void Gaps_and_app_sampling_become_visibility_lines()
    {
        var view = View(sampling: "app");

        // handler 4–56 ms; its children cover 5–15, 16–40, 41–45 and 46–55 → 5.0 ms uncovered.
        Assert.Contains("handler OrderEndpoints.Create: 5.0 ms of this span is not covered", Text(view, "visibility"));
        Assert.Equal("Only requests that your app's own sampler records are visible.", Text(view, "sampling"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests -- --filter-class CodeAtlas.Collector.Tests.ExplainerTests`
Expected: the six tests compile (they only read `TraceView.Explanation`) and FAIL on their assertions, because `Explanation` is still the empty list from Task 4 — e.g. `Expected: "The interaction \"Create order\" triggered…" Actual: ""`.

- [ ] **Step 3: Implement the explainer**

`src/CodeAtlas.Collector/Analysis/Explainer.cs`:
```csharp
using System.Globalization;

namespace CodeAtlas.Collector.Analysis;

/// <summary>
/// Deterministic explanation built only from recorded spans. Each line lists the spans it is based on.
/// Routing metadata and timing-based links are worded as such; nothing unrecorded is stated as fact.
/// </summary>
public static class Explainer
{
    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static IReadOnlyList<ExplainLine> Explain(TraceSummary summary, IReadOnlyList<NodeView> roots)
    {
        var nodes = Flatten(roots).ToList();
        var lines = new List<ExplainLine>();

        var interaction = nodes.FirstOrDefault(n => n.Kind == "interaction");
        lines.Add(interaction is null
            ? new("entry", "No browser interaction was captured for this trace.", [])
            : new("entry", interaction.Correlation == "explicit"
                ? $"The interaction \"{summary.Interaction}\" triggered this request (explicit link)."
                : $"The interaction \"{summary.Interaction}\" likely triggered this request (linked by timing, not proven).",
                [interaction.Span.SpanId]));

        foreach (var server in nodes.Where(n => n.Kind == "server"))
        {
            var a = server.Span.Attributes;
            lines.Add(new("request",
                $"{a.GetValueOrDefault("http.request.method")} {a.GetValueOrDefault("http.route") ?? server.Span.Name} returned {a.GetValueOrDefault("http.response.status_code")} in {Ms(server.Span.DurationMs)} ms.",
                [server.Span.SpanId]));
        }

        var executed = nodes.Where(n => n.Code?.Evidence == "executed").ToList();
        foreach (var node in executed)
            lines.Add(new("executed", $"Executed {node.Code!.Function} ({node.Kind}).", [node.Span.SpanId]));
        foreach (var node in nodes.Where(n => n.Code?.Evidence == "routing" && !Flatten(n.Children).Any(c => c.Code?.Evidence == "executed")))
            lines.Add(new("routing", $"Routed to {node.Code!.Function}; whether it ran is not recorded.", [node.Span.SpanId]));

        var database = nodes.Where(n => n.Kind == "database").ToList();
        if (database.Count > 0)
        {
            var groups = database.GroupBy(n => n.Span.Name).Select(g => $"{g.Key} ×{g.Count()}");
            lines.Add(new("database",
                $"{database.Count} database operation{(database.Count == 1 ? "" : "s")}: {string.Join(", ", groups)}.",
                database.Select(n => n.Span.SpanId).ToList()));
        }

        var calls = nodes.Where(n => n.Kind == "http-client").ToList();
        if (calls.Count > 0)
        {
            var described = calls.Select(n =>
                $"{n.Span.Attributes.GetValueOrDefault("http.request.method")} {n.Span.Attributes.GetValueOrDefault("url.full") ?? n.Span.Name} → {n.Span.Attributes.GetValueOrDefault("http.response.status_code") ?? "no status"}");
            lines.Add(new("http",
                $"{calls.Count} outgoing HTTP call{(calls.Count == 1 ? "" : "s")}: {string.Join(", ", described)}.",
                calls.Select(n => n.Span.SpanId).ToList()));
        }

        var slowest = nodes.Where(n => n.Kind != "interaction").OrderByDescending(n => n.Span.DurationMs).Take(3).ToList();
        if (slowest.Count > 0)
            lines.Add(new("slowest",
                $"Slowest recorded operations: {string.Join(", ", slowest.Select(n => $"{n.Span.Name} ({Ms(n.Span.DurationMs)} ms)"))}.",
                slowest.Select(n => n.Span.SpanId).ToList()));

        foreach (var node in nodes)
        foreach (var ev in node.Span.Events.Where(e => e.Name == "exception"))
            lines.Add(new("exception",
                $"{node.Span.Name} recorded {ev.Attributes.GetValueOrDefault("exception.type")}: {ev.Attributes.GetValueOrDefault("exception.message")}.",
                [node.Span.SpanId]));

        foreach (var node in nodes)
        foreach (var gap in node.Gaps)
            lines.Add(new("visibility", $"{node.Span.Name}: {gap.Message}", [node.Span.SpanId]));

        if (summary.Sampling == "app")
            lines.Add(new("sampling", "Only requests that your app's own sampler records are visible.", []));

        return lines;
    }

    private static IEnumerable<NodeView> Flatten(IEnumerable<NodeView> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
```

Modify `src/CodeAtlas.Collector/Analysis/TraceAnalyzer.cs` — in `Analyze`, replace the return with:
```csharp
        var summary = Summarize(traceId, spans);
        var rootViews = roots.Select(Build).ToList();
        return new TraceView(summary, rootViews, truncated, Explainer.Explain(summary, rootViews));
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: `total: 43, failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests
git commit -m "feat(collector): deterministic Explain Execution from recorded spans"
```

---

### Task 6: Source mapping (PDB + Roslyn) and `api/source`

**Files:**
- Create: `src/CodeAtlas.Collector/Source/PdbSourceMap.cs`, `src/CodeAtlas.Collector/Source/SourceEndpoints.cs`
- Modify: `src/CodeAtlas.Collector/CollectorApp.cs` (register map, map endpoint)
- Test: `tests/CodeAtlas.Collector.Tests/Fixtures/sourcemap/Fixture.cs.txt`, `tests/CodeAtlas.Collector.Tests/Support/FixtureAssembly.cs`, `tests/CodeAtlas.Collector.Tests/SourceMapTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SourceLocation(bool Found, string? Reason, string? File, string? RelativePath, int DisplayStart, int DisplayEnd, int NavigationLine, IReadOnlyList<int> ExecutableLines, string? Kind, bool StateMachine, string? Mapping)` — `Kind` ∈ `method|constructor|accessor|property|lambda|local function|primary constructor|top-level statements`, `Mapping` ∈ `pdb + roslyn|pdb only`
  - `public sealed class PdbSourceMap(IReadOnlyList<string> roots) : IDisposable { bool IsAllowed(string path); SourceLocation Resolve(string assemblyPath, Guid mvid, int token); }`
  - `GET api/source?assembly=&mvid=&token=` → `{ location: SourceLocation, text: string? }` (text = whole verified file when found)
  - test helper `FixtureAssembly.Build(string directory, string sourceText, string fileName = "Fixture.cs", bool crlf = false, bool bom = false)` → `FixtureAssembly { string DllPath; string SourcePath; Guid Mvid; int Token(string type, string method, string? firstParameter = null); int LambdaToken(string containingMethod); int LocalFunctionToken(string name); int ConstructorToken(string type); int TypeToken(string type); }`

- [ ] **Step 1: Write the fixture source**

`tests/CodeAtlas.Collector.Tests/Fixtures/sourcemap/Fixture.cs.txt` (a `.txt` so the test project does not compile it; tests compile it with Roslyn):
```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Fixture;

public class Orders
{
    public int Count(int a) => a + 1;

    public int Count(string a) => a.Length;

    public async Task<int> LoadAsync(int id)
    {
        await Task.Yield();
        return id * 2;
    }

    public IEnumerable<int> Numbers()
    {
        yield return 1;
        yield return 2;
    }

    public Func<int, int> MakeLambda() => x => x * 3;

    public int WithLocal(int value)
    {
        return Twice(value);

        static int Twice(int n) => n * 2;
    }

    public T Echo<T>(T value) => value;
}

public class Prices(decimal rate)
{
    public decimal Rate { get; } = rate;
}

public interface IOrders
{
    void Save();
}
```

- [ ] **Step 2: Write the fixture compiler**

`tests/CodeAtlas.Collector.Tests/Support/FixtureAssembly.cs`:
```csharp
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas.Collector.Tests.Support;

/// <summary>Compiles C# source to a DLL with a side-by-side Portable PDB on disk, like a Debug build, and finds tokens.</summary>
internal sealed class FixtureAssembly
{
    public required string DllPath { get; init; }
    public required string SourcePath { get; init; }
    public required Guid Mvid { get; init; }
    private readonly List<(string Type, string Name, string? FirstParameter, int Token)> _methods = [];
    private readonly Dictionary<string, int> _types = [];

    public static string FixtureText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/sourcemap/Fixture.cs.txt"));

    public static FixtureAssembly Build(string directory, string sourceText, string fileName = "Fixture.cs", bool crlf = false, bool bom = false,
        string assemblyName = "Fixture", string? sourcePathOverride = null)
    {
        Directory.CreateDirectory(directory);
        var text = crlf ? sourceText.Replace("\r\n", "\n").Replace("\n", "\r\n") : sourceText.Replace("\r\n", "\n");
        var bytes = (bom ? Encoding.UTF8.GetPreamble() : []).Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();
        var sourcePath = sourcePathOverride ?? Path.Combine(directory, fileName);
        if (sourcePathOverride is null) File.WriteAllBytes(sourcePath, bytes);

        var source = SourceText.From(bytes, bytes.Length, Encoding.UTF8, SourceHashAlgorithm.Sha256, throwIfBinaryDetected: false, canBeEmbedded: true);
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: sourcePath);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(assemblyName, [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

        var dllPath = Path.Combine(directory, assemblyName + ".dll");
        var pdbPath = Path.Combine(directory, assemblyName + ".pdb");
        using (var dll = File.Create(dllPath))
        using (var pdb = File.Create(pdbPath))
        {
            var result = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath));
            if (!result.Success) throw new InvalidOperationException(string.Join('\n', result.Diagnostics));
        }

        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        var fixture = new FixtureAssembly { DllPath = dllPath, SourcePath = sourcePath, Mvid = md.GetGuid(md.GetModuleDefinition().Mvid) };
        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var typeName = FullName(md, type);
            fixture._types[typeName] = MetadataTokens.GetToken(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var signature = method.DecodeSignature(new FirstParameterName(md), null);
                fixture._methods.Add((typeName, md.GetString(method.Name), signature.ParameterTypes.FirstOrDefault(), MetadataTokens.GetToken(methodHandle)));
            }
        }
        return fixture;
    }

    public int Token(string type, string method, string? firstParameter = null) =>
        _methods.Single(m => m.Type == type && m.Name == method && (firstParameter is null || m.FirstParameter == firstParameter)).Token;

    public int ConstructorToken(string type) => Token(type, ".ctor");

    public int LambdaToken(string containingMethod) =>
        _methods.Single(m => m.Name.StartsWith($"<{containingMethod}>b__", StringComparison.Ordinal)).Token;

    public int LocalFunctionToken(string name) => _methods.Single(m => m.Name.Contains($">g__{name}|", StringComparison.Ordinal)).Token;

    public int TypeToken(string type) => _types[type];

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return $"{FullName(md, md.GetTypeDefinition(declaring))}+{name}";
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private sealed class FirstParameterName(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => md.GetString(md.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => md.GetString(md.GetTypeReference(handle).Name);
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? context, int index) => $"M{index}";
        public string GetGenericTypeParameter(object? context, int index) => $"T{index}";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
    }
}
```

- [ ] **Step 3: Write the failing source-map tests**

`tests/CodeAtlas.Collector.Tests/SourceMapTests.cs`:
```csharp
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
    public async Task The_source_endpoint_returns_the_verified_file()
    {
        await using var host = await CollectorHost.StartAsync($"--source-root={_root}");
        var query = $"assembly={Uri.EscapeDataString(_fixture.DllPath)}&mvid={_fixture.Mvid}&token={_fixture.Token("Fixture.Orders", "Count", "Int32")}";

        var result = await host.Client.GetFromJsonAsync<JsonElement>($"api/source?{query}");

        Assert.True(result.GetProperty("location").GetProperty("found").GetBoolean());
        Assert.Equal(File.ReadAllText(_fixture.SourcePath), result.GetProperty("text").GetString());
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: build error `The type or namespace name 'Source' does not exist in the namespace 'CodeAtlas.Collector'`.

- [ ] **Step 5: Implement the source map and endpoint**

`src/CodeAtlas.Collector/Source/PdbSourceMap.cs`:
```csharp
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Collector.Source;

public sealed record SourceLocation(
    bool Found, string? Reason, string? File, string? RelativePath, int DisplayStart, int DisplayEnd, int NavigationLine,
    IReadOnlyList<int> ExecutableLines, string? Kind, bool StateMachine, string? Mapping)
{
    public static SourceLocation Refused(string reason) => new(false, reason, null, null, 0, 0, 0, [], null, false, null);
}

/// <summary>
/// Maps a runtime method identity (assembly path + MVID + metadata token) to source through the assembly's Portable PDB.
/// Roslyn only widens the PDB location to the enclosing declaration. Refuses, with a reason, whenever the assembly, the
/// PDB or the file does not match: it never guesses.
/// </summary>
public sealed class PdbSourceMap(IReadOnlyList<string> roots) : IDisposable
{
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private readonly Dictionary<string, (DateTime WrittenAt, Loaded? Value)> _cache = new();
    private readonly Lock _lock = new();
    private readonly string[] _roots = roots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();

    private sealed record Loaded(Guid Mvid, int MethodCount, PEReader Pe, MetadataReaderProvider Pdb, Dictionary<int, MethodDefinitionHandle> MoveNextByKickoff) : IDisposable
    {
        public void Dispose()
        {
            Pdb.Dispose();
            Pe.Dispose();
        }
    }

    public bool IsAllowed(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
            if (new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target) full = target.FullName;
        }
        catch (Exception) { return false; }
        return _roots.Any(root => full.StartsWith(root, StringComparison.Ordinal));
    }

    public SourceLocation Resolve(string assemblyPath, Guid mvid, int token)
    {
        if (!IsAllowed(assemblyPath)) return SourceLocation.Refused("The assembly is outside the configured source roots.");
        if (!System.IO.File.Exists(assemblyPath)) return SourceLocation.Refused("The assembly file no longer exists.");
        var loaded = Load(assemblyPath);
        if (loaded is null) return SourceLocation.Refused("No Portable PDB matches this assembly (none found, or its PDB id differs).");
        if (loaded.Mvid != mvid) return SourceLocation.Refused("The assembly was rebuilt after this span was recorded (MVID differs). Repeat the request.");
        if (MetadataTokens.EntityHandle(token) is not { Kind: HandleKind.MethodDefinition } entity)
            return SourceLocation.Refused("The token is not a method definition.");
        var method = (MethodDefinitionHandle)entity;
        if (MetadataTokens.GetRowNumber(method) > loaded.MethodCount) return SourceLocation.Refused("The token is out of range for this assembly.");

        var pdb = loaded.Pdb.GetMetadataReader();
        var points = Visible(pdb.GetMethodDebugInformation(method));
        var stateMachine = false;
        if (points.Count == 0 && loaded.MoveNextByKickoff.TryGetValue(MetadataTokens.GetRowNumber(method), out var moveNext))
        {
            points = Visible(pdb.GetMethodDebugInformation(moveNext));
            stateMachine = true;
        }
        if (points.Count == 0) return SourceLocation.Refused("The PDB has no sequence points for this method (compiler-generated, abstract or extern).");

        var documents = points.Select(p => p.Document).Distinct().ToList();
        if (documents.Count != 1) return SourceLocation.Refused("The method's code spans several documents; refusing to pick one.");
        var document = pdb.GetDocument(documents[0]);
        var file = pdb.GetString(document.Name);
        if (!IsAllowed(file)) return SourceLocation.Refused("The source file is outside the configured source roots.");
        if (!System.IO.File.Exists(file))
            return SourceLocation.Refused(file.Replace('\\', '/').Contains("/obj/")
                ? "This code was produced by a source generator; it has no file on disk."
                : $"The source file from the PDB no longer exists: {file}");

        var bytes = System.IO.File.ReadAllBytes(file);
        var algorithm = pdb.GetGuid(document.HashAlgorithm);
        var actual = algorithm == Sha256 ? SHA256.HashData(bytes) : algorithm == Sha1 ? SHA1.HashData(bytes) : null;
        if (actual is null) return SourceLocation.Refused("Unknown checksum algorithm in the PDB; the file cannot be verified.");
        if (!actual.AsSpan().SequenceEqual(pdb.GetBlobBytes(document.Hash)))
            return SourceLocation.Refused("The source file changed after the build, so line numbers may be wrong. Rebuild to refresh.");

        var executable = points.Select(p => p.StartLine).Distinct().Order().ToList();
        var relative = Path.GetRelativePath(_roots.First(r => Path.GetFullPath(file).StartsWith(r, StringComparison.Ordinal)), file).Replace('\\', '/');
        var declaration = Enclosing(bytes, points);
        return declaration is { } d
            ? new SourceLocation(true, null, file, relative, d.Start, d.End, d.Start, executable, d.Kind, stateMachine, "pdb + roslyn")
            : new SourceLocation(true, null, file, relative, executable[0], executable[^1], executable[0], executable, "method", stateMachine, "pdb only");
    }

    private Loaded? Load(string path)
    {
        var writtenAt = System.IO.File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached) && cached.WrittenAt == writtenAt) return cached.Value;
            cached.Value?.Dispose();
            var loaded = Open(path);
            _cache[path] = (writtenAt, loaded);
            return loaded;
        }
    }

    private static Loaded? Open(string path)
    {
        var pe = new PEReader(System.IO.File.OpenRead(path));
        try
        {
            if (!pe.TryOpenAssociatedPortablePdb(path, p => System.IO.File.Exists(p) ? System.IO.File.OpenRead(p) : null, out var provider, out _) || provider is null)
            {
                pe.Dispose();
                return null;
            }
            var md = pe.GetMetadataReader();
            var pdb = provider.GetMetadataReader();
            var moveNext = new Dictionary<int, MethodDefinitionHandle>();
            foreach (var handle in pdb.MethodDebugInformation)
            {
                var kickoff = pdb.GetMethodDebugInformation(handle).GetStateMachineKickoffMethod();
                if (!kickoff.IsNil) moveNext[MetadataTokens.GetRowNumber(kickoff)] = handle.ToDefinitionHandle();
            }
            return new Loaded(md.GetGuid(md.GetModuleDefinition().Mvid), md.MethodDefinitions.Count, pe, provider, moveNext);
        }
        catch (Exception)
        {
            pe.Dispose();
            return null;
        }
    }

    private static List<SequencePoint> Visible(MethodDebugInformation debug) => debug.GetSequencePoints().Where(p => !p.IsHidden).ToList();

    // Innermost function-like syntax node containing every sequence point.
    private static (int Start, int End, string Kind)? Enclosing(byte[] bytes, List<SequencePoint> points)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(bytes, bytes.Length, System.Text.Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(text);
        int Position(int line, int column) => text.Lines[line - 1].Start + column - 1;
        var positions = points.Select(p => Position(p.StartLine, p.StartColumn)).ToList();
        for (var node = tree.GetRoot().FindToken(positions[0]).Parent; node is not null; node = node.Parent)
        {
            var kind = node switch
            {
                AnonymousFunctionExpressionSyntax => "lambda",
                LocalFunctionStatementSyntax => "local function",
                ConstructorDeclarationSyntax => "constructor",
                BaseMethodDeclarationSyntax => "method",
                AccessorDeclarationSyntax => "accessor",
                PropertyDeclarationSyntax => "property",
                TypeDeclarationSyntax { ParameterList: not null } => "primary constructor",
                CompilationUnitSyntax => "top-level statements",
                _ => null,
            };
            if (kind is null || !positions.All(p => node.FullSpan.Contains(p))) continue;
            var span = node.GetLocation().GetLineSpan();
            return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, kind);
        }
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, value) in _cache.Values) value?.Dispose();
            _cache.Clear();
        }
    }
}
```

`src/CodeAtlas.Collector/Source/SourceEndpoints.cs`:
```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.Collector.Source;

internal static class SourceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Identity only: the file comes from a verified PDB inside a source root, never from the request.
        app.MapGet("/api/source", (PdbSourceMap map, string assembly, Guid mvid, int token) =>
        {
            var location = map.Resolve(assembly, mvid, token);
            return Results.Ok(new { location, text = location.Found ? System.IO.File.ReadAllText(location.File!) : null });
        });
    }
}
```

Modify `src/CodeAtlas.Collector/CollectorApp.cs` — register after the store, and map after ingest:
```csharp
        builder.Services.AddSingleton(new Source.PdbSourceMap(options.SourceRoots));
```
```csharp
        Source.SourceEndpoints.Map(app);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: all pass (`failed: 0`).

If `A_pdb_from_another_build_is_refused` passes the wrong way (the map still finds the original PDB through the absolute path recorded in the DLL), keep the assertion: change the test to delete the original PDB first is NOT allowed — instead confirm with `Console.WriteLine` which PDB path `TryOpenAssociatedPortablePdb` returned, and if it is a path other than the side-by-side one, record a ruling and make the fixture emit with `pdbFilePath` set to the side-by-side path (it already is) before investigating further.

- [ ] **Step 7: Commit**

```bash
git add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests
git commit -m "feat(collector): PDB + Roslyn source mapping with refusal reasons and api/source"
```

---

### Task 7: Editor launcher and the guarded `api/open`

**Files:**
- Create: `src/CodeAtlas.Collector/Editor/EditorLauncher.cs`, `src/CodeAtlas.Collector/Editor/EditorEndpoints.cs`
- Modify: `src/CodeAtlas.Collector/CollectorApp.cs` (register launcher + session token, map endpoints), `src/CodeAtlas.Collector/Api/TraceEndpoints.cs` (`editor.available`)
- Test: `tests/CodeAtlas.Collector.Tests/EditorTests.cs`

**Interfaces:**
- Consumes: `PdbSourceMap.Resolve` (Task 6), `CollectorOptions` (Task 1).
- Produces:
  - `public sealed class EditorLauncher { string Kind; bool Available; string? Executable; TimeSpan Timeout; static EditorLauncher From(CollectorOptions options); Task<(bool Ok, string Message)> OpenAsync(string file, int line); }`
  - `internal sealed record SessionToken(string Value)`
  - `GET api/session` → `{ token }`; `POST api/open` with `{ assembly, mvid, token }` and header `X-CodeAtlas-Token` → 200 `{ message, file, line }` | 401 | 403 | 415 | 422 `{ detail }` | 502 `{ detail }`
  - Origin rule: absent, or same authority as the request's `Host`, or equal to the `--public-url` origin.

- [ ] **Step 1: Write the failing tests**

`tests/CodeAtlas.Collector.Tests/EditorTests.cs`:
```csharp
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
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests -- --filter-class CodeAtlas.Collector.Tests.EditorTests`
Expected: every test FAILS with 404 on `api/session` / `api/open`.

- [ ] **Step 3: Implement the launcher and endpoints**

`src/CodeAtlas.Collector/Editor/EditorLauncher.cs`:
```csharp
using System.Diagnostics;

namespace CodeAtlas.Collector.Editor;

/// <summary>
/// Opens a resolved source location in the developer's editor. The executable is fixed at start-up and arguments are
/// passed as an argument list (no shell), so file names can never run as commands.
/// </summary>
public sealed class EditorLauncher
{
    public required string Kind { get; init; }
    public required string? Executable { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public bool Available => Kind != "none" && Executable is not null && File.Exists(Executable);

    public static EditorLauncher From(CollectorOptions options, int timeoutSeconds = 10)
    {
        var name = options.EditorCommand ?? options.EditorKind switch { "rider" => "rider", "vscode" => "code", "none" => null, _ => "code-server" };
        string? resolved = name is null ? null
            : Path.IsPathRooted(name) ? name
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
        return new EditorLauncher { Kind = options.EditorKind, Executable = resolved, Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)) };
    }

    public async Task<(bool Ok, string Message)> OpenAsync(string file, int line)
    {
        if (!Available) return (false, $"No editor is available (kind '{Kind}'). Copy the path instead: {file}:{line}");
        var start = new ProcessStartInfo(Executable!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in Kind == "rider" ? ["--line", line.ToString(), file] : new[] { "-r", "-g", $"{file}:{line}" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (false, $"The editor command did not finish within {Timeout.TotalSeconds:0} s.");
        }
        return process.ExitCode == 0
            ? (true, $"Opened {Path.GetFileName(file)}:{line} in {Kind}.")
            : (false, $"The editor exited with code {process.ExitCode}: {(await stderr).Trim()}");
    }
}
```

`src/CodeAtlas.Collector/Editor/EditorEndpoints.cs`:
```csharp
using CodeAtlas.Collector.Source;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.Collector.Editor;

internal sealed record SessionToken(string Value);

public sealed record OpenRequest(string Assembly, Guid Mvid, int Token);

internal static class EditorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // No CORS on this route: only same-origin pages can read the token.
        app.MapGet("/api/session", (SessionToken session) => Results.Ok(new { token = session.Value }));

        app.MapPost("/api/open", async (HttpRequest request, OpenRequest body, SessionToken session, CollectorOptions options, PdbSourceMap map, EditorLauncher editor) =>
        {
            if (request.Headers["X-CodeAtlas-Token"] != session.Value)
                return Results.Problem("Missing or wrong session token.", statusCode: StatusCodes.Status401Unauthorized);
            if (!OriginAllowed(request, options))
                return Results.Problem("This page's origin may not open files.", statusCode: StatusCodes.Status403Forbidden);

            // Only an identity comes in; file and line come from the verified PDB mapping.
            var location = map.Resolve(body.Assembly, body.Mvid, body.Token);
            if (!location.Found) return Results.Problem(location.Reason, statusCode: StatusCodes.Status422UnprocessableEntity);

            var (ok, message) = await editor.OpenAsync(location.File!, location.NavigationLine);
            return ok
                ? Results.Ok(new { message, file = location.File, line = location.NavigationLine })
                : Results.Problem(message, statusCode: StatusCodes.Status502BadGateway);
        }).Accepts<OpenRequest>("application/json");
    }

    // Same-origin page (Origin authority equals Host), the configured public URL, or a non-browser client (no Origin).
    private static bool OriginAllowed(HttpRequest request, CollectorOptions options)
    {
        if (request.Headers.Origin is not { Count: > 0 } values) return true;
        if (!Uri.TryCreate(values.ToString(), UriKind.Absolute, out var origin)) return false;
        if (options.PublicUrl is { } publicUrl && origin.GetLeftPart(UriPartial.Authority) == publicUrl.GetLeftPart(UriPartial.Authority)) return true;
        return string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
```

Modify `src/CodeAtlas.Collector/CollectorApp.cs` — register after the source map:
```csharp
        builder.Services.AddSingleton(Editor.EditorLauncher.From(options, builder.Configuration.GetValue("CodeAtlas:Editor:TimeoutSeconds", 10)));
        builder.Services.AddSingleton(new Editor.SessionToken(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
```
and map after `Source.SourceEndpoints.Map(app);`:
```csharp
        Editor.EditorEndpoints.Map(app);
```

Modify `src/CodeAtlas.Collector/Api/TraceEndpoints.cs` — the info endpoint becomes:
```csharp
        app.MapGet("/api/info", (CollectorOptions options, Editor.EditorLauncher editor) => Results.Ok(new
        {
            version = CollectorApp.Version,
            sourceRoots = options.SourceRoots,
            editor = new { kind = editor.Kind, available = editor.Available },
        }));
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Collector.Tests`
Expected: all pass (`failed: 0`).

- [ ] **Step 5: Commit**

```bash
git add src/CodeAtlas.Collector tests/CodeAtlas.Collector.Tests
git commit -m "feat(collector): guarded api/open with session token, origin rule and no-shell editor launcher"
```

---

### Task 8: `codeatlas` dotnet tool

**Files:**
- Create: `scripts/verify-tool.sh`
- Modify: `scripts/pack-local.sh` (also pack the collector)

**Interfaces:**
- Consumes: `CodeAtlas.Collector` (Tasks 1–7).
- Produces: `artifacts/nuget/CodeAtlas.Collector.0.1.0.nupkg`; `scripts/verify-tool.sh` exit 0 = the tool installs from the local feed, starts, answers `api/info`, and refuses a non-loopback address.

- [ ] **Step 1: Extend the pack script**

Append to `scripts/pack-local.sh` before `ls artifacts/nuget`:
```bash
dotnet pack src/CodeAtlas.Collector -c Release -o artifacts/nuget --nologo -v q
```

- [ ] **Step 2: Write the tool check**

`scripts/verify-tool.sh`:
```bash
#!/usr/bin/env bash
# Installs the codeatlas tool from the local feed into a temp folder, starts it, and checks it answers on loopback only.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'kill "${pid:-}" 2>/dev/null || true; rm -rf "$work"' EXIT

"$root/scripts/pack-local.sh" > /dev/null
export NUGET_PACKAGES="$work/packages"
dotnet tool install CodeAtlas.Collector --version 0.1.0 --tool-path "$work/tool" --add-source "$root/artifacts/nuget" > /dev/null

port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
"$work/tool/codeatlas" --urls "http://127.0.0.1:$port" --source-root "$root" > "$work/run.log" 2>&1 &
pid=$!
for _ in $(seq 1 50); do curl -sf "http://127.0.0.1:$port/api/info" > "$work/info.json" && break; sleep 0.2; done

failed=0
check() { if eval "$2"; then echo "PASS  $1"; else echo "FAIL  $1"; failed=1; fi; }
check "tool answers api/info"                  'grep -q "\"version\":\"0.1.0\"" "$work/info.json"'
check "tool refuses a non-loopback address"    '! "$work/tool/codeatlas" --urls "http://0.0.0.0:$port" > "$work/refuse.log" 2>&1 && grep -q loopback "$work/refuse.log"'
exit $failed
```

- [ ] **Step 3: Run it**

```bash
chmod +x scripts/verify-tool.sh && scripts/verify-tool.sh
```
Expected: two `PASS` lines, exit 0.

- [ ] **Step 4: Run every suite**

Run: `dotnet test && scripts/verify-release.sh`
Expected: `failed: 0` and seven `PASS` lines.

- [ ] **Step 5: Commit**

```bash
git add scripts
git commit -m "build: codeatlas dotnet tool packaging and install check"
```
