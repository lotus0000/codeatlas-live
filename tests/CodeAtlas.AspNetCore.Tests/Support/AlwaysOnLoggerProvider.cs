using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>
/// An enabled logger that keeps formatted messages. Like a normal app with logging, ASP.NET Core then creates request
/// activities; tests can also read what CodeAtlas logged.
/// </summary>
internal sealed class AlwaysOnLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Recorder(Messages);
    public void Dispose() { }

    private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}
