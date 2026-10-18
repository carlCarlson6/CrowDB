namespace CrowDB.Engine.Tests;

/// <summary>
/// A throwaway engine data directory, unique per test: one directory holding
/// one log file, exactly what the server passes to the engine.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Absolute path of the data directory (created for you).</summary>
    private string Root { get; }

    /// <summary>Absolute path of the log file inside <see cref="Root"/>.</summary>
    public string LogFile 
    {
        get => Path.Combine(Root, field);
    } = "crowdb.log";

    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "crowdb-tests", $"{Guid.NewGuid()}");
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// Appends raw bytes to the log without the engine's knowledge — used to
    /// simulate what a crash mid-write leaves behind.
    /// </summary>
    public void AppendRaw(byte[] text) => File.AppendAllBytes(LogFile, text);

    /// <summary>
    /// Removes bytes from the end of the log — the other half of a crash
    /// mid-append: a torn write is a frame that never finished.
    /// </summary>
    public void TruncateTail(int byteCount)
    {
        using var log = File.Open(LogFile, FileMode.Open, FileAccess.Write);
        log.SetLength(log.Length - byteCount);
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
