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
}
