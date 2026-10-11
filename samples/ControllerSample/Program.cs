#if CODEATLAS
using CodeAtlas.AspNetCore;
#endif
using ControllerSample.Services;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(OrderService.MeterName));
#if CODEATLAS
builder.AddCodeAtlas();
#endif

var app = builder.Build();
app.MapControllers();
app.Run();
