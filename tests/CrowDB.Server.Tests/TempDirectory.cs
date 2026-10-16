namespace CrowDB.Server.Tests;

/// <summary>
/// A throwaway server data directory, unique per test — the same setup as the
/// engine tests (tests/CrowDB.Engine.Tests/TempDirectory.cs): one directory
/// holding one log file, deleted (best effort) on dispose.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Absolute path of the data directory (created for you).</summary>
    public string Root { get; }

    /// <summary>Absolute path of the log file inside <see cref="Root"/>.</summary>
    public string LogFile => Path.Combine(Root, "crowdb.log");

    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), $"crowdb-api-tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        // Best effort: a leaked file handle must not turn one failure into many.
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
