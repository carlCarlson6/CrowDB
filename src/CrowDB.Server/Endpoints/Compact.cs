using CrowDB.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CrowDB.Server.Endpoints;

// Step 3 (docs/003): a maintenance verb — rewrite the log as only its live
// records so dead puts and tombstones stop occupying disk. 204, no body; the
// engine logs the before/after byte counts.
public static class Compact
{
    public static void MapCompact(this WebApplication app) => app.MapPost("/compact", Handler);

    private static IResult Handler(Store store)
    {
        store.Compact();
        return Results.NoContent();
    }
}
