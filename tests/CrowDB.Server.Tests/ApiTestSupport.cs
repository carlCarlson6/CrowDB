using System.Net;
using System.Text;
using System.Text.Json;

namespace CrowDB.Server.Tests;

/// <summary>Small helpers shared by the API tests.</summary>
internal static class ApiTestSupport
{
    /// <summary>A JSON request body, exactly the bytes given — documents stay opaque.</summary>
    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

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
