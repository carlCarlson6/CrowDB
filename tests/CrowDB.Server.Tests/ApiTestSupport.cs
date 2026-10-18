using System.Net;
using System.Text;
using System.Text.Json;

namespace CrowDB.Server.Tests;

/// <summary>Small helpers shared by the API tests.</summary>
internal static class ApiTestSupport
{
    /// <summary>A JSON request body, exactly the bytes given — documents stay opaque.</summary>
    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// The deployment credential: every request to a server with a key must
    /// carry it (docs/007). Tests that exercise the API rather than the gate
    /// call this on the client the factory handed them; the auth tests build
    /// their headers by hand instead.
    /// </summary>
    public static HttpClient WithKey(this HttpClient client, string key = CrowDbServer.TestKey)
    {
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    /// <summary>The /docs/{id} URL, with the id escaped for a URL path segment.</summary>
    public static string DocUrl(string id) => $"/docs/{Uri.EscapeDataString(id)}";

    /// <summary>GET /docs as the ids it returned.</summary>
    public static async Task<string[]> ListIds(HttpClient client)
    {
        var response = await client.GetAsync("/docs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return parsed.RootElement.EnumerateArray().Select(id => id.GetString()!).ToArray();
    }
}
