using CrowDB.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CrowDB.Server.Endpoints;

public static class GetDocs
{
    public static void MapGetDocs(this WebApplication app)
    {
        app.MapGet("/docs", ListIds)
            .Produces<string[]>(StatusCodes.Status200OK);
        app.MapGet("/docs/{id}", GetDoc)
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>Lists the ids of every live document.</summary>
    /// <response code="200">The ids; the order is unspecified (in-memory hash index).</response>
    public static IResult ListIds(Store store) => Results.Ok(store.ListIds());

    /// <summary>Reads a document by id.</summary>
    /// <response code="200">The document's bytes, exactly as they were written.</response>
    /// <response code="404">No live document has that id.</response>
    public static IResult GetDoc(Store store, string id) =>
        store.TryGet(id, out var document)
            ? Results.Bytes(document, "application/json")
            : Results.NotFound(new { error = "document not found", id });
}
