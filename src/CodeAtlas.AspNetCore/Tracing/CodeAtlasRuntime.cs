using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>Decided once at start-up, when every registration (whatever its order) is known.</summary>
internal sealed class CodeAtlasRuntime(CodeAtlasOptions options, List<string> problems, string? disabledReason)
{
    public CodeAtlasOptions Options { get; } = options;
    public string? DisabledReason { get; } = disabledReason;
    public bool AppHasTracing { get; private set; }
    public bool WrapperSpansEnabled { get; private set; }

    public void AddProblem(string problem)
    {
        lock (problems) problems.Add(problem);
    }

    public void Initialize(IServiceProvider services, ILogger logger)
    {
        lock (problems)
            foreach (var problem in problems) logger.LogWarning("CodeAtlas: {Problem}", problem);
        if (DisabledReason is not null) return;

        // CodeAtlas registers no TracerProvider in DI, so any TracerProvider here belongs to the app.
        AppHasTracing = services.GetService<TracerProvider>() is not null;
        WrapperSpansEnabled = !AppHasTracing || Options.WrapperSpansBesideAppTracing;
        if (AppHasTracing)
            logger.LogWarning(
                "CodeAtlas: this app has its own OpenTelemetry tracing. CodeAtlas follows your app's sampling and shows only " +
                "the requests your sampler records; it never changes what your tracing does.{Wrappers}",
                WrapperSpansEnabled
                    ? " Endpoint, action and handler spans are on (WrapperSpansBesideAppTracing): your own tool will see database and HTTP spans whose parent it never receives."
                    : " Endpoint, action and handler spans are off (set WrapperSpansBesideAppTracing = true to opt in).");
    }
}
