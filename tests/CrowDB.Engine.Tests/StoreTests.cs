using System.Net.Mime;
using System.Text;
using CrowDB.Engine;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// One session, no reopening: point lookups, last-write-wins, listing.
///
/// Every document here is valid JSON, because the HTTP layer guarantees that
/// before calling the engine — the engine's own record envelope is JSON too.
/// </summary>
public class StoreTests(ITestOutputHelper output)
{
    [Fact]
    public void GIVEN_an_empty_store_WHEN_looking_up_an_unknown_id_THEN_TryGet_returns_false()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        Assert.False(store.TryGet("missing", out _));
    }

    [Fact]
    public void GIVEN_a_put_document_WHEN_reading_it_back_THEN_the_exact_bytes_are_returned()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);
        var expected = Bytes("""{"name":"Ada","born":1815}""");

        store.Put("user:1", expected);
        var actual = Read(store, "user:1");

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("""{"text":"first line\nsecond line"}""")]
    [InlineData("""{"quote":"he said \"hi\"","backslash":"c:\\temp"}""")]
    [InlineData("""{"unicode":"ü é 🚀 日本語"}""")]
    [InlineData("""{"nested":{"array":[1,2,{"deep":true}],"missing":null}}""")]
    [InlineData("""{"emptyObject":{},"emptyString":"","zero":0}""")]
    public void GIVEN_tricky_document_content_WHEN_round_tripping_THEN_the_bytes_match(string json)
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        store.Put("tricky", Bytes(json));

        Assert.Equal(Bytes(json), Read(store, "tricky"));
    }

    [Theory]
    [InlineData("user:1")]
    [InlineData("user with spaces")]
    [InlineData("ünïcode-🌍")]
    [InlineData("quote\"inside")]
    [InlineData("line\nbreak")]
    public void GIVEN_an_awkward_id_WHEN_round_tripping_THEN_the_document_is_found(string id)
    {
        // Ids are escaped into the envelope: a newline inside an id must never
        // split one record across two lines.
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        store.Put(id, Bytes("""{"ok":true}"""));

        Assert.Equal(Bytes("""{"ok":true}"""), Read(store, id));
        Assert.Contains(id, store.ListIds());
    }

    [Fact]
    public void GIVEN_an_existing_document_WHEN_putting_a_newer_one_THEN_the_newest_wins()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        store.Put("user:1", Bytes("""{"version":1}"""));
        store.Put("user:1", Bytes("""{"version":2}"""));

        Assert.Equal(Bytes("""{"version":2}"""), Read(store, "user:1"));
    }

    [Fact]
    public void GIVEN_two_documents_WHEN_overwriting_one_THEN_the_other_is_untouched()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        store.Put("user:1", Bytes("""{"name":"Ada"}"""));
        store.Put("user:2", Bytes("""{"name":"Grace"}"""));
        store.Put("user:1", Bytes("""{"name":"Ada Lovelace"}"""));

        Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
    }

    [Fact]
    public void GIVEN_ids_differing_only_by_case_WHEN_round_tripping_THEN_they_stay_distinct()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        store.Put("user:1", Bytes("""{"name":"lower"}"""));
        store.Put("User:1", Bytes("""{"name":"upper"}"""));

        Assert.Equal(Bytes("""{"name":"lower"}"""), Read(store, "user:1"));
        Assert.Equal(Bytes("""{"name":"upper"}"""), Read(store, "User:1"));
        Assert.Equal(2, store.ListIds().Count);
    }

    [Fact]
    public void GIVEN_a_large_document_WHEN_reading_a_document_stored_after_it_THEN_both_round_trip()
    {
        // Big enough to break any fixed-size read buffer.
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        var large = Bytes($$"""{"payload":"{{new string('x', 1_000_000)}}"}""");
        var small = Bytes("""{"small":true}""");

        store.Put("big", large);
        store.Put("small", small);

        Assert.Equal(large, Read(store, "big"));
        Assert.Equal(small, Read(store, "small"));
    }

    [Fact]
    public void GIVEN_an_empty_store_WHEN_listing_ids_THEN_the_list_is_empty()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        Assert.Empty(store.ListIds());
    }

    [Fact]
    public void GIVEN_twenty_documents_WHEN_listing_ids_THEN_every_id_appears_exactly_once()
    {
        using var temp = new TempDirectory();
        using var store = OpenStore(temp.LogFile, output);

        for (var i = 0; i < 20; i++)
        {
            store.Put($"doc:{i}", Bytes($$"""{"index":{{i}}}"""));
        }

        store.Put("doc:7", Bytes("""{"index":999}""")); // overwrite: still exactly 20 ids

        // Ordering is not guaranteed (hash index) — compare as sorted sequences.
        var expected = Enumerable.Range(0, 20).Select(i => $"doc:{i}").Order(StringComparer.Ordinal);
        Assert.Equal(expected, store.ListIds().Order(StringComparer.Ordinal));
    }
}
