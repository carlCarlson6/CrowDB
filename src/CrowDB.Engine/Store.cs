using Microsoft.Extensions.Logging;

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
public sealed class Store : IDisposable
{
    private readonly FileStream _logFileStream;
    private readonly ILogger<Store> _logger;
    private readonly IdsIndex _idsIndex;
    
    public Store(string directory, ILogger<Store> logger)
    {
        var exists = File.Exists(Path.Combine(directory, Constants.LOG_FILENAME));

        _logFileStream = !exists 
            ? File.Create(Path.Combine(directory, Constants.LOG_FILENAME)) 
            : File.Open(Path.Combine(directory, Constants.LOG_FILENAME), FileMode.OpenOrCreate);

        _logger = logger;
        
        _idsIndex = new IdsIndex(_logFileStream);
    }

    /// <summary>
    /// Point lookup: returns the newest document stored under <paramref name="id"/>.
    /// </summary>
    /// <returns><c>false</c> when the id is not in the index.</returns>
    public bool TryGet(string id, out ReadOnlyMemory<byte> document)
    {
        var found = _idsIndex.TryGetValue(id, out var offset);
        if (!found)
        {
            document = default;
            return false;
        }
        
        var docBytes = StoreReader.ReadUntilNewline(_logFileStream, offset);
        
        document = docBytes.AsMemory();
        
        return found;
    }

    /// <summary>
    /// Upsert (last write wins): append a new record for <paramref name="id"/>.
    /// Returns only once the record is durable.
    /// </summary>
    public void Put(string id, ReadOnlyMemory<byte> document)
    {
        var encoder = new DocumentEncoder(docId: id, document: document);
        
        _logger.LogInformation("STORE.PUT - {doc}", encoder.ToString());
        
        var offset = _logFileStream.Length;
        
        lock (_logFileStream)
        {
            _logFileStream.Write(encoder.ToEncodedSpan());
            _logFileStream.Flush();
            _idsIndex[id] = offset;
        }
    }

    /// <summary>
    /// Every id in the store. Unordered: the index is a hash dictionary, so
    /// ordering arrives later, with a sorted index.
    /// </summary>
    public IReadOnlyList<string> ListIds() => _idsIndex.Keys.ToList().AsReadOnly();

    public void Dispose()
    {
        _logFileStream.Close();
        _logFileStream.Dispose();
    }
}
