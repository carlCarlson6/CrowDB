using CrowDB.Engine;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// The step-1 lesson: the log is the source of truth and the index is derived
/// data. Every test here disposes the store and opens a new one on the same
/// directory — whatever the store knew in RAM is thrown away, and recovery
/// must rebuild it by replaying the log.
/// </summary>
public class RecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void GIVEN_a_stored_document_WHEN_the_store_is_reopened_THEN_the_document_survives()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
        }

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(reopened, "user:1"));
        }
    }

    [Fact]
    public void GIVEN_several_documents_WHEN_reopening_THEN_all_ids_are_listed()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Put("user:3", Bytes("""{"name":"Linus"}"""));
        }

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(3, reopened.ListIds().Count);
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(reopened, "user:2"));
        }
    }

    [Fact]
    public void GIVEN_an_overwritten_document_WHEN_reopening_THEN_the_last_write_wins()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Put("user:1", Bytes("""{"version":2}"""));
            store.Put("user:1", Bytes("""{"version":3}"""));
        }

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            // Replay applies records in log order, so the newest record wins.
            Assert.Equal(Bytes("""{"version":3}"""), Read(reopened, "user:1"));
            Assert.Single(reopened.ListIds());
        }
    }

    [Fact]
    public void GIVEN_a_torn_tail_WHEN_reopening_THEN_the_records_before_it_are_readable()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
        }

        // A crash mid-append leaves a partial record — and only ever at the tail.
        temp.AppendRaw(Bytes("\n"+"""{"v":1,"id":"user:3","doc":{"name":"torn"""));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(reopened, "user:1"));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(reopened, "user:2"));
            Assert.DoesNotContain("user:3", reopened.ListIds());
        }
    }

    [Fact]
    public void GIVEN_a_torn_tail_WHEN_writing_after_recovery_and_reopening_again_THEN_the_new_write_survives()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
        }

        temp.AppendRaw(Bytes("\n"+"""{"v":1,"id":"user:2","doc":{"name":"torn\n"""));

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:3", Bytes("""{"name":"Linus"}"""));
        }

        // The torn tail must not poison later writes: the log's invariant is
        // "valid prefix, then at most one partial record", so the store must
        // either truncate the tail at open or overwrite it — never append
        // behind the garbage, or this write would be skipped on the next replay.
        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(reopened, "user:1"));
            Assert.Equal(Bytes("""{"name":"Linus"}"""), Read(reopened, "user:3"));
        }
    }

    [Fact]
    public void GIVEN_a_deleted_document_WHEN_reopening_THEN_it_stays_deleted()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Delete("user:1");
        }

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.False(reopened.TryGet("user:1", out _));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(reopened, "user:2"));
            Assert.Equal(new[] { "user:2" }, reopened.ListIds());
        }
    }

    [Fact]
    public void GIVEN_a_deleted_then_re_put_document_WHEN_reopening_THEN_the_newer_document_is_back()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Delete("user:1");
            store.Put("user:1", Bytes("""{"version":2}"""));
        }

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            // The newest record for the id is a put — replay resurrects it.
            Assert.Equal(Bytes("""{"version":2}"""), Read(reopened, "user:1"));
            Assert.Contains("user:1", reopened.ListIds());
        }
    }

    [Fact]
    public void GIVEN_a_torn_tombstone_tail_WHEN_reopening_THEN_the_document_is_still_readable()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
        }

        // A crash mid-delete: the tombstone never completed. The valid-prefix
        // rule is unchanged — an unparseable tombstone must not delete anything.
        temp.AppendRaw(Bytes("\n" + """{"Version":"1","Id":"user:1","Doc":nul"""));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"name":"Ada"}"""), Read(reopened, "user:1"));
            Assert.Contains("user:1", reopened.ListIds());
        }
    }

    [Fact]
    public void GIVEN_an_id_with_a_newline_WHEN_deleting_and_reopening_THEN_the_id_stays_deleted()
    {
        const string id = "line\nbreak";
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put(id, Bytes("""{"ok":true}"""));
            store.Delete(id);
        }

        // The tombstone's id is escaped into its envelope like any other record:
        // the newline must never split it into two unparseable lines.
        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.False(reopened.TryGet(id, out _));
            Assert.Empty(reopened.ListIds());
        }
    }
}
