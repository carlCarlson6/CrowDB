using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CrowDB.Server.Tests;

/// <summary>
/// The real CrowDB.Server, running in-process: real routing, real engine, real
/// log file — no socket, no port. Each instance gets its own throwaway data
/// directory (<see cref="TempDirectory"/>), so tests never touch <c>data/</c>
/// or each other.
/// </summary>
internal sealed class CrowDbServer(ITestOutputHelper output) : WebApplicationFactory<Program>
{
    private readonly TempDirectory _dataDirectory = new();

    /// <summary>The data directory this instance's engine writes to (created up front).</summary>
    public string DataDirectory => _dataDirectory.Root;

    /// <summary>The log file inside <see cref="DataDirectory"/> (docs/001 §3).</summary>
    public string LogFile => _dataDirectory.LogFile;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same setting as appsettings.json, pointed at this test's directory.
        builder.UseSetting("CrowDB:DataDirectory", DataDirectory);

        // Engine logs land in the test output instead of the test host's console.
        builder.ConfigureLogging(logging => logging.AddProvider(new TestOutputLoggerProvider(output)));
    }

    protected override void Dispose(bool disposing)
    {
        // The host goes down first (file handles closed), then the directory.
        base.Dispose(disposing);
        _dataDirectory.Dispose();
    }
}
