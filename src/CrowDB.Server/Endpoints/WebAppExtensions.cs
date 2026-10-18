using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Scalar.AspNetCore;

namespace CrowDB.Server.Endpoints;

public static class WebAppExtensions
{
    public static void MapCrowDbEndpoints(this WebApplication app)
    {
        // The root is the reference's front door (docs/006 §4): typing the
        // bare host lands in the Scalar UI. Like the UI itself it is public
        // (ApiKeyAuthentication) and excluded from the document — it is not
        // part of the API contract.
        app.MapGet("/", () => Results.Redirect("/scalar/")).ExcludeFromDescription();
        app.MapCompact();
        app.MapDeleteDocs();
        app.MapGetDocs();
        app.MapWriteDocs();

        // The API reference (docs/006): the OpenAPI document generated from
        // these routes, plus the Scalar UI that renders it. The descriptions
        // come from XML comments on the handler methods, which is why those
        // are public — the source generator only reads public members. Both
        // routes stay public when an API key is configured: they describe the
        // API, the key protects the data.
        app.MapOpenApi();
        app.MapScalarApiReference();
    }
}

public static class HttpRequestExtensions
{
    // The HTTP layer checks that the body is JSON and then hands the raw bytes to
    // the engine — the engine never parses documents, it stores bytes. Validating
    // here keeps the log's invariant: every record is a well-formed envelope.
    public static async Task<byte[]?> ReadJsonBodyAsync(this HttpRequest request)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        if (bytes.Length == 0)
            return null;

        try
        {
            using var _ = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }

        return bytes;
    }
} 