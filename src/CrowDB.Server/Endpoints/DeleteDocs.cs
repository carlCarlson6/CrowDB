using CrowDB.Engine;
using Microsoft.AspNetCore.Mvc;

namespace CrowDB.Server.Endpoints;

public static class DeleteDocs
{
    public static void MapDeleteDocs(this WebApplication app) => app.MapDelete(
        "/docs/{id}",
        Handler);

    private static IResult Handler(
        [FromRoute] string id,
        [FromServices] Store store,
        HttpRequest context)
    {
        var result = store.Delete(id);
        return result
            ? Results.NoContent()
            : Results.NotFound();
    }
}