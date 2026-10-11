using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>OTLP/HTTP protobuf to the CodeAtlas collector (always loopback, so no proxy).</summary>
internal sealed class HttpSpanSink : ISpanSink, IDisposable
{
    private static readonly MediaTypeHeaderValue Protobuf = new("application/x-protobuf");
    private readonly Uri _endpoint;
    private readonly PrivateMeterFactory _meters = new();
    private readonly HttpClient _http;

    public HttpSpanSink(Uri endpoint, TimeSpan timeout)
    {
        _endpoint = endpoint;
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = timeout, UseProxy = false, MeterFactory = _meters }) { Timeout = timeout };
    }

    public async Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = Protobuf;
        using var response = await _http.PostAsync(_endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _http.Dispose();
        _meters.Dispose();
    }

    /// <summary>
    /// The handler's HTTP metrics go to a meter nobody listens to by name, so CodeAtlas's own exports never appear in
    /// the app's <c>System.Net.Http</c> metrics.
    /// </summary>
    private sealed class PrivateMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _created = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter("CodeAtlas.Internal.Export", null, null, this);
            lock (_created) _created.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            lock (_created) foreach (var meter in _created) meter.Dispose();
        }
    }
}
