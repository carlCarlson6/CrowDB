using System.Buffers.Binary;
using System.Text;

namespace CrowDB.Engine.Tests;

/// <summary>
/// The v2 frame layout (docs/004 §2), implemented independently of the engine:
/// the tests write and read the bytes themselves, so a broken codec cannot hide
/// behind itself. All numbers are little-endian.
///
/// <code>
/// magic  version  crc32  kind  keyLen  docLen  key      document
/// u16    u8       u32    u8    u16     u32     (utf-8)  (raw bytes)
/// 0      2        3      7     8       10      14       14 + keyLen
/// </code>
///
/// The CRC32 covers everything after the CRC field — kind, lengths, key, and
/// document — using the standard reflected polynomial 0xEDB88320 (CRC-32/ISO-HDLC,
/// the same as zlib; the check value for "123456789" is 0xCBF43926).
/// </summary>
internal static class FrameBytes
{
    public const byte Magic0 = 0x43; // 'C'
    public const byte Magic1 = 0x44; // 'D'
    public const byte Version = 2;
    public const byte KindPut = 0;
    public const byte KindDelete = 1;
    public const int HeaderSize = 14;
    public const int MaxDocLength = 16 * 1024 * 1024;

    /// <summary>Encodes one frame exactly as docs/004 §2 defines it.</summary>
    public static byte[] Encode(string id, ReadOnlySpan<byte> doc, byte kind = KindPut)
    {
        var key = Encoding.UTF8.GetBytes(id);
        var frame = new byte[HeaderSize + key.Length + doc.Length];

        frame[0] = Magic0;
        frame[1] = Magic1;
        frame[2] = Version;
        frame[7] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)key.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(10), (uint)doc.Length);
        key.CopyTo(frame.AsSpan(HeaderSize));
        doc.CopyTo(frame.AsSpan(HeaderSize + key.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3), Crc32(frame.AsSpan(7)));

        return frame;
    }

    /// <summary>A delete frame: kind DELETE, no document (docs/004 §2 — the sentinel is retired).</summary>
    public static byte[] EncodeTombstone(string id) => Encode(id, [], KindDelete);

    /// <summary>Every frame in <paramref name="log"/>, in order, with offsets. Throws when any byte is not a valid frame.</summary>
    public static List<Frame> ParseAll(byte[] log)
    {
        var frames = new List<Frame>();
        var offset = 0;

        while (offset < log.Length)
        {
            var frame = Parse(log, offset);
            frames.Add(frame);
            offset += frame.Length;
        }

        return frames;
    }

    /// <summary>One parsed frame: its fields plus where it starts and how long it is.</summary>
    public readonly record struct Frame(byte Kind, string Id, byte[] Doc, int Offset, int Length);

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return ~crc;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private static Frame Parse(byte[] log, int offset)
    {
        var span = log.AsSpan(offset);
        if (span.Length < HeaderSize)
            throw new InvalidDataException($"frame at {offset}: {span.Length} bytes, need at least {HeaderSize}");

        if (span[0] != Magic0 || span[1] != Magic1)
            throw new InvalidDataException($"frame at {offset}: bad magic {span[0]:X2} {span[1]:X2}");
        if (span[2] != Version)
            throw new InvalidDataException($"frame at {offset}: version {span[2]}, expected {Version}");

        var kind = span[7];
        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var docLen = BinaryPrimitives.ReadUInt32LittleEndian(span[10..]);
        if (docLen > int.MaxValue)
            throw new InvalidDataException($"frame at {offset}: docLen {docLen} is not a plausible length");

        var length = HeaderSize + keyLen + (int)docLen;
        if (span.Length < length)
            throw new InvalidDataException($"frame at {offset}: {length} bytes declared, {span.Length} present");

        var stored = BinaryPrimitives.ReadUInt32LittleEndian(span[3..]);
        var actual = Crc32(span[7..length]);
        if (stored != actual)
            throw new InvalidDataException($"frame at {offset}: crc32 {stored:X8}, expected {actual:X8}");

        var id = Encoding.UTF8.GetString(span[HeaderSize..(HeaderSize + keyLen)]);
        var doc = span[(HeaderSize + keyLen)..length].ToArray();
        return new Frame(kind, id, doc, offset, length);
    }
}
