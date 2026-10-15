using CrowDB.Engine;
using CrowDB.Server;

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

app.MapCrowDbEndpoints(store);

app.Run();
