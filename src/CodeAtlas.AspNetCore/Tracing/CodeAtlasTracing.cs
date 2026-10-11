using CodeAtlas.AspNetCore.Export;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>Owns CodeAtlas's private TracerProvider. Nothing of it is registered in, or shared with, the app's DI.</summary>
internal sealed class CodeAtlasTracing(
    CodeAtlasRuntime runtime, SpanSerializer serializer, ISpanSink sink, IServiceProvider services,
    IHostEnvironment environment, ILogger<CodeAtlasTracing> logger) : IHostedService, IDisposable
{
    public static readonly string[] Sources = [CodeAtlasDiagnostics.SourceName, SpanSerializer.AspNetCoreSource, "Npgsql", "System.Net.Http"];

    private TracerProvider? _provider;

    public CodeAtlasExportProcessor? Processor { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        runtime.Initialize(services, logger);
        var resource = new Resource
        {
            Attributes =
            {
                SpanSerializer.Kv("service.name", runtime.Options.ServiceName ?? environment.ApplicationName),
                SpanSerializer.Kv("codeatlas.app_has_tracing", runtime.AppHasTracing),
            },
        };
        Processor = new CodeAtlasExportProcessor(serializer, sink, resource, runtime.Options.QueueCapacity, runtime.Options.BatchDelay, logger);
        _provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(Sources)
            .SetSampler(new RecordOnlySampler())
            .AddProcessor(Processor)
            .Build();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _provider?.Shutdown(5000);
        return Task.CompletedTask;
    }

    public void Dispose() => _provider?.Dispose();
}
