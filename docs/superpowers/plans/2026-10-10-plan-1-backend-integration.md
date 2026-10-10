# Plan 1 — Backend integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `CodeAtlas.Otlp` and `CodeAtlas.AspNetCore`: a Debug-only ASP.NET Core integration that captures server, endpoint, action, handler, Npgsql and HttpClient spans with method identities, redacts them while serializing, and exports official OTLP protobuf to the collector without ever affecting the app.

**Architecture:** CodeAtlas owns a separate OpenTelemetry `TracerProvider` with a record-only sampler and a custom processor; finished activities are copied into OTLP protobuf messages (privacy rules applied during the copy, the `Activity` is never modified) and sent from a bounded background queue. HTTP facts (method, route, status) come from a first-in-pipeline middleware stored in a side table, not from instrumentation that would touch shared activities. Endpoint/action/handler spans come from a `MatcherPolicy`, a global MVC action filter and an opt-in endpoint filter; they switch off automatically when the app has its own tracing.

**Tech Stack:** .NET 10 (SDK 10.0.401), ASP.NET Core, OpenTelemetry SDK 1.19.1, Google.Protobuf 3.36.2 + Grpc.Tools 2.84.0 (opentelemetry-proto v1.5.0), xunit.v3 4.0.1 on Microsoft.Testing.Platform, Microsoft.AspNetCore.TestHost, Testcontainers.PostgreSql 4.15.0, Npgsql 10.0.3.

**Spec:** `docs/superpowers/specs/2026-10-10-codeatlas-live-mvp-design.md` (§4.1, §4.2, §5, §10, §11)

## Global Constraints

