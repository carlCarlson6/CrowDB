using System.Buffers.Binary;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// The reading half of step 4 (docs/004 §3): frames are validated — magic,
/// version, kind, length caps, CRC32 — and recovery keeps the log's valid
/// prefix. The first invalid frame ends replay and everything from it on is
/// truncated, so nothing can ever be appended behind garbage. These tests
/// hand-craft the bytes that a torn write or bit rot would leave behind.
/// </summary>
public class BinaryFormatTests(ITestOutputHelper output)
{
    [Fact]
    public void GIVEN_the_standard_CRC32_check_value_WHEN_hashing_123456789_THEN_it_matches()
    {
        // CRC-32/ISO-HDLC (the zlib variant): a wrong polynomial or orientation
        // changes this value — and the golden frames in DiskFormatTests with it.
        Assert.Equal(0xCBF43926u, FrameBytes.Crc32("123456789"u8));
    }

    [Fact]
    public void GIVEN_a_frame_with_an_unknown_version_WHEN_opening_THEN_replay_stops_there_and_the_rest_is_truncated()
    {
        using var temp = new TempDirectory();
        var ada = Bytes("""{"name":"Ada"}""");

        var alien = FrameBytes.Encode("user:2", Bytes("""{"version":9}"""));
        alien[2] = 9; // version is outside the CRC scope, so only the version is at fault

        var log = FrameBytes.Encode("user:1", ada)
            .Concat(alien)
            .Concat(FrameBytes.Encode("user:3", Bytes("""{"name":"Linus"}""")))
            .ToArray();
        File.WriteAllBytes(temp.LogFile, log);
        var firstFrameLength = FrameBytes.ParseAll(log).First().Length;

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(ada, Read(store, "user:1"));
            Assert.False(store.TryGet("user:2", out _)); // the unknown version is not read as v2
            Assert.False(store.TryGet("user:3", out _)); // nor is anything after it
            Assert.Equal(new[] { "user:1" }, store.ListIds());
        }

        Assert.Equal(firstFrameLength, new FileInfo(temp.LogFile).Length);

