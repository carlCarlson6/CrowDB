namespace CrowDB.Engine.Tests;

/// <summary>
/// Recovery is the first-class lesson of step 1: the log is the source of
/// truth, the index is derived data, and reopening the store must rebuild the
/// index by replaying the log.
///
/// Unskip each test as the engine gets implemented — see docs/002-step-1-plan.md.
/// </summary>
public class RecoveryTests
{
    [Fact(Skip = "Step 1: implement DocumentStore, then remove this Skip.")]
    public void Value_survives_reopen()
    {
        // 1. Put a document for "user:1" into a fresh temporary directory.
        // 2. Dispose the store (close the log file).
        // 3. Open a new store on the same directory.
        // 4. TryGet("user:1"): same id, same bytes.
    }

    [Fact(Skip = "Step 1: implement DocumentStore, then remove this Skip.")]
    public void Torn_tail_is_ignored()
    {
        // 1. Put two documents, then dispose the store.
        // 2. Append garbage / a truncated record directly to crowdb.log
        //    (a torn write can only live at the tail).
        // 3. Open a new store: both documents are still readable and the
        //    store still accepts new writes.
    }
}
