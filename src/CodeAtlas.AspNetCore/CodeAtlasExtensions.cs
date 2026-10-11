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
    /// <summary>
    /// Adds CodeAtlas Live tracing. Call it inside <c>#if CODEATLAS</c> with a Debug-only package reference.
    /// Does nothing outside Development unless <c>CodeAtlas:Enabled=true</c>.
    /// </summary>
    public static WebApplicationBuilder AddCodeAtlas(this WebApplicationBuilder builder, Action<CodeAtlasOptions>? configure = null)
    {
        var options = new CodeAtlasOptions();
        configure?.Invoke(options);
        var section = builder.Configuration.GetSection("CodeAtlas");
        if (section.GetValue<bool>("Enabled")) options.EnableOutsideDevelopment = true;
        if (section["Endpoint"] is { Length: > 0 } endpoint) options.Endpoint = endpoint;
        if (!builder.Environment.IsDevelopment() && !options.EnableOutsideDevelopment) return builder;

        var services = builder.Services;
        services.AddSingleton(new CodeAtlasRuntime(options));
        services.AddSingleton<HttpFactsStore>();
        services.AddSingleton(sp => new SpanSerializer(options.KeepDbStatements, sp.GetRequiredService<HttpFactsStore>()));
        services.AddSingleton<ISpanSink>(sp => options.SinkFactory?.Invoke(sp) ?? new HttpSpanSink(new Uri(options.Endpoint), options.ExportTimeout));
        services.AddSingleton<IStartupFilter, HttpFactsStartupFilter>();
        services.AddHostedService<CodeAtlasTracing>();
        services.AddSingleton<MatcherPolicy, EndpointSpanPolicy>();
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<ActionSpanFilter>(int.MaxValue));
        return builder;
    }
}
