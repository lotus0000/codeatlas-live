using System.Diagnostics;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>
/// Always asks for data, and sets the sampled flag exactly as the app would have without CodeAtlas:
/// - the app has its own tracing: never sampled, so the app's own sampler alone decides the flag and its exports;
/// - the app has none: inherit the parent's flag, as ASP.NET Core's own activities do when nobody listens.
/// </summary>
internal sealed class CodeAtlasSampler(bool appHasTracing) : Sampler
{
    private static readonly SamplingResult RecordOnly = new(SamplingDecision.RecordOnly);
    private static readonly SamplingResult RecordAndSample = new(SamplingDecision.RecordAndSample);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        !appHasTracing && samplingParameters.ParentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? RecordAndSample : RecordOnly;
}
