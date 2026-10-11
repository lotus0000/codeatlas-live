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
