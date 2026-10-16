using System.Text;
using CrowDB.Engine;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CrowDB.Engine.Tests;

/// <summary>Helpers shared by the engine tests.</summary>
internal static class TestSupport
{
    /// <summary>A store over <paramref name="directory"/>, with engine logs going to the test's output.</summary>
    public static Store OpenStore(string directory, ITestOutputHelper output) =>
        new(directory, new TestOutputLogger<Store>(output));

    /// <summary>Document bytes as the engine sees them: UTF-8, never parsed.</summary>
    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>Reads a document, failing the test when the id is missing.</summary>
    public static byte[] Read(Store store, string id)
    {
        Assert.True(store.TryGet(id, out var document), $"expected a document for id '{id}'");
        return document.ToArray();
    }
}

/// <summary>
/// The store's logger in tests: every log entry lands in the running test's
/// output (xUnit's <see cref="ITestOutputHelper"/>), so a failing test shows
/// what the store was doing. Debug and up, matching the server's development
/// log level.
/// </summary>
/// <remarks>
/// The framework's console logger would be the obvious choice, but it writes
/// through a process-wide background queue and the test host exits before that
/// queue drains, silently dropping most lines. This logger writes immediately,
/// in the thread that logged.
/// </remarks>
internal sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    // Matches how the framework logs categories: the logger's full type name.
    private readonly string _category = typeof(T).FullName!;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        output.WriteLine($"{logLevel}: {_category}[{eventId.Id}] {formatter(state, exception)}");
    }
}
