using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>
/// Asks for data but never sets the sampled flag: the app's own sampler (if any) keeps deciding what its exporters
/// receive and what flag goes downstream, while CodeAtlas still sees every request.
/// </summary>
internal sealed class RecordOnlySampler : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) => new(SamplingDecision.RecordOnly);
}
