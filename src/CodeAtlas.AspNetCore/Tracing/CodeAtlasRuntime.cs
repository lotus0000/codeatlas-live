using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace CodeAtlas.AspNetCore.Tracing;

/// <summary>Decided once at start-up, when every registration (whatever its order) is known.</summary>
internal sealed class CodeAtlasRuntime(CodeAtlasOptions options)
{
    public CodeAtlasOptions Options { get; } = options;
    public bool AppHasTracing { get; private set; }
    public bool WrapperSpansEnabled { get; private set; }

    public void Initialize(IServiceProvider services, ILogger logger)
    {
        // CodeAtlas's own provider is not registered in DI, so any TracerProvider here belongs to the app.
        AppHasTracing = services.GetService<TracerProvider>() is not null;
        WrapperSpansEnabled = !AppHasTracing || Options.WrapperSpansBesideAppTracing;
        if (AppHasTracing && !WrapperSpansEnabled)
            logger.LogWarning(
                "CodeAtlas: this app has its own OpenTelemetry tracing, so CodeAtlas does not add endpoint, action or handler " +
                "spans (they would break parent links in your own tracing tool). Set WrapperSpansBesideAppTracing = true to opt in.");
    }
}
