using CrowDB.Engine;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// Step 2: a log cannot remove a record, so deleting appends a tombstone —
/// another record that says "gone" — and the index stops pointing at the id.
/// One session here; RecoveryTests covers what replay gives back.
/// </summary>
public class DeleteTests(ITestOutputHelper output)
{
    [Fact]
    public void GIVEN_a_stored_document_WHEN_deleting_THEN_it_is_unreadable_unlisted_and_the_delete_reports_true()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        store.Put("user:1", Bytes("""{"name":"Ada"}"""));

        Assert.True(store.Delete("user:1"));

        Assert.False(store.TryGet("user:1", out _));
        Assert.Empty(store.ListIds());
    }

    [Fact]
    public void GIVEN_an_unknown_id_WHEN_deleting_THEN_the_delete_reports_false()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        Assert.False(store.Delete("missing"));
        Assert.Empty(store.ListIds());
    }

    [Fact]
    public void GIVEN_a_deleted_id_WHEN_deleting_it_again_THEN_the_second_delete_reports_false()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);
        store.Put("user:1", Bytes("""{"name":"Ada"}"""));
        Assert.True(store.Delete("user:1"));

        Assert.False(store.Delete("user:1"));
    }

    [Fact]
    public void GIVEN_a_deleted_id_WHEN_putting_a_new_document_THEN_the_id_is_back()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);
        store.Put("user:1", Bytes("""{"version":1}"""));
        Assert.True(store.Delete("user:1"));

        store.Put("user:1", Bytes("""{"version":2}"""));

        Assert.Equal(Bytes("""{"version":2}"""), Read(store, "user:1"));
        Assert.Contains("user:1", store.ListIds());
    }

    [Fact]
    public void GIVEN_two_documents_WHEN_deleting_one_THEN_only_that_one_is_affected()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);
        store.Put("user:1", Bytes("""{"name":"Ada"}"""));
        store.Put("user:2", Bytes("""{"name":"Grace"}"""));

        Assert.True(store.Delete("user:1"));

        Assert.False(store.TryGet("user:1", out _));
        Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
        Assert.Equal(new[] { "user:2" }, store.ListIds());
    }
}
