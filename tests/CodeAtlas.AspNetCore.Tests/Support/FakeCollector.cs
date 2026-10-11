using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>
/// Minimal OTLP/HTTP receiver on HttpListener (not ASP.NET Core), so its own requests produce no server spans
/// that CodeAtlas would record and export again.
/// </summary>
internal sealed class FakeCollector : IDisposable
{
    private readonly HttpListener _listener = new();

    public FakeCollector()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }
    public string TracesUrl => $"http://127.0.0.1:{Port}/v1/traces";
    public ConcurrentQueue<(string ContentType, ExportTraceServiceRequest Request)> Received { get; } = new();

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            Received.Enqueue((context.Request.ContentType ?? "", ExportTraceServiceRequest.Parser.ParseFrom(body.ToArray())));
            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }

    internal static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose() => _listener.Close();
}
