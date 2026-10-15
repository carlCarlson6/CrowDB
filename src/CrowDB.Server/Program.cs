using System.Text.Json;
using CrowDB.Engine;

var builder = WebApplication.CreateBuilder(args);

// The engine keeps everything in one log file that it creates inside this
// directory. The value in appsettings.json ("../../data") resolves to the
// repo-root data/ directory.
var dataDirectory = Path.GetFullPath(
    builder.Configuration["CrowDB:DataDirectory"] ?? "data",
    builder.Environment.ContentRootPath);

// One store for the whole process: it owns the log file handle. Constructing
// it replays the log and rebuilds the in-memory index (recovery).
var store = new DocumentStore(dataDirectory);

var app = builder.Build();

// A root route so that http://localhost:5217/ is not a 404.
app.MapGet("/", () => "CrowDB.Server is running. The API lives under /docs.");

// GET /docs -> every id in the store. Unordered; it is the index's key set.
app.MapGet("/docs", () => store.ListIds());

// GET /docs/{id} -> the stored document, verbatim.
app.MapGet("/docs/{id}", (string id) =>
    store.TryGet(id, out var document)
        ? Results.Bytes(document, "application/json")
        : Results.NotFound(new { error = "document not found", id }));

// PUT /docs/{id} -> upsert (last write wins).
app.MapPut("/docs/{id}", async (string id, HttpRequest request) =>
{
    var document = await ReadJsonBodyAsync(request);
    if (document is null)
        return Results.BadRequest(new { error = "request body must be a JSON document" });

    store.Put(id, document);
    return Results.NoContent();
});

// POST /docs -> store under a server-generated id and return it.
app.MapPost("/docs", async (HttpRequest request) =>
{
    var document = await ReadJsonBodyAsync(request);
    if (document is null)
        return Results.BadRequest(new { error = "request body must be a JSON document" });

    var id = Guid.NewGuid().ToString("n");
    store.Put(id, document);
    return Results.Created($"/docs/{id}", new { id });
});

app.Run();

// The HTTP layer checks that the body is JSON and then hands the raw bytes to
// the engine — the engine never parses documents, it stores bytes. Validating
// here keeps the log's invariant: every record is a well-formed envelope.
static async Task<byte[]?> ReadJsonBodyAsync(HttpRequest request)
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
