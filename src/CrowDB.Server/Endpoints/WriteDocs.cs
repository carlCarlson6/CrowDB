using System;
using System.Text.Json;
using System.Threading.Tasks;
using CrowDB.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CrowDB.Server.Endpoints;

public static class WriteDocs
{
    public static void MapWriteDocs(this WebApplication app)
    {
        app.MapPost("/docs", CreateDocument)
            .Accepts<JsonElement>("application/json")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);
        app.MapPut("/docs/{id}", UpsertDocument)
            .Accepts<JsonElement>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);
    }

    /// <summary>Creates a document under a server-generated id.</summary>
    /// <remarks>
    /// The body is any JSON document; the engine stores its bytes verbatim.
    /// The id in the response is the document's only name — keep it to read
    /// the document back.
    /// </remarks>
    /// <response code="201">The document was appended to the log.</response>
    /// <response code="400">The body was empty or not a JSON document.</response>
    public static async Task<IResult> CreateDocument(Store store, HttpRequest request)
    {
        var document = await request.ReadJsonBodyAsync();
        if (document is null)
            return Results.BadRequest(new { error = "request body must be a JSON document" });

        var id = Guid.NewGuid().ToString("n");
        store.Put(id, document);
        return Results.Created($"/docs/{id}", new { id });
    }

    /// <summary>Stores a document under the id the client chose — last write wins.</summary>
    /// <remarks>
    /// The body is any JSON document; the engine stores its bytes verbatim.
    /// Writing an existing id appends a newer record: reads and listings then
    /// answer with the newest version.
    /// </remarks>
    /// <response code="204">The document was appended to the log.</response>
    /// <response code="400">The body was empty or not a JSON document.</response>
    public static async Task<IResult> UpsertDocument(Store store, HttpRequest request, string id)
    {
        var document = await request.ReadJsonBodyAsync();
        if (document is null)
            return Results.BadRequest(new { error = "request body must be a JSON document" });

        store.Put(id, document);
        return Results.NoContent();
    }
}
