using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAtlas.Collector.Tests.Support;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace CodeAtlas.Collector.Tests;

/// <summary>
/// The viewer's test fixtures are real responses of a running collector. This test fails when the collector's JSON
/// changes and the fixtures were not regenerated. Regenerate with
/// <c>CODEATLAS_UPDATE_VIEWER_FIXTURES=1 dotnet test --project tests/CodeAtlas.Collector.Tests</c> and review the diff.
/// </summary>
public sealed class ViewerContractTests : IDisposable
{
    public const string Mvid = "0f0f0f0f-0000-4000-8000-000000000001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-contract-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CodeAtlas.Live.slnx"))) return dir.FullName;
        throw new InvalidOperationException("CodeAtlas.Live.slnx was not found above the test output folder.");
    }

    [Fact]
    public async Task Viewer_fixtures_are_what_the_collector_serves()
    {
        var fixture = FixtureAssembly.Build(Path.Combine(_root, "app"), FixtureAssembly.FixtureText);
        var mvid = fixture.Mvid.ToString();
        var token = fixture.Token("Fixture.Orders", "LoadAsync");
        // A command that exists everywhere, so "editor.available" is the same on every machine.
        await using var host = await CollectorHost.StartAsync($"--source-root={_root}", $"--editor-command={Environment.ProcessPath}");
        foreach (var request in Scenarios.All(fixture.DllPath, mvid, token))
            Assert.Equal(HttpStatusCode.OK, (await TestOtlp.PostAsync(host.Client, request)).StatusCode);

        var source = $"api/source?assembly={Uri.EscapeDataString(fixture.DllPath)}&token={token}&mvid=";
        var outputs = new Dictionary<string, string>
        {
            ["summaries.json"] = await host.Client.GetStringAsync("api/traces"),
            ["success.json"] = await host.Client.GetStringAsync($"api/traces/{Scenarios.Success}"),
            ["failure.json"] = await host.Client.GetStringAsync($"api/traces/{Scenarios.Failure}"),
            ["heuristic.json"] = await host.Client.GetStringAsync($"api/traces/{Scenarios.Heuristic}"),
            ["info.json"] = await host.Client.GetStringAsync("api/info"),
            ["source-found.json"] = await host.Client.GetStringAsync(source + mvid),
            ["source-refused.json"] = await host.Client.GetStringAsync(source + Guid.Empty),
        };

        var directory = Path.Combine(RepoRoot(), "src", "viewer", "test", "fixtures");
        var update = Environment.GetEnvironmentVariable("CODEATLAS_UPDATE_VIEWER_FIXTURES") == "1";
        var stale = new List<string>();
        foreach (var (name, raw) in outputs)
        {
            var text = Normalize(raw, mvid);
            var path = Path.Combine(directory, name);
            if (update)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(path, text);
            }
            else if (!File.Exists(path) || await File.ReadAllTextAsync(path) != text) stale.Add(name);
        }
        Assert.True(stale.Count == 0,
            $"Viewer fixtures differ from the collector's output: {string.Join(", ", stale)}. " +
            "Regenerate with CODEATLAS_UPDATE_VIEWER_FIXTURES=1 and review the diff.");
    }

    // Per-run values (temp folder, freshly compiled MVID, test-host path) become fixed placeholders; output is indented.
    private string Normalize(string json, string mvid)
    {
        var fixedText = json.Replace(_root, "/repo").Replace(mvid, Mvid)
            .Replace(JsonEncodedText.Encode(Environment.ProcessPath!).ToString(), "/usr/bin/editor");
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return JsonNode.Parse(fixedText)!.ToJsonString(options) + "\n";
    }

    /// <summary>Three traces a minute apart: success (oldest), failure, heuristic (newest).</summary>
    private static class Scenarios
    {
        public static readonly string Success = TestOtlp.TraceId(0xa1);
        public static readonly string Failure = TestOtlp.TraceId(0xa2);
        public static readonly string Heuristic = TestOtlp.TraceId(0xa3);
        private const string Web = "sample-web";
        private const string Api = "MinimalApiSample";
        private static readonly (string Key, object Value) Sampling = ("codeatlas.sampling", "codeatlas");

        private static string S(int trace, int n) => TestOtlp.SpanId(trace * 0x100 + n);

        private static Span At(double baseMs, string traceId, string spanId, string? parent, string name, SpanKind kind,
            double startMs, double endMs, params (string Key, object Value)[] attributes) =>
            TestOtlp.Span(traceId, spanId, parent, name, kind, baseMs + startMs, baseMs + endMs, attributes);

        private static (string Key, object Value)[] Code(string component, string evidence, string dll, string mvid, int token) =>
        [
            ("codeatlas.component", component), ("codeatlas.code.function", "Fixture.Orders.LoadAsync"),
            ("codeatlas.code.mvid", mvid), ("codeatlas.code.token", token),
            ("codeatlas.code.assembly_path", dll), ("codeatlas.code.evidence", evidence),
        ];

        public static IEnumerable<ExportTraceServiceRequest> All(string dll, string mvid, int token)
        {
            // 1. Success: explicit interaction → fetch → server → endpoint (routing) → handler (executed) → 2 × database + HTTP call.
            var t = Success;
            const double a = 0;
            yield return TestOtlp.Request(Web, "codeatlas-web",
            [
                At(a, t, S(1, 1), null, "interaction: Create order", SpanKind.Internal, 0, 62,
                    ("codeatlas.interaction.name", "Create order"), ("codeatlas.correlation", "explicit")),
                At(a, t, S(1, 2), S(1, 1), "POST /api/orders", SpanKind.Client, 1, 60,
                    ("http.request.method", "POST"), ("url.path", "/api/orders"), ("http.response.status_code", 201), ("codeatlas.correlation", "explicit")),
            ]);
            yield return TestOtlp.Request(Api, "Microsoft.AspNetCore",
            [
                At(a, t, S(1, 3), S(1, 2), "POST /api/orders", SpanKind.Server, 2, 58,
                    ("http.request.method", "POST"), ("http.route", "/api/orders"), ("url.path", "/api/orders"), ("http.response.status_code", 201)),
            ], Sampling);
            yield return TestOtlp.Request(Api, "CodeAtlas.Components",
            [
                At(a, t, S(1, 4), S(1, 3), "endpoint POST /api/orders", SpanKind.Internal, 3, 57, Code("endpoint", "routing", dll, mvid, token)),
                At(a, t, S(1, 5), S(1, 4), "handler Orders.LoadAsync", SpanKind.Internal, 4, 56, Code("handler", "executed", dll, mvid, token)),
            ], Sampling);
            yield return TestOtlp.Request(Api, "Npgsql",
            [
                At(a, t, S(1, 6), S(1, 5), "SELECT products", SpanKind.Client, 5, 15,
                    ("db.system.name", "postgresql"), ("db.namespace", "shop"), ("db.query.summary", "SELECT products")),
                At(a, t, S(1, 7), S(1, 5), "INSERT orders", SpanKind.Client, 16, 40,
                    ("db.system.name", "postgresql"), ("db.namespace", "shop"), ("db.query.summary", "INSERT orders")),
            ], Sampling);
            yield return TestOtlp.Request(Api, "System.Net.Http",
            [
                At(a, t, S(1, 8), S(1, 5), "GET", SpanKind.Client, 41, 55,
                    ("http.request.method", "GET"), ("url.full", "https://stock.example/api/check"), ("http.response.status_code", 200)),
            ], Sampling);

            // 2. Failure: the handler throws, the server returns 500.
            t = Failure;
            const double b = 60_000;
            yield return TestOtlp.Request(Web, "codeatlas-web",
            [
                At(b, t, S(2, 1), null, "interaction: Create order", SpanKind.Internal, 0, 26,
                    ("codeatlas.interaction.name", "Create order"), ("codeatlas.correlation", "explicit")),
                At(b, t, S(2, 2), S(2, 1), "POST /api/orders", SpanKind.Client, 1, 25,
                    ("http.request.method", "POST"), ("url.path", "/api/orders"), ("http.response.status_code", 500), ("codeatlas.correlation", "explicit")),
            ]);
            var server = At(b, t, S(2, 3), S(2, 2), "POST /api/orders", SpanKind.Server, 2, 24,
                ("http.request.method", "POST"), ("http.route", "/api/orders"), ("url.path", "/api/orders"), ("http.response.status_code", 500));
            server.Status = new Status { Code = Status.Types.StatusCode.Error };
            yield return TestOtlp.Request(Api, "Microsoft.AspNetCore", [server], Sampling);
            var handler = At(b, t, S(2, 5), S(2, 4), "handler Orders.LoadAsync", SpanKind.Internal, 4, 21, Code("handler", "executed", dll, mvid, token));
            handler.Status = new Status { Code = Status.Types.StatusCode.Error, Message = "Out of stock: SKU-42" };
            handler.Events.Add(new Span.Types.Event
            {
                Name = "exception",
                TimeUnixNano = TestOtlp.Epoch + (ulong)((b + 20) * 1_000_000),
                Attributes =
                {
                    TestOtlp.Kv("exception.type", "System.InvalidOperationException"),
                    TestOtlp.Kv("exception.message", "Out of stock: SKU-42"),
                    TestOtlp.Kv("exception.stacktrace",
                        "System.InvalidOperationException: Out of stock: SKU-42\n   at Fixture.Stock.Reserve(String sku) in /repo/app/Stock.cs:line 9\n   at Fixture.Orders.LoadAsync(Int32 id) in /repo/app/Fixture.cs:line 15"),
                },
            });
            yield return TestOtlp.Request(Api, "CodeAtlas.Components",
            [
                At(b, t, S(2, 4), S(2, 3), "endpoint POST /api/orders", SpanKind.Internal, 3, 23, Code("endpoint", "routing", dll, mvid, token)),
                handler,
            ], Sampling);
            yield return TestOtlp.Request(Api, "Npgsql",
            [
                At(b, t, S(2, 6), S(2, 5), "SELECT products", SpanKind.Client, 5, 18,
                    ("db.system.name", "postgresql"), ("db.namespace", "shop"), ("db.query.summary", "SELECT products")),
            ], Sampling);

            // 3. Heuristic click: one request reaches an endpoint without a handler span, one never reaches the backend,
            //    and one database span's parent never arrives.
            t = Heuristic;
            const double c = 120_000;
            yield return TestOtlp.Request(Web, "codeatlas-web",
            [
                At(c, t, S(3, 1), null, "interaction: Refresh", SpanKind.Internal, 0, 0,
                    ("codeatlas.interaction.name", "Refresh"), ("codeatlas.interaction.label_source", "text"),
                    ("codeatlas.interaction.event", "click"), ("codeatlas.correlation", "heuristic")),
                At(c, t, S(3, 2), S(3, 1), "GET /api/products", SpanKind.Client, 3, 30,
                    ("http.request.method", "GET"), ("url.path", "/api/products"), ("http.response.status_code", 200), ("codeatlas.correlation", "heuristic")),
                At(c, t, S(3, 9), S(3, 1), "GET /api/stats", SpanKind.Client, 4, 12,
                    ("http.request.method", "GET"), ("url.path", "/api/stats"), ("http.response.status_code", 200), ("codeatlas.correlation", "heuristic")),
            ]);
            yield return TestOtlp.Request(Api, "Microsoft.AspNetCore",
            [
                At(c, t, S(3, 3), S(3, 2), "GET /api/products", SpanKind.Server, 4, 28,
                    ("http.request.method", "GET"), ("http.route", "/api/products"), ("url.path", "/api/products"), ("http.response.status_code", 200)),
            ], Sampling);
            yield return TestOtlp.Request(Api, "CodeAtlas.Components",
            [
                At(c, t, S(3, 4), S(3, 3), "endpoint GET /api/products", SpanKind.Internal, 5, 27, Code("endpoint", "routing", dll, mvid, token)),
            ], Sampling);
            yield return TestOtlp.Request(Api, "Npgsql",
            [
                At(c, t, S(3, 5), S(3, 4), "SELECT products", SpanKind.Client, 6, 20,
                    ("db.system.name", "postgresql"), ("db.namespace", "shop"), ("db.query.summary", "SELECT products")),
                At(c, t, S(3, 6), S(3, 0x99), "SELECT categories", SpanKind.Client, 21, 24,
                    ("db.system.name", "postgresql"), ("db.namespace", "shop"), ("db.query.summary", "SELECT categories")),
            ], Sampling);
        }
    }
}
