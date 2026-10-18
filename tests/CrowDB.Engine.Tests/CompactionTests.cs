using System.Collections.Concurrent;
using System.Text;
using CrowDB.Engine;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// Step 3: compaction — rewriting the log as only its live records, then
/// swapping the new file in with one atomic rename. The index is the compaction
/// plan: everything it points at is copied verbatim; dead puts and tombstones
/// are reclaimed. (Torn tails are reclaimed even earlier, by recovery at open —
/// step 4, docs/004 §3.) Nothing here may change what the store answers — only
/// how many bytes it takes to answer.
///
/// File *contents* are inspected only while the store is closed: it holds the
/// log open with <c>FileShare.None</c>, so a second reader is refused on purpose
/// (<c>FileInfo.Length</c> is a stat and is fine at any time). Since step 4 the
/// log is binary frames, so the tests parse frames rather than read text.
/// </summary>
public class CompactionTests(ITestOutputHelper output)
{
    [Fact]
    public void GIVEN_overwritten_documents_WHEN_compacting_THEN_dead_bytes_are_reclaimed_and_every_live_document_survives()
    {
        using var temp = new TempDirectory();
        var ada2 = Bytes("""{"name":"Ada Lovelace","note":"ü é 🚀"}""");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Put("user:1", ada2); // supersedes the first record — dead bytes
            var sizeBefore = new FileInfo(temp.LogFile).Length;

            store.Compact();

            Assert.True(new FileInfo(temp.LogFile).Length < sizeBefore,
                "the overwritten record should be reclaimed");
            Assert.Equal(ada2, Read(store, "user:1"));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
            Assert.Equal(2, store.ListIds().Count);
        }

