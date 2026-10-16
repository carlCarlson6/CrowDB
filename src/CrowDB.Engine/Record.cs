using System.Text;
using System.Text.Json;

namespace CrowDB.Engine;

public record Record(string Id, string Version, string Doc)
{
    public ReadOnlySpan<byte> Encode() => Encoding.UTF8.GetBytes(ToString());
    
    public static Record Create(string docId, ReadOnlyMemory<byte> document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docId);
        if (document.Length == 0)
            throw new ArgumentException("document cannot be empty", nameof(document));

        return new Record(
            Id:       docId,
            Version:  Constants.ENCODE_VERSION,
            Doc:      Encoding.UTF8.GetString(document.ToArray())
        );
    }
    
    public ReadOnlyMemory<byte> ToMemoryDoc() => Encoding.UTF8.GetBytes(Doc);

    public static Record Decode(byte[] recordBytes)
    {
        var record = JsonSerializer.Deserialize<Record>(recordBytes);
        return record ?? throw new Exception("empty record");
    }

    public static byte[] ReadRecordSegment(FileStream fileStream, long offset)
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

    public override string ToString() => JsonSerializer.Serialize(this);
}

public record Metadata(string Collection = "");