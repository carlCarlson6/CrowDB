using System.Buffers.Binary;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// The on-disk contract of v2 (docs/004 §2): one <c>crowdb.log</c> holding
/// concatenated binary frames — declared lengths, a CRC32 per frame, no
/// separators, documents as raw bytes. The log is the source of truth, so
/// these tests read it directly. v1 bytes (JSONL) appear only as legacy input
/// to migrate — see <see cref="MigrationTests"/>.
/// </summary>
public class DiskFormatTests(ITestOutputHelper output)
{
    // docs/004 §2 golden bytes: magic "CD" | version 2 | crc32 | kind | keyLen | docLen | key | doc.
    // This literal is what fixes field order, endianness, and the CRC scope and
    // variant (CRC-32/ISO-HDLC) — a "known frame" any reader must agree with.
    private static readonly byte[] PutFrame =
    [
        0x43, 0x44,                                     // magic "CD"
        0x02,                                           // version 2
        0x63, 0x34, 0x4B, 0x43,                         // crc32 = 0x434B3463, little-endian
        0x00,                                           // kind: PUT (0)
        0x06, 0x00,                                     // keyLen = 6
        0x0E, 0x00, 0x00, 0x00,                         // docLen = 14
        0x75, 0x73, 0x65, 0x72, 0x3A, 0x31,             // "user:1"
        0x7B, 0x22, 0x6E, 0x61, 0x6D, 0x65, 0x22, 0x3A, // {"name":"
        0x22, 0x41, 0x64, 0x61, 0x22, 0x7D,             // Ada"}
    ];

    private static readonly byte[] DeleteFrame =
    [
        0x43, 0x44,             // magic "CD"
        0x02,                   // version 2
        0xC6, 0x59, 0xB8, 0xF7, // crc32 = 0xF7B859C6, little-endian
        0x01,                   // kind: DELETE (1)
        0x06, 0x00,             // keyLen = 6
        0x00, 0x00, 0x00, 0x00, // docLen = 0 — a tombstone carries no document
        0x75, 0x73, 0x65, 0x72, 0x3A, 0x31, // "user:1"
    ];

