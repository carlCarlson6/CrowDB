using System.Net;
using System.Text.Json;
using static CrowDB.Server.Tests.ApiTestSupport;

namespace CrowDB.Server.Tests;

/// <summary>
/// The HTTP contract of CrowDB.Server: the four locked endpoints (docs/001 §7),
/// their status codes, and the fact that documents travel as raw bytes. The
/// server is the real thing running in-process; only the data directory is
/// temporary.
/// </summary>
public class DocsApiTests
{
    [Fact]
    public async Task GIVEN_an_empty_server_WHEN_listing_documents_THEN_the_response_is_an_empty_json_array()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/docs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task GIVEN_a_put_document_WHEN_getting_it_THEN_the_bytes_come_back_verbatim()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();
        const string document = """{"name":"Ada","tags":["math","navy"],"note":"ü é 🚀"}""";

        var put = await client.PutAsync(DocUrl("user:1"), Json(document));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("application/json", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(document, await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GIVEN_a_put_document_WHEN_putting_a_newer_one_THEN_the_newest_wins()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();

        await client.PutAsync(DocUrl("user:1"), Json("""{"version":1}"""));
        var put = await client.PutAsync(DocUrl("user:1"), Json("""{"version":2}"""));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal("""{"version":2}""", await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GIVEN_two_documents_WHEN_listing_THEN_both_ids_are_returned()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();

        await client.PutAsync(DocUrl("user:1"), Json("""{"name":"Ada"}"""));
        await client.PutAsync(DocUrl("user:2"), Json("""{"name":"Grace"}"""));

        var ids = await ListIds(client);

        Assert.Equal(new[] { "user:1", "user:2" }, ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GIVEN_a_posted_document_WHEN_reading_the_response_THEN_a_generated_id_and_location_come_back()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();

        var response = await client.PostAsync("/docs", Json("""{"name":"generated"}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal($"/docs/{id}", response.Headers.Location?.ToString());

        var get = await client.GetAsync(DocUrl(id!));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("""{"name":"generated"}""", await get.Content.ReadAsStringAsync());
        Assert.Contains(id, await ListIds(client));
    }

    [Fact]
    public async Task GIVEN_an_unknown_id_WHEN_getting_it_THEN_the_response_is_404()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync(DocUrl("missing"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GIVEN_a_body_that_is_not_json_WHEN_putting_THEN_the_response_is_400_and_nothing_is_stored()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();

        var response = await client.PutAsync(DocUrl("user:1"), Json("not json at all"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.DoesNotContain("user:1", await ListIds(client));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocUrl("user:1"))).StatusCode);
    }

    [Fact]
    public async Task GIVEN_an_empty_body_WHEN_posting_THEN_the_response_is_400()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();

        var response = await client.PostAsync("/docs", new StringContent(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GIVEN_an_id_that_needs_url_encoding_WHEN_round_tripping_THEN_the_id_survives_exactly()
    {
        using var server = new CrowDbServer();
        using var client = server.CreateClient();
        const string id = "ünïcode id:🌍";

        var put = await client.PutAsync(DocUrl(id), Json("""{"ok":true}"""));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        Assert.Contains(id, await ListIds(client));
        var get = await client.GetAsync(DocUrl(id));
        Assert.Equal("""{"ok":true}""", await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GIVEN_a_log_from_a_previous_run_WHEN_the_server_starts_THEN_the_documents_are_readable()
    {
        using var server = new CrowDbServer();

        // The on-disk format is the contract (docs/001 §3): a previous process
        // can leave a valid log behind, and a restart must serve it.
        await File.WriteAllTextAsync(server.LogFile,
            """{"v":1,"id":"user:1","doc":{"name":"Ada"}}""" + "\n");

        using var client = server.CreateClient();

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("""{"name":"Ada"}""", await get.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "user:1" }, await ListIds(client));
    }
}
