using Microsoft.Extensions.Logging;

namespace CodeAtlas.AspNetCore.Tests.Support;

/// <summary>An enabled, silent logger: like a normal app with logging, ASP.NET Core then creates request activities.</summary>
internal sealed class AlwaysOnLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Silent();
    public void Dispose() { }

    private sealed class Silent : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