    [Fact]
    public void GIVEN_two_puts_WHEN_inspecting_the_log_THEN_each_record_is_one_v2_frame()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
        }

        var frames = FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile));
        Assert.Equal(2, frames.Count);

        Assert.Equal(0, frames[0].Offset); // the first frame starts at byte 0 — no file header
        Assert.Equal(FrameBytes.KindPut, frames[0].Kind);
        Assert.Equal("user:1", frames[0].Id);
        Assert.Equal(Bytes("""{"name":"Ada"}"""), frames[0].Doc);

        // Frames are concatenated with no separators: the next frame begins
        // where the previous one's declared length ends.
        Assert.Equal(frames[0].Length, frames[1].Offset);
        Assert.Equal("user:2", frames[1].Id);
    }

    [Fact]
    public void GIVEN_a_known_put_WHEN_inspecting_the_log_THEN_the_bytes_match_the_documented_frame()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
            store.Put("user:1", Bytes("""{"name":"Ada"}""")); // id 6 bytes, doc 14 bytes

        Assert.Equal(PutFrame, File.ReadAllBytes(temp.LogFile));
    }

    [Fact]
    public void GIVEN_a_delete_WHEN_inspecting_the_log_THEN_the_tombstone_is_a_DELETE_frame_with_no_document()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Delete("user:1");
        }

        var log = File.ReadAllBytes(temp.LogFile);

        // The second frame is the tombstone — and it is byte-exactly the
        // documented shape: kind DELETE, docLen 0, the id and nothing else.
        Assert.Equal(DeleteFrame, log.AsSpan(PutFrame.Length).ToArray());

        // The v1 sentinel is retired: deletion is a kind, not a reserved document.
        Assert.Equal(-1, log.AsSpan().IndexOf("--DELETED--"u8));
    }

    [Fact]
    public void GIVEN_an_overwrite_WHEN_inspecting_the_log_THEN_the_dead_frame_stays_and_a_newer_one_is_appended()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Put("user:1", Bytes("""{"version":2}"""));
        }

        var frames = FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile));
        Assert.Equal(2, frames.Count);
        Assert.Equal(Bytes("""{"version":1}"""), frames[0].Doc); // dead, never modified
        Assert.Equal(Bytes("""{"version":2}"""), frames[1].Doc); // appended after it
        Assert.Equal(frames[0].Length, frames[1].Offset);

        using var reopened = OpenStore(temp.LogFile, output);
        Assert.Equal(Bytes("""{"version":2}"""), Read(reopened, "user:1"));
    }

    [Fact]
    public void GIVEN_document_bytes_that_mimic_framing_WHEN_round_tripping_THEN_the_bytes_are_exact_and_boundaries_come_from_lengths()
    {
        // The document contains the magic bytes, a version byte, raw newlines,
        // NULs and invalid UTF-8 — everything a line format would trip over.
        var tricky = new byte[] { 0x43, 0x44, 0x02, 0x00, 0xFF, 0x0A, 0x7B, 0x7D, 0x00, (byte)'C' };
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("frame:mimic", tricky);
            store.Put("after", Bytes("""{"next":true}"""));
        }

        // Replay reads by declared lengths, never by scanning for the magic.
        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(tricky, Read(reopened, "frame:mimic"));
            Assert.Equal(Bytes("""{"next":true}"""), Read(reopened, "after"));
        }

        var frames = FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile));
        Assert.Equal(2, frames.Count);
        Assert.Equal(tricky, frames[0].Doc);
    }

    [Fact]
    public void GIVEN_a_hand_written_v2_log_WHEN_opening_THEN_the_store_replays_frames_it_did_not_write()
    {
        using var temp = new TempDirectory();

        var log = FrameBytes.Encode("user:1", Bytes("""{"name":"Ada"}"""))
            .Concat(FrameBytes.Encode("user:1", Bytes("""{"name":"Ada Lovelace"}""")))
            .Concat(FrameBytes.EncodeTombstone("user:1"))
            .Concat(FrameBytes.Encode("user:2", Bytes("""{"name":"Grace"}""")))
            .ToArray();
        File.WriteAllBytes(temp.LogFile, log);

        using var store = OpenStore(temp.LogFile, output);

        Assert.False(store.TryGet("user:1", out _)); // the newest record for user:1 is a tombstone
        Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
        Assert.Equal(new[] { "user:2" }, store.ListIds());
    }

    [Fact]
    public void GIVEN_a_document_over_the_length_cap_WHEN_putting_THEN_it_is_rejected_and_the_store_stays_usable()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);
        var tooBig = new byte[FrameBytes.MaxDocLength + 1];

        Assert.Throws<ArgumentException>(() => store.Put("big", tooBig));

        // Nothing landed: the store still answers and still accepts normal writes.
        store.Put("ok", Bytes("""{"ok":true}"""));
        Assert.Equal(Bytes("""{"ok":true}"""), Read(store, "ok"));
    }

    [Fact]
    public void GIVEN_an_id_over_the_key_length_cap_WHEN_putting_THEN_it_is_rejected()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);
        var tooLong = new string('x', ushort.MaxValue + 1);

        Assert.Throws<ArgumentException>(() => store.Put(tooLong, Bytes("""{"ok":true}""")));
        Assert.Empty(store.ListIds());
    }

    [Fact]
    public void GIVEN_overwritten_records_WHEN_compacting_THEN_the_log_holds_exactly_the_live_frames_in_log_order()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Put("user:1", Bytes("""{"version":2}""")); // supersedes frame 0 — dead
            store.Compact();
        }

        var log = File.ReadAllBytes(temp.LogFile);
        var frames = FrameBytes.ParseAll(log);

        // One frame per live record; the dead one is physically gone.
        Assert.Equal(2, frames.Count);
        Assert.Equal(-1, log.AsSpan().IndexOf("""{"version":1}"""u8));

        // Record order follows the log, not the index's dictionary order:
        // user:2's frame was written before user:1's newest one. Bytes are verbatim.
        Assert.Equal("user:2", frames[0].Id);
        Assert.Equal(Bytes("""{"name":"Grace"}"""), frames[0].Doc);
        Assert.Equal("user:1", frames[1].Id);
        Assert.Equal(Bytes("""{"version":2}"""), frames[1].Doc);
    }
}
