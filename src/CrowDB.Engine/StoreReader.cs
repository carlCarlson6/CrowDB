namespace CrowDB.Engine;

public class StoreReader
{
    public static byte[] ReadUntilNewline(FileStream stream, long offset)
    {
        stream.Seek(offset, SeekOrigin.Begin);

        using var record = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
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
}