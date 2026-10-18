using CrowDB.Engine;

namespace CrowDB.Server.Endpoints;

public static class DeleteDocs
{
    public static void MapDeleteDocs(this WebApplication app) => app.MapDelete(
        "/docs/{id}",
        Handler);

    private static IResult Handler(string id, Store store)
    {
        var result = store.Delete(id);
        return result
            ? Results.NoContent()
            : Results.NotFound();
    }
}