#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Fixture").AddOtlpExporter());
#if CODEATLAS
builder.AddCodeAtlas();
#endif

var app = builder.Build();
app.MapGet("/ping", () => "pong");
app.Run();
