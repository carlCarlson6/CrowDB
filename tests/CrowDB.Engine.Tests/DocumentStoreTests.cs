namespace CrowDB.Engine.Tests;

/// <summary>
/// Point lookups and last-write-wins, without reopening the store.
///
/// Unskip each test as the engine gets implemented — see docs/002-step-1-plan.md.
/// </summary>
public class DocumentStoreTests
{
    [Fact(Skip = "Step 1: implement DocumentStore, then remove this Skip.")]
    public void Put_then_get_round_trips()
    {
        // Put a document under "user:1" and read it back in the same session:
        // the bytes handed out must be exactly the bytes that went in.
    }

    [Fact(Skip = "Step 1: implement DocumentStore, then remove this Skip.")]
    public void Overwrite_wins()
    {
        // Put "user:1" twice, read it back: the second document wins, and
        // ListIds() still reports exactly one entry for the id.
    }
}
