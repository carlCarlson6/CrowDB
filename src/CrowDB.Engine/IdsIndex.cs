using System.Text;
using System.Text.Json;
using static CrowDB.Engine.Kind;

namespace CrowDB.Engine;

public class IdsIndex : Dictionary<string, long>
{
    public IdsIndex(string path)
    {
        var allFile = ReadAllFile(path);
        var records = allFile
            .GroupBy(x => x.record.Id)
            .Select(group => group
                .MaxBy(y => y.offset));
        foreach (var (record, offset) in records)
            this[record.Id] = offset;
    }
    
    private List<(Record record, long offset)> ReadAllFile(string path)
    {
        var allText = File.ReadAllText(path);
        var allLines = allText.Split("\n");
        var lastSumOffset = 0;
        var records = new List<(Record record, long offset)>();
        
        foreach (var line in allLines)
        {
            try
            {
                var record = JsonSerializer.Deserialize<Record>(line);
                if (record is null) throw new Exception("Could not deserialize record");
                if (record.Kind is DELETE)
                {
                    records = records
                        .Where(x => x.record.Id != record.Id)
                        .ToList();
                    continue;
                }
                
                records.Add((record, lastSumOffset));
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            finally
            {
                lastSumOffset += Encoding.UTF8.GetByteCount(line+"\n");
            }
        }
        
        return records;
    }
}