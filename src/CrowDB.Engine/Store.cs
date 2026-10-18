using Microsoft.Extensions.Logging;

namespace CrowDB.Engine;

public sealed class Store : IDisposable
{
    private readonly string _path;
    private FileStream _logFileStream;
    private readonly ILogger<Store> _logger;
    private readonly IdsIndex _idsIndex;
    private readonly Lock _lock = new();
    
    public Store(string path, ILogger<Store> logger)
    {
        lock (_lock)
        {
            _path = path;
            File.Open(path, FileMode.OpenOrCreate).Close();
            _logger = logger;
            _idsIndex = new IdsIndex(path);
            _logFileStream = File.Open(path, FileMode.OpenOrCreate);    
        }
    }

    public bool TryGet(string id, out ReadOnlyMemory<byte> document)
    {
        lock (_lock)
        {
            if (!_idsIndex.TryGetValue(id, out var offset))
            {
                document = default;
                return false;
            }

            var recordSegment = _logFileStream.ReadRecordSegment(offset);
            document = Record.Decode(recordSegment).ToMemoryDoc();    
        }
        
        return true;
    }
    
    public void Put(string id, ReadOnlyMemory<byte> document)
    {
        var record = Record.Create(id, document);
        
        _logger.LogInformation("STORE.PUT - {doc}", record.ToString());
        
        lock (_lock)
        {
            var offset = _logFileStream.WriteRecord(record);
            _idsIndex[id] = offset;
        }
    }
    
    public IReadOnlyList<string> ListIds()
    {
        lock (_lock) return _idsIndex.Keys.ToList().AsReadOnly();
    }

    public bool Delete(string id)
    {
        _logger.LogInformation("STORE.DELETE - {id}", id);

        lock (_lock)
        {
            var found = _idsIndex.TryGetValue(id, out var offset);
            if (!found) return false;

            try
            {
                var recordSegment = _logFileStream.ReadRecordSegment(offset);
                var deletedRecord = Record
                    .Decode(recordSegment)
                    .Delete();
                
                _logFileStream.WriteRecord(deletedRecord);
                _idsIndex.Remove(id);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error deleting record - {id}", id);
                return false;
            }
        }
        
        return true;
    }
    
    public void Compact()
    {
        lock (_lock)
        { 
            var compactFilePath = $"{_path}.compact";
            var compactFileStream = File.Open(compactFilePath, FileMode.Create);
            _logFileStream.Close();
            _logFileStream.Dispose();

            var allRecords = FileStreamExtensions.ReadAllLiveRecords(_path);
            foreach (var (record, _) in allRecords)
                compactFileStream.WriteRecord(record);
            
            compactFileStream.Close();
            
            File.Move(compactFilePath, _path, overwrite: true);
            _idsIndex.Recreate();
            _logFileStream = File.Open(_path, FileMode.OpenOrCreate);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _logFileStream.Close();
            _logFileStream.Dispose();
        }
    }
}