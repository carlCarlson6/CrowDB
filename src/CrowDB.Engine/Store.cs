using Microsoft.Extensions.Logging;

namespace CrowDB.Engine;

// TODO - resolve lock on filestream - "The field is sometimes used inside synchronized block and sometimes used without synchronization"

public sealed class Store : IDisposable
{
    private readonly string _path;
    private FileStream _logFileStream;
    private readonly ILogger<Store> _logger;
    private readonly IdsIndex _idsIndex;
    
    public Store(string path, ILogger<Store> logger)
    {
        _path = path;
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

        var recordSegment = _logFileStream.ReadRecordSegment(offset);
        document = Record.Decode(recordSegment).ToMemoryDoc();
        
        return true;
    }
    
    public void Put(string id, ReadOnlyMemory<byte> document)
    {
        var record = Record.Create(id, document);
        
        _logger.LogInformation("STORE.PUT - {doc}", record.ToString());
        
        lock (_logFileStream)  // TODO - duplicated on PUT - unify
        {
            var offset = _logFileStream.WriteRecord(record);
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
                var recordSegment = _logFileStream.ReadRecordSegment(offset);
                var deletedRecord = Record
                    .Decode(recordSegment)
                    .Delete();
                
                _logFileStream.WriteRecord(deletedRecord);
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
    
    // Step 3 (docs/004): compaction — rewrite the log as only its live records,
    // then swap the new file in with a single atomic rename (rename(2) on
    // macOS/Linux). The tests are red until this is implemented.
    public void Compact()
    {
        lock (_logFileStream)
        {
            var compactFileName = $"crowdb_compact_{Guid.NewGuid()}.log";
            var compactFilePath = _path.Replace(Constants.LogFilename, compactFileName);
            var compactFileStream = File.Open(compactFilePath, FileMode.OpenOrCreate);
            
            foreach (var offset in _idsIndex.Values)
            {
                var recordSegment = _logFileStream.ReadRecordSegment(offset);
                var record = Record.Decode(recordSegment);
                compactFileStream.WriteRecord(record);
            }
            
            compactFileStream.Close();
            _logFileStream.Close();
            
            File.Move(compactFilePath, _path, overwrite: true);
            _logFileStream = File.Open(_path, FileMode.OpenOrCreate);
        }
    }

    public void Dispose()
    {
        _logFileStream.Close();
        _logFileStream.Dispose();
    }
}