- .NET SDK `10.0.401` via `global.json` (`rollForward: latestFeature`); all projects target `net10.0`.
- OpenTelemetry packages `1.19.1` (`OpenTelemetry.Instrumentation.AspNetCore` `1.19.0` in tests only).
- OTLP schema: official `opentelemetry-proto` **v1.5.0**, generated with `Access="Internal"` so CodeAtlas never exposes public types that could clash with another library.
- Default collector endpoint: `http://127.0.0.1:4318/v1/traces`, protobuf over HTTP, timeout 2 s, queue 2,048 spans, drop oldest.
- **CodeAtlas never modifies an `Activity` it did not create.** It never calls `AddOpenTelemetry()` on the app's services and never changes the app's sampler, processors, exporters or instrumentation options.
- By default nothing below leaves the app: SQL text, SQL parameters, connection strings, URL query strings, request/response bodies, headers, method arguments. Exception messages and stack traces are kept (Development only).
- Attribute names (exact): `codeatlas.code.function`, `codeatlas.code.mvid`, `codeatlas.code.token`, `codeatlas.code.assembly_path`, `codeatlas.code.evidence` (`executed` | `routing`), `codeatlas.component` (`endpoint` | `action` | `handler`), `codeatlas.http.unhandled_exception`, `db.query.summary`; resource attributes `service.name`, `codeatlas.app_has_tracing`.
- Span names: endpoint `endpoint <display name>`, action `action <Type>.<Method>`, handler `handler <Type>.<Method>`, server `<METHOD> <route>`, database = its `db.query.summary`.
- Integration is Debug-only in target apps (conditional `PackageReference` + `CODEATLAS` define); `AddCodeAtlas` is also a no-op outside Development unless `CodeAtlas:Enabled=true`.
- Excluded from this plan and the MVP: DI decorators/DispatchProxy, MediatR, BackgroundService tracing, persistence, remote access.
- Tests in `CodeAtlas.AspNetCore.Tests` run **sequentially** (ActivitySource listeners are process-wide; parallel test hosts would see each other's spans).

## Review Focus

- `AddCodeAtlas` called *before* the app's own `AddOpenTelemetry()` — must still detect app tracing and switch wrapper spans off → Task 4 test `Detects_app_tracing_even_when_AddCodeAtlas_comes_first`.
- Endpoints with no handler method (health checks, middleware-built endpoints) — must get an endpoint span without identity and still respond normally → Task 5 test `Endpoints_without_a_method_still_get_a_span`.
- Tags with arrays, custom objects or `null` values — serializer must not throw or drop the span → Task 2 test `Unusual_tag_values_are_serialized_safely`.
- Many concurrent requests — each server span must get its own route and status → Task 4 test `Parallel_requests_get_their_own_http_facts`.
- App shutdown with spans still queued — they must be flushed, not lost → Task 3 test `Shutdown_flushes_queued_spans`.

## File Structure

```
global.json                                  SDK + test runner
Directory.Build.props                        shared MSBuild settings
.gitignore
CodeAtlas.Live.slnx
src/CodeAtlas.Otlp/
  CodeAtlas.Otlp.csproj                      generated OTLP classes (internal) + IVT
  protos/opentelemetry/proto/**/*.proto      vendored v1.5.0
  protos/NOTICE.md                           origin + licence
  OtlpJson.cs                                OTLP/JSON reader (hex ids)
src/CodeAtlas.AspNetCore/
  CodeAtlas.AspNetCore.csproj
  CodeAtlasOptions.cs                        public options
  CodeAtlasExtensions.cs                     AddCodeAtlas
  HandlerSpanExtensions.cs                   WithCodeAtlasHandlerSpan (public)
  Tracing/CodeAtlasDiagnostics.cs            ActivitySource + Fail helper
  Tracing/CodeAtlasRuntime.cs                app-tracing detection, wrapper switch
  Tracing/RecordOnlySampler.cs
  Tracing/CodeAtlasTracing.cs                hosted service owning the TracerProvider
  Export/SqlSummary.cs                       SQL → "OP tables"
  Export/SpanSerializer.cs                   Activity → OTLP span with privacy rules
  Export/ISpanSink.cs  Export/HttpSpanSink.cs
  Export/CodeAtlasExportProcessor.cs         bounded queue + background sender
  Http/HttpFacts.cs                          HttpFacts record + HttpFactsStore
  Http/HttpFactsStartupFilter.cs             first middleware
  Spans/MethodIdentity.cs                    identity attributes
  Spans/EndpointSpanPolicy.cs                zero-change endpoint span
  Spans/ActionSpanFilter.cs                  zero-change MVC action span
tests/CodeAtlas.Otlp.Tests/                  OtlpJson tests
tests/CodeAtlas.AspNetCore.Tests/
  Support/TestActivities.cs  Support/RecordingSink.cs  Support/BlockingSink.cs
  Support/FakeCollector.cs  Support/StubServer.cs  Support/TestApp.cs  Support/PostgresFixture.cs
  SqlSummaryTests.cs  SpanSerializerTests.cs  ExportProcessorTests.cs  AddCodeAtlasTests.cs
  CoexistenceTests.cs  EndpointSpanTests.cs  ControllerSpanTests.cs  DatabaseSpanTests.cs
  HttpClientSpanTests.cs  FailureIsolationTests.cs
tests/release-fixture/                       not in the solution; used by verify-release.sh
  ReleaseFixtureApp.csproj  Program.cs  nuget.config
scripts/pack-local.sh  scripts/verify-release.sh
docs/integration.md
```

---

### Task 1: Repository skeleton and `CodeAtlas.Otlp`

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.gitignore`, `CodeAtlas.Live.slnx`
- Create: `src/CodeAtlas.Otlp/CodeAtlas.Otlp.csproj`, `src/CodeAtlas.Otlp/OtlpJson.cs`, `src/CodeAtlas.Otlp/protos/**`, `src/CodeAtlas.Otlp/protos/NOTICE.md`
- Test: `tests/CodeAtlas.Otlp.Tests/CodeAtlas.Otlp.Tests.csproj`, `tests/CodeAtlas.Otlp.Tests/OtlpJsonTests.cs`

**Interfaces:**
- Produces: internal generated types in namespaces `OpenTelemetry.Proto.Collector.Trace.V1` (`ExportTraceServiceRequest`, `ExportTraceServiceResponse`), `OpenTelemetry.Proto.Trace.V1` (`ResourceSpans`, `ScopeSpans`, `Span`, `Status`), `OpenTelemetry.Proto.Common.V1` (`KeyValue`, `AnyValue`, `ArrayValue`, `InstrumentationScope`), `OpenTelemetry.Proto.Resource.V1` (`Resource`); `internal static ExportTraceServiceRequest CodeAtlas.Otlp.OtlpJson.ParseTraces(string json)`. Visible to `CodeAtlas.AspNetCore`, `CodeAtlas.Collector`, and their test projects.

- [ ] **Step 1: Create the repository files**

`global.json`:
```json
{
  "sdk": { "version": "10.0.401", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

`.gitignore`:
```
bin/
obj/
artifacts/
TestResults/
node_modules/
*.user
```

Run:
```bash
dotnet new sln -n CodeAtlas.Live --format slnx
```

- [ ] **Step 2: Vendor the official OTLP protos**

```bash
mkdir -p src/CodeAtlas.Otlp/protos
cd src/CodeAtlas.Otlp/protos
for f in common/v1/common resource/v1/resource trace/v1/trace collector/trace/v1/trace_service; do
  mkdir -p opentelemetry/proto/$(dirname $f)
  curl -sSfL https://raw.githubusercontent.com/open-telemetry/opentelemetry-proto/v1.5.0/opentelemetry/proto/$f.proto -o opentelemetry/proto/$f.proto
done
cd -
```

`src/CodeAtlas.Otlp/protos/NOTICE.md`:
```markdown
Files under `opentelemetry/` are copied unchanged from https://github.com/open-telemetry/opentelemetry-proto
at tag v1.5.0, licensed under the Apache License 2.0.
```

- [ ] **Step 3: Create the project**

`src/CodeAtlas.Otlp/CodeAtlas.Otlp.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>CodeAtlas.Otlp</PackageId>
    <Version>0.1.0</Version>
    <Description>OTLP trace messages (opentelemetry-proto v1.5.0) used internally by CodeAtlas Live.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.36.2" />
    <PackageReference Include="Grpc.Tools" Version="2.84.0" PrivateAssets="all" />
    <Protobuf Include="protos/**/*.proto" ProtoRoot="protos" GrpcServices="None" Access="Internal" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="CodeAtlas.AspNetCore" />
    <InternalsVisibleTo Include="CodeAtlas.Collector" />
    <InternalsVisibleTo Include="CodeAtlas.Otlp.Tests" />
    <InternalsVisibleTo Include="CodeAtlas.AspNetCore.Tests" />
    <InternalsVisibleTo Include="CodeAtlas.Collector.Tests" />
  </ItemGroup>
</Project>
```

Create the test project:
```bash
mkdir -p tests/CodeAtlas.Otlp.Tests
```

`tests/CodeAtlas.Otlp.Tests/CodeAtlas.Otlp.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/CodeAtlas.Otlp/CodeAtlas.Otlp.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln add src/CodeAtlas.Otlp tests/CodeAtlas.Otlp.Tests
```

- [ ] **Step 4: Write the failing tests**

`tests/CodeAtlas.Otlp.Tests/OtlpJsonTests.cs`:
```csharp
using CodeAtlas.Otlp;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.Otlp.Tests;

public class OtlpJsonTests
{
    private const string BrowserExport = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"web"}}]},
          "scopeSpans":[{"scope":{"name":"codeatlas-web"},"spans":[{
            "traceId":"0af7651916cd43dd8448eb211c80319c","spanId":"b7ad6b7169203331","parentSpanId":"",
            "name":"POST /api/orders","kind":3,
            "startTimeUnixNano":"1700000000000000000","endTimeUnixNano":"1700000000500000000",
            "attributes":[{"key":"http.response.status_code","value":{"intValue":"201"}}],
            "droppedAttributesCount":0,"someFutureField":true}]}]}]}
        """;

    [Fact]
    public void Hex_ids_decode_to_the_same_bytes_protobuf_would_carry()
    {
        var span = OtlpJson.ParseTraces(BrowserExport).ResourceSpans[0].ScopeSpans[0].Spans[0];

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", Convert.ToHexStringLower(span.TraceId.Span));
        Assert.Equal("b7ad6b7169203331", Convert.ToHexStringLower(span.SpanId.Span));
        Assert.True(span.ParentSpanId.IsEmpty);
        Assert.Equal(Span.Types.SpanKind.Client, span.Kind);
        Assert.Equal(1700000000500000000UL, span.EndTimeUnixNano);
        Assert.Equal(201, span.Attributes[0].Value.IntValue);
    }

    [Fact]
    public void Unknown_fields_are_ignored() =>
        Assert.Single(OtlpJson.ParseTraces(BrowserExport).ResourceSpans);

    [Theory]
    [InlineData("""{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"zz"}]}]}]}""")]
    [InlineData("""{"resourceSpans":[{"scopeSpans":[{"spans":[{"spanId":"abc"}]}]}]}""")]
    public void Invalid_hex_ids_are_rejected(string json) =>
        Assert.Throws<FormatException>(() => OtlpJson.ParseTraces(json));

    [Fact]
    public void Protobuf_round_trips()
    {
        var request = new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = "s" },
                            Spans = { new Span { Name = "n", TraceId = ByteString.CopyFrom(new byte[16]), SpanId = ByteString.CopyFrom(new byte[8]) } },
                        },
                    },
                },
            },
        };

        var parsed = ExportTraceServiceRequest.Parser.ParseFrom(request.ToByteArray());

        Assert.Equal(request, parsed);
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test --project tests/CodeAtlas.Otlp.Tests`
Expected: build error `The name 'OtlpJson' does not exist in the current context`.

- [ ] **Step 6: Implement `OtlpJson`**

`src/CodeAtlas.Otlp/OtlpJson.cs`:
```csharp
using System.Text.Json.Nodes;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.Otlp;

/// <summary>
/// Reads OTLP/JSON trace exports. OTLP/JSON is the proto3 JSON mapping except that trace and span ids are hex strings
/// instead of base64 (OTLP specification, "JSON Protobuf Encoding"), so ids are converted before the standard parser runs.
/// </summary>
internal static class OtlpJson
{
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    private static readonly HashSet<string> IdFields = ["traceId", "spanId", "parentSpanId"];

    public static ExportTraceServiceRequest ParseTraces(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new FormatException("Empty OTLP JSON document.");
        HexIdsToBase64(node);
        return Parser.Parse<ExportTraceServiceRequest>(node.ToJsonString());
    }

    private static void HexIdsToBase64(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (IdFields.Contains(key) && value is JsonValue v && v.TryGetValue<string>(out var hex))
                        obj[key] = hex.Length == 0 ? "" : Convert.ToBase64String(Convert.FromHexString(hex));
                    else
                        HexIdsToBase64(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array) HexIdsToBase64(item);
                break;
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.Otlp.Tests`
Expected: `total: 5, failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add global.json Directory.Build.props .gitignore CodeAtlas.Live.slnx src/CodeAtlas.Otlp tests/CodeAtlas.Otlp.Tests
git commit -m "feat(otlp): vendored opentelemetry-proto v1.5.0 and OTLP/JSON reader"
```

---

### Task 2: Span serializer with privacy rules

**Files:**
- Create: `src/CodeAtlas.AspNetCore/CodeAtlas.AspNetCore.csproj`, `src/CodeAtlas.AspNetCore/Export/SqlSummary.cs`, `src/CodeAtlas.AspNetCore/Export/SpanSerializer.cs`, `src/CodeAtlas.AspNetCore/Http/HttpFacts.cs`
- Test: `tests/CodeAtlas.AspNetCore.Tests/CodeAtlas.AspNetCore.Tests.csproj`, `tests/CodeAtlas.AspNetCore.Tests/AssemblyInfo.cs`, `tests/CodeAtlas.AspNetCore.Tests/Support/TestActivities.cs`, `tests/CodeAtlas.AspNetCore.Tests/SqlSummaryTests.cs`, `tests/CodeAtlas.AspNetCore.Tests/SpanSerializerTests.cs`

**Interfaces:**
- Consumes: OTLP types from Task 1.
- Produces:
  - `internal static string SqlSummary.Summarize(string sql)`
  - `internal sealed record HttpFacts(string Method, string? Route, int StatusCode, bool UnhandledException)`
  - `internal sealed class HttpFactsStore { void Set(Activity, HttpFacts); HttpFacts? Get(Activity); }`
  - `internal sealed record SerializedSpan(string Scope, OpenTelemetry.Proto.Trace.V1.Span Span)`
  - `internal sealed class SpanSerializer(bool keepDbStatements, HttpFactsStore facts) { const string AspNetCoreSource = "Microsoft.AspNetCore"; SerializedSpan Serialize(Activity); static KeyValue Kv(string key, object value); }`
  - test helper `TestActivities.Finished(string source, string name, ActivityKind kind = ActivityKind.Internal, Action<Activity>? setup = null) : Activity`

- [ ] **Step 1: Create the projects**

`src/CodeAtlas.AspNetCore/CodeAtlas.AspNetCore.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>CodeAtlas.AspNetCore</PackageId>
    <Version>0.1.0</Version>
    <Description>Development-only CodeAtlas Live integration for ASP.NET Core. Reference it in Debug builds only.</Description>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="OpenTelemetry" Version="1.19.1" />
    <ProjectReference Include="../CodeAtlas.Otlp/CodeAtlas.Otlp.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="CodeAtlas.AspNetCore.Tests" />
  </ItemGroup>
</Project>
```

`tests/CodeAtlas.AspNetCore.Tests/CodeAtlas.AspNetCore.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.12" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.19.1" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.19.0" />
    <PackageReference Include="OpenTelemetry.Exporter.InMemory" Version="1.19.1" />
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.15.0" />
    <PackageReference Include="Npgsql" Version="10.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/CodeAtlas.AspNetCore/CodeAtlas.AspNetCore.csproj" />
  </ItemGroup>
</Project>
```

`tests/CodeAtlas.AspNetCore.Tests/AssemblyInfo.cs`:
```csharp
// ActivitySource listeners are process-wide: parallel test hosts would record each other's spans.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

```bash
dotnet sln add src/CodeAtlas.AspNetCore tests/CodeAtlas.AspNetCore.Tests
```

`tests/CodeAtlas.AspNetCore.Tests/Support/TestActivities.cs`:
```csharp
using System.Diagnostics;

namespace CodeAtlas.AspNetCore.Tests.Support;

internal static class TestActivities
{
    /// <summary>A stopped, recorded activity from the named source, as an instrumentation library would produce it.</summary>
    public static Activity Finished(string source, string name, ActivityKind kind = ActivityKind.Internal, Action<Activity>? setup = null)
    {
        using var activitySource = new ActivitySource(source);
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => ReferenceEquals(s, activitySource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var activity = activitySource.StartActivity(name, kind)!;
        setup?.Invoke(activity);
        activity.Stop();
        return activity;
    }
}
```

- [ ] **Step 2: Write the failing SQL summary tests**

`tests/CodeAtlas.AspNetCore.Tests/SqlSummaryTests.cs`:
```csharp
using CodeAtlas.AspNetCore.Export;

namespace CodeAtlas.AspNetCore.Tests;

public class SqlSummaryTests
{
    [Theory]
    [InlineData("SELECT p.id FROM products AS p WHERE p.sku = @p0", "SELECT products")]
    [InlineData("INSERT INTO \"audit_logs\" (a) VALUES ($1);\nINSERT INTO products (sku) VALUES ($2)", "INSERT audit_logs products")]
    [InlineData("UPDATE users SET name = $1 WHERE id = $2", "UPDATE users")]
    [InlineData("DELETE FROM refresh_tokens WHERE expires < now()", "DELETE refresh_tokens")]
    [InlineData("WITH recent AS (SELECT 1) SELECT * FROM orders o JOIN customers c ON c.id = o.customer_id", "WITH orders customers")]
    [InlineData("SELECT o.id FROM public.\"Orders\" AS o", "SELECT public.Orders")]
    [InlineData("SELECT 1", "SELECT")]
    [InlineData("VACUUM", "SQL")]
    public void Keeps_only_operation_and_tables(string sql, string expected) =>
        Assert.Equal(expected, SqlSummary.Summarize(sql));

    [Theory]
    [InlineData("SELECT * FROM t WHERE note = 'from secret_table'")]
    [InlineData("SELECT * FROM t -- join secret_table\n")]
    [InlineData("SELECT * FROM t /* from secret_table */")]
    public void Words_inside_literals_and_comments_never_appear(string sql)
    {
        var summary = SqlSummary.Summarize(sql);

        Assert.Equal("SELECT t", summary);
        Assert.DoesNotContain("secret", summary);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: build error `The type or namespace name 'SqlSummary' could not be found`.

- [ ] **Step 4: Implement `SqlSummary`**

`src/CodeAtlas.AspNetCore/Export/SqlSummary.cs`:
```csharp
using System.Text.RegularExpressions;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>Reduces SQL to its operation and table names. Literals, comments, parameters and columns never survive.</summary>
internal static partial class SqlSummary
{
    public static string Summarize(string sql)
    {
        var code = Comment().Replace(Literal().Replace(sql, "''"), " ");
        var operation = Operation().Match(code);
        var tables = Table().Matches(code).Select(m => m.Groups[1].Value.Replace("\"", "")).Distinct().Take(4);
        return string.Join(' ', new[] { operation.Success ? operation.Groups[1].Value.ToUpperInvariant() : "SQL" }.Concat(tables));
    }

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex Literal();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^\s*(SELECT|INSERT|UPDATE|DELETE|WITH|MERGE|CALL)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Operation();

    [GeneratedRegex(@"\b(?:FROM|INTO|UPDATE|JOIN)\s+((?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)(?:\.(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*))?)", RegexOptions.IgnoreCase)]
    private static partial Regex Table();
}
```

- [ ] **Step 5: Run the SQL tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests -- --filter-class CodeAtlas.AspNetCore.Tests.SqlSummaryTests`
Expected: `total: 11, failed: 0`.

- [ ] **Step 6: Write the failing serializer tests**

`tests/CodeAtlas.AspNetCore.Tests/SpanSerializerTests.cs`:
```csharp
using System.Diagnostics;
using System.Text;
using CodeAtlas.AspNetCore.Export;
using CodeAtlas.AspNetCore.Http;
using CodeAtlas.AspNetCore.Tests.Support;
using Google.Protobuf;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests;

public class SpanSerializerTests
{
    private readonly HttpFactsStore _facts = new();
    private SpanSerializer Serializer(bool keepDb = false) => new(keepDb, _facts);

    private static string Attr(Span span, string key) => span.Attributes.Single(a => a.Key == key).Value.StringValue;
    private static bool Has(Span span, string key) => span.Attributes.Any(a => a.Key == key);

    [Fact]
    public void Copies_ids_times_kind_and_scope()
    {
        var activity = TestActivities.Finished("Test.Source", "work", ActivityKind.Client);

        var (scope, span) = Serializer().Serialize(activity);

        Assert.Equal("Test.Source", scope);
        Assert.Equal(activity.TraceId.ToHexString(), Convert.ToHexStringLower(span.TraceId.Span));
        Assert.Equal(activity.SpanId.ToHexString(), Convert.ToHexStringLower(span.SpanId.Span));
        Assert.True(span.ParentSpanId.IsEmpty);
        Assert.Equal(Span.Types.SpanKind.Client, span.Kind);
        Assert.Equal((ulong)(activity.StartTimeUtc - DateTime.UnixEpoch).Ticks * 100, span.StartTimeUnixNano);
        Assert.Equal((ulong)activity.Duration.Ticks * 100, span.EndTimeUnixNano - span.StartTimeUnixNano);
    }

    [Fact]
    public void Sql_text_becomes_a_summary_and_never_leaves()
    {
        var activity = TestActivities.Finished("Npgsql", "postgresql", ActivityKind.Client, a =>
        {
            a.SetTag("db.query.text", "SELECT name FROM products WHERE sku = 'SECRET-SKU'");
            a.SetTag("db.npgsql.data_source", "Host=db;Username=app_user");
            a.SetTag("db.system.name", "postgresql");
        });

        var (_, span) = Serializer().Serialize(activity);
        var bytes = Encoding.UTF8.GetString(span.ToByteArray());

        Assert.Equal("SELECT products", span.Name);
        Assert.Equal("SELECT products", span.Attributes.Single(a => a.Key == "db.query.summary").Value.StringValue);
        Assert.False(Has(span, "db.query.text"));
        Assert.False(Has(span, "db.npgsql.data_source"));
        Assert.True(Has(span, "db.system.name"));
        Assert.DoesNotContain("SECRET-SKU", bytes);
        Assert.DoesNotContain("app_user", bytes);
    }

    [Fact]
    public void Sql_text_is_kept_only_when_asked()
    {
        var activity = TestActivities.Finished("Npgsql", "postgresql", ActivityKind.Client, a => a.SetTag("db.statement", "SELECT 1 FROM t"));

        var (_, span) = Serializer(keepDb: true).Serialize(activity);

        Assert.True(Has(span, "db.statement"));
        Assert.Equal("SELECT t", span.Name);
    }

    [Fact]
    public void Query_strings_are_removed()
    {
        var activity = TestActivities.Finished("System.Net.Http", "GET", ActivityKind.Client, a =>
        {
            a.SetTag("url.full", "https://api.example.com/data?token=SECRET#frag");
            a.SetTag("url.query", "token=SECRET");
        });

        var (_, span) = Serializer().Serialize(activity);

        Assert.Contains("https://api.example.com/data", Attr(span, "url.full"));
        Assert.False(Has(span, "url.query"));
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(span.ToByteArray()));
    }

    [Fact]
    public void The_activity_itself_is_never_modified()
    {
        var activity = TestActivities.Finished("Npgsql", "postgresql", ActivityKind.Client, a =>
        {
            a.SetTag("db.query.text", "SELECT 1 FROM products");
            a.SetTag("db.npgsql.data_source", "Host=db");
        });
        var before = activity.TagObjects.ToList();

        Serializer().Serialize(activity);

        Assert.Equal(before, activity.TagObjects.ToList());
        Assert.Equal("postgresql", activity.DisplayName);
    }

    [Fact]
    public void Server_spans_get_route_method_and_status_from_http_facts()
    {
        var activity = TestActivities.Finished(SpanSerializer.AspNetCoreSource, "Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Server);
        _facts.Set(activity, new HttpFacts("POST", "/api/orders", 201, UnhandledException: false));

        var (_, span) = Serializer().Serialize(activity);

        Assert.Equal("POST /api/orders", span.Name);
        Assert.Equal(201, span.Attributes.Single(a => a.Key == "http.response.status_code").Value.IntValue);
        Assert.Equal("/api/orders", span.Attributes.Single(a => a.Key == "http.route").Value.StringValue);
        Assert.Equal(Status.Types.StatusCode.Unset, span.Status.Code);
    }

    [Fact]
    public void Unhandled_exceptions_mark_the_server_span_as_error()
    {
        var activity = TestActivities.Finished(SpanSerializer.AspNetCoreSource, "in", ActivityKind.Server);
        _facts.Set(activity, new HttpFacts("GET", "/boom", 500, UnhandledException: true));

        var (_, span) = Serializer().Serialize(activity);

        Assert.Equal(Status.Types.StatusCode.Error, span.Status.Code);
        Assert.True(span.Attributes.Single(a => a.Key == "codeatlas.http.unhandled_exception").Value.BoolValue);
    }

    [Fact]
    public void Status_and_exception_events_are_copied()
    {
        var activity = TestActivities.Finished("Test.Source", "work", setup: a =>
        {
            a.SetStatus(ActivityStatusCode.Error, "Not enough stock");
            a.AddException(new InvalidOperationException("Not enough stock"));
        });

        var (_, span) = Serializer().Serialize(activity);

        Assert.Equal(Status.Types.StatusCode.Error, span.Status.Code);
        Assert.Equal("Not enough stock", span.Status.Message);
        var ev = Assert.Single(span.Events);
        Assert.Equal("exception", ev.Name);
        Assert.Equal("Not enough stock", ev.Attributes.Single(a => a.Key == "exception.message").Value.StringValue);
    }

    [Fact]
    public void Unusual_tag_values_are_serialized_safely()
    {
        var activity = TestActivities.Finished("Test.Source", "work", setup: a =>
        {
            a.SetTag("array", new[] { "a", "b" });
            a.SetTag("ints", new[] { 1, 2 });
            a.SetTag("object", new Uri("https://example.com/x"));
            a.SetTag("big", ulong.MaxValue);
            a.SetTag("dbl", 1.5);
        });

        var (_, span) = Serializer().Serialize(activity);

        Assert.Equal(2, span.Attributes.Single(a => a.Key == "array").Value.ArrayValue.Values.Count);
        Assert.Equal(2, span.Attributes.Single(a => a.Key == "ints").Value.ArrayValue.Values[1].IntValue);
        Assert.Equal("https://example.com/x", span.Attributes.Single(a => a.Key == "object").Value.StringValue);
        Assert.Equal(ulong.MaxValue.ToString(), span.Attributes.Single(a => a.Key == "big").Value.StringValue);
        Assert.Equal(1.5, span.Attributes.Single(a => a.Key == "dbl").Value.DoubleValue);
    }
}
```

- [ ] **Step 7: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: build errors for `HttpFactsStore`, `SpanSerializer`, `HttpFacts`.

- [ ] **Step 8: Implement `HttpFacts` and `SpanSerializer`**

`src/CodeAtlas.AspNetCore/Http/HttpFacts.cs`:
```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CodeAtlas.AspNetCore.Http;

internal sealed record HttpFacts(string Method, string? Route, int StatusCode, bool UnhandledException);

/// <summary>
/// Side table keyed by the request's Activity object. CodeAtlas records HTTP facts here instead of adding tags to an
/// Activity that other tracing pipelines also see. Entries disappear with the Activity.
/// </summary>
internal sealed class HttpFactsStore
{
    private readonly ConditionalWeakTable<Activity, HttpFacts> _facts = new();

    public void Set(Activity activity, HttpFacts facts) => _facts.AddOrUpdate(activity, facts);

    public HttpFacts? Get(Activity activity) => _facts.TryGetValue(activity, out var facts) ? facts : null;
}
```

`src/CodeAtlas.AspNetCore/Export/SpanSerializer.cs`:
```csharp
using System.Collections;
using System.Diagnostics;
using CodeAtlas.AspNetCore.Http;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace CodeAtlas.AspNetCore.Export;

internal sealed record SerializedSpan(string Scope, ProtoSpan Span);

/// <summary>
/// Copies a finished Activity into an OTLP span and applies CodeAtlas's privacy rules during the copy.
/// The Activity is never modified, so any other tracing pipeline in the app sees it exactly as before.
/// </summary>
internal sealed class SpanSerializer(bool keepDbStatements, HttpFactsStore facts)
{
    public const string AspNetCoreSource = "Microsoft.AspNetCore";

    private static readonly HashSet<string> DbText = ["db.statement", "db.query.text"];
    private static readonly HashSet<string> Dropped = ["db.npgsql.data_source", "db.connection_string", "url.query"];

    public SerializedSpan Serialize(Activity activity)
    {
        var span = new ProtoSpan
        {
            TraceId = Id(activity.TraceId),
            SpanId = Id(activity.SpanId),
            ParentSpanId = activity.ParentSpanId == default ? ByteString.Empty : Id(activity.ParentSpanId),
            Name = activity.DisplayName,
            Kind = Kind(activity.Kind),
            StartTimeUnixNano = Nanos(activity.StartTimeUtc),
            EndTimeUnixNano = Nanos(activity.StartTimeUtc + activity.Duration),
        };

        foreach (var (key, value) in activity.TagObjects)
        {
            if (value is null || Dropped.Contains(key)) continue;
            if (DbText.Contains(key) && value is string sql)
            {
                var summary = SqlSummary.Summarize(sql);
                span.Name = summary;
                span.Attributes.Add(Kv("db.query.summary", summary));
                if (keepDbStatements) span.Attributes.Add(Kv(key, sql));
                continue;
            }
            span.Attributes.Add(Kv(key, key == "url.full" && value is string url ? StripQuery(url) : value));
        }

        foreach (var e in activity.Events)
        {
            var ev = new ProtoSpan.Types.Event { Name = e.Name, TimeUnixNano = Nanos(e.Timestamp.UtcDateTime) };
            foreach (var (key, value) in e.TagObjects)
                if (value is not null) ev.Attributes.Add(Kv(key, value));
            span.Events.Add(ev);
        }

        span.Status = activity.Status switch
        {
            ActivityStatusCode.Error => new Status { Code = Status.Types.StatusCode.Error, Message = activity.StatusDescription ?? "" },
            ActivityStatusCode.Ok => new Status { Code = Status.Types.StatusCode.Ok },
            _ => new Status(),
        };

        if (activity.Source.Name == AspNetCoreSource && facts.Get(activity) is { } http) ApplyHttpFacts(span, http);
        return new SerializedSpan(activity.Source.Name, span);
    }

    public static KeyValue Kv(string key, object value) => new() { Key = key, Value = Any(value) };

    private static void ApplyHttpFacts(ProtoSpan span, HttpFacts http)
    {
        void AddIfMissing(string key, object value)
        {
            if (span.Attributes.All(a => a.Key != key)) span.Attributes.Add(Kv(key, value));
        }

        AddIfMissing("http.request.method", http.Method);
        AddIfMissing("http.response.status_code", http.StatusCode);
        if (http.Route is not null)
        {
            AddIfMissing("http.route", http.Route);
            span.Name = $"{http.Method} {http.Route}";
        }
        if (http.UnhandledException)
        {
            span.Attributes.Add(Kv("codeatlas.http.unhandled_exception", true));
            span.Status = new Status { Code = Status.Types.StatusCode.Error, Message = "Unhandled exception" };
        }
        else if (http.StatusCode >= 500 && span.Status.Code == Status.Types.StatusCode.Unset)
        {
            span.Status = new Status { Code = Status.Types.StatusCode.Error, Message = $"HTTP {http.StatusCode}" };
        }
    }

    private static AnyValue Any(object value) => value switch
    {
        string s => new AnyValue { StringValue = s },
        bool b => new AnyValue { BoolValue = b },
        int or long or short or byte or sbyte or ushort or uint => new AnyValue { IntValue = Convert.ToInt64(value) },
        double or float or decimal => new AnyValue { DoubleValue = Convert.ToDouble(value) },
        IEnumerable list => ArrayOf(list),
        _ => new AnyValue { StringValue = value.ToString() ?? "" },
    };

    private static AnyValue ArrayOf(IEnumerable list)
    {
        var array = new ArrayValue();
        foreach (var item in list)
            if (item is not null) array.Values.Add(Any(item));
        return new AnyValue { ArrayValue = array };
    }

    private static string StripQuery(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    private static ByteString Id(ActivityTraceId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.CopyTo(bytes);
        return ByteString.CopyFrom(bytes);
    }

    private static ByteString Id(ActivitySpanId id)
    {
        Span<byte> bytes = stackalloc byte[8];
        id.CopyTo(bytes);
        return ByteString.CopyFrom(bytes);
    }

    private static ulong Nanos(DateTime utc) => (ulong)(utc - DateTime.UnixEpoch).Ticks * 100;

    private static ProtoSpan.Types.SpanKind Kind(ActivityKind kind) => kind switch
    {
        ActivityKind.Server => ProtoSpan.Types.SpanKind.Server,
        ActivityKind.Client => ProtoSpan.Types.SpanKind.Client,
        ActivityKind.Producer => ProtoSpan.Types.SpanKind.Producer,
        ActivityKind.Consumer => ProtoSpan.Types.SpanKind.Consumer,
        _ => ProtoSpan.Types.SpanKind.Internal,
    };
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: `total: 20, failed: 0`.

- [ ] **Step 10: Commit**

```bash
git add src/CodeAtlas.AspNetCore tests/CodeAtlas.AspNetCore.Tests CodeAtlas.Live.slnx
git commit -m "feat(aspnetcore): span serializer with SQL summaries and privacy rules"
```

---

### Task 3: Export processor and HTTP sink

**Files:**
- Create: `src/CodeAtlas.AspNetCore/Export/ISpanSink.cs`, `src/CodeAtlas.AspNetCore/Export/HttpSpanSink.cs`, `src/CodeAtlas.AspNetCore/Export/CodeAtlasExportProcessor.cs`
- Test: `tests/CodeAtlas.AspNetCore.Tests/Support/RecordingSink.cs`, `tests/CodeAtlas.AspNetCore.Tests/Support/BlockingSink.cs`, `tests/CodeAtlas.AspNetCore.Tests/Support/FakeCollector.cs`, `tests/CodeAtlas.AspNetCore.Tests/ExportProcessorTests.cs`

**Interfaces:**
- Consumes: `SpanSerializer`, `SerializedSpan`, `HttpFactsStore` (Task 2).
- Produces:
  - `internal interface ISpanSink { Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken); }`
  - `internal sealed class HttpSpanSink(Uri endpoint, TimeSpan timeout) : ISpanSink, IDisposable`
  - `internal sealed class CodeAtlasExportProcessor(SpanSerializer serializer, ISpanSink sink, Resource resource, int capacity, TimeSpan batchDelay, ILogger logger) : BaseProcessor<Activity>` with `long Dropped`, `long SendFailures`
  - test helpers `RecordingSink` (with `Spans`, `Requests`, `WaitForAsync`, `WaitForTraceAsync`), `RecordedSpan`, `BlockingSink`, `ThrowingSink`, `FakeCollector`

- [ ] **Step 1: Write the test support types**

`tests/CodeAtlas.AspNetCore.Tests/Support/RecordingSink.cs`:
```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using CodeAtlas.AspNetCore.Export;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests.Support;

internal sealed record RecordedSpan(string Scope, Span Span, Resource Resource)
{
    public string Name => Span.Name;
    public string TraceId => Convert.ToHexStringLower(Span.TraceId.Span);
    public string SpanId => Convert.ToHexStringLower(Span.SpanId.Span);
    public string ParentSpanId => Convert.ToHexStringLower(Span.ParentSpanId.Span);
    public bool IsError => Span.Status?.Code == Status.Types.StatusCode.Error;

    public object? Attr(string key)
    {
        var value = Span.Attributes.FirstOrDefault(a => a.Key == key)?.Value;
        return value?.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            null => null,
            _ => value.ToString(),
        };
    }
}

internal sealed class RecordingSink : ISpanSink
{
    private readonly ConcurrentQueue<RecordedSpan> _spans = new();
    private readonly ConcurrentQueue<ExportTraceServiceRequest> _requests = new();

    public IReadOnlyList<RecordedSpan> Spans => [.. _spans];
    public IReadOnlyList<ExportTraceServiceRequest> Requests => [.. _requests];

    public Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        foreach (var rs in request.ResourceSpans)
        foreach (var ss in rs.ScopeSpans)
        foreach (var span in ss.Spans)
            _spans.Enqueue(new RecordedSpan(ss.Scope.Name, span, rs.Resource));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RecordedSpan>> WaitForAsync(Func<IReadOnlyList<RecordedSpan>, bool> done, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            var now = Spans;
            if (done(now)) return now;
            await Task.Delay(20);
        }
        throw new TimeoutException("Spans received:\n" + string.Join('\n', Spans.Select(s => $"{s.Scope} | {s.Name} | {s.TraceId}")));
    }

    /// <summary>All spans of one trace, once its server span has arrived (children end, and are sent, before it).</summary>
    public async Task<IReadOnlyList<RecordedSpan>> WaitForTraceAsync(string traceId, int timeoutMs = 5000)
    {
        await WaitForAsync(s => s.Any(x => x.TraceId == traceId && x.Span.Kind == Span.Types.SpanKind.Server), timeoutMs);
        await Task.Delay(50);
        return Spans.Where(s => s.TraceId == traceId).ToList();
    }
}
```

`tests/CodeAtlas.AspNetCore.Tests/Support/BlockingSink.cs`:
```csharp
using CodeAtlas.AspNetCore.Export;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>The first send blocks until <see cref="Release"/>; later sends record normally.</summary>
internal sealed class BlockingSink : ISpanSink
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RecordingSink Recorded { get; } = new();

    public async Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            FirstCallStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
        await Recorded.SendAsync(request, cancellationToken);
    }

    public void Release() => _release.TrySetResult();
}

internal sealed class ThrowingSink : ISpanSink
{
    public int Calls;

    public Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        throw new HttpRequestException("collector unreachable");
    }
}
```

`tests/CodeAtlas.AspNetCore.Tests/Support/FakeCollector.cs`:
```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>
/// Minimal OTLP/HTTP receiver on HttpListener (not ASP.NET Core), so its own requests produce no server spans
/// that CodeAtlas would record and export again.
/// </summary>
internal sealed class FakeCollector : IDisposable
{
    private readonly HttpListener _listener = new();

    public FakeCollector()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }
    public string TracesUrl => $"http://127.0.0.1:{Port}/v1/traces";
    public ConcurrentQueue<(string ContentType, ExportTraceServiceRequest Request)> Received { get; } = new();

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            Received.Enqueue((context.Request.ContentType ?? "", ExportTraceServiceRequest.Parser.ParseFrom(body.ToArray())));
            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }

    internal static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose() => _listener.Close();
}
```

- [ ] **Step 2: Write the failing processor tests**

`tests/CodeAtlas.AspNetCore.Tests/ExportProcessorTests.cs`:
```csharp
using System.Diagnostics;
using CodeAtlas.AspNetCore.Export;
using CodeAtlas.AspNetCore.Http;
using CodeAtlas.AspNetCore.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Resource.V1;

namespace CodeAtlas.AspNetCore.Tests;

public class ExportProcessorTests
{
    private static readonly Resource Resource = new() { Attributes = { SpanSerializer.Kv("service.name", "test") } };

    private static CodeAtlasExportProcessor Processor(ISpanSink sink, int capacity = 2048, int batchDelayMs = 10) =>
        new(new SpanSerializer(false, new HttpFactsStore()), sink, Resource, capacity, TimeSpan.FromMilliseconds(batchDelayMs), NullLogger.Instance);

    private static Activity Span(string name) => TestActivities.Finished("Test.Source", name);

    [Fact]
    public async Task Delivers_spans_grouped_by_scope_with_the_resource()
    {
        var sink = new RecordingSink();
        using var processor = Processor(sink);

        processor.OnEnd(Span("a"));
        processor.OnEnd(TestActivities.Finished("Other.Source", "b"));

        var spans = await sink.WaitForAsync(s => s.Count == 2);
        Assert.Equal(["Other.Source", "Test.Source"], spans.Select(s => s.Scope).Order());
        Assert.All(spans, s => Assert.Equal("test", s.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue));
    }

    [Fact]
    public async Task Drops_the_oldest_spans_when_full_and_never_blocks_the_caller()
    {
        var sink = new BlockingSink();
        using var processor = Processor(sink, capacity: 4);
        processor.OnEnd(Span("first"));
        await sink.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) processor.OnEnd(Span($"n{i}"));
        clock.Stop();
        sink.Release();
        processor.Shutdown(5000);

        Assert.True(clock.ElapsedMilliseconds < 200, $"OnEnd took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(6, processor.Dropped);
        Assert.Equal(["first", "n6", "n7", "n8", "n9"], sink.Recorded.Spans.Select(s => s.Name));
    }

    [Fact]
    public async Task Sink_failures_are_counted_and_never_thrown()
    {
        var sink = new ThrowingSink();
        using var processor = Processor(sink);

        processor.OnEnd(Span("a"));
        processor.OnEnd(Span("b"));

        var clock = Stopwatch.StartNew();
        while (processor.SendFailures == 0 && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.True(processor.SendFailures >= 1);
    }

    [Fact]
    public void Shutdown_flushes_queued_spans()
    {
        var sink = new RecordingSink();
        var processor = Processor(sink, batchDelayMs: 60_000);

        processor.OnEnd(Span("a"));
        processor.OnEnd(Span("b"));
        Assert.True(processor.Shutdown(5000));

        Assert.Equal(["a", "b"], sink.Spans.Select(s => s.Name));
        processor.Dispose();
    }

    [Fact]
    public async Task Http_sink_posts_protobuf_to_the_collector()
    {
        using var collector = new FakeCollector();
        using var sink = new HttpSpanSink(new Uri(collector.TracesUrl), TimeSpan.FromSeconds(2));
        using var processor = Processor(sink);

        processor.OnEnd(Span("over-the-wire"));

        var clock = Stopwatch.StartNew();
        while (collector.Received.IsEmpty && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
        var (contentType, request) = Assert.Single(collector.Received);
        Assert.Equal("application/x-protobuf", contentType);
        Assert.Equal("over-the-wire", request.ResourceSpans[0].ScopeSpans[0].Spans[0].Name);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: build errors for `ISpanSink`, `HttpSpanSink`, `CodeAtlasExportProcessor`.

- [ ] **Step 4: Implement sink and processor**

`src/CodeAtlas.AspNetCore/Export/ISpanSink.cs`:
```csharp
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Export;

internal interface ISpanSink
{
    Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken);
}
```

`src/CodeAtlas.AspNetCore/Export/HttpSpanSink.cs`:
```csharp
using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>OTLP/HTTP protobuf to the CodeAtlas collector.</summary>
internal sealed class HttpSpanSink(Uri endpoint, TimeSpan timeout) : ISpanSink, IDisposable
{
    private static readonly MediaTypeHeaderValue Protobuf = new("application/x-protobuf");
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = timeout }) { Timeout = timeout };

    public async Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = Protobuf;
        using var response = await _http.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
```

`src/CodeAtlas.AspNetCore/Export/CodeAtlasExportProcessor.cs`:
```csharp
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>
/// Serializes each finished activity immediately (a snapshot, so later changes by others cannot leak in), queues it in
/// a bounded channel that drops the oldest entry when full, and sends batches from a background task.
/// Nothing here can block or fail a request.
/// </summary>
internal sealed class CodeAtlasExportProcessor : BaseProcessor<Activity>
{
    private const int MaxBatch = 512;

    private readonly SpanSerializer _serializer;
    private readonly ISpanSink _sink;
    private readonly Resource _resource;
    private readonly TimeSpan _batchDelay;
    private readonly ILogger _logger;
    private readonly Channel<SerializedSpan> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _flushNow = new();
    private readonly Task _loop;
    private long _dropped;
    private long _sendFailures;
    private long _lastWarning;

    public CodeAtlasExportProcessor(SpanSerializer serializer, ISpanSink sink, Resource resource, int capacity, TimeSpan batchDelay, ILogger logger)
    {
        _serializer = serializer;
        _sink = sink;
        _resource = resource;
        _batchDelay = batchDelay;
        _logger = logger;
        _queue = Channel.CreateBounded<SerializedSpan>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
        _loop = Task.Run(RunAsync);
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public long SendFailures => Interlocked.Read(ref _sendFailures);

    public override void OnEnd(Activity activity)
    {
        try { _queue.Writer.TryWrite(_serializer.Serialize(activity)); }
        catch (Exception e) { Warn("serialization", e); }
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        _queue.Writer.TryComplete();
        _flushNow.Cancel(); // end a pending batch delay so queued spans go out now
        var finished = _loop.Wait(timeoutMilliseconds < 0 ? Timeout.Infinite : timeoutMilliseconds);
        _stopping.Cancel();
        return finished;
    }

    private async Task RunAsync()
    {
        // Our own HTTP export must never be traced (it would feed itself).
        using var suppress = SuppressInstrumentationScope.Begin();
        Activity.Current = null;
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stopping.Token))
            {
                try { await Task.Delay(_batchDelay, _flushNow.Token); }
                catch (OperationCanceledException) when (_flushNow.IsCancellationRequested) { }
                var batch = new List<SerializedSpan>();
                while (batch.Count < MaxBatch && reader.TryRead(out var span)) batch.Add(span);
                if (batch.Count > 0) await SendAsync(batch);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task SendAsync(List<SerializedSpan> batch)
    {
        var resourceSpans = new ResourceSpans { Resource = _resource };
        foreach (var group in batch.GroupBy(s => s.Scope))
        {
            var scope = new ScopeSpans { Scope = new InstrumentationScope { Name = group.Key } };
            scope.Spans.AddRange(group.Select(s => s.Span));
            resourceSpans.ScopeSpans.Add(scope);
        }
        try
        {
            await _sink.SendAsync(new ExportTraceServiceRequest { ResourceSpans = { resourceSpans } }, _stopping.Token);
        }
        catch (Exception e) when (!_stopping.IsCancellationRequested)
        {
            Interlocked.Increment(ref _sendFailures);
            Warn("export", e);
        }
    }

    // At most one log line a minute, at Debug: an offline collector is normal and must not spam the app's logs.
    private void Warn(string stage, Exception e)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarning);
        if (last != 0 && now - last < 60_000) return;
        Interlocked.Exchange(ref _lastWarning, now);
        _logger.LogDebug(e, "CodeAtlas {Stage} failed; CodeAtlas telemetry is being dropped. The application is not affected.", stage);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: `total: 25, failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/CodeAtlas.AspNetCore/Export tests/CodeAtlas.AspNetCore.Tests
git commit -m "feat(aspnetcore): bounded export processor and OTLP/HTTP sink"
```

---

### Task 4: `AddCodeAtlas`, own tracer provider, HTTP facts — and the coexistence gate

**Files:**
- Create: `src/CodeAtlas.AspNetCore/CodeAtlasOptions.cs`, `src/CodeAtlas.AspNetCore/CodeAtlasExtensions.cs`, `src/CodeAtlas.AspNetCore/Tracing/CodeAtlasDiagnostics.cs`, `src/CodeAtlas.AspNetCore/Tracing/CodeAtlasRuntime.cs`, `src/CodeAtlas.AspNetCore/Tracing/RecordOnlySampler.cs`, `src/CodeAtlas.AspNetCore/Tracing/CodeAtlasTracing.cs`, `src/CodeAtlas.AspNetCore/Http/HttpFactsStartupFilter.cs`
- Test: `tests/CodeAtlas.AspNetCore.Tests/Support/TestApp.cs`, `tests/CodeAtlas.AspNetCore.Tests/AddCodeAtlasTests.cs`, `tests/CodeAtlas.AspNetCore.Tests/CoexistenceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–3.
- Produces:
  - `public sealed class CodeAtlasOptions { string Endpoint; string? ServiceName; bool EnableOutsideDevelopment; bool KeepDbStatements; bool WrapperSpansBesideAppTracing; int QueueCapacity; TimeSpan ExportTimeout; internal TimeSpan BatchDelay; internal Func<IServiceProvider, ISpanSink>? SinkFactory; }`
  - `public static WebApplicationBuilder CodeAtlas.AspNetCore.CodeAtlasExtensions.AddCodeAtlas(this WebApplicationBuilder builder, Action<CodeAtlasOptions>? configure = null)`
  - `internal static class CodeAtlasDiagnostics { const string SourceName = "CodeAtlas.Components"; static readonly ActivitySource Source; static void Fail(Activity?, Exception); }`
  - `internal sealed class CodeAtlasRuntime { CodeAtlasOptions Options; bool AppHasTracing; bool WrapperSpansEnabled; void Initialize(IServiceProvider, ILogger); }`
  - `internal sealed class CodeAtlasTracing : IHostedService { CodeAtlasExportProcessor? Processor; static string[] Sources; }`
  - test helper `TestApp` with `StartAsync(...)`, `SendAsync(...)` returning `(HttpResponseMessage, string TraceId)`

- [ ] **Step 1: Write the test host helper**

`tests/CodeAtlas.AspNetCore.Tests/Support/TestApp.cs`:
```csharp
using System.Net.Http.Json;
using System.Security.Cryptography;
using CodeAtlas.AspNetCore.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.AspNetCore.Tests.Support;

internal sealed class TestApp : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required RecordingSink Sink { get; init; }

    public static async Task<TestApp> StartAsync(
        Action<WebApplication> map,
        Action<WebApplicationBuilder>? configure = null,
        Action<CodeAtlasOptions>? codeAtlas = null,
        string environment = "Development",
        bool addCodeAtlas = true,
        bool codeAtlasFirst = false,
        ISpanSink? sink = null,
        bool realHttpSink = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(TestApp).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var recording = new RecordingSink();

        void AddAtlas() => builder.AddCodeAtlas(o =>
        {
            if (!realHttpSink) o.SinkFactory = _ => sink ?? recording;
            o.BatchDelay = TimeSpan.FromMilliseconds(10);
            codeAtlas?.Invoke(o);
        });

        if (addCodeAtlas && codeAtlasFirst) AddAtlas();
        configure?.Invoke(builder);
        if (addCodeAtlas && !codeAtlasFirst) AddAtlas();

        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return new TestApp { App = app, Client = app.GetTestClient(), Sink = recording };
    }

    /// <summary>Sends a request with a fresh sampled traceparent and returns the trace id to look the spans up by.</summary>
    public async Task<(HttpResponseMessage Response, string TraceId)> SendAsync(HttpMethod method, string url, object? json = null)
    {
        var traceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var parentId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("traceparent", $"00-{traceId}-{parentId}-01");
        if (json is not null) request.Content = JsonContent.Create(json);
        HttpResponseMessage response;
        try { response = await Client.SendAsync(request); }
        catch (Exception) { response = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError); }
        return (response, traceId);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
```

- [ ] **Step 2: Write the failing `AddCodeAtlas` tests**

`tests/CodeAtlas.AspNetCore.Tests/AddCodeAtlasTests.cs`:
```csharp
using CodeAtlas.AspNetCore.Tests.Support;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tests;

public class AddCodeAtlasTests
{
    private static void Map(WebApplication app)
    {
        app.MapGet("/orders/{id:int}", (int id) => id);
        app.MapGet("/boom", (Func<string>)(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Does_nothing_outside_Development()
    {
        await using var app = await TestApp.StartAsync(Map, environment: "Production");

        await app.SendAsync(HttpMethod.Get, "/orders/1");
        await Task.Delay(300);

        Assert.Null(app.App.Services.GetService<CodeAtlasRuntime>());
        Assert.Empty(app.Sink.Spans);
    }

    [Fact]
    public async Task Config_switch_enables_it_outside_Development()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Configuration["CodeAtlas:Enabled"] = "true", environment: "Production");

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/1");

        Assert.NotEmpty(await app.Sink.WaitForTraceAsync(traceId));
    }

    [Fact]
    public async Task Records_the_server_span_with_route_method_status_and_resource()
    {
        await using var app = await TestApp.StartAsync(Map);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/7");

        var server = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.Equal("Microsoft.AspNetCore", server.Scope);
        Assert.Equal("GET /orders/{id:int}", server.Name);
        Assert.Equal("/orders/{id:int}", server.Attr("http.route"));
        Assert.Equal(200L, server.Attr("http.response.status_code"));
        Assert.Equal("CodeAtlas.AspNetCore.Tests", server.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue);
        Assert.False(server.Resource.Attributes.Single(a => a.Key == "codeatlas.app_has_tracing").Value.BoolValue);
    }

    [Fact]
    public async Task Continues_the_incoming_traceparent()
    {
        await using var app = await TestApp.StartAsync(Map);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/orders/1");

        var server = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.Equal(traceId, server.TraceId);
        Assert.Equal(16, server.ParentSpanId.Length);
    }

    [Fact]
    public async Task Unhandled_exceptions_mark_the_server_span()
    {
        await using var app = await TestApp.StartAsync(Map);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/boom");

        var server = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        Assert.True(server.IsError);
        Assert.Equal(true, server.Attr("codeatlas.http.unhandled_exception"));
        Assert.Equal(500L, server.Attr("http.response.status_code"));
    }

    [Fact]
    public async Task Parallel_requests_get_their_own_http_facts()
    {
        await using var app = await TestApp.StartAsync(a => a.MapGet("/items/{i:int}", (int i) => i % 2 == 0 ? Results.Ok(i) : Results.NotFound()));

        var sent = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => app.SendAsync(HttpMethod.Get, $"/items/{i}")));

        await app.Sink.WaitForAsync(s => s.Count(x => x.Span.Kind == Span.Types.SpanKind.Server) >= 50);
        for (var i = 0; i < 50; i++)
        {
            var server = app.Sink.Spans.Single(s => s.TraceId == sent[i].TraceId && s.Span.Kind == Span.Types.SpanKind.Server);
            Assert.Equal("/items/{i:int}", server.Attr("http.route"));
            Assert.Equal(i % 2 == 0 ? 200L : 404L, server.Attr("http.response.status_code"));
        }
    }

    [Fact]
    public async Task Metrics_only_OpenTelemetry_is_not_app_tracing()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("App")));

        var runtime = app.App.Services.GetRequiredService<CodeAtlasRuntime>();
        Assert.False(runtime.AppHasTracing);
        Assert.True(runtime.WrapperSpansEnabled);
    }

    [Fact]
    public async Task Detects_app_tracing_even_when_AddCodeAtlas_comes_first()
    {
        await using var app = await TestApp.StartAsync(Map, b => b.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("App")), codeAtlasFirst: true);

        var runtime = app.App.Services.GetRequiredService<CodeAtlasRuntime>();
        Assert.True(runtime.AppHasTracing);
        Assert.False(runtime.WrapperSpansEnabled);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: build errors for `AddCodeAtlas`, `CodeAtlasOptions`, `CodeAtlasRuntime`.

- [ ] **Step 4: Implement options, diagnostics, runtime, sampler**

`src/CodeAtlas.AspNetCore/CodeAtlasOptions.cs`:
```csharp
using CodeAtlas.AspNetCore.Export;

namespace CodeAtlas.AspNetCore;

public sealed class CodeAtlasOptions
{
    /// <summary>OTLP/HTTP traces endpoint of the CodeAtlas collector. Config: <c>CodeAtlas:Endpoint</c>.</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:4318/v1/traces";

    /// <summary>Defaults to the application name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>CodeAtlas is off outside Development unless this is set. Config: <c>CodeAtlas:Enabled</c>.</summary>
    public bool EnableOutsideDevelopment { get; set; }

    /// <summary>Keep SQL text (never parameters) on database spans. Off by default.</summary>
    public bool KeepDbStatements { get; set; }

    /// <summary>
    /// Add endpoint/action/handler spans even though the app has its own tracing. They become parents of database and
    /// HTTP spans, which then point at parents the app's own tracing tool never receives.
    /// </summary>
    public bool WrapperSpansBesideAppTracing { get; set; }

    public int QueueCapacity { get; set; } = 2048;

    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    internal Func<IServiceProvider, ISpanSink>? SinkFactory { get; set; }
}
```

`src/CodeAtlas.AspNetCore/Tracing/CodeAtlasDiagnostics.cs`:
```csharp
using System.Diagnostics;

namespace CodeAtlas.AspNetCore.Tracing;

internal static class CodeAtlasDiagnostics
{
    public const string SourceName = "CodeAtlas.Components";

    public static readonly ActivitySource Source = new(SourceName, "0.1.0");

    public static void Fail(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddException(exception);
    }
}
```

`src/CodeAtlas.AspNetCore/Tracing/CodeAtlasRuntime.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>Decided once at start-up, when every registration (whatever its order) is known.</summary>
internal sealed class CodeAtlasRuntime(CodeAtlasOptions options)
{
    public CodeAtlasOptions Options { get; } = options;
    public bool AppHasTracing { get; private set; }
    public bool WrapperSpansEnabled { get; private set; }

    public void Initialize(IServiceProvider services, ILogger logger)
    {
        // CodeAtlas's own provider is not registered in DI, so any TracerProvider here belongs to the app.
        AppHasTracing = services.GetService<TracerProvider>() is not null;
        WrapperSpansEnabled = !AppHasTracing || Options.WrapperSpansBesideAppTracing;
        if (AppHasTracing && !WrapperSpansEnabled)
            logger.LogWarning(
                "CodeAtlas: this app has its own OpenTelemetry tracing, so CodeAtlas does not add endpoint, action or handler " +
                "spans (they would break parent links in your own tracing tool). Set WrapperSpansBesideAppTracing = true to opt in.");
    }
}
```

`src/CodeAtlas.AspNetCore/Tracing/RecordOnlySampler.cs`:
```csharp
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>
/// Asks for data but never sets the sampled flag: the app's own sampler (if any) keeps deciding what its exporters
/// receive and what flag goes downstream, while CodeAtlas still sees every request.
/// </summary>
internal sealed class RecordOnlySampler : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) => new(SamplingDecision.RecordOnly);
}
```

- [ ] **Step 5: Implement the hosted tracer, facts middleware and `AddCodeAtlas`**

`src/CodeAtlas.AspNetCore/Tracing/CodeAtlasTracing.cs`:
```csharp
using CodeAtlas.AspNetCore.Export;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>Owns CodeAtlas's private TracerProvider. Nothing of it is registered in, or shared with, the app's DI.</summary>
internal sealed class CodeAtlasTracing(
    CodeAtlasRuntime runtime, SpanSerializer serializer, ISpanSink sink, IServiceProvider services,
    IHostEnvironment environment, ILogger<CodeAtlasTracing> logger) : IHostedService, IDisposable
{
    public static readonly string[] Sources = [CodeAtlasDiagnostics.SourceName, SpanSerializer.AspNetCoreSource, "Npgsql", "System.Net.Http"];

    private TracerProvider? _provider;

    public CodeAtlasExportProcessor? Processor { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        runtime.Initialize(services, logger);
        var resource = new Resource
        {
            Attributes =
            {
                SpanSerializer.Kv("service.name", runtime.Options.ServiceName ?? environment.ApplicationName),
                SpanSerializer.Kv("codeatlas.app_has_tracing", runtime.AppHasTracing),
            },
        };
        Processor = new CodeAtlasExportProcessor(serializer, sink, resource, runtime.Options.QueueCapacity, runtime.Options.BatchDelay, logger);
        _provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(Sources)
            .SetSampler(new RecordOnlySampler())
            .AddProcessor(Processor)
            .Build();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _provider?.Shutdown(5000);
        return Task.CompletedTask;
    }

    public void Dispose() => _provider?.Dispose();
}
```

`src/CodeAtlas.AspNetCore/Http/HttpFactsStartupFilter.cs`:
```csharp
using System.Diagnostics;
using CodeAtlas.AspNetCore.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeAtlas.AspNetCore.Http;

/// <summary>First middleware in the pipeline: notes method, route and status for the request's Activity, without tagging it.</summary>
internal sealed class HttpFactsStartupFilter(HttpFactsStore store) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var activity = Activity.Current;
            var failed = false;
            try
            {
                await nextMiddleware(context);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                if (activity?.Source.Name == SpanSerializer.AspNetCoreSource)
                    store.Set(activity, new HttpFacts(
                        context.Request.Method,
                        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                        failed ? 500 : context.Response.StatusCode,
                        failed));
            }
        });
        next(app);
    };
}
```

`src/CodeAtlas.AspNetCore/CodeAtlasExtensions.cs`:
```csharp
using CodeAtlas.AspNetCore.Export;
using CodeAtlas.AspNetCore.Http;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeAtlas.AspNetCore;

public static class CodeAtlasExtensions
{
    /// <summary>
    /// Adds CodeAtlas Live tracing. Call it inside <c>#if CODEATLAS</c> with a Debug-only package reference.
    /// Does nothing outside Development unless <c>CodeAtlas:Enabled=true</c>.
    /// </summary>
    public static WebApplicationBuilder AddCodeAtlas(this WebApplicationBuilder builder, Action<CodeAtlasOptions>? configure = null)
    {
        var options = new CodeAtlasOptions();
        configure?.Invoke(options);
        var section = builder.Configuration.GetSection("CodeAtlas");
        if (section.GetValue<bool>("Enabled")) options.EnableOutsideDevelopment = true;
        if (section["Endpoint"] is { Length: > 0 } endpoint) options.Endpoint = endpoint;
        if (!builder.Environment.IsDevelopment() && !options.EnableOutsideDevelopment) return builder;

        var services = builder.Services;
        services.AddSingleton(new CodeAtlasRuntime(options));
        services.AddSingleton<HttpFactsStore>();
        services.AddSingleton(sp => new SpanSerializer(options.KeepDbStatements, sp.GetRequiredService<HttpFactsStore>()));
        services.AddSingleton<ISpanSink>(sp => options.SinkFactory?.Invoke(sp) ?? new HttpSpanSink(new Uri(options.Endpoint), options.ExportTimeout));
        services.AddSingleton<IStartupFilter, HttpFactsStartupFilter>();
        services.AddHostedService<CodeAtlasTracing>();
        return builder;
    }
}
```

- [ ] **Step 6: Run the `AddCodeAtlas` tests to verify they pass**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests -- --filter-class CodeAtlas.AspNetCore.Tests.AddCodeAtlasTests`
Expected: `total: 8, failed: 0`.

If `Records_the_server_span_...` fails because the server span's scope is not `Microsoft.AspNetCore`, print `server.Scope` and stop: the hosting activity source name in .NET 10 differs from the spec and §5.4 needs revisiting with the maintainer.

- [ ] **Step 7: Write the coexistence gate tests**

`tests/CodeAtlas.AspNetCore.Tests/CoexistenceTests.cs`:
```csharp
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CodeAtlas.AspNetCore.Tests.Support;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tests;

/// <summary>
/// GATE (spec §5.4): an app with its own tracing must export exactly the same data with and without CodeAtlas.
/// If any test here fails, stop implementation and report the failing output to the maintainer.
/// </summary>
public class CoexistenceTests
{
    private static readonly ActivitySource AppSource = new("App.Work");

    private static Action<WebApplicationBuilder> AppTracing(List<Activity> exported, Sampler sampler) => b =>
        b.Services.AddOpenTelemetry().WithTracing(t => t
            .AddAspNetCoreInstrumentation(o =>
            {
                o.RecordException = true;
                o.EnrichWithHttpRequest = (activity, _) => activity.SetTag("app.custom", "x");
            })
            .AddSource("App.Work")
            .SetSampler(sampler)
            .AddInMemoryExporter(exported));

    private static void Map(WebApplication app)
    {
        app.MapGet("/work", () =>
        {
            using (AppSource.StartActivity("app-work")) { }
            return Results.Ok(new { recorded = Activity.Current?.Recorded ?? false });
        });
        app.MapGet("/fail", (Func<string>)(() => throw new InvalidOperationException("app failure")));
    }

    // What the app's own tracing tool would show, without per-run ids and timings.
    private static List<string> Shape(IEnumerable<Activity> spans)
    {
        var list = spans.ToList();
        return list.Select(a =>
        {
            var parent = list.FirstOrDefault(p => p.SpanId == a.ParentSpanId);
            var parentName = a.ParentSpanId == default ? "root" : parent?.DisplayName ?? "MISSING";
            var tags = string.Join(",", a.TagObjects.Select(t => $"{t.Key}={t.Value}").Order());
            var events = string.Join(",", a.Events.Select(e => e.Name));
            return $"{a.Source.Name}|{a.DisplayName}|{a.Kind}|{a.Status}|{a.StatusDescription}|recorded={a.Recorded}|parent={parentName}|events={events}|{tags}";
        }).Order().ToList();
    }

    private static async Task<(List<string> Shape, List<bool> Recorded, TestApp App)> RunAsync(Func<Sampler> sampler, bool withCodeAtlas, Action<CodeAtlas.AspNetCore.CodeAtlasOptions>? options = null)
    {
        var exported = new List<Activity>();
        var app = await TestApp.StartAsync(Map, AppTracing(exported, sampler()), options, addCodeAtlas: withCodeAtlas);
        var recorded = new List<bool>();
        for (var i = 0; i < 3; i++)
            recorded.Add((await app.Client.GetFromJsonAsync<JsonElement>("/work")).GetProperty("recorded").GetBoolean());
        await app.SendAsync(HttpMethod.Get, "/fail");
        await Task.Delay(300);
        lock (exported) return (Shape(exported), recorded, app);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task App_exports_and_sampling_are_identical_with_and_without_CodeAtlas(bool sampleEverything)
    {
        Sampler Sampler() => sampleEverything ? new AlwaysOnSampler() : new AlwaysOffSampler();

        var (without, recordedWithout, plain) = await RunAsync(Sampler, withCodeAtlas: false);
        await plain.DisposeAsync();
        var (with, recordedWith, traced) = await RunAsync(Sampler, withCodeAtlas: true);
        await using var _ = traced;

        Assert.Equal(without, with);
        Assert.Equal(recordedWithout, recordedWith);
        // 4 server spans (/work ×3, /fail) + 3 app-work spans when sampled; nothing otherwise.
        Assert.Equal(sampleEverything ? 7 : 0, with.Count);
    }

    [Fact]
    public async Task CodeAtlas_still_sees_every_request_when_the_app_samples_nothing()
    {
        var (_, _, app) = await RunAsync(() => new AlwaysOffSampler(), withCodeAtlas: true);
        await using var _ = app;

        await app.Sink.WaitForAsync(s => s.Count(x => x.Span.Kind == Span.Types.SpanKind.Server) >= 4);
    }

    [Fact]
    public async Task Wrapper_spans_are_off_beside_app_tracing_and_can_be_opted_in()
    {
        var (_, _, off) = await RunAsync(() => new AlwaysOnSampler(), withCodeAtlas: true);
        Assert.False(off.App.Services.GetRequiredService<CodeAtlasRuntime>().WrapperSpansEnabled);
        await off.DisposeAsync();

        var (_, _, on) = await RunAsync(() => new AlwaysOnSampler(), withCodeAtlas: true, o => o.WrapperSpansBesideAppTracing = true);
        await using var _ = on;
        Assert.True(on.App.Services.GetRequiredService<CodeAtlasRuntime>().WrapperSpansEnabled);
    }
}
```

- [ ] **Step 8: Run the gate**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests -- --filter-class CodeAtlas.AspNetCore.Tests.CoexistenceTests`
Expected: `total: 4, failed: 0`.

**If any coexistence test fails: stop here.** Do not change the assertions to make them pass. Commit the tests as they are on a branch, copy the failure output into the spec under §13 ("Open items needing the maintainer"), and ask the maintainer to choose between the two fallbacks named in spec §5.4.

- [ ] **Step 9: Commit**

```bash
git add src/CodeAtlas.AspNetCore tests/CodeAtlas.AspNetCore.Tests
git commit -m "feat(aspnetcore): AddCodeAtlas with private tracer provider; coexistence gate tests"
```

---

### Task 5: Endpoint, action and handler spans with method identity

**Files:**
- Create: `src/CodeAtlas.AspNetCore/Spans/MethodIdentity.cs`, `src/CodeAtlas.AspNetCore/Spans/EndpointSpanPolicy.cs`, `src/CodeAtlas.AspNetCore/Spans/ActionSpanFilter.cs`, `src/CodeAtlas.AspNetCore/HandlerSpanExtensions.cs`
- Modify: `src/CodeAtlas.AspNetCore/CodeAtlasExtensions.cs` (register policy and filter)
- Test: `tests/CodeAtlas.AspNetCore.Tests/EndpointSpanTests.cs`, `tests/CodeAtlas.AspNetCore.Tests/ControllerSpanTests.cs`

**Interfaces:**
- Consumes: `CodeAtlasDiagnostics`, `CodeAtlasRuntime` (Task 4); `TestApp`, `RecordingSink` (Tasks 3–4).
- Produces:
  - `internal enum Evidence { Executed, Routing }`
  - `internal static class MethodIdentity { static void Tag(Activity?, MethodInfo?, Evidence); static MethodInfo? HandlerOf(Endpoint); }`
  - `public static TBuilder HandlerSpanExtensions.WithCodeAtlasHandlerSpan<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder`
  - span names and attributes listed in Global Constraints.

- [ ] **Step 1: Write the failing Minimal API tests**

`tests/CodeAtlas.AspNetCore.Tests/EndpointSpanTests.cs`:
```csharp
using System.Reflection;
using CodeAtlas.AspNetCore.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests;

public record OrderRequest(int Quantity);

public static class OrderEndpoints
{
    public static IResult Create(OrderRequest request) =>
        request.Quantity > 99 ? throw new InvalidOperationException("Too many") : Results.Created("/orders/1", request);
}

public class EndpointSpanTests
{
    private static async ValueTask<object?> Validate(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.GetArgument<OrderRequest>(0).Quantity <= 0 ? Results.BadRequest() : await next(context);

    private static readonly MethodInfo Create = typeof(OrderEndpoints).GetMethod(nameof(OrderEndpoints.Create))!;

    private static Task<TestApp> StartAsync() => TestApp.StartAsync(
        app =>
        {
            app.MapPost("/orders", OrderEndpoints.Create).AddEndpointFilter(Validate).WithCodeAtlasHandlerSpan();
            var group = app.MapGroup("/g").WithCodeAtlasHandlerSpan();
            group.MapPost("/orders", OrderEndpoints.Create).AddEndpointFilter(Validate);
            app.MapGet("/lambda", () => "hi");
            app.MapHealthChecks("/health");
        },
        b => b.Services.AddHealthChecks());

    private static RecordedSpan One(IEnumerable<RecordedSpan> spans, string component) =>
        spans.Single(s => (string?)s.Attr("codeatlas.component") == component);

    [Fact]
    public async Task Endpoint_span_carries_routing_identity_and_handler_span_carries_executed_identity()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Post, "/orders", new OrderRequest(1));

        Assert.Equal(201, (int)response.StatusCode);
        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var server = spans.Single(s => s.Span.Kind == Span.Types.SpanKind.Server);
        var endpoint = One(spans, "endpoint");
        var handler = One(spans, "handler");
        Assert.Equal(server.SpanId, endpoint.ParentSpanId);
        Assert.Equal(endpoint.SpanId, handler.ParentSpanId);
        Assert.Equal("routing", endpoint.Attr("codeatlas.code.evidence"));
        Assert.Equal("executed", handler.Attr("codeatlas.code.evidence"));
        Assert.Equal("handler OrderEndpoints.Create", handler.Name);
        foreach (var span in new[] { endpoint, handler })
        {
            Assert.Equal((long)Create.MetadataToken, span.Attr("codeatlas.code.token"));
            Assert.Equal(Create.Module.ModuleVersionId.ToString(), span.Attr("codeatlas.code.mvid"));
            Assert.Equal(Create.Module.Assembly.Location, span.Attr("codeatlas.code.assembly_path"));
            Assert.Equal("CodeAtlas.AspNetCore.Tests.OrderEndpoints.Create", span.Attr("codeatlas.code.function"));
        }
    }

    [Fact]
    public async Task Validation_failure_has_an_endpoint_span_and_no_handler_span()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Post, "/orders", new OrderRequest(0));

        Assert.Equal(400, (int)response.StatusCode);
        var spans = await app.Sink.WaitForTraceAsync(traceId);
        Assert.Single(spans, s => (string?)s.Attr("codeatlas.component") == "endpoint");
        Assert.DoesNotContain(spans, s => (string?)s.Attr("codeatlas.component") == "handler");
    }

    [Fact]
    public async Task Handler_exceptions_are_recorded_on_handler_and_endpoint_spans()
    {
        await using var app = await StartAsync();

        var (_, traceId) = await app.SendAsync(HttpMethod.Post, "/orders", new OrderRequest(100));

        var spans = await app.Sink.WaitForTraceAsync(traceId);
        foreach (var component in new[] { "handler", "endpoint" })
        {
            var span = One(spans, component);
            Assert.True(span.IsError);
            Assert.Equal("Too many", span.Span.Events.Single(e => e.Name == "exception").Attributes.Single(a => a.Key == "exception.message").Value.StringValue);
        }
    }

    [Fact]
    public async Task Group_level_handler_span_also_covers_the_endpoints_own_filters()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Post, "/g/orders", new OrderRequest(0));

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Single(await app.Sink.WaitForTraceAsync(traceId), s => (string?)s.Attr("codeatlas.component") == "handler");
    }

    [Fact]
    public async Task Lambda_endpoint_identity_is_the_compiled_lambda_method()
    {
        await using var app = await StartAsync();
        var lambda = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/lambda").Metadata.GetMetadata<MethodInfo>()!;

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/lambda");

        var endpoint = One(await app.Sink.WaitForTraceAsync(traceId), "endpoint");
        Assert.Equal((long)lambda.MetadataToken, endpoint.Attr("codeatlas.code.token"));
        Assert.Contains("<", (string)endpoint.Attr("codeatlas.code.function")!);
    }

    [Fact]
    public async Task Endpoints_without_a_method_still_get_a_span()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Get, "/health");

        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        var endpoint = One(await app.Sink.WaitForTraceAsync(traceId), "endpoint");
        Assert.Null(endpoint.Attr("codeatlas.code.token"));
    }
}
```

- [ ] **Step 2: Write the failing controller tests**

`tests/CodeAtlas.AspNetCore.Tests/ControllerSpanTests.cs`:
```csharp
using System.Reflection;
using CodeAtlas.AspNetCore.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.AspNetCore.Tests;

[ApiController]
[Route("api/orders")]
public class TestOrdersController : ControllerBase
{
    [HttpGet("{id:int}")]
    public IActionResult Get(int id) => Ok(id);

    [HttpGet("by-ref/{reference}")]
    public IActionResult Get(string reference) => Ok(reference);

    [HttpPost("fail")]
    public IActionResult Fail() => throw new InvalidOperationException("action failed");
}

public class ControllerSpanTests
{
    private static Task<TestApp> StartAsync() => TestApp.StartAsync(app => app.MapControllers(), b => b.Services.AddControllers());

    private static MethodInfo Overload(Type parameter) => typeof(TestOrdersController).GetMethod("Get", [parameter])!;

    [Theory]
    [InlineData("/api/orders/5", typeof(int))]
    [InlineData("/api/orders/by-ref/abc", typeof(string))]
    public async Task Action_span_identifies_the_exact_overload(string url, Type parameter)
    {
        await using var app = await StartAsync();

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, url);

        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var endpoint = spans.Single(s => (string?)s.Attr("codeatlas.component") == "endpoint");
        var action = spans.Single(s => (string?)s.Attr("codeatlas.component") == "action");
        Assert.Equal(endpoint.SpanId, action.ParentSpanId);
        Assert.Equal("action TestOrdersController.Get", action.Name);
        Assert.Equal("executed", action.Attr("codeatlas.code.evidence"));
        Assert.Equal((long)Overload(parameter).MetadataToken, action.Attr("codeatlas.code.token"));
        Assert.Equal("routing", endpoint.Attr("codeatlas.code.evidence"));
        Assert.Equal((long)Overload(parameter).MetadataToken, endpoint.Attr("codeatlas.code.token"));
    }

    [Fact]
    public async Task Action_exceptions_mark_the_action_span()
    {
        await using var app = await StartAsync();

        var (_, traceId) = await app.SendAsync(HttpMethod.Post, "/api/orders/fail");

        var action = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => (string?)s.Attr("codeatlas.component") == "action");
        Assert.True(action.IsError);
        Assert.Equal("action failed", action.Span.Status.Message);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: build error `'RouteHandlerBuilder' does not contain a definition for 'WithCodeAtlasHandlerSpan'`.

- [ ] **Step 4: Implement identity, policy, filter and handler extension**

`src/CodeAtlas.AspNetCore/Spans/MethodIdentity.cs`:
```csharp
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CodeAtlas.AspNetCore.Spans;

internal enum Evidence { Executed, Routing }

/// <summary>MVID + metadata token identify the exact method (overloads, lambdas) for PDB lookup in the collector.</summary>
internal static class MethodIdentity
{
    public static void Tag(Activity? activity, MethodInfo? method, Evidence evidence)
    {
        if (activity is null || method?.DeclaringType is null) return;
        activity.SetTag("codeatlas.code.function", $"{method.DeclaringType.FullName}.{method.Name}");
        activity.SetTag("codeatlas.code.mvid", method.Module.ModuleVersionId.ToString());
        activity.SetTag("codeatlas.code.token", method.MetadataToken);
        activity.SetTag("codeatlas.code.assembly_path", method.Module.Assembly.Location);
        activity.SetTag("codeatlas.code.evidence", evidence == Evidence.Executed ? "executed" : "routing");
    }

    public static MethodInfo? HandlerOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.MethodInfo ?? endpoint.Metadata.GetMetadata<MethodInfo>();
}
```

`src/CodeAtlas.AspNetCore/Spans/EndpointSpanPolicy.cs`:
```csharp
using System.Runtime.CompilerServices;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace CodeAtlas.AspNetCore.Spans;

/// <summary>
/// Zero-change endpoint span for Minimal APIs and controllers: the matched endpoint is swapped for a copy (same pattern,
/// order and metadata) whose delegate is wrapped. The span covers binding, filters, handler and result writing; its
/// method identity is routing metadata, because a filter may stop the request before the handler runs.
/// </summary>
internal sealed class EndpointSpanPolicy(CodeAtlasRuntime runtime) : MatcherPolicy, IEndpointSelectorPolicy
{
    private readonly ConditionalWeakTable<RouteEndpoint, RouteEndpoint> _wrapped = new();

    public override int Order => int.MaxValue;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => true;

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        if (!runtime.WrapperSpansEnabled) return Task.CompletedTask;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i) || candidates[i].Endpoint is not RouteEndpoint { RequestDelegate: not null } endpoint) continue;
            candidates.ReplaceEndpoint(i, _wrapped.GetValue(endpoint, Wrap), candidates[i].Values);
        }
        return Task.CompletedTask;
    }

    private static RouteEndpoint Wrap(RouteEndpoint endpoint)
    {
        var inner = endpoint.RequestDelegate!;
        var handler = MethodIdentity.HandlerOf(endpoint);
        return new RouteEndpoint(async context =>
        {
            using var activity = CodeAtlasDiagnostics.Source.StartActivity($"endpoint {endpoint.DisplayName}");
            activity?.SetTag("codeatlas.component", "endpoint");
            MethodIdentity.Tag(activity, handler, Evidence.Routing);
            try
            {
                await inner(context);
            }
            catch (Exception e)
            {
                CodeAtlasDiagnostics.Fail(activity, e);
                throw;
            }
        }, endpoint.RoutePattern, endpoint.Order, endpoint.Metadata, endpoint.DisplayName);
    }
}
```

`src/CodeAtlas.AspNetCore/Spans/ActionSpanFilter.cs`:
```csharp
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CodeAtlas.AspNetCore.Spans;

/// <summary>Zero-change MVC action span. Registered with the highest order, so it is the innermost filter around the action method.</summary>
internal sealed class ActionSpanFilter(CodeAtlasRuntime runtime) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!runtime.WrapperSpansEnabled)
        {
            await next();
            return;
        }
        var method = (context.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo;
        using var activity = CodeAtlasDiagnostics.Source.StartActivity($"action {method?.DeclaringType?.Name}.{method?.Name}");
        activity?.SetTag("codeatlas.component", "action");
        MethodIdentity.Tag(activity, method, Evidence.Executed);
        var executed = await next();
        if (executed.Exception is { } exception && !executed.ExceptionHandled) CodeAtlasDiagnostics.Fail(activity, exception);
    }
}
```

`src/CodeAtlas.AspNetCore/HandlerSpanExtensions.cs`:
```csharp
using CodeAtlas.AspNetCore.Spans;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.AspNetCore;

public static class HandlerSpanExtensions
{
    /// <summary>
    /// Opt-in Minimal API handler span. Placed after an endpoint's own filters (e.g. after <c>WithValidation</c>) it covers
    /// the handler only; on a group it is the outermost filter and also covers the endpoints' filters.
    /// Wrap the call in <c>#if CODEATLAS</c>.
    /// </summary>
    public static TBuilder WithCodeAtlasHandlerSpan<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((factory, next) =>
        {
            var method = factory.MethodInfo;
            return async context =>
            {
                if (context.HttpContext.RequestServices.GetService<CodeAtlasRuntime>() is not { WrapperSpansEnabled: true })
                    return await next(context);
                using var activity = CodeAtlasDiagnostics.Source.StartActivity($"handler {method.DeclaringType?.Name}.{method.Name}");
                activity?.SetTag("codeatlas.component", "handler");
                MethodIdentity.Tag(activity, method, Evidence.Executed);
                try
                {
                    return await next(context);
                }
                catch (Exception e)
                {
                    CodeAtlasDiagnostics.Fail(activity, e);
                    throw;
                }
            };
        });
}
```

Modify `src/CodeAtlas.AspNetCore/CodeAtlasExtensions.cs` — add the usings and two registrations before `return builder;`:
```csharp
using CodeAtlas.AspNetCore.Spans;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
```
```csharp
        services.AddSingleton<MatcherPolicy, EndpointSpanPolicy>();
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<ActionSpanFilter>(int.MaxValue));
```

- [ ] **Step 5: Run all tests**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests`
Expected: all pass (`failed: 0`), including the coexistence gate (the policy and filter must stay inactive when the app has tracing).

- [ ] **Step 6: Commit**

```bash
git add src/CodeAtlas.AspNetCore tests/CodeAtlas.AspNetCore.Tests
git commit -m "feat(aspnetcore): endpoint, action and handler spans with method identity"
```

---

### Task 6: Database, HttpClient and failure isolation

**Files:**
- Create: `tests/CodeAtlas.AspNetCore.Tests/Support/PostgresFixture.cs`, `tests/CodeAtlas.AspNetCore.Tests/Support/StubServer.cs`
- Test: `tests/CodeAtlas.AspNetCore.Tests/DatabaseSpanTests.cs`, `tests/CodeAtlas.AspNetCore.Tests/HttpClientSpanTests.cs`, `tests/CodeAtlas.AspNetCore.Tests/FailureIsolationTests.cs`
- Modify (only if a test shows it is needed): `src/CodeAtlas.AspNetCore/Export/SpanSerializer.cs`

**Interfaces:**
- Consumes: everything above. No new production types are expected: Npgsql and `System.Net.Http` sources are already in `CodeAtlasTracing.Sources`, and privacy is in `SpanSerializer`. These tests prove it against the real libraries.

- [ ] **Step 1: Write the fixtures**

`tests/CodeAtlas.AspNetCore.Tests/Support/PostgresFixture.cs`:
```csharp
using Npgsql;
using Testcontainers.PostgreSql;

namespace CodeAtlas.AspNetCore.Tests.Support;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand("CREATE TABLE products (sku text PRIMARY KEY, name text NOT NULL); INSERT INTO products VALUES ('SECRET-SKU-123', 'Apple')", connection);
        await create.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
```

`tests/CodeAtlas.AspNetCore.Tests/Support/StubServer.cs`:
```csharp
using System.Collections.Concurrent;
using System.Net;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>Downstream HTTP API on HttpListener: answers 200 and remembers the traceparent it received.</summary>
internal sealed class StubServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public StubServer()
    {
        Port = FakeCollector.FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                TraceParents.Enqueue(context.Request.Headers["traceparent"] ?? "");
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        });
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public ConcurrentQueue<string> TraceParents { get; } = new();

    public void Dispose() => _listener.Close();
}
```

- [ ] **Step 2: Write the database tests**

`tests/CodeAtlas.AspNetCore.Tests/DatabaseSpanTests.cs`:
```csharp
using System.Text;
using CodeAtlas.AspNetCore.Tests.Support;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CodeAtlas.AspNetCore.Tests;

public class DatabaseSpanTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private Task<TestApp> StartAsync(bool keepDb = false) => TestApp.StartAsync(
        app => app.MapGet("/products/{sku}", async (string sku, NpgsqlDataSource data) =>
        {
            await using var command = data.CreateCommand("SELECT name FROM products WHERE sku = $1");
            command.Parameters.AddWithValue(sku);
            return Results.Ok(await command.ExecuteScalarAsync());
        }).WithCodeAtlasHandlerSpan(),
        b => b.Services.AddSingleton(NpgsqlDataSource.Create(db.ConnectionString)),
        o => o.KeepDbStatements = keepDb);

    [Fact]
    public async Task Database_span_is_a_summary_under_the_handler_with_no_sql_parameters_or_credentials()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Get, "/products/SECRET-SKU-123");

        Assert.Equal("\"Apple\"", await response.Content.ReadAsStringAsync());
        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var handler = spans.Single(s => (string?)s.Attr("codeatlas.component") == "handler");
        var query = spans.Single(s => s.Scope == "Npgsql" && s.Name == "SELECT products");
        Assert.Equal(handler.SpanId, query.ParentSpanId);
        Assert.Equal("SELECT products", query.Attr("db.query.summary"));
        Assert.Null(query.Attr("db.query.text"));
        Assert.Null(query.Attr("db.npgsql.data_source"));

        var wire = string.Join("\n", app.Sink.Requests.Select(r => Encoding.UTF8.GetString(r.ToByteArray())));
        Assert.DoesNotContain("SECRET-SKU-123", wire);
        Assert.DoesNotContain("WHERE sku", wire);
        Assert.DoesNotContain("Password", wire);
    }

    [Fact]
    public async Task Sql_text_can_be_kept_but_parameter_values_still_never_leave()
    {
        await using var app = await StartAsync(keepDb: true);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/products/SECRET-SKU-123");

        var query = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Scope == "Npgsql" && s.Name == "SELECT products");
        Assert.Equal("SELECT name FROM products WHERE sku = $1", query.Attr("db.query.text"));
        Assert.DoesNotContain("SECRET-SKU-123", string.Join("\n", app.Sink.Requests.Select(r => Encoding.UTF8.GetString(r.ToByteArray()))));
    }
}
```

- [ ] **Step 3: Write the HttpClient tests**

`tests/CodeAtlas.AspNetCore.Tests/HttpClientSpanTests.cs`:
```csharp
using System.Text;
using CodeAtlas.AspNetCore.Tests.Support;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests;

public class HttpClientSpanTests
{
    [Fact]
    public async Task Outgoing_call_is_a_child_of_the_handler_without_query_string_and_propagates_the_trace()
    {
        using var stub = new StubServer();
        await using var app = await TestApp.StartAsync(
            a => a.MapGet("/call", async (IHttpClientFactory factory) =>
            {
                using var response = await factory.CreateClient().GetAsync($"{stub.Url}/data?token=SECRET-TOKEN");
                return Results.Ok((int)response.StatusCode);
            }).WithCodeAtlasHandlerSpan(),
            b => b.Services.AddHttpClient());

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/call");

        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var handler = spans.Single(s => (string?)s.Attr("codeatlas.component") == "handler");
        var call = spans.Single(s => s.Scope == "System.Net.Http");
        Assert.Equal(Span.Types.SpanKind.Client, call.Span.Kind);
        Assert.Equal(handler.SpanId, call.ParentSpanId);
        Assert.Equal($"{stub.Url}/data", call.Attr("url.full"));
        Assert.Equal(200L, call.Attr("http.response.status_code"));
        Assert.Contains(traceId, stub.TraceParents.Single());
        Assert.DoesNotContain("SECRET-TOKEN", string.Join("\n", app.Sink.Requests.Select(r => Encoding.UTF8.GetString(r.ToByteArray()))));
    }
}
```

- [ ] **Step 4: Write the failure isolation tests**

`tests/CodeAtlas.AspNetCore.Tests/FailureIsolationTests.cs`:
```csharp
using System.Diagnostics;
using CodeAtlas.AspNetCore.Tests.Support;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeAtlas.AspNetCore.Tests;

public class FailureIsolationTests
{
    private static void Map(WebApplication app) => app.MapGet("/ping", () => "pong");

    private static CodeAtlasTracing Tracing(TestApp app) =>
        app.App.Services.GetServices<IHostedService>().OfType<CodeAtlasTracing>().Single();

    [Fact]
    public async Task A_stuck_collector_never_slows_or_fails_requests()
    {
        var sink = new BlockingSink();
        await using var app = await TestApp.StartAsync(Map, codeAtlas: o => o.QueueCapacity = 16, sink: sink);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            Assert.Equal("pong", await app.Client.GetStringAsync("/ping"));
        clock.Stop();
        sink.Release();

        Assert.True(clock.ElapsedMilliseconds < 5000, $"100 requests took {clock.ElapsedMilliseconds} ms");
        Assert.True(Tracing(app).Processor!.Dropped > 0);
    }

    [Fact]
    public async Task A_failing_collector_never_fails_requests()
    {
        var sink = new ThrowingSink();
        await using var app = await TestApp.StartAsync(Map, sink: sink);

        for (var i = 0; i < 20; i++)
            Assert.Equal("pong", await app.Client.GetStringAsync("/ping"));

        var clock = Stopwatch.StartNew();
        while (Tracing(app).Processor!.SendFailures == 0 && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.True(Tracing(app).Processor!.SendFailures > 0);
    }

    [Fact]
    public async Task An_unreachable_collector_with_the_real_sink_never_fails_requests()
    {
        var closedPort = FakeCollector.FreePort();
        await using var app = await TestApp.StartAsync(Map, codeAtlas: o => o.Endpoint = $"http://127.0.0.1:{closedPort}/v1/traces", realHttpSink: true);

        for (var i = 0; i < 20; i++)
            Assert.Equal("pong", await app.Client.GetStringAsync("/ping"));
    }

    [Fact]
    public async Task Exports_to_the_collector_are_not_traced_themselves()
    {
        using var collector = new FakeCollector();
        await using var app = await TestApp.StartAsync(Map, codeAtlas: o => o.Endpoint = collector.TracesUrl, realHttpSink: true);

        for (var i = 0; i < 5; i++) await app.Client.GetStringAsync("/ping");
        var clock = Stopwatch.StartNew();
        while (collector.Received.Count < 2 && clock.ElapsedMilliseconds < 5000) await Task.Delay(50);
        await Task.Delay(500);

        var urls = collector.Received
            .SelectMany(r => r.Request.ResourceSpans).SelectMany(r => r.ScopeSpans).SelectMany(s => s.Spans)
            .SelectMany(s => s.Attributes).Where(a => a.Key is "url.full" or "server.port").Select(a => a.Value.ToString());
        Assert.DoesNotContain(urls, u => u.Contains(collector.Port.ToString()));
    }
}
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --project tests/CodeAtlas.AspNetCore.Tests` (Docker must be running for Testcontainers)
Expected: all pass.

If `Outgoing_call_...` finds no span with scope `System.Net.Http`, print the scopes of the trace: if .NET 10 emits the client activity under another source name, add that name to `CodeAtlasTracing.Sources` and to this test. If `url.full` or `http.response.status_code` is missing, adjust only those assertion keys to the names .NET 10 actually emits. Never weaken the privacy assertions.

- [ ] **Step 6: Commit**

```bash
git add tests/CodeAtlas.AspNetCore.Tests src/CodeAtlas.AspNetCore
git commit -m "test(aspnetcore): Npgsql and HttpClient privacy, failure isolation, no self-tracing"
```

---

### Task 7: Local packaging, backend release verification, integration guide

**Files:**
- Create: `scripts/pack-local.sh`, `scripts/verify-release.sh`, `tests/release-fixture/ReleaseFixtureApp.csproj`, `tests/release-fixture/Program.cs`, `tests/release-fixture/nuget.config`, `docs/integration.md`

**Interfaces:**
- Consumes: `CodeAtlas.Otlp` and `CodeAtlas.AspNetCore` packages (version `0.1.0`).
- Produces: `./artifacts/nuget/CodeAtlas.Otlp.0.1.0.nupkg`, `./artifacts/nuget/CodeAtlas.AspNetCore.0.1.0.nupkg`; `scripts/verify-release.sh` exit code 0 = Release builds are clean. Plan 5 extends this script with frontend checks.

- [ ] **Step 1: Write the pack script**

`scripts/pack-local.sh`:
```bash
#!/usr/bin/env bash
# Packs the integration packages into ./artifacts/nuget, a local folder feed. Nothing is published.
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf artifacts/nuget
dotnet pack src/CodeAtlas.Otlp -c Release -o artifacts/nuget --nologo -v q
dotnet pack src/CodeAtlas.AspNetCore -c Release -o artifacts/nuget --nologo -v q
ls artifacts/nuget
```

```bash
chmod +x scripts/pack-local.sh && scripts/pack-local.sh
```
Expected output lists `CodeAtlas.AspNetCore.0.1.0.nupkg` and `CodeAtlas.Otlp.0.1.0.nupkg`.

- [ ] **Step 2: Write the release fixture (a target app integrated exactly as the guide says)**

`tests/release-fixture/nuget.config`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="codeatlas-local" value="../../artifacts/nuget" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

`tests/release-fixture/ReleaseFixtureApp.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <!-- The app's own OpenTelemetry. It must survive the Release build untouched. -->
  <ItemGroup>
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.19.1" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.19.1" />
  </ItemGroup>

  <!-- CodeAtlas: Debug builds only. -->
  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
    <PackageReference Include="CodeAtlas.AspNetCore" Version="0.1.0" />
  </ItemGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <DefineConstants>$(DefineConstants);CODEATLAS</DefineConstants>
  </PropertyGroup>
</Project>
```

`tests/release-fixture/Program.cs`:
```csharp
#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Fixture").AddOtlpExporter());
#if CODEATLAS
builder.AddCodeAtlas();
#endif

var app = builder.Build();
app.MapGet("/ping", () => "pong");
app.Run();
```

- [ ] **Step 3: Write the verification script**

`scripts/verify-release.sh`:
```bash
#!/usr/bin/env bash
# Proves a target app's Release build contains no CodeAtlas code or CodeAtlas-only packages,
# and keeps the app's own OpenTelemetry. Debug is checked too, so a broken fixture cannot pass silently.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

"$root/scripts/pack-local.sh" > /dev/null
export NUGET_PACKAGES="$work/packages"   # never reuse a stale cached 0.1.0
fixture="$root/tests/release-fixture/ReleaseFixtureApp.csproj"
for configuration in Debug Release; do
  dotnet publish "$fixture" -c "$configuration" -o "$work/$configuration" --nologo -v q > /dev/null
done

failed=0
check() {
  if eval "$2"; then echo "PASS  $1"; else echo "FAIL  $1"; failed=1; fi
}
check "Debug contains CodeAtlas.AspNetCore.dll"              '[ -f "$work/Debug/CodeAtlas.AspNetCore.dll" ]'
check "Release has no CodeAtlas assemblies"                   '! ls "$work/Release" | grep -qi codeatlas'
# Exact names, not "codeatlas": the build path itself (…/codeatlas-live/…) is embedded in every assembly's PDB entry.
check "Release deps.json never mentions CodeAtlas packages"   '! grep -q "CodeAtlas\." "$work/Release/ReleaseFixtureApp.deps.json"'
check "Release app assembly has no CodeAtlas reference"       '! grep -aq "CodeAtlas.AspNetCore" "$work/Release/ReleaseFixtureApp.dll"'
check "Release has no Google.Protobuf (CodeAtlas-only here)"  '[ ! -f "$work/Release/Google.Protobuf.dll" ]'
check "Release keeps the app's OpenTelemetry.dll"             '[ -f "$work/Release/OpenTelemetry.dll" ]'
check "Release keeps the app's OTLP exporter"                 '[ -f "$work/Release/OpenTelemetry.Exporter.OpenTelemetryProtocol.dll" ]'
exit $failed
```

- [ ] **Step 4: Run it**

```bash
chmod +x scripts/verify-release.sh && scripts/verify-release.sh
```
Expected: seven `PASS` lines, exit code 0.

- [ ] **Step 5: Write the backend part of the integration guide**

`docs/integration.md`:
````markdown
# Integrating CodeAtlas Live into an ASP.NET Core app

CodeAtlas runs only in Debug builds and only in Development. Release builds contain none of it
(`scripts/verify-release.sh` proves this on every change).

## 1. Make the packages available

```bash
# in the codeatlas-live repo
scripts/pack-local.sh
```

Add a `nuget.config` next to your solution (or extend yours):

```xml
<configuration>
  <packageSources>
    <add key="codeatlas-local" value="/path/to/codeatlas-live/artifacts/nuget" />
  </packageSources>
</configuration>
```

## 2. Reference it in Debug only

```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="CodeAtlas.AspNetCore" Version="0.1.0" />
</ItemGroup>
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <DefineConstants>$(DefineConstants);CODEATLAS</DefineConstants>
</PropertyGroup>
```

## 3. Turn it on

```csharp
#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif

// after your own service registrations
#if CODEATLAS
builder.AddCodeAtlas();
#endif
```

## 4. Optional: handler spans for Minimal APIs

Controllers get an action span automatically. Minimal APIs get an endpoint span automatically; for a span around the
handler itself add one line after the endpoint's own filters:

```csharp
group.MapPost("/", Create).WithValidation<ProductRequest>()
#if CODEATLAS
    .WithCodeAtlasHandlerSpan()
#endif
    .RequirePermission(ProductPermissions.Create);
```

## What you get

| Span | Code change | Evidence |
|---|---|---|
| `POST /api/products/` (server) | none | executed |
| `endpoint …` (binding + filters + handler + result) | none | executed; its method is *routing metadata* |
| `action Controller.Method` | none | executed |
| `handler Type.Method` | the optional line above | executed |
| `SELECT products` (Npgsql) | none | executed |
| `GET https://…` (HttpClient) | none | executed |

## Options

| Option | Default | Config key |
|---|---|---|
| `Endpoint` | `http://127.0.0.1:4318/v1/traces` | `CodeAtlas:Endpoint` |
| `EnableOutsideDevelopment` | `false` | `CodeAtlas:Enabled` |
| `ServiceName` | application name | — |
| `KeepDbStatements` | `false` (SQL replaced by "operation + tables") | — |
| `WrapperSpansBesideAppTracing` | `false` | — |
| `QueueCapacity` / `ExportTimeout` | 2048 / 2 s | — |

## If your app already uses OpenTelemetry tracing

CodeAtlas runs its own private tracer provider and never changes your sampler, exporters, instrumentation or the data
they send. Because its endpoint/action/handler spans would become parents your own tool never receives, they are
**off** in that case (a warning is logged once). You still get server, database and HttpClient spans.

## Privacy

Never sent: SQL text and parameters, connection strings, URL query strings, request/response bodies, headers, method
arguments. Sent: exception messages and stack traces (Development only).

## If the collector is not running

Nothing happens to your app: spans go to a bounded background queue and are dropped when it is full.
````

- [ ] **Step 6: Run the whole suite once more**

Run: `dotnet test` (repo root)
Expected: every test project passes, `failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add scripts tests/release-fixture docs/integration.md
git commit -m "build: local packaging, backend release verification and integration guide"
```
