using System.Net;
using System.Text.Json;
using Xunit.Abstractions;
using static CrowDB.Server.Tests.ApiTestSupport;

namespace CrowDB.Server.Tests;

/// <summary>
/// The deployment contract of CrowDB.Server (docs/005, amended by docs/007):
/// the API key is mandatory — no environment serves without one, and a
/// configured key gates every endpoint, the check happening at the HTTP edge
/// before routing and before the engine, so an unauthenticated request can
/// never reach the store. The API reference is the one exception (docs/006):
/// / (which redirects into the UI), /scalar and /openapi are public.
/// </summary>
public class ApiKeyAuthTests(ITestOutputHelper output)
{
    private const string Key = "correct-horse-battery-staple";

    [Theory]
    [InlineData("GET", "/docs")]
    [InlineData("GET", "/docs/user:1")]
    [InlineData("PUT", "/docs/user:1")]
    [InlineData("POST", "/docs")]
    [InlineData("DELETE", "/docs/user:1")]
    [InlineData("POST", "/compact")]
    public async Task GIVEN_a_configured_api_key_WHEN_a_request_arrives_without_the_header_THEN_it_is_rejected_with_401(string method, string path)
    {
        // The factory's default environment is Development, so this theory
        // also pins the removal of the development exemption (docs/007): a
        // configured key gates the local server exactly like a deployment.
        using var server = new CrowDbServer(output, apiKey: Key);
        using var client = server.CreateClient();
        using var request = Request(method, path);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("correct-horse-battery-stap")]       // a prefix of the key
    [InlineData("correct-horse-battery-staple-extra")] // the key plus a suffix
    [InlineData("")]
    public async Task GIVEN_a_configured_api_key_WHEN_the_header_carries_the_wrong_key_THEN_401(string wrongKey)
    {
        using var server = new CrowDbServer(output, apiKey: Key);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", wrongKey);

        var response = await client.GetAsync("/docs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GIVEN_a_configured_api_key_WHEN_the_right_key_is_sent_THEN_every_operation_works()
    {
        // Production is the deployment shape: a key is configured, and the
        // full API surface answers normally with it (docs/005 §4).
        using var server = new CrowDbServer(output, apiKey: Key, environment: "Production");
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);

        var post = await client.PostAsync("/docs", Json("""{"name":"generated"}"""));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        using var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        var generatedId = created.RootElement.GetProperty("id").GetString()!;

        var put = await client.PutAsync(DocUrl("user:1"), Json("""{"name":"Ada"}"""));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync(DocUrl("user:1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("""{"name":"Ada"}""", await get.Content.ReadAsStringAsync());

        Assert.Contains("user:1", await ListIds(client));

        var delete = await client.DeleteAsync(DocUrl(generatedId));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var compact = await client.PostAsync("/compact", content: null);
        Assert.Equal(HttpStatusCode.NoContent, compact.StatusCode);
    }

    [Fact]
    public async Task GIVEN_a_configured_api_key_WHEN_an_unauthenticated_put_arrives_THEN_nothing_reaches_the_store()
    {
        using var server = new CrowDbServer(output, apiKey: Key);
        using var unauthenticated = server.CreateClient();
        using var authenticated = server.CreateClient();
        authenticated.DefaultRequestHeaders.Add("X-Api-Key", Key);

        var rejected = await unauthenticated.PutAsync(DocUrl("user:1"), Json("""{"name":"Ada"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await authenticated.GetAsync(DocUrl("user:1"))).StatusCode);
        Assert.Empty(await ListIds(authenticated));
    }

    [Fact]
    public async Task GIVEN_a_configured_api_key_WHEN_fetching_the_api_reference_THEN_it_is_public_while_the_api_stays_gated()
    {
        // docs/006 amends docs/005: the reference describes the API, the key
        // protects the data. A browser must load /scalar and the document it
        // fetches without the header — everything under /docs still needs it.
        using var server = new CrowDbServer(output, apiKey: Key, environment: "Production");
        using var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);

        // The root is the reference's front door and redirects into it; the
        // client follows.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        // Only the reference prefixes are public — matched per path segment,
        // and not as loose string prefixes.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/scalar-extra")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/docs")).StatusCode);
    }

    [Theory]
    [InlineData(null, "Development")]
    [InlineData("", "Development")]
    [InlineData("   ", "Development")]
    [InlineData(null, "Production")]
    [InlineData("", "Production")]
    [InlineData("   ", "Production")]
    public void GIVEN_no_usable_key_WHEN_the_server_starts_THEN_it_refuses_to_start_in_every_environment(string? apiKey, string environment)
    {
        // Fail closed everywhere (docs/007): a server without a key — local
        // `dotnet run` included — must not quietly serve everyone, and the
        // error names the setting to fix.
        using var server = new CrowDbServer(output, apiKey: apiKey, environment: environment);

        var exception = Assert.Throws<InvalidOperationException>(() => server.CreateClient());
        var message = $"{exception.Message} {exception.InnerException?.Message}";

        Assert.Contains("CrowDB:ApiKey", message);
    }

    /// <summary>A request for the auth theory: writes carry a JSON body, like real ones do.</summary>
    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST" && path != "/compact")
            request.Content = Json("""{"name":"Ada"}""");
        return request;
    }
}
