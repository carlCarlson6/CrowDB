using System.Collections.Generic;
using Id = string;
using Offset = long;

namespace CrowDB.Engine;

public class IdsIndex : Dictionary<Id, Offset>
{
    private readonly string _path;
    
    public IdsIndex(string path)
    {
        _path = path;
        Recreate();
    }
    
    public void Recreate()
    {
        Clear();
        
        var records = FileStreamExtensions.ReadAllLiveRecords(_path);
        
        foreach (var (record, offset) in records)
            this[record.Id] = offset;
    }
}