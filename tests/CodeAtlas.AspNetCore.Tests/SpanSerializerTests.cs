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

        Assert.Equal("https://api.example.com/data", Attr(span, "url.full"));
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
