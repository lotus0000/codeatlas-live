using CodeAtlas.AspNetCore.Export;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>The first send blocks until <see cref="Release"/>; later sends record normally.</summary>
internal sealed class BlockingSink : ISpanSink
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RecordingSink Recorded { get; } = new();

    public async Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            FirstCallStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
        await Recorded.SendAsync(request, cancellationToken);
    }

    public void Release() => _release.TrySetResult();
}

internal sealed class ThrowingSink : ISpanSink
{
    public int Calls;

    public Task SendAsync(ExportTraceServiceRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        throw new HttpRequestException("collector unreachable");
    }
}
