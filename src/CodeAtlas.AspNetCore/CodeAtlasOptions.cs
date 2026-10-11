using CodeAtlas.AspNetCore.Export;

namespace CodeAtlas.AspNetCore;

public sealed class CodeAtlasOptions
{
    /// <summary>OTLP/HTTP traces endpoint of the CodeAtlas collector. Config: <c>CodeAtlas:Endpoint</c>.</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:4318/v1/traces";

    /// <summary>Defaults to the application name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>CodeAtlas is off outside Development unless this is set. Config: <c>CodeAtlas:Enabled</c>.</summary>
    public bool EnableOutsideDevelopment { get; set; }

    /// <summary>Keep SQL text (never parameters) on database spans. Off by default.</summary>
    public bool KeepDbStatements { get; set; }

    /// <summary>
    /// Add endpoint/action/handler spans even though the app has its own tracing. They become parents of database and
    /// HTTP spans, which then point at parents the app's own tracing tool never receives.
    /// </summary>
    public bool WrapperSpansBesideAppTracing { get; set; }

    public int QueueCapacity { get; set; } = 2048;

    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    internal Func<IServiceProvider, ISpanSink>? SinkFactory { get; set; }
}
