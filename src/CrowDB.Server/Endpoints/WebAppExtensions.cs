using System.Text.Json;

namespace CrowDB.Server.Endpoints;

public static class WebAppExtensions
{
    public static void MapCrowDbEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => "CrowDB.Server is running. The API lives under /docs.");
        app.MapDeleteDocs();
        app.MapGetDocs();
        app.MapWriteDocs();
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