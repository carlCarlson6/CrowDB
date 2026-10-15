namespace CrowDB.Engine;

/// <summary>
/// The CrowDB storage engine: an append-only log of records plus an in-memory
/// index mapping each id to the byte offset of that id's newest record.
/// </summary>
/// <remarks>
/// Step 1 skeleton — the surface below is locked (docs/001 §7); the internals
/// are the work of step 1 (docs/002-step-1-plan.md).
///
/// Concepts in play: the log is the source of truth, the index is derived data,
/// and constructing the store rebuilds the index by replaying the log (recovery).
///
/// The engine stores opaque bytes: it parses only its own record envelopes,
/// never the documents themselves.
/// </remarks>
public sealed class DocumentStore : IDisposable
{
    /// <param name="directory">Directory that holds the log file (<c>crowdb.log</c>).</param>
    public DocumentStore(string directory)
    {
        throw new NotImplementedException(
            "Step 1: open (or create) the log, replay it, and build the index — see docs/002-step-1-plan.md.");
    }

    /// <summary>
    /// Point lookup: returns the newest document stored under <paramref name="id"/>.
    /// </summary>
    /// <returns><c>false</c> when the id is not in the index.</returns>
    public bool TryGet(string id, out ReadOnlyMemory<byte> document)
    {
        throw new NotImplementedException(
            "Step 1: index lookup -> seek to the offset -> read the record -> hand back the document bytes.");
    }

    /// <summary>
    /// Upsert (last write wins): append a new record for <paramref name="id"/>.
    /// Returns only once the record is durable.
    /// </summary>
    public void Put(string id, ReadOnlyMemory<byte> document)
    {
        throw new NotImplementedException(
            "Step 1: encode -> append at the end of the log -> fsync -> index[id] = offset.");
    }

    /// <summary>
    /// Every id in the store. Unordered: the index is a hash dictionary, so
    /// ordering arrives later, with a sorted index.
    /// </summary>
    public IReadOnlyList<string> ListIds()
    {
        throw new NotImplementedException("Step 1: the index's keys.");
    }

    /// <summary>Closes the log file handle.</summary>
    public void Dispose()
    {
        throw new NotImplementedException("Step 1: nothing is buffered, but the log file handle must be closed.");
    }
}
