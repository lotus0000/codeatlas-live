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
