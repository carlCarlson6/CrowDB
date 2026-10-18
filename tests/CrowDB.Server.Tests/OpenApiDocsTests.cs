using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace CrowDB.Server.Tests;

/// <summary>
/// The API-reference contract (docs/006): the OpenAPI document is generated
/// from the mapped routes — so it cannot silently drift from the API — and the
/// Scalar UI at /scalar serves its page and its JavaScript from this server
/// (no CDN), pointing the browser at that document.
/// </summary>
public class OpenApiDocsTests(ITestOutputHelper output)
{
    private const string DocumentUrl = "/openapi/v1.json";

    /// <summary>Every route of the API, as the document must report it.</summary>
    private static readonly (string Path, string Method)[] Routes =
    [
        ("/docs", "get"),
        ("/docs", "post"),
        ("/docs/{id}", "get"),
        ("/docs/{id}", "put"),
        ("/docs/{id}", "delete"),
        ("/compact", "post"),
    ];

    /// <summary>The status codes the operations without a plain 200 answer with.</summary>
    private static readonly Dictionary<string, string[]> Responses = new()
    {
        ["get /docs/{id}"] = ["200", "404"],
        ["put /docs/{id}"] = ["204", "400"],
        ["post /docs"] = ["201", "400"],
        ["delete /docs/{id}"] = ["204", "404"],
        ["post /compact"] = ["204"],
    };

    [Fact]
    public async Task GIVEN_the_server_WHEN_requesting_the_openapi_document_THEN_it_is_served_as_json()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();

        var response = await client.GetAsync(DocumentUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("openapi", out _));
        Assert.True(document.RootElement.TryGetProperty("paths", out _));
    }

    [Fact]
    public async Task GIVEN_the_openapi_document_WHEN_reading_its_paths_THEN_every_route_appears_and_nothing_else_does()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(DocumentUrl));

        var documented = (
            from path in document.RootElement.GetProperty("paths").EnumerateObject()
            from operation in path.Value.EnumerateObject()
            select $"{operation.Name} {path.Name}").ToArray();
        var expected = Routes.Select(route => $"{route.Method} {route.Path}").ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GIVEN_the_openapi_document_WHEN_reading_each_operation_THEN_it_carries_a_summary_from_the_code()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(DocumentUrl));

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var summary = operation.Value.GetProperty("summary").GetString();
                Assert.False(
                    string.IsNullOrWhiteSpace(summary),
                    $"{operation.Name} {path.Name} has no summary — the XML comment did not reach the document.");
            }
        }
    }

    [Fact]
    public async Task GIVEN_the_openapi_document_WHEN_reading_an_operation_THEN_its_status_codes_match_the_api()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(DocumentUrl));
        var paths = document.RootElement.GetProperty("paths");

        foreach (var (path, method) in Routes)
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            var declared = operation.GetProperty("responses").EnumerateObject()
                .Select(response => response.Name).Order(StringComparer.Ordinal).ToArray();
            var expected = (Responses.TryGetValue($"{method} {path}", out var codes) ? codes : ["200"])
                .Order(StringComparer.Ordinal).ToArray();

            Assert.Equal(expected, declared);
        }

        // A response that only the XML comment can describe, not the framework.
        var created = paths.GetProperty("/docs").GetProperty("post")
            .GetProperty("responses").GetProperty("201").GetProperty("description").GetString();
        Assert.False(string.IsNullOrWhiteSpace(created));
    }

    [Fact]
    public async Task GIVEN_a_configured_api_key_WHEN_reading_the_document_THEN_it_declares_the_x_api_key_scheme_and_requires_it()
    {
        // The document mirrors the deployment shape (docs/006 §3): a browser
        // pastes the key into Scalar's authentication panel, which sends it
        // with every "try it" request.
        using var server = new CrowDbServer(output, apiKey: "correct-horse-battery-staple", environment: "Production");
        using var client = server.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(DocumentUrl));
        var root = document.RootElement;

        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKeyAuth");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());

        var requirement = root.GetProperty("security").EnumerateArray().Single();
        Assert.True(requirement.TryGetProperty("ApiKeyAuth", out _));
    }

    [Fact]
    public async Task GIVEN_no_key_in_development_WHEN_reading_the_document_THEN_it_does_not_require_security()
    {
        // The reference describes the server it runs on: nothing to authorize
        // locally, so there is no scheme and no requirement to show.
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(DocumentUrl));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("security", out _));
        Assert.False(root.GetProperty("components").TryGetProperty("securitySchemes", out _));
    }

    [Fact]
    public async Task GIVEN_a_request_that_came_through_a_proxy_WHEN_reading_the_document_THEN_the_server_url_uses_the_forwarded_scheme_and_host()
    {
        // On Railway the edge terminates TLS and calls the container over
        // plain HTTP (docs/006 §4): without honoring the forwarded headers the
        // document would advertise an http:// URL to an https:// page, and the
        // browser would block every "try it" request as mixed content.
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DocumentUrl);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "crowdb.up.railway.app");

        var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            "https://crowdb.up.railway.app/",
            document.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task GIVEN_the_server_root_WHEN_opening_it_THEN_it_sends_the_browser_to_the_reference()
    {
        // The root is the reference's front door (docs/006 §4): typing the
        // bare host lands in the Scalar UI.
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/scalar/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task GIVEN_a_browser_WHEN_opening_the_scalar_ui_THEN_the_page_is_served_and_points_at_the_document()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();

        // /scalar redirects to /scalar/; the client follows. The page carries
        // the document's route (relative, resolved against /scalar/) in its
        // configuration.
        var response = await client.GetAsync("/scalar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("openapi/v1.json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GIVEN_the_scalar_page_WHEN_fetching_its_scripts_THEN_this_server_serves_them()
    {
        using var server = new CrowDbServer(output);
        using var client = server.CreateClient();
        var page = await client.GetAsync("/scalar");
        page.EnsureSuccessStatusCode();
        var pageUri = page.RequestMessage!.RequestUri!;

        var scripts = Regex.Matches(await page.Content.ReadAsStringAsync(), """src=["']([^"']+\.js)["']""")
            .Select(match => new Uri(pageUri, match.Groups[1].Value))
            .ToArray();
        Assert.NotEmpty(scripts);

        foreach (var script in scripts)
        {
            // Same server, no CDN: the reference must work offline, and in the container.
            Assert.Equal(pageUri.Host, script.Host);
            var asset = await client.GetAsync(script.PathAndQuery);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.EndsWith("javascript", asset.Content.Headers.ContentType?.MediaType);
        }
    }
}
