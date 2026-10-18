using CrowDB.Engine;
using CrowDB.Server;
using CrowDB.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// The engine keeps everything in one log file inside this directory. The value
// in appsettings.json ("../../data") resolves to the repo-root data/ directory.
var dataDirectory = Path.GetFullPath(
    builder.Configuration["CrowDB:DataDirectory"] ?? "data",
    builder.Environment.ContentRootPath);

// The caller owns the directory, the store owns the file inside it — the same
// split as in the engine tests (TempDirectory creates the directory, the store
// gets <directory>/crowdb.log). data/ is gitignored, so a fresh clone has none.
Directory.CreateDirectory(dataDirectory);

// One store for the whole process: it owns the log file handle. Constructing
// it replays the log and rebuilds the in-memory index (recovery).
var logFile = Path.Combine(dataDirectory, Constants.LogFilename);
builder.Services
    .AddSingleton<Store>(sp => new Store(logFile, sp.GetRequiredService<ILogger<Store>>()));


// Logging is already wired by WebApplication.CreateBuilder: it adds the console
// logger, and appsettings*.json sets the levels. The store gets its ILogger<Store>
// from the host's service provider, so engine logs use that same pipeline — the
// engine itself only knows the ILogger abstraction.
var app = builder.Build();



app.MapCrowDbEndpoints();

app.Run();

// The API tests (tests/CrowDB.Server.Tests) boot this program in-process with
// WebApplicationFactory<Program>, which needs the entry point to be public.
public partial class Program;
