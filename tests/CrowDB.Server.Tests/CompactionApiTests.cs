using System.Net;
using CrowDB.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using static CrowDB.Server.Tests.ApiTestSupport;

namespace CrowDB.Server.Tests;

/// <summary>
/// The HTTP contract of compaction (docs/003): <c>POST /compact</c> rewrites the
/// log as only its live records and answers <c>204</c>; the server keeps
/// serving, and the compacted file remains a valid log for the next start.
/// </summary>
public class CompactionApiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GIVEN_overwrites_and_a_delete_WHEN_posting_compact_THEN_204_comes_back_and_dead_bytes_are_gone()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();
        await client.PutAsync(DocUrl("user:1"), Json("""{"version":1}"""));
        await client.PutAsync(DocUrl("user:1"), Json("""{"version":2}"""));
        await client.PutAsync(DocUrl("user:2"), Json("""{"name":"Grace"}"""));
        await client.DeleteAsync(DocUrl("user:2"));
        var sizeBefore = new FileInfo(server.LogFile).Length;

        var response = await client.PostAsync("/compact", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(new FileInfo(server.LogFile).Length < sizeBefore,
            "the dead put and the tombstone should be reclaimed");

        // Still serving, with the same answers as before.
        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal("""{"version":2}""", await get.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocUrl("user:2"))).StatusCode);
        Assert.Equal(new[] { "user:1" }, await ListIds(client));
        Assert.False(File.Exists(server.LogFile + ".compact"), "the rename consumes the temp file");
    }

    [Fact]
    public async Task GIVEN_an_empty_server_WHEN_posting_compact_THEN_204_comes_back_and_writes_still_work()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();

        var response = await client.PostAsync("/compact", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ListIds(client));

        var put = await client.PutAsync(DocUrl("user:1"), Json("""{"ok":true}"""));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal("""{"ok":true}""", await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GIVEN_a_log_compacted_by_an_earlier_run_WHEN_the_server_starts_THEN_every_live_document_is_served()
    {
        // Produce a compacted log with the real engine, then hand its exact
        // bytes to a fresh server — the compacted file must be a valid log.
        using var temp = new TempDirectory();
        using (var store = new Store(temp.LogFile, NullLogger<Store>.Instance))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Put("user:1", Bytes("""{"version":2}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
            store.Delete("user:2");
            store.Compact();
        }

        using var server = new CrowDbServer(output);
        await File.WriteAllBytesAsync(server.LogFile, await File.ReadAllBytesAsync(temp.LogFile));
        using var client = server.CreateClient();

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("""{"version":2}""", await get.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "user:1" }, await ListIds(client));
    }

    private static byte[] Bytes(string json) => System.Text.Encoding.UTF8.GetBytes(json);
}
