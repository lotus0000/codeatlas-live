using CodeAtlas.AspNetCore.Export;
using CodeAtlas.AspNetCore.Http;
using CodeAtlas.AspNetCore.Spans;
using CodeAtlas.AspNetCore.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeAtlas.AspNetCore;

public static class CodeAtlasExtensions
{
    private static readonly TimeSpan DefaultExportTimeout = TimeSpan.FromSeconds(2);
    private const int DefaultQueueCapacity = 2048;

    /// <summary>
    /// Adds CodeAtlas Live tracing. Call it inside <c>#if CODEATLAS</c> with a Debug-only package reference.
    /// On in Development, off elsewhere; <c>CodeAtlas:Enabled</c> (true/false) overrides both.
    /// Invalid settings never stop the app: they are logged at start-up and replaced by defaults, or disable CodeAtlas.
    /// Only the first call counts; later calls are ignored with a warning.
    /// </summary>
    public static WebApplicationBuilder AddCodeAtlas(this WebApplicationBuilder builder, Action<CodeAtlasOptions>? configure = null)
    {
        var services = builder.Services;
        if (services.FirstOrDefault(d => d.ServiceType == typeof(CodeAtlasRuntime))?.ImplementationInstance is CodeAtlasRuntime existing)
        {
            existing.AddProblem("AddCodeAtlas was called more than once; only the first call's settings are used.");
            return builder;
        }

        var problems = new List<string>();
        var options = new CodeAtlasOptions();
        configure?.Invoke(options);

        var section = builder.Configuration.GetSection("CodeAtlas");
        bool? enabled = null;
        if (section["Enabled"] is { Length: > 0 } rawEnabled)
        {
            if (bool.TryParse(rawEnabled, out var parsed)) enabled = parsed;
            else problems.Add($"CodeAtlas:Enabled '{rawEnabled}' is not true or false and was ignored.");
        }
        if (enabled == false) return builder;
        if (enabled == true) options.EnableOutsideDevelopment = true;
        if (!builder.Environment.IsDevelopment() && !options.EnableOutsideDevelopment) return builder;

        if (section["Endpoint"] is { Length: > 0 } configured) options.Endpoint = configured;
        string? disabledReason = null;
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            disabledReason = $"the collector endpoint '{options.Endpoint}' is not an absolute http(s) URL";
            endpoint = null;
        }
        if (options.QueueCapacity < 1)
        {
            problems.Add($"QueueCapacity {options.QueueCapacity} is invalid; using {DefaultQueueCapacity}.");
            options.QueueCapacity = DefaultQueueCapacity;
        }
        if (options.ExportTimeout <= TimeSpan.Zero)
        {
            problems.Add($"ExportTimeout {options.ExportTimeout} is invalid; using {DefaultExportTimeout.TotalSeconds} s.");
            options.ExportTimeout = DefaultExportTimeout;
        }

        services.AddSingleton(new CodeAtlasRuntime(options, problems, disabledReason));
        services.AddSingleton<HttpFactsStore>();
        services.AddSingleton(sp => new SpanSerializer(options.KeepDbStatements, sp.GetRequiredService<HttpFactsStore>()));
        services.AddSingleton<ISpanSink>(sp => options.SinkFactory?.Invoke(sp)
            ?? (endpoint is null ? new NoopSpanSink() : new HttpSpanSink(endpoint, options.ExportTimeout)));
        services.AddSingleton<IStartupFilter, HttpFactsStartupFilter>();
        services.AddHostedService<CodeAtlasTracing>();
        services.AddSingleton<MatcherPolicy, EndpointSpanPolicy>();
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<ActionSpanFilter>(int.MaxValue));
        return builder;
    }
}
