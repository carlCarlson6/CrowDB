using Microsoft.Extensions.Logging;

namespace CrowDB.Engine;

// TODO - resolve lock on filestream - "The field is sometimes used inside synchronized block and sometimes used without synchronization"

public sealed class Store : IDisposable
{
    private readonly FileStream _logFileStream;
    private readonly ILogger<Store> _logger;
    private readonly IdsIndex _idsIndex;
    
    public Store(string path, ILogger<Store> logger)
    {
        File.Open(path, FileMode.OpenOrCreate).Close();
        _logger = logger;
        _idsIndex = new IdsIndex(path);
        _logFileStream = File.Open(path, FileMode.OpenOrCreate);
    }

    public bool TryGet(string id, out ReadOnlyMemory<byte> document)
    {
        if (!_idsIndex.TryGetValue(id, out var offset))
        {
            document = default;
            return false;
        }
        
        var recordSegment = Record.ReadRecordSegment(_logFileStream, offset);
        document = Record.Decode(recordSegment).ToMemoryDoc();
        
        return true;
    }
    
    public bool CheckExists(string id) => _idsIndex.ContainsKey(id);
    
    public void Put(string id, ReadOnlyMemory<byte> document)
    {
        var record = Record.Create(id, document);
        
        _logger.LogInformation("STORE.PUT - {doc}", record.ToString());
        
        lock (_logFileStream)  // TODO - duplicated on PUT - unify
        {
            _logFileStream.Seek(0, SeekOrigin.End);
            if (_logFileStream.Length > 0) _logFileStream.Write("\n"u8);
            var offset = _logFileStream.Position;
            _logFileStream.Write(record.Encode());
            _logFileStream.Flush(true);
            _idsIndex[id] = offset;
        }
    }
    
    public IReadOnlyList<string> ListIds() => _idsIndex.Keys.ToList().AsReadOnly();

    public bool Delete(string id)
    {
        _logger.LogInformation("STORE.DELETE - {id}", id);
        
        var found = _idsIndex.TryGetValue(id, out var offset);
        if (!found) return false;

        try
        {
            lock (_logFileStream)
            {
                var recordSegment = Record.ReadRecordSegment(_logFileStream, offset);
                var deletedRecord = Record.Decode(recordSegment).Delete().Encode();
                
                
                // TODO - duplicated on PUT - unify
                _logFileStream.Seek(0, SeekOrigin.End);
                if (_logFileStream.Length > 0) _logFileStream.Write("\n"u8);
                _logFileStream.Write(deletedRecord);
                _logFileStream.Flush(true);

                _idsIndex.Remove(id);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error deleting record - {id}", id);
            return false;
        }
        
        return true;
    }
    
    public void Dispose()
    {
        _logFileStream.Close();
        _logFileStream.Dispose();
    }
}
