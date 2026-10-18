using System.Text;
using System.Text.Json;
using static CrowDB.Engine.Kind;
using Offset = long;

namespace CrowDB.Engine;

public static class FileStreamExtensions
{
    public static byte[] ReadRecordSegment(this FileStream fileStream, Offset offset)
    {
        fileStream.Seek(offset, SeekOrigin.Begin);

        using var record = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var read = fileStream.Read(chunk, 0, chunk.Length);
            if (read == 0)                       // EOF, no '\n' seen
                break;

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                record.Write(chunk, 0, newline); // terminator excluded
                break;
            }
            record.Write(chunk, 0, read);
        }
        
        return record.ToArray();
    }

    public static IEnumerable<(Record record, Offset offset)> ReadAllLiveRecords(string path) =>
        ReadAllFile(path)
            .GroupBy(x => x.record.Id)
            .Select(group => group
                .MaxBy(y => y.offset))
            .Where(x => x.record.Kind != DELETE)
            .OrderBy(x => x.offset);

    private static List<(Record record, Offset offset)> ReadAllFile(string path)
    {
        var allText = File.ReadAllText(path);
        var allLines = allText.Split("\n");
        var lastSumOffset = 0;
        var records = new List<(Record record, Offset offset)>();
        
        foreach (var line in allLines)
        {
            try
            {
                var record = JsonSerializer.Deserialize<Record>(line);
                if (record is null) throw new Exception("Could not deserialize record");
                
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

    public static Offset WriteRecord(this FileStream fileStream, Record record)
    {
        fileStream.Seek(0, SeekOrigin.End);
        if (fileStream.Length > 0) fileStream.Write("\n"u8);
        var offset = fileStream.Position;
        fileStream.Write(record.Encode());
        fileStream.Flush(true);
        
        return offset;
    }
}