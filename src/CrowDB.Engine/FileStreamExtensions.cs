using Offset = long;

namespace CrowDB.Engine;

public static class FileStreamExtensions
{
    public static byte[] ReadRecordSegment(this FileStream fileStream, long offset)
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