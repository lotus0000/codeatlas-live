using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Export;

internal interface ISpanSink
{
    Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken);
}
