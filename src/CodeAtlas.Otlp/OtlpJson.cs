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
