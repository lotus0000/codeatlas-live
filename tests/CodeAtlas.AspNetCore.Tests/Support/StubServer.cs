using System.Collections.Concurrent;
using System.Net;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>Downstream HTTP API on HttpListener: answers 200 and remembers the traceparent it received.</summary>
internal sealed class StubServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public StubServer()
    {
        Port = FakeCollector.FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                TraceParents.Enqueue(context.Request.Headers["traceparent"] ?? "");
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        });
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public ConcurrentQueue<string> TraceParents { get; } = new();

    public void Dispose() => _listener.Close();
}
