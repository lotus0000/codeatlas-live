using CodeAtlas.Collector;

Microsoft.AspNetCore.Builder.WebApplication app;
try
{
    app = CollectorApp.Create(args);
}
catch (InvalidOperationException e)
{
    // Invalid settings (e.g. a non-loopback address) are a message for the user, not a crash.
    Console.Error.WriteLine($"codeatlas: {e.Message}");
    return 2;
}
app.Run();
return 0;
