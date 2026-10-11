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
        TestOtlp.Request("api", "Microsoft.AspNetCore", [TestOtlp.Span(TestOtlp.TraceId(trace), TestOtlp.SpanId(1), null, "GET /ping", SpanKind.Server, 0, 5, ("http.route", "/ping"))]);

    [Fact]
    public async Task Protobuf_is_stored_and_answered_in_protobuf()
    {
        await using var host = await CollectorHost.StartAsync();

        using var response = await TestOtlp.PostAsync(host.Client, OneSpan());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        ExportTraceServiceResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        var spans = await host.Client.GetFromJsonAsync<List<SpanRecord>>($"api/traces/{TestOtlp.TraceId(1)}/spans");
        var span = Assert.Single(spans!);
        Assert.Equal("server", span.Kind);
        Assert.Equal("api", span.Service);
        Assert.Equal("/ping", span.Attributes["http.route"]?.ToString());
        Assert.Equal(TestOtlp.Epoch + 5_000_000, span.EndUnixNano);
    }

    [Fact]
    public async Task Timestamps_are_strings_in_json()
    {
        await using var host = await CollectorHost.StartAsync();
        (await TestOtlp.PostAsync(host.Client, OneSpan())).Dispose();

        var json = await host.Client.GetStringAsync($"api/traces/{TestOtlp.TraceId(1)}/spans");

        Assert.Contains($"\"startUnixNano\":\"{TestOtlp.Epoch}\"", json);
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
        Assert.NotNull(await host.Client.GetFromJsonAsync<List<SpanRecord>>($"api/traces/{TestOtlp.TraceId(1)}/spans"));
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
