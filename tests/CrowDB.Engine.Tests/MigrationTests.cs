using System.Text.Json;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// Step 4's migration (docs/000 §4.6, docs/004 §4.5): a log whose first byte is
/// '{' is v1 JSONL; opening reads it with the frozen v1 reader, rewrites every
/// parseable record as a v2 frame, and swaps the new file in with one atomic
/// rename — the encoding changes, the information does not. Migration is not
/// compaction: dead puts and tombstones are carried over on purpose.
/// </summary>
public class MigrationTests(ITestOutputHelper output)
{
    [Fact]
    public void GIVEN_a_v1_log_WHEN_opening_THEN_the_documents_are_readable_and_the_file_is_v2()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, string.Join('\n', new[]
        {
            """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""",
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
            """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada Lovelace\u0022}"}""",
            """{"Version":"1","Id":"user:2","Doc":"--DELETED--","Kind":"DELETE"}""",
        }));

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada Lovelace"}"""), Read(store, "user:1")); // last write wins
            Assert.False(store.TryGet("user:2", out _));
            Assert.Equal(new[] { "user:1" }, store.ListIds());
        }

        var log = File.ReadAllBytes(temp.LogFile);
        Assert.Equal(FrameBytes.Magic0, log[0]);
        Assert.Equal(FrameBytes.Magic1, log[1]);
        Assert.Equal(-1, log.AsSpan().IndexOf("{\"Version\""u8)); // no v1 text survives
        Assert.Equal(4, FrameBytes.ParseAll(log).Count);
    }

    [Fact]
    public void GIVEN_a_v1_log_WHEN_opening_THEN_every_record_is_carried_over_including_dead_puts_and_tombstones()
    {
        // Migration is not compaction: it converts records, it does not drop
        // them. Reclaiming the dead ones is Compact()'s job — an explicit call,
        // and a separate lesson (step 3 done, docs/003).
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, string.Join('\n', new[]
        {
            """{"Version":"1","Id":"user:1","Doc":"{\u0022version\u0022:1}"}""",
            """{"Version":"1","Id":"user:1","Doc":"{\u0022version\u0022:2}"}""", // supersedes the first
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
            """{"Version":"1","Id":"user:2","Doc":"--DELETED--","Kind":"DELETE"}""",
        }));

        using (OpenStore(temp.LogFile, output))
        {
        }

        var frames = FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile));
        Assert.Equal(4, frames.Count);
        Assert.Equal(Bytes("""{"version":1}"""), frames[0].Doc); // dead, still converted
        Assert.Equal(Bytes("""{"version":2}"""), frames[1].Doc);
        Assert.Equal(FrameBytes.KindDelete, frames[3].Kind);
        Assert.Empty(frames[3].Doc); // the sentinel is not copied — kind is the tombstone now
    }

    [Fact]
    public void GIVEN_a_v1_log_WHEN_opening_it_again_THEN_the_second_open_leaves_the_bytes_unchanged()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""");

        using (OpenStore(temp.LogFile, output))
        {
        }

        var migrated = File.ReadAllBytes(temp.LogFile);

        using (OpenStore(temp.LogFile, output))
        {
        }

        Assert.Equal(migrated, File.ReadAllBytes(temp.LogFile)); // v2 in, v2 out — no rewrite
    }

    [Fact]
    public void GIVEN_a_v1_log_with_multi_byte_document_content_WHEN_migrating_THEN_the_document_bytes_are_exact()
    {
        // v1 stored documents as JSON strings (unicode escapes, quote escapes);
        // migration must hand back the exact UTF-8 bytes step 1 promised.
        var document = """{"note":"ü é 🚀","lines":"a\nb","quote":"\""}""";
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile,
            $$"""{"Version":"1","Id":"user:1","Doc":{{JsonSerializer.Serialize(document)}}}""");

        using var store = OpenStore(temp.LogFile, output);

        Assert.Equal(Bytes(document), Read(store, "user:1"));
    }

    [Fact]
    public void GIVEN_a_v1_log_with_a_torn_tail_WHEN_opening_THEN_the_torn_line_is_dropped_and_the_prefix_migrates()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, string.Join('\n', new[]
        {
            """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""",
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
            """{"Version":"1","Id":"user:3","Doc":"{\u0022name\u0022:tor""", // the crash happened here
        }));

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(store, "user:1"));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
            Assert.False(store.TryGet("user:3", out _));
        }

        var log = File.ReadAllBytes(temp.LogFile);
        Assert.Equal(2, FrameBytes.ParseAll(log).Count);
        Assert.Equal(-1, log.AsSpan().IndexOf("user:3"u8)); // the torn tail was not migrated
    }

    [Fact]
    public void GIVEN_a_v1_log_with_an_unparseable_line_in_the_middle_WHEN_opening_THEN_replay_skips_it_and_keeps_going()
    {
        // The v1 reader's locked rule (docs/001 as-built): unparseable lines are
        // skipped and replay continues. That resync ability is a property of a
        // line format — v2 frames have no delimiter to resync on, which is why
        // v2 stops at the first invalid frame instead (docs/004 §4.4).
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, string.Join('\n', new[]
        {
            """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""",
            "this line is not JSON at all",
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
        }));

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(store, "user:1"));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
        }

        Assert.Equal(2, FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile)).Count);
    }

    [Fact]
    public void GIVEN_a_v1_log_and_a_leftover_migrate_temp_file_WHEN_opening_THEN_the_temp_is_overwritten_and_migration_succeeds()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""");

        // What a crash mid-migration leaves behind: a partial <log>.migrate
        // beside an untouched v1 log. Opening must ignore it; migrating
        // overwrites it, and the rename consumes it.
        File.WriteAllBytes(temp.LogFile + ".migrate", Bytes("garbage from a crashed migration"));

        using (var store = OpenStore(temp.LogFile, output))
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(store, "user:1"));

        Assert.False(File.Exists(temp.LogFile + ".migrate"), "the rename consumes the temp file");
        Assert.Equal(-1, File.ReadAllBytes(temp.LogFile).AsSpan().IndexOf("garbage"u8));
    }

    [Fact]
    public void GIVEN_a_v1_log_WHEN_migrating_THEN_new_writes_land_as_v2_frames_after_the_migrated_ones()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
        }

        var frames = FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile));
        Assert.Equal(2, frames.Count);
        Assert.Equal("user:2", frames[1].Id);
        Assert.Equal(Bytes("""{"name":"Grace"}"""), frames[1].Doc);

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(reopened, "user:1"));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(reopened, "user:2"));
        }
    }

    [Fact]
    public void GIVEN_a_migrated_log_WHEN_compacting_THEN_the_dead_records_are_reclaimed_and_reads_are_unchanged()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, string.Join('\n', new[]
        {
            """{"Version":"1","Id":"user:1","Doc":"{\u0022version\u0022:1}"}""",
            """{"Version":"1","Id":"user:1","Doc":"{\u0022version\u0022:2}"}""",
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
            """{"Version":"1","Id":"user:2","Doc":"--DELETED--","Kind":"DELETE"}""",
        }));

        using (OpenStore(temp.LogFile, output))
        {
        }

        // Closed store: the migrated log can be inspected — migration kept everything.
        Assert.Equal(4, FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile)).Count);

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Compact();

            Assert.Equal(Bytes("""{"version":2}"""), Read(store, "user:1"));
            Assert.False(store.TryGet("user:2", out _));
        }

        var frames = FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile));
        Assert.Single(frames); // …and compaction then reclaimed everything dead
        Assert.Equal("user:1", frames[0].Id);
    }

    [Fact]
    public void GIVEN_a_v1_file_with_no_parseable_record_WHEN_opening_THEN_the_store_is_empty_and_writable()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, """{"Version":"1","Id":"user"""); // torn during the very first write

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Empty(store.ListIds());

            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(store, "user:1"));
        }

        Assert.Single(FrameBytes.ParseAll(File.ReadAllBytes(temp.LogFile)));

        using (var reopened = OpenStore(temp.LogFile, output))
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(reopened, "user:1"));
    }

    [Fact]
    public void GIVEN_a_hand_written_v1_log_with_loose_whitespace_and_member_order_WHEN_opening_THEN_json_is_parsed_and_the_log_migrates()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.LogFile, string.Join('\n', new[]
        {
            """{ "Version":"1", "Id":"user:1", "Doc":"{\u0022name\u0022:\u0022Ada\u0022}" }""",
            """{ "Doc":"{\u0022name\u0022:\u0022Ada Lovelace\u0022}", "Id":"user:1","Version":"1"}""",
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
        }) + "\n");

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada Lovelace"}"""), Read(store, "user:1"));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
            Assert.Equal(2, store.ListIds().Count);
        }

        var log = File.ReadAllBytes(temp.LogFile);
        Assert.Equal(FrameBytes.Magic0, log[0]);
        Assert.Equal(3, FrameBytes.ParseAll(log).Count);
    }
}
