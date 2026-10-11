using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>Used when CodeAtlas is disabled by invalid configuration; never receives anything.</summary>
internal sealed class NoopSpanSink : ISpanSink
{
    public Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
}
