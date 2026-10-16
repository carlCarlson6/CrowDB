using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CrowDB.Server.Tests;

/// <summary>
/// The real CrowDB.Server, running in-process: real routing, real engine, real
/// log file — no socket, no port. Each instance gets its own throwaway data
/// directory, deleted on dispose, so tests never touch <c>data/</c> or each other.
/// </summary>
internal sealed class CrowDbServer(ITestOutputHelper output) : WebApplicationFactory<Program>
{
    /// <summary>The data directory this instance's engine writes to (created up front).</summary>
    public string DataDirectory { get; } = CreateDataDirectory();

    private static string CreateDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "crowdb-api-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The log file inside <see cref="DataDirectory"/> (docs/001 §3).</summary>
    public string LogFile => Path.Combine(DataDirectory, "crowdb.log");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same setting as appsettings.json, pointed at this test's directory.
        builder.UseSetting("CrowDB:DataDirectory", DataDirectory);

        // Engine logs land in the test output instead of the test host's console.
        builder.ConfigureLogging(logging => logging.AddProvider(new TestOutputLoggerProvider(output)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // Best effort: a leaked file handle must not turn one failure into many.
        try
        {
            if (Directory.Exists(DataDirectory))
                Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
