using System.Net;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;
using static CrowDB.Server.Tests.ApiTestSupport;

namespace CrowDB.Server.Tests;

/// <summary>
/// The HTTP contract of CrowDB.Server: the locked endpoints (docs/000 §7, plus
/// the DELETE added in docs/002), their status codes, and the fact that
/// documents travel as raw bytes. Since docs/006 the write endpoints declare
/// their request body, so a non-JSON Content-Type is answered 415 before the
/// handler — the JSON-content-type 400s are unchanged. The server is the real
/// thing running in-process; only the data directory is temporary.
/// </summary>
public class DocsApiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GIVEN_an_empty_server_WHEN_listing_documents_THEN_the_response_is_an_empty_json_array()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

        var response = await client.GetAsync("/docs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task GIVEN_a_put_document_WHEN_getting_it_THEN_the_bytes_come_back_verbatim()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();
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
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

        await client.PutAsync(DocUrl("user:1"), Json("""{"version":1}"""));
        var put = await client.PutAsync(DocUrl("user:1"), Json("""{"version":2}"""));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal("""{"version":2}""", await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GIVEN_two_documents_WHEN_listing_THEN_both_ids_are_returned()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

        await client.PutAsync(DocUrl("user:1"), Json("""{"name":"Ada"}"""));
        await client.PutAsync(DocUrl("user:2"), Json("""{"name":"Grace"}"""));

        var ids = await ListIds(client);

        Assert.Equal(new[] { "user:1", "user:2" }, ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GIVEN_a_posted_document_WHEN_reading_the_response_THEN_a_generated_id_and_location_come_back()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

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
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

        var response = await client.GetAsync(DocUrl("missing"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GIVEN_a_body_that_is_not_json_WHEN_putting_THEN_the_response_is_400_and_nothing_is_stored()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

        var response = await client.PutAsync(DocUrl("user:1"), Json("not json at all"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.DoesNotContain("user:1", await ListIds(client));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocUrl("user:1"))).StatusCode);
    }

    [Fact]
    public async Task GIVEN_an_empty_json_body_WHEN_posting_THEN_the_response_is_400()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

        // The body is empty but declares JSON, so the content-type gate
        // (docs/006 §5) lets it through to the handler's JSON check.
        var response = await client.PostAsync("/docs", Json(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GIVEN_a_non_json_content_type_WHEN_writing_THEN_the_response_is_415_and_nothing_is_stored()
    {
        // Declaring the request body (Accepts) switches on .NET 10's
        // content-type validation: text/plain is refused before the handler
        // ever reads the bytes (docs/006 §5).
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();
        var body = new StringContent("""{"name":"Ada"}""", Encoding.UTF8, "text/plain");

        var response = await client.PutAsync(DocUrl("user:1"), body);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await ListIds(client));
    }

    [Fact]
    public async Task GIVEN_an_id_that_needs_url_encoding_WHEN_round_tripping_THEN_the_id_survives_exactly()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();
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
        using var server = new CrowDbServer(output);

        // The on-disk format is the contract (docs/000 §3): a previous process
        // can leave a valid log behind, and a restart must serve it. A v1 log
        // is migrated to v2 on open (step 4, docs/004) — the answers must not change.
        await File.WriteAllTextAsync(server.LogFile,
            """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}"""+"\n");

        using var client = server.CreateClient().WithKey();

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("""{"name":"Ada"}""", await get.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "user:1" }, await ListIds(client));
    }

    [Fact]
    public async Task GIVEN_a_stored_document_WHEN_deleting_it_THEN_204_comes_back_and_it_is_gone_everywhere()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();
        await client.PutAsync(DocUrl("user:1"), Json("""{"name":"Ada"}"""));

        var delete = await client.DeleteAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocUrl("user:1"))).StatusCode);
        Assert.DoesNotContain("user:1", await ListIds(client));
    }

    [Fact]
    public async Task GIVEN_an_unknown_id_WHEN_deleting_it_THEN_the_response_is_404()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();

        var response = await client.DeleteAsync(DocUrl("missing"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GIVEN_a_deleted_document_WHEN_deleting_it_again_THEN_the_response_is_404()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();
        await client.PutAsync(DocUrl("user:1"), Json("""{"name":"Ada"}"""));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(DocUrl("user:1"))).StatusCode);

        var second = await client.DeleteAsync(DocUrl("user:1"));

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task GIVEN_a_deleted_id_WHEN_putting_it_again_THEN_it_is_stored_again()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient().WithKey();
        await client.PutAsync(DocUrl("user:1"), Json("""{"version":1}"""));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(DocUrl("user:1"))).StatusCode);

        var put = await client.PutAsync(DocUrl("user:1"), Json("""{"version":2}"""));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("""{"version":2}""", await get.Content.ReadAsStringAsync());
        Assert.Contains("user:1", await ListIds(client));
    }

    [Fact]
    public async Task GIVEN_a_log_with_a_tombstone_from_a_previous_run_WHEN_the_server_starts_THEN_the_id_stays_deleted()
    {
        using var server = new CrowDbServer(output);

        // The on-disk format is the contract (docs/002 §3): a previous process
        // can leave a tombstoned — v1 — log behind, and a restart must serve it
        // that way; the migration (step 4, docs/004) keeps the verdict.
        await File.WriteAllTextAsync(server.LogFile, string.Join('\n', new[]
        {
            """{"Version":"1","Id":"user:1","Doc":"{\u0022name\u0022:\u0022Ada\u0022}"}""",
            """{"Version":"1","Id":"user:1","Doc":"--DELETED--", "Kind": "DELETE" }""",
            """{"Version":"1","Id":"user:2","Doc":"{\u0022name\u0022:\u0022Grace\u0022}"}""",
        }) + "\n");

        using var client = server.CreateClient().WithKey();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DocUrl("user:1"))).StatusCode);
        var grace = await client.GetAsync(DocUrl("user:2"));
        Assert.Equal("""{"name":"Grace"}""", await grace.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "user:2" }, await ListIds(client));
    }
}