        var log = File.ReadAllBytes(temp.LogFile);
        var frames = FrameBytes.ParseAll(log);
        Assert.Equal(2, frames.Count);
        Assert.Equal(-1, log.AsSpan().IndexOf("""{"name":"Ada"}"""u8)); // the dead frame is physically gone
        Assert.Equal("user:2", frames[0].Id);                             // copied in log order, not dictionary order
        Assert.Equal(ada2, frames[1].Doc);                                // …and verbatim
        Assert.Equal("user:1", frames[1].Id);

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(ada2, Read(reopened, "user:1"));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(reopened, "user:2"));
            Assert.Equal(2, reopened.ListIds().Count);
        }
    }

    [Fact]
    public void GIVEN_a_deleted_document_WHEN_compacting_THEN_the_dead_put_and_the_tombstone_are_reclaimed_and_it_stays_deleted()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Delete("user:1");
            var sizeBefore = new FileInfo(temp.LogFile).Length;

            store.Compact();

            Assert.True(new FileInfo(temp.LogFile).Length < sizeBefore,
                "the dead put and the tombstone should be reclaimed");
            Assert.False(store.TryGet("user:1", out _));
            Assert.Equal(new[] { "user:2" }, store.ListIds());
        }

        // Closed store: the log can be inspected — the deleted document's bytes
        // and its tombstone are physically gone, not merely ignored.
        var log = File.ReadAllBytes(temp.LogFile);
        var frames = FrameBytes.ParseAll(log);
        Assert.Single(frames);
        Assert.Equal("user:2", frames[0].Id);
        Assert.Equal(-1, log.AsSpan().IndexOf("Ada"u8));
        Assert.DoesNotContain(FrameBytes.KindDelete, frames.Select(f => f.Kind));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.False(reopened.TryGet("user:1", out _));
            Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(reopened, "user:2"));
            Assert.Equal(new[] { "user:2" }, reopened.ListIds());
        }
    }

    [Fact]
    public void GIVEN_a_deleted_then_re_put_id_WHEN_compacting_THEN_only_the_newest_record_remains()
    {
        using var temp = new TempDirectory();
        var version2 = Bytes("""{"version":2}""");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Delete("user:1");
            store.Put("user:1", version2);

            store.Compact();

            Assert.Equal(version2, Read(store, "user:1"));
        }

        var log = File.ReadAllBytes(temp.LogFile);
        var frames = FrameBytes.ParseAll(log);
        Assert.Single(frames);
        Assert.Equal("user:1", frames[0].Id);
        Assert.Equal(version2, frames[0].Doc);

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(version2, Read(reopened, "user:1"));
        }
    }

    [Fact]
    public void GIVEN_a_log_with_nothing_to_reclaim_WHEN_compacting_THEN_the_bytes_are_unchanged()
    {
        // Live records are copied in log order, verbatim: with no dead bytes the
        // output is byte-for-byte the input, and doing it twice is a no-op.
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
        }

        var before = File.ReadAllBytes(temp.LogFile);

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Compact();
        }

        Assert.Equal(before, File.ReadAllBytes(temp.LogFile));

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Compact(); // idempotent: compaction of a compacted log is a no-op
        }

        Assert.Equal(before, File.ReadAllBytes(temp.LogFile));
    }

    [Fact]
    public void GIVEN_an_empty_store_WHEN_compacting_THEN_it_stays_empty_and_accepts_writes()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Compact();

            Assert.Empty(store.ListIds());
            Assert.False(store.TryGet("missing", out _));
            Assert.Equal(0, new FileInfo(temp.LogFile).Length);

            store.Put("user:1", Bytes("""{"ok":true}"""));
            Assert.Equal(Bytes("""{"ok":true}"""), Read(store, "user:1"));
        }

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(Bytes("""{"ok":true}"""), Read(reopened, "user:1"));
        }
    }

    [Fact]
    public void GIVEN_a_compacted_log_WHEN_writing_and_deleting_afterwards_THEN_the_new_records_land_correctly()
    {
        using var temp = new TempDirectory();
        var grace = Bytes("""{"name":"Grace"}""");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Put("user:2", grace);
            store.Compact();

            // The index was rebuilt at the new offsets: a delete right after
            // compaction must find the id, and new appends must land in the
            // compacted file.
            Assert.True(store.Delete("user:1"));
            store.Put("user:3", Bytes("""{"name":"Linus"}"""));

            Assert.False(store.TryGet("user:1", out _));
            Assert.Equal(grace, Read(store, "user:2"));
            Assert.Equal(Bytes("""{"name":"Linus"}"""), Read(store, "user:3"));
        }

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.False(reopened.TryGet("user:1", out _));
            Assert.Equal(grace, Read(reopened, "user:2"));
            Assert.Equal(Bytes("""{"name":"Linus"}"""), Read(reopened, "user:3"));
        }
    }

    [Fact]
    public void GIVEN_a_leftover_temp_file_from_a_crashed_compaction_WHEN_compacting_THEN_it_is_overwritten_and_the_result_is_correct()
    {
        using var temp = new TempDirectory();
        var version2 = Bytes("""{"version":2}""");

        // What a crash mid-compaction leaves behind: a partial <log>.compact
        // beside an untouched log. Opening must ignore it; the next compaction
        // must overwrite it.
        File.WriteAllText(temp.LogFile + ".compact", "garbage from a crashed compaction");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Put("user:1", version2);

            store.Compact();

            Assert.Equal(version2, Read(store, "user:1"));
            Assert.False(File.Exists(temp.LogFile + ".compact"), "the rename consumes the temp file");
        }

        var log = File.ReadAllBytes(temp.LogFile);
        Assert.Equal(-1, log.AsSpan().IndexOf("garbage"u8));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(version2, Read(reopened, "user:1"));
        }
    }

    [Fact]
    public void GIVEN_a_torn_tail_WHEN_opening_THEN_recovery_truncates_it_and_compaction_sees_only_frames()
    {
        // Step 4 moved tail reclamation from compaction to recovery: opening a
        // log whose last frame is torn keeps the valid prefix and truncates the
        // rest (docs/004 §3). By the time Compact() runs there is no garbage
        // left — only the dead records it was already reclaiming.
        using var temp = new TempDirectory();
        var ada = Bytes("""{"name":"Ada"}""");

        using (var store = OpenStore(temp.LogFile, output))
        {
            store.Put("user:1", ada);
        }

        // A crash mid-append: valid prefix, then a frame that never finished.
        temp.AppendRaw(FrameBytes.Encode("user:2", Bytes("""{"name":"torn"}""")).AsSpan(0, 11).ToArray());
        var sizeBefore = new FileInfo(temp.LogFile).Length;

        using (var store = OpenStore(temp.LogFile, output))
        {
            Assert.True(new FileInfo(temp.LogFile).Length < sizeBefore,
                "recovery should have reclaimed the torn tail at open");

            store.Compact();

            Assert.Equal(ada, Read(store, "user:1"));
        }

        var log = File.ReadAllBytes(temp.LogFile);
        Assert.Equal(-1, log.AsSpan().IndexOf("torn"u8));
        Assert.Single(FrameBytes.ParseAll(log));

        using (var reopened = OpenStore(temp.LogFile, output))
        {
            Assert.Equal(ada, Read(reopened, "user:1"));
            Assert.Equal(new[] { "user:1" }, reopened.ListIds());
        }
    }

    [Fact]
    public async Task GIVEN_writers_readers_and_a_compactor_running_at_the_same_time_WHEN_they_finish_THEN_the_state_is_exact_and_nothing_threw()
    {
        // The first concurrency question (docs/003 §4.5): a compaction swaps the
        // file handle and every offset while readers and writers are in flight.
        // One lock for all operations is the answer this step expects.
        using var temp = new TempDirectory();
        var store = OpenStore(temp.LogFile, output);

        var ids = Enumerable.Range(0, 6).Select(i => $"doc:{i}").ToArray();
        foreach (var id in ids)
            store.Put(id, Doc(id, version: 0));

        var failures = new ConcurrentQueue<Exception>();
        using var gate = new ManualResetEventSlim();
        var tasks = new List<Task>();

        foreach (var id in ids)
        {
            var local = id;
            tasks.Add(Task.Run(() =>
            {
                gate.Wait();
                try
                {
                    for (var version = 1; version <= 10; version++)
                        store.Put(local, Doc(local, version));
                }
                catch (Exception e)
                {
                    failures.Enqueue(e);
                }
            }));
        }

        tasks.Add(Task.Run(() =>
        {
            gate.Wait();
            try
            {
                for (var i = 0; i < 50; i++)
                {
                    foreach (var id in ids)
                    {
                        if (store.TryGet(id, out var document))
                            Assert.StartsWith($$"""{"id":"{{id}}",""", Encoding.UTF8.GetString(document.Span));
                        else
                            Assert.Fail($"'{id}' disappeared while a compaction was running");
                    }
                }
            }
            catch (Exception e)
            {
                failures.Enqueue(e);
            }
        }));

        tasks.Add(Task.Run(() =>
        {
            gate.Wait();
            try
            {
                for (var i = 0; i < 2; i++)
                    store.Compact();
            }
            catch (Exception e)
            {
                failures.Enqueue(e);
            }
        }));

        gate.Set();
        await Task.WhenAll(tasks);

        Assert.Empty(failures);
        foreach (var id in ids)
            Assert.Equal(Doc(id, version: 10), Read(store, id)); // each id has one writer: v=10 is final
        Assert.Equal(ids.Length, store.ListIds().Count);

        store.Dispose();
        
        using (var reopened = OpenStore(temp.LogFile, output))
        {
            foreach (var id in ids)
                Assert.Equal(Doc(id, version: 10), Read(reopened, id));
        }
    }

    /// <summary>A document that carries its own id, so a reader can prove it got the right record.</summary>
    private static byte[] Doc(string id, int version) => Bytes($$"""{"id":"{{id}}","v":{{version}}}""");
}
