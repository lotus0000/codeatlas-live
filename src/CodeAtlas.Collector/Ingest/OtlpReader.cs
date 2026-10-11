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
