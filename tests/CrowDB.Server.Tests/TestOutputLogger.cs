using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CrowDB.Server.Tests;

/// <summary>
/// Routes the test server's log entries into the running test's output (xUnit's
/// <see cref="ITestOutputHelper"/>), so a failing API test shows what the engine
/// and the request pipeline were doing. One provider per test server.
/// </summary>
internal sealed class TestOutputLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestOutputLogger(output, categoryName);

    public void Dispose()
    {
    }

    private sealed class TestOutputLogger(ITestOutputHelper output, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The engine's own logs only (and Debug and up): a red API test does
        // not need the host's startup chatter.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Debug && !categoryName.StartsWith("Microsoft.");

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            output.WriteLine($"{logLevel}: {categoryName}[{eventId.Id}] {formatter(state, exception)}");
        }
    }
}
