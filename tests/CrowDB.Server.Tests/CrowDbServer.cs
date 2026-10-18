using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CrowDB.Server.Tests;

/// <summary>
/// The real CrowDB.Server, running in-process: real routing, real engine, real
/// log file — no socket, no port. Each instance gets its own throwaway data
/// directory (<see cref="TempDirectory"/>) — or a caller-owned one, for tests
/// that restart the server over the same bytes — so tests never touch
/// <c>data/</c> or each other.
/// </summary>
internal sealed class CrowDbServer : WebApplicationFactory<Program>
{
    private readonly ITestOutputHelper _output;
    private readonly TempDirectory _dataDirectory;
    private readonly bool _ownsDirectory;

    /// <summary>A server over its own throwaway directory, or over one the caller keeps alive.</summary>
    public CrowDbServer(ITestOutputHelper output, TempDirectory? dataDirectory = null)
    {
        _output = output;
        _dataDirectory = dataDirectory ?? new TempDirectory();
        _ownsDirectory = dataDirectory is null;
    }

    /// <summary>The data directory this instance's engine writes to (created up front).</summary>
    public string DataDirectory => _dataDirectory.Root;

    /// <summary>The log file inside <see cref="DataDirectory"/> (docs/000 §3).</summary>
    public string LogFile => _dataDirectory.LogFile;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same setting as appsettings.json, pointed at this test's directory.
        builder.UseSetting("CrowDB:DataDirectory", DataDirectory);

        // Engine logs land in the test output instead of the test host's console.
        builder.ConfigureLogging(logging => logging.AddProvider(new TestOutputLoggerProvider(_output)));
    }

    protected override void Dispose(bool disposing)
    {
        // The host goes down first (file handles closed), then the directory —
        // and only when the directory is ours to delete.
        base.Dispose(disposing);
        if (_ownsDirectory)
            _dataDirectory.Dispose();
    }
}
