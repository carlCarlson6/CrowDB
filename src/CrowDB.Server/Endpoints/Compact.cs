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
    public static void MapCompact(this WebApplication app) => app.MapPost("/compact", Handler)
        .Produces(StatusCodes.Status204NoContent);

    /// <summary>Rewrites the log as only its live records, reclaiming dead puts and tombstones.</summary>
    /// <remarks>
    /// The rewrite lands in a new file that is swapped in with one atomic
    /// rename, so readers and writers keep working throughout (docs/003).
    /// </remarks>
    /// <response code="204">The log now holds only live documents.</response>
    public static IResult Handler(Store store)
    {
        store.Compact();
        return Results.NoContent();
    }
}
