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
    private readonly string? _apiKey;
    private readonly string _environment;

    /// <summary>A server over its own throwaway directory, or over one the caller keeps alive.</summary>
    /// <param name="apiKey">The value of <c>CrowDB:ApiKey</c>; null leaves it unset (docs/005).</param>
    /// <param name="environment">
    /// The hosting environment. Development (the default) is the only one allowed
    /// to run without a key; anything else refuses to start (docs/005 §3).
    /// </param>
    public CrowDbServer(
        ITestOutputHelper output,
        TempDirectory? dataDirectory = null,
        string? apiKey = null,
        string environment = "Development")
    {
        _output = output;
        _dataDirectory = dataDirectory ?? new TempDirectory();
        _ownsDirectory = dataDirectory is null;
        _apiKey = apiKey;
        _environment = environment;
    }

    /// <summary>The data directory this instance's engine writes to (created up front).</summary>
    public string DataDirectory => _dataDirectory.Root;

    /// <summary>The log file inside <see cref="DataDirectory"/> (docs/000 §3).</summary>
    public string LogFile => _dataDirectory.LogFile;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Pin the environment so the suites never inherit the machine's (the
        // auth rule keys off it, docs/005 §3) and hand the API key in like
        // appsettings/`CROWDB__APIKEY` would.
        builder.UseEnvironment(_environment);
        if (_apiKey is not null)
            builder.UseSetting("CrowDB:ApiKey", _apiKey);

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