        // The truncated tail must not poison what comes next.
        using (var store = OpenStore(temp.LogFile, output))
            store.Put("user:4", Bytes("""{"name":"Margaret"}"""));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(ada, Read(reopened, "user:1"));
            Assert.Equal(Bytes("""{"name":"Margaret"}"""), Read(reopened, "user:4"));
        }
    }

    [Fact]
    public void GIVEN_a_frame_with_an_unknown_kind_WHEN_opening_THEN_replay_stops_there()
    {
        using var temp = new TempDirectory();

        var alien = FrameBytes.Encode("user:2", Bytes("""{"version":9}"""), kind: 0x7F);
        BinaryPrimitives.WriteUInt32LittleEndian(alien.AsSpan(3), FrameBytes.Crc32(alien.AsSpan(7))); // keep the CRC honest: only the kind is at fault

        File.WriteAllBytes(temp.LogFile, FrameBytes.Encode("user:1", Bytes("""{"name":"Ada"}"""))
            .Concat(alien)
            .Concat(FrameBytes.Encode("user:3", Bytes("""{"name":"Linus"}""")))
            .ToArray());

        using var store = OpenStore(temp.LogFile, output);

        Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(store, "user:1"));
        Assert.False(store.TryGet("user:2", out _));
        Assert.False(store.TryGet("user:3", out _));
        Assert.Equal(new[] { "user:1" }, store.ListIds());
    }

    [Fact]
    public void GIVEN_a_DELETE_frame_that_carries_a_document_WHEN_opening_THEN_the_frame_is_invalid_and_replay_stops()
    {
        // A shape the writer cannot produce: a delete with a document. The
        // reader accepts exactly what the writer can write — fail closed.
        using var temp = new TempDirectory();

        File.WriteAllBytes(temp.LogFile, FrameBytes.Encode("user:1", Bytes("""{"name":"Ada"}"""))
            .Concat(FrameBytes.Encode("user:2", Bytes("""{"why":"not empty"}"""), FrameBytes.KindDelete))
            .Concat(FrameBytes.Encode("user:3", Bytes("""{"name":"Linus"}""")))
            .ToArray());

        using var store = OpenStore(temp.LogFile, output);

        Assert.Equal(new[] { "user:1" }, store.ListIds());
        Assert.False(store.TryGet("user:2", out _));
        Assert.False(store.TryGet("user:3", out _));
    }

    [Fact]
    public void GIVEN_a_bit_flip_in_the_newest_frame_WHEN_opening_THEN_the_CRC_catches_it_and_the_previous_value_survives()
    {
        using var temp = new TempDirectory();
        var version1 = Bytes("""{"version":1}""");
        var version2 = Bytes("""{"version":2}""");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", version1);
            store.Put("user:1", version2);
        }

        var valid = File.ReadAllBytes(temp.LogFile);
        var frames = FrameBytes.ParseAll(valid);
        var mutated = (byte[])valid.Clone();
        mutated[^1] ^= 0xFF; // one flipped bit inside the newest document
        File.WriteAllBytes(temp.LogFile, mutated);

        using (var store = OpenStore(temp.LogFile, output))
        {
            // The corrupt frame is rejected, not returned as garbage: the older
            // value stands — and the corrupt bytes are truncated, not kept.
            Assert.Equal(version1, Read(store, "user:1"));
        }

        Assert.Equal(frames[0].Length, new FileInfo(temp.LogFile).Length);
    }

    [Fact]
    public void GIVEN_a_bit_flip_in_a_middle_frame_WHEN_opening_THEN_replay_stops_at_the_bad_frame_and_later_records_are_not_visible()
    {
        // Bit rot is not a torn tail: it can hit any frame. The bytes after a
        // bad frame cannot even be trusted to be frame boundaries — there is no
        // delimiter to resync on, so the later records go with it (docs/004 §3).
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Put("user:3", Bytes("""{"name":"Linus"}"""));
        }

        var valid = File.ReadAllBytes(temp.LogFile);
        var frames = FrameBytes.ParseAll(valid);
        var mutated = (byte[])valid.Clone();
        mutated[frames[1].Offset + frames[1].Length - 1] ^= 0xFF; // last byte of user:2's document
        File.WriteAllBytes(temp.LogFile, mutated);

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(store, "user:1"));
            Assert.False(store.TryGet("user:2", out _));
            Assert.False(store.TryGet("user:3", out _));
            Assert.Equal(new[] { "user:1" }, store.ListIds());
        }

        Assert.Equal(frames[1].Offset, new FileInfo(temp.LogFile).Length); // truncated at the bad frame

        // The store remains writable — at the end of what survived.
        using (var store = OpenStore(temp.LogFile, output))
            store.Put("user:4", Bytes("""{"name":"Margaret"}"""));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            // Ordering is not guaranteed (hash index) — compare as sorted sequences.
            Assert.Equal(new[] { "user:1", "user:4" }, reopened.ListIds().Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void GIVEN_a_truncated_final_frame_WHEN_opening_THEN_the_valid_prefix_is_kept_and_the_tail_is_dropped()
    {
        using var temp = new TempDirectory();
        var ada = Bytes("""{"name":"Ada"}""");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", ada);
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
        }

        var valid = File.ReadAllBytes(temp.LogFile);
        var firstFrameLength = FrameBytes.ParseAll(valid).First().Length;
        temp.TruncateTail(3); // the crash happened mid-append

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(ada, Read(store, "user:1"));
            Assert.False(store.TryGet("user:2", out _));
        }

        Assert.Equal(firstFrameLength, new FileInfo(temp.LogFile).Length);

        using (var store = OpenStore(temp.LogFile, output))
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(reopened, "user:2"));
        }
    }

    [Fact]
    public void GIVEN_a_header_claiming_an_absurd_document_length_WHEN_opening_THEN_the_frame_is_rejected_before_its_payload_is_trusted()
    {
        using var temp = new TempDirectory();
        var ada = Bytes("""{"name":"Ada"}""");

        // A corrupt header is refused up front — a four-gigabyte docLen is never
        // believed, and docs/004 §2 caps documents at 16 MiB.
        var crafted = new byte[FrameBytes.HeaderSize + 1];
        crafted[0] = 0x43;
        crafted[1] = 0x44;
        crafted[2] = 0x02;
        crafted[7] = FrameBytes.KindPut;
        BinaryPrimitives.WriteUInt16LittleEndian(crafted.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(crafted.AsSpan(10), uint.MaxValue);
        crafted[14] = (byte)'x';

        var good = FrameBytes.Encode("user:1", ada);
        File.WriteAllBytes(temp.LogFile, good.Concat(crafted).ToArray());

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(ada, Read(store, "user:1"));
            Assert.Equal(new[] { "user:1" }, store.ListIds());
        }

        Assert.Equal(good.Length, new FileInfo(temp.LogFile).Length);

        using (var store = OpenStore(temp.LogFile, output))
            store.Put("ok", Bytes("""{"ok":true}"""));

        using (var reopened = OpenStore(temp.LogFile, output))
            Assert.Equal(Bytes("""{"ok":true}"""), Read(reopened, "ok"));
    }

    [Fact]
    public void GIVEN_a_torn_first_write_WHEN_opening_THEN_the_store_is_empty_and_the_fragment_is_truncated()
    {
        // A crash during the very first append can leave any prefix of a frame.
        // Both flavors open as an empty store: the fragment is proof of a torn
        // write, and truncation makes byte 0 writable again.
        using (var temp = new TempDirectory())
        {
            File.WriteAllBytes(temp.LogFile, FrameBytes.Encode("user:1", Bytes("""{"name":"Ada"}""")).AsSpan(0, 5).ToArray());

            using (var store = OpenStore(temp.LogFile, output))
            {
                Assert.Empty(store.ListIds());
                Assert.Equal(0, new FileInfo(temp.LogFile).Length);

                store.Put("user:1", Bytes("""{"name":"Ada"}"""));
                Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(store, "user:1"));
            }
        }

        using (var temp = new TempDirectory())
        {
            File.WriteAllBytes(temp.LogFile, [FrameBytes.Magic0]); // one lone byte of the magic

            using var store = OpenStore(temp.LogFile, output);

            Assert.Empty(store.ListIds());
            Assert.Equal(0, new FileInfo(temp.LogFile).Length);
        }
    }

    [Fact]
    public void GIVEN_a_log_from_an_unknown_future_format_WHEN_opening_THEN_the_store_refuses_it_and_leaves_the_bytes_untouched()
    {
        // "CD" plus a version this engine has never heard of: maybe a file from
        // the future, maybe a mangled first frame — at offset 0 the two cannot
        // be told apart, and truncating a revision we did not write would be
        // data destruction, not recovery. Refuse. (An unknown version *deeper*
        // in a valid v2 log is ordinary corruption and follows the valid-prefix
        // policy instead — see the test above.)
        using var temp = new TempDirectory();
        var bytes = new byte[] { 0x43, 0x44, 0x09, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70 };
        File.WriteAllBytes(temp.LogFile, bytes);

        Assert.Throws<InvalidDataException>(() => OpenStore(temp.LogFile, output));

        Assert.Equal(bytes, File.ReadAllBytes(temp.LogFile));
    }

    [Fact]
    public void GIVEN_a_file_that_is_not_a_CrowDB_log_WHEN_opening_THEN_the_store_refuses_it_and_leaves_it_untouched()
    {
        // The magic number's whole job: fail cleanly on foreign input instead of
        // guessing — and never eat a file that is not ours.
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, "this is not a CrowDB log");

        Assert.Throws<InvalidDataException>(() => OpenStore(temp.LogFile, output));

        Assert.Equal("this is not a CrowDB log", File.ReadAllText(temp.LogFile));
    }
}
