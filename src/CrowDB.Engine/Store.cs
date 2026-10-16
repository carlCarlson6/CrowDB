using System.Text;
using Microsoft.Extensions.Logging;

namespace CrowDB.Engine;

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
    
    public void Put(string id, ReadOnlyMemory<byte> document)
    {
        var record = Record.Create(id, document, new Metadata());
        
        _logger.LogInformation("STORE.PUT - {doc}", record.ToString());
        
        lock (_logFileStream)
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

    public void Dispose()
    {
        _logFileStream.Close();
        _logFileStream.Dispose();
    }
}
