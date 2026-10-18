using CrowDB.Engine;
using Microsoft.AspNetCore.Mvc;

namespace CrowDB.Server.Endpoints;

public static class GetDocs
{
    public static void MapGetDocs(this WebApplication app)
    {
        app.MapGet("/docs", ListIds);
        app.MapGet("/docs/{id}", GetDoc);
    }

    private static IResult ListIds(Store store) => Results.Ok(store.ListIds());

    private static IResult GetDoc(Store store, string id) => 
        store.TryGet(id, out var document)
            ? Results.Bytes(document, "application/json")
            : Results.NotFound(new { error = "document not found", id });
}