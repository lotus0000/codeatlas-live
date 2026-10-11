using CodeAtlas.Collector.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.Collector;

public static class CollectorApp
{
    public const string Version = "0.1.0";
    public const string IngestCors = "otlp-ingest";

    public static readonly IReadOnlyDictionary<string, string> Switches = new Dictionary<string, string>
    {
        ["--source-root"] = "CodeAtlas:SourceRoots",
        ["--public-url"] = "CodeAtlas:PublicUrl",
        ["--allowed-origin"] = "CodeAtlas:AllowedOrigins",
        ["--editor"] = "CodeAtlas:Editor:Kind",
        ["--editor-command"] = "CodeAtlas:Editor:Command",
    };

    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Configuration.AddCommandLine(args, Switches.ToDictionary());
        configure?.Invoke(builder);

        var options = CollectorOptions.From(builder.Configuration, Directory.GetCurrentDirectory());
        builder.WebHost.UseUrls(options.Urls.Select(u => u.ToString()).ToArray());
        // Host filtering (on by default in WebApplication) blocks DNS-rebinding pages that resolve to 127.0.0.1.
        builder.Configuration["AllowedHosts"] = string.Join(';', options.AllowedHosts);
        builder.Services.AddSingleton(options);
        builder.Services.AddCors(cors => cors.AddPolicy(IngestCors, policy => policy
            .WithOrigins([.. options.AllowedOrigins]).WithMethods("POST").WithHeaders("content-type", "content-encoding")));
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        if (options.PathBase is { } pathBase) app.UsePathBase(pathBase);
        // WebApplication adds routing at the very start unless it is added explicitly; it must run after the path base.
        app.UseRouting();
        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        TraceEndpoints.Map(app);
        return app;
    }
}
