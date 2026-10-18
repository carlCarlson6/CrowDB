using System.Net;
using Xunit.Abstractions;
using static CrowDB.Server.Tests.ApiTestSupport;

namespace CrowDB.Server.Tests;

/// <summary>
/// Step 4 at the HTTP edge (docs/004 §4.5): a data directory left behind by a
/// v1 process is migrated when the server opens the log, and the API keeps
/// every promise — same answers, same status codes. The surface is unchanged;
/// migration is a storage-layer event, invisible over HTTP.
/// </summary>
public class MigrationApiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GIVEN_a_v1_log_with_dead_records_tombstones_and_a_torn_tail_WHEN_the_server_starts_THEN_the_live_documents_are_served()
    {
        using var server = new CrowDbServer(output);

        // A v1 log (JSONL, one envelope per line, no trailing newline — the
        // exact bytes a pre-step-4 process left behind).
        await File.WriteAllTextAsync(server.LogFile, string.Join('\n', new[]
        {
            """{"Version":"1","Id":"user:1","Doc":"{\u0022version\u0022:1}"}""",
            """{"Version":"1","Id":"user:1","Doc":"{\u0022version\u0022:2}"}""",
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
            """{"Version":"1","Id":"user:2","Doc":"--DELETED--","Kind":"DELETE"}""",
            """{"Version":"1","Id":"user:3","Doc":"{\u0022name\u0022:tor""", // torn tail: dropped
        }));

        using var client = server.CreateClient();

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("""{"version":2}""", await get.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocUrl("user:2"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocUrl("user:3"))).StatusCode);
        Assert.Equal(new[] { "user:1" }, await ListIds(client));
    }

    [Fact]
    public async Task GIVEN_a_v1_log_WHEN_the_server_starts_THEN_the_file_on_disk_is_now_v2_and_a_restart_serves_the_migrated_data()
    {
        using var data = new TempDirectory();
        await File.WriteAllTextAsync(data.LogFile,
            """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""");

        using (var server = new CrowDbServer(output, data))
        using (var client = server.CreateClient())
        {
            var get = await client.GetAsync(DocUrl("user:1"));
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("""{"name":"Ada"}""", await get.Content.ReadAsStringAsync());
        }

        // The old process is gone; the file it left is a v2 log now.
        var log = await File.ReadAllBytesAsync(data.LogFile);
        Assert.Equal(0x43, log[0]); // magic "C"
        Assert.Equal(0x44, log[1]); // magic "D"
        Assert.Equal(-1, log.AsSpan().IndexOf("{\"Version\""u8));

        // A fresh server over the migrated bytes serves the same answers, and
        // new writes land as frames after the migrated ones.
        using (var server = new CrowDbServer(output, data))
        using (var client = server.CreateClient())
        {
            Assert.Equal("""{"name":"Ada"}""", await (await client.GetAsync(DocUrl("user:1"))).Content.ReadAsStringAsync());

            var put = await client.PutAsync(DocUrl("user:1"), Json("""{"name":"Ada Lovelace"}"""));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        }

        var finalLog = await File.ReadAllBytesAsync(data.LogFile);
        Assert.NotEqual(-1, finalLog.AsSpan().IndexOf("""{"name":"Ada Lovelace"}"""u8)); // documents are raw bytes on disk now
    }
}
