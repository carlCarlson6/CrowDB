using CrowDB.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CrowDB.Server.Endpoints;

public static class DeleteDocs
{
    public static void MapDeleteDocs(this WebApplication app) => app.MapDelete(
        "/docs/{id}",
        Handler)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

    /// <summary>Deletes a document by appending a tombstone — the bytes stay, the id stops resolving.</summary>
    /// <response code="204">The tombstone was appended.</response>
    /// <response code="404">No live document has that id.</response>
    public static IResult Handler(string id, Store store)
    {
        var result = store.Delete(id);
        return result
            ? Results.NoContent()
            : Results.NotFound();
    }
}
