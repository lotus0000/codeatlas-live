using System.Diagnostics;
using CodeAtlas.AspNetCore.Export;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Proto.Resource.V1;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>
/// Observes activities with a plain <see cref="ActivityListener"/>, not an OpenTelemetry provider, because only a plain
/// listener can decline to take part in sampling (<see cref="ActivitySamplingResult.None"/>):
/// - the app has its own tracing: CodeAtlas never influences whether or how an activity is created; it exports what the
///   app's sampler records, plus (when opted in) its own wrapper spans inside requests the app records;
/// - the app has none: CodeAtlas records everything and inherits the parent's sampled flag, exactly as ASP.NET Core's
///   own activities do when nobody listens.
/// Nothing of it is registered in, or shared with, the app's DI.
/// </summary>
internal sealed class CodeAtlasTracing(
    CodeAtlasRuntime runtime, SpanSerializer serializer, ISpanSink sink, IServiceProvider services,
    IHostEnvironment environment, ILogger<CodeAtlasTracing> logger) : IHostedService, IDisposable
{
    public static readonly string[] Sources = [CodeAtlasDiagnostics.SourceName, SpanSerializer.AspNetCoreSource, "Npgsql", "System.Net.Http"];

    private ActivityListener? _listener;

    public CodeAtlasExportProcessor? Processor { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        runtime.Initialize(services, logger);
        if (runtime.DisabledReason is { } reason)
        {
            logger.LogWarning("CodeAtlas is disabled: {Reason}. The application runs normally without it.", reason);
            return Task.CompletedTask;
        }

        var resource = new Resource
        {
            Attributes =
            {
                SpanSerializer.Kv("service.name", runtime.Options.ServiceName ?? environment.ApplicationName),
                SpanSerializer.Kv("codeatlas.app_has_tracing", runtime.AppHasTracing),
                // Tells the viewer whether what it sees is limited by the app's own sampler.
                SpanSerializer.Kv("codeatlas.sampling", runtime.AppHasTracing ? "app" : "codeatlas"),
            },
        };
        var processor = new CodeAtlasExportProcessor(serializer, sink, resource, runtime.Options.QueueCapacity, runtime.Options.BatchDelay, logger);
        Processor = processor;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => Sources.Contains(source.Name),
            Sample = Sample,
            ActivityStopped = activity =>
            {
                if (activity.IsAllDataRequested) processor.OnEnd(activity);
            },
        };
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
    {
        if (Sdk.SuppressInstrumentation) return ActivitySamplingResult.None; // CodeAtlas's own export calls
        var parentSampled = options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded);
        if (!runtime.AppHasTracing)
            return parentSampled ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.AllData;
        return options.Source.Name == CodeAtlasDiagnostics.SourceName && runtime.WrapperSpansEnabled && parentSampled
            ? ActivitySamplingResult.AllDataAndRecorded
            : ActivitySamplingResult.None;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        Processor?.Shutdown(5000);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
        Processor?.Dispose();
    }
}
