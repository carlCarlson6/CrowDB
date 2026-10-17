using CrowDB.Engine;
using Microsoft.AspNetCore.Mvc;

namespace CrowDB.Server.Endpoints;

public static class WriteDocs
{
    public static void MapWriteDocs(this WebApplication app)
    {
        app.MapPost("/docs", CreateDocument);
        app.MapPut("/docs/{id}", UpsertDocument);
    }
    
    private static async Task<IResult> CreateDocument(Store store, HttpRequest request)
    {
        var document = await request.ReadJsonBodyAsync();
        if (document is null)
            return Results.BadRequest(new { error = "request body must be a JSON document" });

        var id = Guid.NewGuid().ToString("n");
        store.Put(id, document);
        return Results.Created($"/docs/{id}", new { id });
    }
    
    private static async Task<IResult> UpsertDocument(Store store, HttpRequest request, string id)
    {
        var document = await request.ReadJsonBodyAsync();
        if (document is null)
            return Results.BadRequest(new { error = "request body must be a JSON document" });

        store.Put(id, document);
        return Results.NoContent();
    }
}